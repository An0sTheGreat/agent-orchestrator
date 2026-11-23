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
// Local: a file of this kind (by name) in the user's models folder is used instead of downloading it, e.g. any Qwen3.5-9B GGUF.
internal sealed record ImageFile(string Flag, string Repo, string Path, long Size, string Local = "");

// Family: a model file whose name matches it (the user's models folder) is this recipe's main model (Files[0]); the rest of
// its files come from the folder (ImageFile.Local) or download.
internal sealed record ImageRecipe(string Id, string Name, string Note, ImageFile[] Files, int Steps, double Cfg, string Sampler, bool Negative, int Multiple, string Family)
{
    public long TotalSize => Files.Sum(f => f.Size);
}

// What one generation used: saved as the last-used settings (AppSettings.ImageGen, Seed -1 = random) and beside each
// picture as <name>.json (Seed then that picture's own seed). Aspect ("16:9", "2:3" after the swap, or "Custom") and the Width and
// Height boxes (CustomWidth/CustomHeight, 0 = empty) are the page's choices that made Width/Height. LongSide is from before the boxes.
internal sealed class ImageSettings
{
    public string RecipeId { get; set; } = ImageGen.Recipes[0].Id;
    public string Prompt { get; set; } = "";
    public string Negative { get; set; } = "";
    public string Aspect { get; set; } = "1:1";
    public int LongSide { get; set; } = 1024;
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public int CustomWidth { get; set; } // the Width box (0 = empty: the preset's recommended width)
    public int CustomHeight { get; set; }
    public bool SizeUnlocked { get; set; } // the lock beside the size boxes open: sizes go as typed, no ratio and no rounding to the model's multiple
    public int Steps { get; set; } = 8;
    public double Cfg { get; set; } = 1.0;
    public long Seed { get; set; } = -1;
    public string Sampler { get; set; } = ""; // "" = sd-cli's default for the model
    public string Scheduler { get; set; } = ""; // "" = sd-cli's default for the model (--scheduler)
    public int Batch { get; set; } = 1;
    public bool LowVram { get; set; } = true; // one model on the GPU at a time: the text encoder, then the image model
    public double Strength { get; set; } = 0.75;
    public string InitImage { get; set; } = ""; // "" = text to image
    public string LocalModel { get; set; } = ""; // a main model from the user's models folder in place of the recipe's download
    public string RemoteModel { get; set; } = ""; // "" = local; an OpenRouter image model id otherwise
    public string RemoteProvider { get; set; } = ""; // comma-separated provider tags to pin, "" = OpenRouter chooses
    public string SharedModel { get; set; } = ""; // "" = not shared; a model id from the primary PC's /images/models otherwise (ImageShare.cs)
}

internal sealed record ImageProgress(int Step, int Steps, string Speed, bool Loading);

internal static class ImageGen
{
    public static string RuntimeFolder => Path.Combine(AppSettings.DataRoot, "sd.cpp");
    public static string CliPath => Path.Combine(RuntimeFolder, "sd-cli.exe");
    public static string ModelsRoot => Path.Combine(AppSettings.DataRoot, "image-models");
    public static string ImagesFolder => Path.Combine(AppSettings.DataRoot, "images");
    public const string ReleaseUrl = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest";

    // sd-cli --help's --sampling-method list
    // sd-cli --help's --scheduler list (normal is an alias of discrete)
    public static readonly string[] Schedulers = ["discrete", "karras", "exponential", "ays", "gits", "sgm_uniform", "simple", "smoothstep", "kl_optimal",
        "lcm", "bong_tangent", "beta", "logit_normal", "flux", "flux2"];
    public static readonly string[] Samplers = ["euler", "euler_a", "heun", "dpm2", "dpm++2s_a", "dpm++2m", "dpm++2mv2", "ipndm", "ipndm_v", "lcm",
        "ddim_trailing", "tcd", "res_multistep", "res_2s", "er_sde", "euler_cfg_pp", "euler_a_cfg_pp", "euler_ge", "dpm++2m_sde", "dpm++2m_sde_bt", "lms"];

