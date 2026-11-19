using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// Local image generation (the Images page, MainWindow.ImageGen.cs) with stable-diffusion.cpp's sd-cli.exe, installed into
// <data>\sd.cpp the way llama-server is. The curated recipes download from Hugging Face into <data>\image-models
// (HubLayout.Flat: root\<repo name>\<file>); a file two recipes share downloads once.

// One model file: the sd-cli flag that takes it, its repo and path in the repo, and its size (shown before the download;
// the download asks the hub for the real size).
internal sealed record ImageFile(string Flag, string Repo, string Path, long Size);

internal sealed record ImageRecipe(string Id, string Name, string Note, ImageFile[] Files, int Steps, double Cfg, string Sampler, bool Negative, int Multiple)
{
    public long TotalSize => Files.Sum(f => f.Size);
}

// What one generation used: saved as the last-used settings (AppSettings.ImageGen, Seed -1 = random) and beside each
// picture as <name>.json (Seed then that picture's own seed). Aspect and LongSide are the page's choices that made Width/Height.
internal sealed class ImageSettings
{
    public string RecipeId { get; set; } = ImageGen.Recipes[0].Id;
    public string Prompt { get; set; } = "";
    public string Negative { get; set; } = "";
    public string Aspect { get; set; } = "1:1";
    public int LongSide { get; set; } = 1024;
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public int Steps { get; set; } = 8;
    public double Cfg { get; set; } = 1.0;
    public long Seed { get; set; } = -1;
    public string Sampler { get; set; } = ""; // "" = sd-cli's default for the model
    public int Batch { get; set; } = 1;
    public bool LowVram { get; set; }
    public double Strength { get; set; } = 0.75;
    public string InitImage { get; set; } = ""; // "" = text to image
}

internal sealed record ImageProgress(int Step, int Steps, string Speed, bool Loading);

internal static class ImageGen
{
    public static string RuntimeFolder => Path.Combine(AppSettings.DataRoot, "sd.cpp");
    public static string CliPath => Path.Combine(RuntimeFolder, "sd-cli.exe");
    public static string ModelsRoot => Path.Combine(AppSettings.DataRoot, "image-models");
    public static string ImagesFolder => Path.Combine(AppSettings.DataRoot, "images");
    public const string ReleaseUrl = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest";

    public static readonly string[] Aspects = ["1:1", "4:3", "3:4", "16:9", "9:16"];
    public static readonly int[] LongSides = [512, 768, 1024, 1536];
    // sd-cli --help's --sampling-method list
    public static readonly string[] Samplers = ["euler", "euler_a", "heun", "dpm2", "dpm++2s_a", "dpm++2m", "dpm++2mv2", "ipndm", "ipndm_v", "lcm",
        "ddim_trailing", "tcd", "res_multistep", "res_2s", "er_sde", "euler_cfg_pp", "euler_a_cfg_pp", "euler_ge", "dpm++2m_sde", "dpm++2m_sde_bt", "lms"];

