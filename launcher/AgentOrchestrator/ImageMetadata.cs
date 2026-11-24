using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace AgentOrchestrator;

// The generation settings a picture carries (A1111's "parameters" text, which sd-cli writes too), read for a picture dropped on the
// Images page (MainWindow.ImageDrop.cs). Values are mapped to the page's names; what the page has no control for is left out.
internal sealed class ImageParameters
{
    public string Prompt = "", Negative = "", Sampler = "", Scheduler = "", Model = "";
    public int? Steps, Width, Height;
    public double? Cfg, Strength;
    public long? Seed;
}

internal static class ImageMetadata
{
    // The "parameters" text of a PNG (tEXt, zTXt or iTXt), else a JPEG's EXIF UserComment; null when there is none.
    public static string? ReadParameters(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return PngText(bytes, "parameters") ?? ExifComment(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    // The chunks walked directly (length, type, data, CRC; the CRC not checked); null if not a PNG or the key is not there.
    public static string? PngText(byte[] png, string key)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(PngSignature))
            return null;
        for (var at = 8; at + 12 <= png.Length;)
        {
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            if (length < 0 || at + 12 + length > png.Length)
                return null;
            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);
            at += 12 + length;
            if (type == "IEND")
                return null;
            if (type is not ("tEXt" or "zTXt" or "iTXt"))
                continue;
            var nul = data.IndexOf((byte)0);
            if (nul < 0 || Encoding.Latin1.GetString(data[..nul]) != key)
                continue;
            var rest = data[(nul + 1)..];
            if (type == "tEXt")
                return Encoding.Latin1.GetString(rest);
            if (type == "zTXt")
                return rest.Length > 0 ? Encoding.Latin1.GetString(Inflate(rest[1..])) : null;
            // iTXt: compression flag, method, language\0, translated keyword\0, then UTF-8 text
            if (rest.Length < 2)
                return null;
            var compressed = rest[0] == 1;
            var text = rest[2..];
            for (var skip = 0; skip < 2; skip++)
                text = text.IndexOf((byte)0) is var z and >= 0 ? text[(z + 1)..] : [];
            return Encoding.UTF8.GetString(compressed ? Inflate(text) : text);
        }
        return null;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> zlib)
    {
        using var input = new ZLibStream(new MemoryStream(zlib.ToArray()), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    // EXIF UserComment (where A1111 puts it in a JPEG): an 8-byte charset id, then the text.
    private static string? ExifComment(byte[] bytes)
    {
        try
        {
            var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            var value = (frame.Metadata as BitmapMetadata)?.GetQuery("/app1/ifd/exif/subifd:{uint=37510}");
            var raw = value switch { byte[] b => b, BitmapMetadataBlob blob => blob.GetBlobValue(), string s => Encoding.UTF8.GetBytes(s), _ => null };
            if (raw is null || raw.Length == 0)
                return null;
            if (raw.Length < 8)
                return Encoding.UTF8.GetString(raw).TrimEnd('\0');
            var id = Encoding.ASCII.GetString(raw, 0, 8);
            var body = raw.AsSpan(8);
            // ponytail: UTF-16 byte order guessed from the first character (ASCII text starts with a zero byte in big-endian)
            var text = id.StartsWith("UNICODE") ? (body.Length > 0 && body[0] == 0 ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(body)
                : id.StartsWith("ASCII") || id.StartsWith("\0\0\0") ? Encoding.UTF8.GetString(body) : Encoding.UTF8.GetString(raw);
            text = text.TrimEnd('\0');
            return text.Length > 0 ? text : null;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException) { return null; }
    }

    // A1111's settings line: Key: value, Key: "quoted, value", ...
    private static readonly Regex Setting = new(@"\s*(\w[\w \-/]+):\s*(""(?:\\.|[^\\""])+""|[^,]*)(?:,|$)");

    // The prompt lines, "Negative prompt:" to the settings line (which starts with "Steps:"), then the settings; null without a settings line.
    public static ImageParameters? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var settingsAt = Array.FindLastIndex(lines, l => l.TrimStart().StartsWith("Steps:", StringComparison.Ordinal));
        if (settingsAt < 0)
            return null;
        var p = new ImageParameters();
        var negativeAt = Array.FindIndex(lines, 0, settingsAt, l => l.StartsWith("Negative prompt:", StringComparison.Ordinal));
        var promptEnd = negativeAt >= 0 ? negativeAt : settingsAt;
        p.Prompt = string.Join("\n", lines[..promptEnd]).Trim();
        if (negativeAt >= 0)
            p.Negative = string.Join("\n", lines[negativeAt..settingsAt])["Negative prompt:".Length..].Trim();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Setting.Matches(lines[settingsAt]))
            values.TryAdd(m.Groups[1].Value.Trim(), Unquote(m.Groups[2].Value.Trim()));
        string? Get(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        static double? Number(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

        p.Steps = int.TryParse(Get("Steps"), out var steps) ? steps : null;
        p.Cfg = Number(Get("Guidance")) ?? Number(Get("CFG scale"));
        p.Seed = long.TryParse(Get("Seed"), out var seed) ? seed : null;
        p.Strength = Number(Get("Denoising strength"));
        p.Model = Get("Model") ?? "";
        if (Get("Size")?.Split('x') is [var w, var h] && int.TryParse(w, out var width) && int.TryParse(h, out var height))
            (p.Width, p.Height) = (width, height);
        if (Get("Sampler") is { } sampler)
        {
            // "DPM++ 2M Karras", "Euler a", sd-cli's "euler_a karras": a trailing scheduler name split off, then "a"/"sde" joined with "_"
            var words = sampler.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count > 1 && ImageGen.Schedulers.Contains(words[^1]))
            {
                p.Scheduler = words[^1];
                words.RemoveAt(words.Count - 1);
            }
            var name = string.Concat(words.Select((word, i) => i > 0 && word is "a" or "sde" ? "_" + word : word));
            p.Sampler = ImageGen.Samplers.Contains(name) ? name : "";
        }
        if ((Get("Schedule type") ?? Get("Scheduler"))?.ToLowerInvariant().Replace(' ', '_') is { } schedule && ImageGen.Schedulers.Contains(schedule))
            p.Scheduler = schedule;
        return p;
    }

    // A1111 quotes a value with json.dumps
    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"')
            return value;
        try { return JsonSerializer.Deserialize<string>(value) ?? value; }
        catch (JsonException) { return value.Trim('"'); }
    }

    // A tEXt chunk put after the IHDR (the self-test and --ui-smoke; CRC left zero, which the reader does not check).
    internal static byte[] WithText(byte[] png, string key, string text)
    {
        var data = Encoding.Latin1.GetBytes($"{key}\0{text}");
        var chunk = new byte[12 + data.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes("tEXt").CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        const int afterHeader = 8 + 25;
        return [.. png[..afterHeader], .. chunk, .. png[afterHeader..]];
    }

    internal static byte[] TinyPng()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    internal static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException($"Image metadata self-test: {what}");
        }
        const string a1111 = "masterpiece, a lighthouse at dusk\nsoft light\nNegative prompt: blurry, lowres,\nbad hands\n" +
            "Steps: 28, Sampler: DPM++ 2M Karras, CFG scale: 7, Seed: 1234567890, Size: 832x1216, Model hash: abc123, Model: sd_xl_base_1.0, " +
            "Denoising strength: 0.45, Lora hashes: \"detail: 1a2b, style: 3c4d\", Version: v1.10.1";
        var p = Parse(a1111);
        Check(p is not null, "an A1111 text gives settings");
        Check(p!.Prompt == "masterpiece, a lighthouse at dusk\nsoft light", $"the prompt lines ({p.Prompt})");
        Check(p.Negative == "blurry, lowres,\nbad hands", $"the negative prompt spans lines ({p.Negative})");
        Check(p.Steps == 28 && p.Cfg == 7 && p.Seed == 1234567890 && p.Width == 832 && p.Height == 1216 && p.Strength == 0.45,
            "steps, CFG, seed, size and denoising strength");
        Check(p.Sampler == "dpm++2m" && p.Scheduler == "karras", $"\"DPM++ 2M Karras\" is dpm++2m with karras ({p.Sampler}, {p.Scheduler})");
        Check(p.Model == "sd_xl_base_1.0", "the model name");
        // sd-cli's own text: "Guidance" beside "CFG scale" wins; a quoted value keeps its commas
        var cli = Parse("a cat\nSteps: 8, CFG scale: 1, Guidance: 3.5, Seed: 42, Size: 1024x768, Model: \"z_image, turbo.gguf\", Sampler: euler_a karras, Version: stable-diffusion.cpp");
        Check(cli is { Negative: "", Cfg: 3.5, Sampler: "euler_a", Scheduler: "karras", Model: "z_image, turbo.gguf" }, "sd-cli's Guidance, sampler and a quoted value with commas");
        Check(Parse("Euler a")?.Sampler is null && Parse("just a prompt\nwith two lines") is null && Parse("") is null, "text without a settings line gives nothing");
        Check(Parse("x\nSteps: 20, Sampler: Euler a, Schedule type: SGM Uniform")! is { Sampler: "euler_a", Scheduler: "sgm_uniform" }, "Schedule type and \"Euler a\"");
        // a PNG with a tEXt "parameters" chunk round-trips; one without has none
        var png = TinyPng();
        Check(PngText(WithText(png, "parameters", a1111), "parameters") == a1111 && PngText(png, "parameters") is null &&
              PngText(WithText(png, "Comment", "x"), "parameters") is null, "the PNG tEXt chunk");
        var file = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-meta-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(file, WithText(png, "parameters", a1111));
            Check(Parse(ReadParameters(file))?.Negative == "blurry, lowres,\nbad hands", "a PNG file read from disk");
        }
        finally { File.Delete(file); }
    }
}