    // Comfy-Org/z_image_turbo and Comfy-Org/flux2-klein were renamed; the hub redirects the old names, the downloader does not.
    public static readonly ImageRecipe[] Recipes =
    [
        new("z-image-turbo", "Z-Image Turbo", "Recommended · runs on 4-6 GB VRAM",
        [
            new("--diffusion-model", "leejet/Z-Image-Turbo-GGUF", "z_image_turbo-Q4_K.gguf", 3864250304),
            new("--vae", "Comfy-Org/Z-Image-Turbo", "split_files/vae/ae.safetensors", 335304388),
            new("--llm", "unsloth/Qwen3-4B-Instruct-2507-GGUF", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2497281120, Local: @"^qwen3-4b.*\.gguf$"),
        ], Steps: 8, Cfg: 1.0, Sampler: "", Negative: false, Multiple: 16, Family: @"z[-_ ]?image"),
        new("flux3-klein", "FLUX.3 Image Klein", "",
        [
            new("--diffusion-model", "leejet/flux-3-image-klein-GGUF", "flux3-image-klein-Q4_K.gguf", 3514023104),
            new("--vae", "Comfy-Org/Flux-2-Klein-4B", "split_files/vae/flux2-vae.safetensors", 336211292),
            new("--llm", "Qwen/Qwen3-VL-4B-Instruct-GGUF", "Qwen3VL-4B-Instruct-Q4_K_M.gguf", 2497281664, Local: @"^qwen3-?vl-4b.*\.gguf$"),
        ], Steps: 16, Cfg: 1.0, Sampler: "euler", Negative: false, Multiple: 16, Family: @"flux[-_.]?3"),
        new("flux2-klein-4b", "FLUX.2 Klein 4B", "",
        [
            new("--diffusion-model", "leejet/FLUX.2-klein-4B-GGUF", "flux-2-klein-4b-Q4_0.gguf", 2460378560),
            new("--vae", "Comfy-Org/Flux-2-Klein-4B", "split_files/vae/flux2-vae.safetensors", 336211292),
            new("--llm", "unsloth/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf", 2497281312, Local: @"^qwen3-4b-(?!instruct).*\.gguf$"),
        ], Steps: 4, Cfg: 1.0, Sampler: "euler", Negative: false, Multiple: 16, Family: @"flux[-_.]?2[-_.]?klein[-_.]?4b"),
        new("qwen-image-2.1", "Qwen-Image 2.1", "Large",
        [
            new("--diffusion-model", "leejet/Qwen-Image-2.1-GGUF", "qwen_image_2.1-Q4_K.gguf", 4197494816),
            new("--vae", "Comfy-Org/Qwen-Image-2.1", "vae/qwen_image_2.1_vae_bf16.safetensors", 675509688),
            new("--llm", "Qwen/Qwen3-VL-8B-Instruct-GGUF", "Qwen3VL-8B-Instruct-Q4_K_M.gguf", 5027784800, Local: @"^qwen3-?vl-8b.*\.gguf$"),
        ], Steps: 20, Cfg: 6.0, Sampler: "euler", Negative: false, Multiple: 32, Family: @"qwen[-_ ]?image"),
        new("moss-image-sft", "MOSS-Image SFT", "",
        [
            new("--diffusion-model", "leejet/MOSS-Image-SFT-GGUF", "MOSS-Image-SFT-Q4_K.gguf", 3327400800),
            new("--vae", "Comfy-Org/MOSS-Image", "vae/flux2-vae.safetensors", 336211292),
            new("--llm", "unsloth/Qwen3.5-9B-GGUF", "Qwen3.5-9B-Q4_K_M.gguf", 5680522464, Local: @"^qwen3\.5-9b.*\.gguf$"),
            new("--tokenizer", "OpenMOSS-Team/MOSS-Image-Base", "tokenizer/tokenizer.json", 19989325),
        ], Steps: 20, Cfg: 3.0, Sampler: "", Negative: true, Multiple: 16, Family: @"moss[-_ ]?image"),
        new("sdxl", "SDXL 1.0", "",
        [
            new("-m", "stabilityai/stable-diffusion-xl-base-1.0", "sd_xl_base_1.0.safetensors", 6938078334),
            new("--vae", "madebyollin/sdxl-vae-fp16-fix", "sdxl_vae.safetensors", 334641162),
        ], Steps: 30, Cfg: 7.0, Sampler: "", Negative: true, Multiple: 64, Family: @"sd_?xl"),
    ];

    public static ImageRecipe Recipe(string id) => Recipes.FirstOrDefault(r => r.Id == id) ?? Recipes[0];

    public static string LocalPath(string root, ImageFile file) => Path.Combine(root, file.Repo.Split('/')[^1], file.Path.Replace('/', Path.DirectorySeparatorChar));