    // Comfy-Org/z_image_turbo and Comfy-Org/flux2-klein were renamed; the hub redirects the old names, the downloader does not.
    public static readonly ImageRecipe[] Recipes =
    [
        new("z-image-turbo", "Z-Image Turbo", "Recommended · runs on 4-6 GB VRAM",
        [
            new("--diffusion-model", "leejet/Z-Image-Turbo-GGUF", "z_image_turbo-Q4_K.gguf", 3864250304),
            new("--vae", "Comfy-Org/Z-Image-Turbo", "split_files/vae/ae.safetensors", 335304388),
            new("--llm", "unsloth/Qwen3-4B-Instruct-2507-GGUF", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2497281120),
        ], Steps: 8, Cfg: 1.0, Sampler: "", Negative: false, Multiple: 16),
        new("flux3-klein", "FLUX.3 Image Klein", "",
        [
            new("--diffusion-model", "leejet/flux-3-image-klein-GGUF", "flux3-image-klein-Q4_K.gguf", 3514023104),
            new("--vae", "Comfy-Org/Flux-2-Klein-4B", "split_files/vae/flux2-vae.safetensors", 336211292),
            new("--llm", "Qwen/Qwen3-VL-4B-Instruct-GGUF", "Qwen3VL-4B-Instruct-Q4_K_M.gguf", 2497281664),
        ], Steps: 16, Cfg: 1.0, Sampler: "euler", Negative: false, Multiple: 16),
        new("qwen-image-2.1", "Qwen-Image 2.1", "Large",
        [
            new("--diffusion-model", "leejet/Qwen-Image-2.1-GGUF", "qwen_image_2.1-Q4_K.gguf", 4197494816),
            new("--vae", "Comfy-Org/Qwen-Image-2.1", "vae/qwen_image_2.1_vae_bf16.safetensors", 675509688),
            new("--llm", "Qwen/Qwen3-VL-8B-Instruct-GGUF", "Qwen3VL-8B-Instruct-Q4_K_M.gguf", 5027784800),
        ], Steps: 20, Cfg: 6.0, Sampler: "euler", Negative: false, Multiple: 32),
        new("sdxl", "SDXL 1.0", "",
        [
            new("-m", "stabilityai/stable-diffusion-xl-base-1.0", "sd_xl_base_1.0.safetensors", 6938078334),
            new("--vae", "madebyollin/sdxl-vae-fp16-fix", "sdxl_vae.safetensors", 334641162),
        ], Steps: 30, Cfg: 7.0, Sampler: "", Negative: true, Multiple: 64),
    ];

    public static ImageRecipe Recipe(string id) => Recipes.FirstOrDefault(r => r.Id == id) ?? Recipes[0];

    public static string LocalPath(string root, ImageFile file) => Path.Combine(root, file.Repo.Split('/')[^1], file.Path.Replace('/', Path.DirectorySeparatorChar));

    // Width and height for an aspect ("16:9") and long side, each rounded to the nearest multiple (at least one multiple).
    public static (int Width, int Height) Size(string aspect, int longSide, int multiple)
    {
        var parts = aspect.Split(':');
        double a = double.Parse(parts[0], CultureInfo.InvariantCulture), b = double.Parse(parts[1], CultureInfo.InvariantCulture);
        int Round(double x) => Math.Max(multiple, (int)Math.Round(x / multiple, MidpointRounding.AwayFromZero) * multiple);
        return a >= b ? (Round(longSide), Round(longSide * b / a)) : (Round(longSide * a / b), Round(longSide));
    }

    public static List<string> Args(ImageRecipe recipe, ImageSettings s, string modelsRoot, string output)
    {
        var args = new List<string>();
        foreach (var file in recipe.Files)
            args.AddRange([file.Flag, LocalPath(modelsRoot, file)]);
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        args.AddRange(["-p", s.Prompt]);
        if (recipe.Negative && s.Negative.Trim().Length > 0)
            args.AddRange(["-n", s.Negative]);
        args.AddRange(["--cfg-scale", F(s.Cfg), "--steps", s.Steps.ToString(), "-W", s.Width.ToString(), "-H", s.Height.ToString(), "-s", s.Seed.ToString()]);
        if (s.Batch > 1)
            args.AddRange(["-b", s.Batch.ToString()]);
        if (s.Sampler.Length > 0)
            args.AddRange(["--sampling-method", s.Sampler]);
        if (s.LowVram)
            args.AddRange(["--offload-to-cpu", "--vae-tiling"]);
        if (s.InitImage.Length > 0)
            args.AddRange(["-i", s.InitImage, "--strength", F(s.Strength)]);
        args.AddRange(["-o", output]);
        return args;
    }

    // sd-cli's progress line (stdout, \r-separated): "  |=====>    | 3/8 - 1.23s/it" while sampling (it/s when faster),
    // "  |#####     | 120/500 - 300.00MB/s" while loading weights. Null for any other line.
    public static ImageProgress? ParseProgress(string line)
    {
        var m = Regex.Match(line, @"\|\s*(\d+)/(\d+) - +([\d.]+)(s/it|it/s|MB/s|GB/s)");
        return m.Success ? new(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Value + " " + m.Groups[4].Value, m.Groups[4].Value.EndsWith("B/s")) : null;
    }

