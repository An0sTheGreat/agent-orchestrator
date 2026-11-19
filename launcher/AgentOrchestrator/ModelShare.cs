using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// Sharing this PC's models with another PC over Tailscale (docs/superpowers/specs/2026-11-19-share-models-design.md): the
// pure parts both servers use (chat on ChatPort, images on ImagePort). The servers bind only the Tailscale IPv4 address.
// Keys live in ApiKeys, never in settings.json: OwnKeyEnv on the primary PC, KeyEnv on the PC that connects to it.
internal static class ModelShare
{
    public const int ChatPort = 8781, ImagePort = 8782;
    public const string OwnKeyEnv = "AO_SHARE_OWN_KEY", KeyEnv = "AO_SHARE_KEY";
    public const string Scheme = "ao-share://";
    public static string Folder => Path.Combine(AppSettings.DataRoot, "share");
    public static string IniPath => Path.Combine(Folder, "models.ini");

    // Tailscale's CGNAT range, 100.64.0.0/10.
    public static bool IsTailscale(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [100, var b, _, _] && (b & 0xC0) == 64;

    // The first Tailscale IPv4 among `addresses` (default: the addresses of the network interfaces that are up); null = none.
    public static IPAddress? TailscaleIp(IEnumerable<IPAddress>? addresses = null) =>
        (addresses ?? NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))).FirstOrDefault(IsTailscale);

    // 32 random bytes, base64url without padding (43 characters).
    public static string NewKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Code(string ip, string key) => $"{Scheme}{ip}?key={key}";

    // ao-share://<Tailscale IPv4>?key=<43 base64url characters>; anything else is null (the key only ever goes to a Tailscale address).
    public static (string Ip, string Key)? ParseCode(string? code) =>
        Regex.Match(code?.Trim() ?? "", @"^ao-share://([0-9.]{7,15})\?key=([A-Za-z0-9_-]{43})$") is { Success: true } m &&
        IPAddress.TryParse(m.Groups[1].Value, out var ip) && ip.ToString() == m.Groups[1].Value && IsTailscale(ip)
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
        static IPAddress A(string s) => IPAddress.Parse(s);
        Check(TailscaleIp([A("192.168.1.5"), A("fd7a:115c:a1e0::1"), A("100.64.0.1"), A("100.100.1.2")])?.ToString() == "100.64.0.1", "the first Tailscale address");
        Check(TailscaleIp([A("100.127.255.254")]) is not null, "the top of 100.64.0.0/10");
        Check(TailscaleIp([A("192.168.1.5"), A("100.128.0.1"), A("100.63.255.255"), A("10.0.0.1")]) is null, "addresses outside 100.64.0.0/10");
        Check(TailscaleIp([]) is null, "no addresses");

        var key = NewKey();
        Check(key.Length == 43 && Regex.IsMatch(key, "^[A-Za-z0-9_-]+$") && key != NewKey(), "a new key");
        Check(ParseCode(" " + Code("100.101.102.103", key) + " ") == ("100.101.102.103", key), "the connection code round trip");
        foreach (var bad in new[] { $"http://100.64.0.1?key={key}", "ao-share://100.64.0.1", "ao-share://100.64.0.1?key=", $"ao-share://100.64.0.1?key={key}x",
                     $"ao-share://192.168.1.5?key={key}", $"ao-share://100.064.0.1?key={key}", $"ao-share://100.64.0.1:8781?key={key}", "", null })
            Check(ParseCode(bad) is null, $"a bad code was accepted: {bad}");

        Check(Ini([new("Qwen3-GGUF  ·  Q4_K_M", @"C:\hub\qwen3-Q4_K_M.gguf", null), new("gemma-vision", @"D:\models\gemma.gguf", @"D:\models\mmproj-gemma.gguf")]) ==
            "version = 1\n\n[Qwen3-GGUF  ·  Q4_K_M]\nmodel = C:\\hub\\qwen3-Q4_K_M.gguf\n\n[gemma-vision]\nmodel = D:\\models\\gemma.gguf\nmmproj = D:\\models\\mmproj-gemma.gguf\n",
            "the models.ini contents");
        Check(DisplayId(new("unsloth/Qwen3-GGUF", "Q4_K_M", @"C:\x\q.gguf")) == "Qwen3-GGUF  ·  Q4_K_M" && DisplayId(new("", "", @"D:\m\My Model.gguf")) == "My Model",
            "display ids");
    }
}