    // Width and height for an aspect ("16:9") and long side, each rounded to the nearest multiple (at least one multiple).
    public static (int Width, int Height) Size(string aspect, int longSide, int multiple)
    {
        var (a, b) = Parts(aspect);
        int Round(double x) => Math.Max(multiple, (int)Math.Round(x / multiple, MidpointRounding.AwayFromZero) * multiple);
        return a >= b ? (Round(longSide), Round(longSide * b / a)) : (Round(longSide * a / b), Round(longSide));
    }

    private static (double A, double B) Parts(string ratio)
    {
        var parts = ratio.Split(':');
        return (double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    // A typed width or height (0 = the box is empty: the recommended one), rounded to the nearest multiple and kept to 64-4096.
    public static int Pick(int typed, int recommended, int multiple) =>
        typed <= 0 ? recommended : Math.Clamp((int)Math.Round((double)typed / multiple, MidpointRounding.AwayFromZero) * multiple, 64, 4096);

    // The Aspect ratio presets, Custom first. The swap button shows them flipped ("Photo (2:3)"): that is how 4:5 and the portraits are picked.
    public static readonly (string Name, string Ratio)[] Presets =
        [("Custom", ""), ("Square", "1:1"), ("", "5:4"), ("Photo", "3:2"), ("Landscape", "4:3"), ("Widescreen", "16:9"), ("Ultrawide", "21:9")];
    public const int RecommendedLongSide = 1024; // a preset's recommended size, the grey text in the empty Width and Height boxes
    public static readonly int[] SizeChoices = Enumerable.Range(0, 29).Select(i => 256 + 64 * i).ToArray(); // the boxes' lists: 256-2048

    public static string Flip(string ratio) => ratio.Length == 0 ? "" : string.Join(':', ratio.Split(':').Reverse());

    public static string PresetLabel((string Name, string Ratio) preset, bool flipped)
    {
        var ratio = flipped ? Flip(preset.Ratio) : preset.Ratio;
        return ratio.Length == 0 ? preset.Name : preset.Name.Length == 0 ? ratio : $"{preset.Name} ({ratio})";
    }

    // With a ratio chosen, the other box follows: from a width the height (fromWidth), else the width, rounded and kept like a typed one.
    public static int Linked(int value, string ratio, bool fromWidth, int multiple)
    {
        var (a, b) = Parts(ratio);
        return Pick(Math.Max(1, (int)Math.Round(value * (fromWidth ? b / a : a / b))), 0, multiple);
    }

    // Whether a width and height are the ratio, give or take the rounding to the multiple (a 21:9 height of 4096 whose width stops at 4096 is not).
    public static bool Matches(int width, int height, string ratio, int multiple)
    {
        var (a, b) = Parts(ratio);
        return Math.Abs(width * b - height * a) <= (a + b) * multiple / 2;
    }

    // OpenRouter is sent an aspect_ratio, not a size: the nearest of the ones it takes.
    public static readonly string[] RemoteAspects = ["1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9"];

    public static string RemoteAspect(int width, int height) =>
        RemoteAspects.MinBy(r => Parts(r) is var (a, b) ? Math.Abs(Math.Log((double)width / height) - Math.Log(a / b)) : 0)!;

    // An image model in the user's models folder: its recipe and path. Chat GGUFs and an image model's parts (VAE, text
    // encoder, LoRA...) are not listed; a file found twice (a hub snapshot's copies) is listed once.
    internal sealed record LocalModel(ImageRecipe Recipe, string Path);
    private const string Part = @"vae|text[-_]?encoder|mmproj|tokenizer|lora|controlnet|clip|t5xxl|upscal";

    // ponytail: walks the whole folder on each page open; cache by folder timestamp if a huge folder makes that slow.
    public static (List<LocalModel> Models, List<string> Files) ScanFolder(string folder)
    {
        if (folder.Length == 0 || !Directory.Exists(folder))
            return ([], []);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        var files = Directory.EnumerateFiles(folder, "*", options)
            .Where(f => f.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)).ToList();
        var models = new List<LocalModel>();
        var seen = new HashSet<(string, long)>();
        foreach (var file in files)
        {
            var name = System.IO.Path.GetFileName(file);
            if (Regex.IsMatch(name, Part, RegexOptions.IgnoreCase) || Recipes.FirstOrDefault(r => Regex.IsMatch(name, r.Family, RegexOptions.IgnoreCase)) is not { } recipe)
                continue;
            if (seen.Add((name.ToLowerInvariant(), new FileInfo(file).Length)))
                models.Add(new(recipe, file));
        }
        return ([.. models.OrderBy(m => Array.IndexOf(Recipes, m.Recipe)).ThenBy(m => System.IO.Path.GetFileName(m.Path), StringComparer.OrdinalIgnoreCase)], files);
    }

    // Where each of the recipe's files is: the local main model, a matching file in the models folder, else the download.
    public static List<string> Paths(ImageRecipe recipe, string localModel, IReadOnlyList<string> folderFiles, string modelsRoot) =>
        [.. recipe.Files.Select((f, i) => i == 0 && localModel.Length > 0 ? localModel
            : f.Local.Length > 0 && folderFiles.FirstOrDefault(p => Regex.IsMatch(System.IO.Path.GetFileName(p), f.Local, RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(System.IO.Path.GetFileName(p), "mmproj", RegexOptions.IgnoreCase)) is { } found ? found
            : LocalPath(modelsRoot, f))];

    public static List<string> Args(ImageRecipe recipe, ImageSettings s, IReadOnlyList<string> paths, string output)
    {
        var args = new List<string>();
        foreach (var (file, path) in recipe.Files.Zip(paths))
            args.AddRange([file.Flag, path]);
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        args.AddRange(["-p", s.Prompt]);
        if (recipe.Negative && s.Negative.Trim().Length > 0)
            args.AddRange(["-n", s.Negative]);
        args.AddRange(["--cfg-scale", F(s.Cfg), "--steps", s.Steps.ToString(), "-W", s.Width.ToString(), "-H", s.Height.ToString(), "-s", s.Seed.ToString()]);
        if (s.Batch > 1)
            args.AddRange(["-b", s.Batch.ToString()]);
        if (s.Sampler.Length > 0)
            args.AddRange(["--sampling-method", s.Sampler]);
        if (s.Scheduler.Length > 0)
            args.AddRange(["--scheduler", s.Scheduler]);
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
        var s = new ImageSettings { Prompt = "a cat", Negative = "blurry", Width = 512, Height = 512, Steps = 4, Cfg = 1, Seed = 42, LowVram = false };
        Check(string.Join(" ", Args(z, s, Paths(z, "", [], root), @"C:\o\x.png")) ==
            @"--diffusion-model C:\m\Z-Image-Turbo-GGUF\z_image_turbo-Q4_K.gguf --vae C:\m\Z-Image-Turbo\split_files\vae\ae.safetensors " +
            @"--llm C:\m\Qwen3-4B-Instruct-2507-GGUF\Qwen3-4B-Instruct-2507-Q4_K_M.gguf -p a cat --cfg-scale 1 --steps 4 -W 512 -H 512 -s 42 -o C:\o\x.png",
            "Z-Image args (no negative prompt for a recipe without one)");
        s.LowVram = true;
        s.InitImage = @"C:\in.png";
        s.Strength = 0.6;
        s.Batch = 2;
        s.Sampler = "euler";
        Check(Args(z, new() { Prompt = "p", Scheduler = "karras" }, Paths(z, "", [], root), "o.png") is var withScheduler &&
            withScheduler[withScheduler.IndexOf("--scheduler") + 1] == "karras" && !Args(z, new() { Prompt = "p" }, Paths(z, "", [], root), "o.png").Contains("--scheduler"),
            "the scheduler goes as --scheduler, and only when chosen");
        s.Cfg = 7.5;
        Check(string.Join(" ", Args(sdxl, s, Paths(sdxl, "", [], root), "o.png")) ==
            @"-m C:\m\stable-diffusion-xl-base-1.0\sd_xl_base_1.0.safetensors --vae C:\m\sdxl-vae-fp16-fix\sdxl_vae.safetensors -p a cat -n blurry " +
            @"--cfg-scale 7.5 --steps 4 -W 512 -H 512 -s 42 -b 2 --sampling-method euler --offload-to-cpu --vae-tiling -i C:\in.png --strength 0.6 -o o.png",
            "SDXL args with negative prompt, Low VRAM, init image");
        s.Negative = "  ";
        Check(!Args(sdxl, s, Paths(sdxl, "", [], root), "o.png").Contains("-n"), "a blank negative prompt is left out");
        // the models folder: image models only (no chat GGUF, no parts), one per name and size; their parts found there or downloaded
        var folder = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-imgscan-{Guid.NewGuid():N}");
        try
        {
            foreach (var (rel, bytes) in new[] { (@"a\MOSS-Image-SFT-Q8_0.gguf", 3), (@"a\diffusion_models\MOSS\MOSS-Image-SFT-Q8_0.gguf", 3), (@"b\Qwen3.5-9B-Q8_0.gguf", 2),
                (@"b\Qwen3.5-9B-mmproj-F16.gguf", 1), (@"c\gemma-4-E4B-it-Q4_K_M.gguf", 1), (@"d\Qwen-Image-2.1-text_encoder-INT8.safetensors", 1), (@"d\qwen-image-2.1-Q4_K_M.gguf", 1),
                (@"e\Z-Image-Turbo-FP8.safetensors", 1), (@"e\notes.txt", 1), (@"f\flux-2-klein-4b-Q5_K_M.gguf", 1), (@"f\flux-2-klein-9b-Q4_0.gguf", 1),
                (@"g\Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 1) })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, rel))!);
                File.WriteAllBytes(Path.Combine(folder, rel), new byte[bytes]);
            }
            var (found, all) = ScanFolder(folder);
            Check(string.Join(",", found.Select(m => $"{m.Recipe.Id}={Path.GetFileName(m.Path)}")) ==
                "z-image-turbo=Z-Image-Turbo-FP8.safetensors,flux2-klein-4b=flux-2-klein-4b-Q5_K_M.gguf,qwen-image-2.1=qwen-image-2.1-Q4_K_M.gguf,moss-image-sft=MOSS-Image-SFT-Q8_0.gguf", "the folder's image models");
            var klein = found.First(m => m.Recipe.Id == "flux2-klein-4b");
            var kleinPaths = Paths(klein.Recipe, klein.Path, all, root);
            Check(kleinPaths[0] == klein.Path && kleinPaths[1] == LocalPath(root, klein.Recipe.Files[1]) && kleinPaths[2] == LocalPath(root, klein.Recipe.Files[2]),
                "Klein: its own model, VAE and Qwen3-4B downloaded (the Instruct-2507 file is not its text encoder)");
            var moss = found[^1];
            var paths = Paths(moss.Recipe, moss.Path, all, root);
            Check(paths[0] == moss.Path && paths[2] == Path.Combine(folder, @"b\Qwen3.5-9B-Q8_0.gguf") && paths[1] == LocalPath(root, moss.Recipe.Files[1]) &&
                paths[3] == LocalPath(root, moss.Recipe.Files[3]), "MOSS: its own model, the folder's Qwen3.5-9B, the rest downloaded");
        }
        finally { Directory.Delete(folder, recursive: true); }
        Check(Size("16:9", 1024, 32) == (1024, 576) && Size("16:9", 1024, 64) == (1024, 576), "16:9 at 1024");
        Check(Size("16:9", 1536, 32) == (1536, 864) && Size("16:9", 1536, 64) == (1536, 896), "16:9 at 1536 rounds to the multiple");
        Check(Size("9:16", 1024, 64) == (576, 1024) && Size("3:4", 768, 16) == (576, 768) && Size("1:1", 512, 64) == (512, 512), "portrait and square sizes");
        Check(Size("4:5", 1024, 16) == (816, 1024) && Size("5:4", 1024, 16) == (1024, 816), "4:5 and 5:4 at 1024");
        Check(Pick(0, 832, 16) == 832 && Pick(1000, 832, 16) == 1008 && Pick(50, 832, 16) == 64 && Pick(99999, 832, 16) == 4096,
            "typed sizes: empty is the recommended one, others round to the multiple within 64-4096");
        Check(Linked(1024, "16:9", true, 16) == 576 && Linked(576, "16:9", false, 16) == 1024 && Linked(4096, "21:9", false, 16) == 4096, "a ratio links width and height");
        Check(Matches(1024, 448, "21:9", 16) && Matches(576, 1024, "9:16", 64) && !Matches(4096, 4096, "21:9", 16) && !Matches(1024, 1024, "4:3", 16), "a size matches its ratio within the rounding");
        Check(PresetLabel(Presets[3], true) == "Photo (2:3)" && PresetLabel(Presets[2], true) == "4:5" && PresetLabel(Presets[1], true) == "Square (1:1)" && PresetLabel(Presets[0], true) == "Custom",
            "the swap flips the presets' ratios");
        Check(RemoteAspect(1024, 576) == "16:9" && RemoteAspect(1000, 1000) == "1:1" && RemoteAspect(448, 1024) == "9:16" && RemoteAspect(1024, 448) == "21:9", "OpenRouter gets the nearest aspect ratio");
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
