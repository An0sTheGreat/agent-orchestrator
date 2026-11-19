using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// Sharing this PC's models with another PC (docs/superpowers/specs/2026-11-19-share-models-design.md): the pure parts.
// llama-server binds 127.0.0.1:ChatPort; ImageShare (ImagePort), which proxies /v1/ to it, binds the Tailscale IPv4 on the
// Tailscale network, or 127.0.0.1 behind one Cloudflare quick tunnel on the Cloudflare one. Keys live in ApiKeys, never in settings.json: OwnKeyEnv on the primary PC,
// KeyEnv on the PC that connects to it.
internal static class ModelShare
{
    public const int ChatPort = 8781, ImagePort = 8782;
    public const string OwnKeyEnv = "AO_SHARE_OWN_KEY", KeyEnv = "AO_SHARE_KEY";
    public const string Scheme = "ao-share://";
    public static string Folder => Path.Combine(AppSettings.DataRoot, "share");
    public static string IniPath => Path.Combine(Folder, "models.ini");

    public const string Tailscale = "tailscale", Cloudflare = "cloudflare"; // AppSettings.ShareNetwork

    // Tailscale's CGNAT range, 100.64.0.0/10.
    public static bool IsTailscale(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [100, var b, _, _] && (b & 0xC0) == 64;

    // The first Tailscale IPv4 among `addresses` (default: the addresses of the network interfaces that are up); null = none.
    public static IPAddress? TailscaleIp(IEnumerable<IPAddress>? addresses = null) =>
        (addresses ?? NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))).FirstOrDefault(IsTailscale);

    // The network sharing uses: the user's pick, else Tailscale when this PC has a Tailscale address, else Cloudflare.
    public static string Network(string picked, IEnumerable<IPAddress>? addresses = null) =>
        picked is Tailscale or Cloudflare ? picked : TailscaleIp(addresses) is null ? Cloudflare : Tailscale;

    // A host from a connection code that is a Tailscale IPv4, written canonically (no port, no leading zeros).
    public static bool IsTailscaleHost(string host) => IPAddress.TryParse(host, out var ip) && ip.ToString() == host && IsTailscale(ip);

    // The host as the other PC calls it: a Tailscale IP straight to ImageShare over plain HTTP (Tailscale encrypts the link),
    // a <name>.trycloudflare.com tunnel over HTTPS.
    public static string ChatUrl(string host) => ImagesUrl(host) + "/v1";
    public static string ImagesUrl(string host) => IsTailscaleHost(host) ? $"http://{host}:{ImagePort}" : $"https://{host}";

    // 32 random bytes, base64url without padding (43 characters).
    public static string NewKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Code(string host, string key) => $"{Scheme}{host}?key={key}";

    // ao-share://<name>.trycloudflare.com?key=<base64url> or ao-share://<Tailscale IPv4>?key=<base64url>; anything else is null
    // (no other IP or host, no port: the key only ever goes to a Cloudflare quick tunnel or a Tailscale address).
    public static (string Host, string Key)? ParseCode(string? code) =>
        Regex.Match(code?.Trim() ?? "", @"^ao-share://([A-Za-z0-9-]+\.trycloudflare\.com|[0-9.]{7,15})\?key=([A-Za-z0-9_-]+)$") is { Success: true } m &&
        (m.Groups[1].Value.EndsWith(".trycloudflare.com", StringComparison.Ordinal) || IsTailscaleHost(m.Groups[1].Value))
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
        Check(Network("", [A("192.168.1.5"), A("100.80.211.78")]) == Tailscale && Network("", [A("192.168.1.5")]) == Cloudflare && Network("", []) == Cloudflare,
            "the default network with and without a Tailscale address");
        Check(Network(Cloudflare, [A("100.80.211.78")]) == Cloudflare && Network(Tailscale, []) == Tailscale, "the picked network wins");

        var key = NewKey();
        Check(key.Length == 43 && Regex.IsMatch(key, "^[A-Za-z0-9_-]+$") && key != NewKey(), "a new key");
        Check(ParseCode(" " + Code("seasonal-deck-sf.trycloudflare.com", key) + " ") == ("seasonal-deck-sf.trycloudflare.com", key), "the connection code round trip");
        Check(ParseCode("ao-share://abc-def.trycloudflare.com?key=K") == ("abc-def.trycloudflare.com", "K"), "a short key");
        Check(ParseCode("ao-share://100.101.102.103?key=K") == ("100.101.102.103", "K") && ParseCode(Code("100.64.0.1", key)) == ("100.64.0.1", key), "a Tailscale code");
        foreach (var bad in new[] { $"ao-share://100.128.0.1?key={key}", $"ao-share://100.63.255.255?key={key}", $"ao-share://192.168.1.5?key={key}",
                     $"ao-share://100.101.102.103:8782?key={key}", $"ao-share://100.064.0.1?key={key}", $"ao-share://100.64.1?key={key}", $"http://100.64.0.1?key={key}", $"ao-share://evil.com?key={key}", $"ao-share://x.trycloudflare.com.evil.com?key={key}",
                     $"ao-share://x.trycloudflare.com:443?key={key}", $"ao-share://x.y.trycloudflare.com?key={key}", $"ao-share://trycloudflare.com?key={key}",
                     $"https://x.trycloudflare.com?key={key}", "ao-share://x.trycloudflare.com", "ao-share://x.trycloudflare.com?key=", "ao-share://x.trycloudflare.com?key=a b", "", null })
            Check(ParseCode(bad) is null, $"a bad code was accepted: {bad}");

        Check(ChatUrl("a-b.trycloudflare.com") == "https://a-b.trycloudflare.com/v1" && ImagesUrl("a-b.trycloudflare.com") == "https://a-b.trycloudflare.com", "the tunnel URLs");
        Check(ChatUrl("100.101.102.103") == "http://100.101.102.103:8782/v1" && ImagesUrl("100.101.102.103") == "http://100.101.102.103:8782", "the Tailscale URLs");
        Check(Ini([new("Qwen3-GGUF  ·  Q4_K_M", @"C:\hub\qwen3-Q4_K_M.gguf", null), new("gemma-vision", @"D:\models\gemma.gguf", @"D:\models\mmproj-gemma.gguf")]) ==
            "version = 1\n\n[Qwen3-GGUF  ·  Q4_K_M]\nmodel = C:\\hub\\qwen3-Q4_K_M.gguf\n\n[gemma-vision]\nmodel = D:\\models\\gemma.gguf\nmmproj = D:\\models\\mmproj-gemma.gguf\n",
            "the models.ini contents");
        Check(DisplayId(new("unsloth/Qwen3-GGUF", "Q4_K_M", @"C:\x\q.gguf")) == "Qwen3-GGUF  ·  Q4_K_M" && DisplayId(new("", "", @"D:\m\My Model.gguf")) == "My Model",
            "display ids");
    }
}