    // The release's Windows build for this machine: NVIDIA → the CUDA zip and its matching cudart zip, else Vulkan, else CPU.
    public static LauncherLogic.LlamaAssets PickAssets(string releaseJson, bool nvidia)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var assets = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? "")).ToList();
        string? Find(string pattern) => assets.FirstOrDefault(a => Regex.IsMatch(a.Name, pattern, RegexOptions.IgnoreCase)).Url;
        if (nvidia)
            foreach (var cuda in assets.Select(a => Regex.Match(a.Name, @"^sd-.*-bin-win-cuda(\d+)-x64\.zip$", RegexOptions.IgnoreCase)).Where(m => m.Success)
                         .OrderByDescending(m => int.Parse(m.Groups[1].Value)))
                if (Find($@"^cudart-sd-bin-win-cu{cuda.Groups[1].Value}-x64\.zip$") is { } cudart)
                    return new(tag, $"CUDA {cuda.Groups[1].Value}", [Find("^" + Regex.Escape(cuda.Value) + "$")!, cudart]);
        foreach (var (kind, flavour) in new[] { ("vulkan", "Vulkan"), ("cpu", "CPU") })
            if (Find($@"^sd-.*-bin-win-{kind}-x64\.zip$") is { } url)
                return new(tag, flavour, [url]);
        throw new InvalidOperationException($"stable-diffusion.cpp release {tag} has no Windows x64 build.");
    }

    // sd-cli's -o for a generation and where each picture ends up: <stamp>-<seed>.png; sd-cli writes a batch as
    // <base>_0.png … <base>_{n-1}.png (no %d in -o), each with seed + index, renamed to <stamp>-<seed + index>.png.
    public static (string Output, List<(string Written, string Final, long Seed)> Files) OutputNames(string folder, DateTime now, long seed, int batch)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var output = Path.Combine(folder, $"{stamp}-{seed}.png");
        var files = Enumerable.Range(0, batch).Select(i => (
            batch == 1 ? output : Path.Combine(folder, $"{stamp}-{seed}_{i}.png"),
            Path.Combine(folder, $"{stamp}-{seed + i}.png"), seed + i)).ToList();
        return (output, files);
    }

    internal static void ImageGenSelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException($"Image generation self-test: {what}");
        }
        var root = @"C:\m";
        var z = Recipe("z-image-turbo");
        var sdxl = Recipe("sdxl");
        var s = new ImageSettings { Prompt = "a cat", Negative = "blurry", Width = 512, Height = 512, Steps = 4, Cfg = 1, Seed = 42 };
        Check(string.Join(" ", Args(z, s, root, @"C:\o\x.png")) ==
            @"--diffusion-model C:\m\Z-Image-Turbo-GGUF\z_image_turbo-Q4_K.gguf --vae C:\m\Z-Image-Turbo\split_files\vae\ae.safetensors " +
            @"--llm C:\m\Qwen3-4B-Instruct-2507-GGUF\Qwen3-4B-Instruct-2507-Q4_K_M.gguf -p a cat --cfg-scale 1 --steps 4 -W 512 -H 512 -s 42 -o C:\o\x.png",
            "Z-Image args (no negative prompt for a recipe without one)");
        s.LowVram = true;
        s.InitImage = @"C:\in.png";
        s.Strength = 0.6;
        s.Batch = 2;
        s.Sampler = "euler";
        s.Cfg = 7.5;
        Check(string.Join(" ", Args(sdxl, s, root, "o.png")) ==
            @"-m C:\m\stable-diffusion-xl-base-1.0\sd_xl_base_1.0.safetensors --vae C:\m\sdxl-vae-fp16-fix\sdxl_vae.safetensors -p a cat -n blurry " +
            @"--cfg-scale 7.5 --steps 4 -W 512 -H 512 -s 42 -b 2 --sampling-method euler --offload-to-cpu --vae-tiling -i C:\in.png --strength 0.6 -o o.png",
            "SDXL args with negative prompt, Low VRAM, init image");
        s.Negative = "  ";
        Check(!Args(sdxl, s, root, "o.png").Contains("-n"), "a blank negative prompt is left out");
        Check(Size("16:9", 1024, 32) == (1024, 576) && Size("16:9", 1024, 64) == (1024, 576), "16:9 at 1024");
        Check(Size("16:9", 1536, 32) == (1536, 864) && Size("16:9", 1536, 64) == (1536, 896), "16:9 at 1536 rounds to the multiple");
        Check(Size("9:16", 1024, 64) == (576, 1024) && Size("3:4", 768, 16) == (576, 768) && Size("1:1", 512, 64) == (512, 512), "portrait and square sizes");
        Check(ParseProgress("\r  |================>                                 | 3/8 - 1.23s/it\u001b[K") == new ImageProgress(3, 8, "1.23 s/it", false), "a sampling line");
        Check(ParseProgress("  |==================================================| 20/20 -     4.56it/s - ETA 00:01") is { Step: 20, Steps: 20, Speed: "4.56 it/s" }, "it/s");
        Check(ParseProgress("  |#########                                         | 120/662 - 812.40MB/s") is { Loading: true, Step: 120 }, "a loading line");
        Check(ParseProgress("[INFO ] stable-diffusion.cpp:123 - loading model") is null, "a log line is not progress");
        var release = """
            {"tag_name": "master-1106-532d2c1", "assets": [
             {"name": "cudart-sd-bin-win-cu12-x64.zip", "browser_download_url": "u/cudart"},
             {"name": "sd-master-532d2c1-bin-Linux-Ubuntu-26.04-x86_64-vulkan.zip", "browser_download_url": "u/linux"},
             {"name": "sd-master-532d2c1-bin-win-cpu-x64.zip", "browser_download_url": "u/cpu"},
             {"name": "sd-master-532d2c1-bin-win-cuda12-x64.zip", "browser_download_url": "u/cuda"},
             {"name": "sd-master-532d2c1-bin-win-rocm-10.1.0-x64.zip", "browser_download_url": "u/rocm"},
             {"name": "sd-master-532d2c1-bin-win-vulkan-x64.zip", "browser_download_url": "u/vulkan"}]}
            """;
        Check(PickAssets(release, nvidia: true) is { Tag: "master-1106-532d2c1", Flavour: "CUDA 12", Urls: ["u/cuda", "u/cudart"] }, "NVIDIA: cuda12 + cudart");
        Check(PickAssets(release, nvidia: false) is { Flavour: "Vulkan", Urls: ["u/vulkan"] }, "no NVIDIA: vulkan");
        Check(PickAssets(release.Replace("cudart-sd", "x-sd").Replace("bin-win-vulkan", "bin-x"), nvidia: true) is { Flavour: "CPU", Urls: ["u/cpu"] }, "no cudart, no vulkan: cpu");
        var at = new DateTime(2026, 11, 18, 9, 5, 7);
        var one = OutputNames(@"C:\i", at, 7, 1);
        Check(one.Output == @"C:\i\20261118-090507-7.png" && one.Files is [(@"C:\i\20261118-090507-7.png", @"C:\i\20261118-090507-7.png", 7)], "one picture");
        var three = OutputNames(@"C:\i", at, 7, 3);
        Check(three.Output == @"C:\i\20261118-090507-7.png" && three.Files.Select(f => (f.Written, f.Final, f.Seed)).SequenceEqual(new[]
        {
            (@"C:\i\20261118-090507-7_0.png", @"C:\i\20261118-090507-7.png", 7L), (@"C:\i\20261118-090507-7_1.png", @"C:\i\20261118-090507-8.png", 8L),
            (@"C:\i\20261118-090507-7_2.png", @"C:\i\20261118-090507-9.png", 9L),
        }), "a batch: sd-cli's _0, _1, _2 renamed by seed");
    }
}
