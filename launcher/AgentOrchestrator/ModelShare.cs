using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// Sharing this PC's models with another PC through one Cloudflare quick tunnel (docs/superpowers/specs/2026-11-19-share-models-design.md):
// the pure parts. Both servers bind 127.0.0.1 only (llama-server on ChatPort, ImageShare on ImagePort); the tunnel goes to
// ImageShare, which proxies /v1/ to llama-server. Keys live in ApiKeys, never in settings.json: OwnKeyEnv on the primary PC,
// KeyEnv on the PC that connects to it.
internal static class ModelShare
{
    public const int ChatPort = 8781, ImagePort = 8782;
    public const string OwnKeyEnv = "AO_SHARE_OWN_KEY", KeyEnv = "AO_SHARE_KEY";
    public const string Scheme = "ao-share://";
    public static string Folder => Path.Combine(AppSettings.DataRoot, "share");
    public static string IniPath => Path.Combine(Folder, "models.ini");

    // The tunnel host (<name>.trycloudflare.com) as the other PC calls it.
    public static string ChatUrl(string host) => $"https://{host}/v1";
    public static string ImagesUrl(string host) => $"https://{host}";

    // 32 random bytes, base64url without padding (43 characters).
    public static string NewKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Code(string host, string key) => $"{Scheme}{host}?key={key}";

    // ao-share://<name>.trycloudflare.com?key=<base64url>; anything else is null (no IP, other host or port: the key only ever
    // goes to a Cloudflare quick tunnel).
    public static (string Host, string Key)? ParseCode(string? code) =>
        Regex.Match(code?.Trim() ?? "", @"^ao-share://([A-Za-z0-9-]+\.trycloudflare\.com)\?key=([A-Za-z0-9_-]+)$") is { Success: true } m
            ? (m.Groups[1].Value, m.Groups[2].Value) : null;

    public sealed record SharedModel(string Id, string Model, string? Mmproj);

    // llama-server's --models-preset file: one section per model, named by the id the other PC picks it by.
    public static string Ini(IEnumerable<SharedModel> models)
    {
        var text = new StringBuilder("version = 1\n");
        foreach (var m in models)
        {
            text.Append($"\n[{m.Id}]\nmodel = {m.Model}\n");
            if (m.Mmproj is { Length: > 0 } mmproj)
                text.Append($"mmproj = {mmproj}\n");
        }
        return text.ToString();
    }

    // The local chat GGUFs (FindLocalGgufs skips mmproj files): image models (no tokenizer) left out, each with its vision
    // projector, named as the model picker names them (MainWindow.FetchModelChoicesAsync).
    public static List<SharedModel> LocalModels(string modelsFolder, string hubCache) =>
        [.. LauncherLogic.FindLocalGgufs(modelsFolder, hubCache)
            .Where(g => LauncherLogic.ReadGgufMetadata(g.File)?.ContainsKey("tokenizer.ggml.model") == true)
            .Select(g => new SharedModel(DisplayId(g), g.File, LauncherLogic.VisionProjector(g.File)))
            .DistinctBy(m => m.Id)];

    // The model picker's label for a local GGUF: "<repo name>  ·  <quant>" for a Hugging Face cache file, the file name otherwise.
    public static string DisplayId(LauncherLogic.LocalGguf g) =>
        g.Repo.Length > 0 ? $"{g.Repo.Split('/')[^1]}  ·  {(g.Variant.Length > 0 ? g.Variant : Path.GetFileName(g.File))}" : Path.GetFileNameWithoutExtension(g.File);

    public static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("ModelShare: " + what);
        }
        var key = NewKey();
        Check(key.Length == 43 && Regex.IsMatch(key, "^[A-Za-z0-9_-]+$") && key != NewKey(), "a new key");
        Check(ParseCode(" " + Code("seasonal-deck-sf.trycloudflare.com", key) + " ") == ("seasonal-deck-sf.trycloudflare.com", key), "the connection code round trip");
        Check(ParseCode("ao-share://abc-def.trycloudflare.com?key=K") == ("abc-def.trycloudflare.com", "K"), "a short key");
        foreach (var bad in new[] { $"ao-share://100.64.0.1?key={key}", $"ao-share://evil.com?key={key}", $"ao-share://x.trycloudflare.com.evil.com?key={key}",
                     $"ao-share://x.trycloudflare.com:443?key={key}", $"ao-share://x.y.trycloudflare.com?key={key}", $"ao-share://trycloudflare.com?key={key}",
                     $"https://x.trycloudflare.com?key={key}", "ao-share://x.trycloudflare.com", "ao-share://x.trycloudflare.com?key=", "ao-share://x.trycloudflare.com?key=a b", "", null })
            Check(ParseCode(bad) is null, $"a bad code was accepted: {bad}");

        Check(ChatUrl("a-b.trycloudflare.com") == "https://a-b.trycloudflare.com/v1" && ImagesUrl("a-b.trycloudflare.com") == "https://a-b.trycloudflare.com", "the URLs");
        Check(Ini([new("Qwen3-GGUF  ·  Q4_K_M", @"C:\hub\qwen3-Q4_K_M.gguf", null), new("gemma-vision", @"D:\models\gemma.gguf", @"D:\models\mmproj-gemma.gguf")]) ==
            "version = 1\n\n[Qwen3-GGUF  ·  Q4_K_M]\nmodel = C:\\hub\\qwen3-Q4_K_M.gguf\n\n[gemma-vision]\nmodel = D:\\models\\gemma.gguf\nmmproj = D:\\models\\mmproj-gemma.gguf\n",
            "the models.ini contents");
        Check(DisplayId(new("unsloth/Qwen3-GGUF", "Q4_K_M", @"C:\x\q.gguf")) == "Qwen3-GGUF  ·  Q4_K_M" && DisplayId(new("", "", @"D:\m\My Model.gguf")) == "My Model",
            "display ids");
    }
}
