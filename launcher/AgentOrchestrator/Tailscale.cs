using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// The Tailscale CLI (tailscale.exe) for the profile card's sign-in and the share pairing (MainWindow.Tailnet.cs): status,
// login, logout and whois. Every PC signed in to the same Tailscale account is on one tailnet; a PC sharing its models hands
// its share key (GET /share/pair) only to a caller whose Tailscale address whois says belongs to that same account.
internal static class Tailscale
{
    public sealed record Device(string UserId, string Name, string Os, IPAddress? Ip, bool Online, string DnsName); // DnsName: laptop.tail1234.ts.net

    // Backend: tailscaled's BackendState ("Running", "NeedsLogin", "Stopped", …); Login: the signed-in account's login name.
    // DnsName: this PC's MagicDNS name (home.tail1234.ts.net); MagicDnsSuffix: the tailnet's (tail1234.ts.net), "" while MagicDNS
    // is off; CertDomains: the names tailscaled can get HTTPS certificates for (empty until HTTPS is on for the tailnet).
    public sealed record State(string Backend, Device? Self, IReadOnlyList<Device> Peers, string Login, string DnsName, string MagicDnsSuffix,
        IReadOnlyList<string> CertDomains)
    {
        public bool SignedIn => Backend == "Running" && Self is { UserId.Length: > 0 };
        public IEnumerable<Device> Mine => Peers.Where(p => p.UserId == Self?.UserId); // this account's other PCs
    }

    public const string DownloadPage = "https://tailscale.com/download/windows";

    // tailscale.exe in Program Files, else on PATH; null = not installed.
    public static string? Exe =>
        new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(d => d.IndexOfAny(Path.GetInvalidPathChars()) < 0).Select(d => Path.Combine(d, "tailscale.exe")))
            .FirstOrDefault(File.Exists);

    private static async Task<(int Code, string Output)> RunAsync(TimeSpan timeout, CancellationToken ct, params string[] args)
    {
        if (Exe is not { } exe)
            return (-1, "Tailscale is not installed");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info)!;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(timeout);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(stop.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                var partial = await output + await error; // what it printed before it was killed (Serve's enable link)
                return (-1, partial.Trim().Length > 0 ? partial : "tailscale did not answer");
            }
            return (process.ExitCode, await output + await error);
        }
        catch (Exception x) when (x is Win32Exception or InvalidOperationException or IOException) { return (-1, x.Message); }
    }

    // `tailscale status --json`; null when Tailscale is not installed or not answering.
    public static async Task<State?> StatusAsync()
    {
        var (_, output) = await RunAsync(TimeSpan.FromSeconds(5), default, "status", "--json");
        // status exits non-zero when signed out but still prints the JSON
        try { return output.TrimStart().StartsWith('{') ? ParseStatus(output) : null; }
        catch (Exception x) when (x is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    private static string Text(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "" : "";

    private static Device ParseDevice(JsonElement d) => new(Text(d, "UserID"), Text(d, "HostName"), Text(d, "OS"),
        d.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
            ? ips.EnumerateArray().Select(i => IPAddress.TryParse(i.GetString(), out var a) ? a : null).FirstOrDefault(a => a is not null && ModelShare.IsTailscale(a)) : null,
        d.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.True, Text(d, "DNSName").TrimEnd('.'));

    public static State ParseStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var self = root.TryGetProperty("Self", out var s) && s.ValueKind == JsonValueKind.Object ? ParseDevice(s) : null;
        var peers = root.TryGetProperty("Peer", out var p) && p.ValueKind == JsonValueKind.Object ? p.EnumerateObject().Select(x => ParseDevice(x.Value)).ToList() : [];
        var login = self is not null && root.TryGetProperty("User", out var users) && users.ValueKind == JsonValueKind.Object && users.TryGetProperty(self.UserId, out var user)
            ? Text(user, "LoginName") : "";
        var tailnet = root.TryGetProperty("CurrentTailnet", out var t) ? t : default;
        var certs = root.TryGetProperty("CertDomains", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(d => d.GetString() ?? "").Where(d => d.Length > 0).ToList() : [];
        return new(Text(root, "BackendState"), self, peers, login, Text(s, "DNSName").TrimEnd('.'), Text(tailnet, "MagicDNSSuffix"), certs);
    }

    // The phone app (MainWindow.WebApp.cs): Serve publishes http://127.0.0.1:<port> at https://<this PC's DnsName>/ on the
    // tailnet only (`--bg`: it stays until turned off, across restarts). True when tailscale took it.
    // Output is what tailscale printed, also when it was killed at the timeout (it waits when Serve is not enabled on the tailnet).
    public static async Task<(bool Ok, string Output)> ServeAsync(int port, CancellationToken ct)
    {
        var (code, output) = await RunAsync(TimeSpan.FromSeconds(20), ct, "serve", "--bg", "--https=443", $"http://127.0.0.1:{port}");
        return (code == 0, output);
    }

    // The https link in tailscale serve's output ("Serve is not enabled on your tailnet. To enable, visit: <url>"); null if none.
    public static string? ServeEnableUrl(string output) =>
        Regex.Match(output, @"https://login\.tailscale\.com/[^\s""'<>]+") is { Success: true } m ? m.Value.TrimEnd('.', ',', ';', ')') : null;

    // The state line's reason: the first line tailscale printed, "" when it printed nothing.
    public static string ServeFailure(string output) =>
        output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    public static Task ServeOffAsync(CancellationToken ct) => RunAsync(TimeSpan.FromSeconds(20), ct, "serve", "--https=443", "off");

    // Null when the phone app can be published; else what the user has to do first.
    public static string? WebAppProblem(State? s) => s switch
    {
        null => "Tailscale is not installed",
        { SignedIn: false } => "Tailscale is not signed in",
        { MagicDnsSuffix.Length: 0 } or { DnsName.Length: 0 } => "Turn on MagicDNS for your tailnet in the Tailscale admin console (DNS page)",
        { CertDomains.Count: 0 } => "Turn on HTTPS certificates for your tailnet in the Tailscale admin console (DNS page)",
        _ => null,
    };

    // `tailscale whois --json <ip>`: the id of the account that address belongs to; null = unknown.
    public static async Task<string?> WhoIsAsync(IPAddress ip)
    {
        var (code, output) = await RunAsync(TimeSpan.FromSeconds(5), default, "whois", "--json", ip.ToString());
        return code == 0 ? ParseWhoIs(output) : null;
    }

    public static string? ParseWhoIs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("UserProfile", out var user) && Text(user, "ID") is { Length: > 0 } id ? id : null;
        }
        catch (JsonException) { return null; }
    }

    // GET /share/pair answers only a caller on a Tailscale address that belongs to this PC's own signed-in account.
    public static bool MayPair(IPAddress? caller, string? callerUser, string? ownUser) =>
        caller is not null && ModelShare.IsTailscale(caller) && callerUser is { Length: > 0 } && callerUser == ownUser;

    public sealed record Pairing(string Key, string Name);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public static string PairJson(string key, string name) => JsonSerializer.Serialize(new Pairing(key, name), Web);
    public static Pairing? ParsePair(string json)
    {
        try { return JsonSerializer.Deserialize<Pairing>(json, Web) is { Key.Length: > 0 } p ? p : null; }
        catch (JsonException) { return null; }
    }

    // The sign-in page the CLI prints ("To authenticate, visit: https://login.tailscale.com/a/…"); null = none in this text.
    public static string? LoginUrl(string text) => Regex.Match(text, @"https://login\.tailscale\.com/\S+") is { Success: true } m ? m.Value : null;

    // `tailscale up` when signed in but disconnected, else `tailscale login`: the sign-in page opens in the browser and this
    // waits (up to 10 minutes) for it to finish. Null = signed in; else why not.
    public static async Task<string?> SignInAsync(bool disconnected)
    {
        if (Exe is not { } exe)
            return "Tailscale is not installed";
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(disconnected ? "up" : "login");
        try
        {
            using var process = Process.Start(info)!;
            var opened = false;
            var said = new System.Text.StringBuilder();
            void Line(string? line)
            {
                if (line is null)
                    return;
                lock (said)
                    said.AppendLine(line);
                if (!opened && LoginUrl(line) is { } url)
                {
                    opened = true;
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
            }
            process.OutputDataReceived += (_, e) => Line(e.Data);
            process.ErrorDataReceived += (_, e) => Line(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try { await process.WaitForExitAsync(stop.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return "The sign-in was not finished within 10 minutes";
            }
            lock (said)
                return process.ExitCode == 0 ? null : said.ToString().Trim() is { Length: > 0 } why ? why : $"tailscale exited with code {process.ExitCode}";
        }
        catch (Exception x) when (x is Win32Exception or InvalidOperationException or IOException) { return x.Message; }
    }

    public static async Task<string?> SignOutAsync() => (await RunAsync(TimeSpan.FromSeconds(20), default, "logout")) is var (code, output) && code == 0 ? null : output.Trim();

    // winget installs Tailscale (its installer asks for administrator rights); without winget, the download page opens.
    // Null = installed; else why not.
    public static async Task<string?> InstallAsync()
    {
        try
        {
            using var winget = Process.Start(new ProcessStartInfo("winget", "install --id Tailscale.Tailscale -e --accept-source-agreements --accept-package-agreements")
                { UseShellExecute = true })!;
            await winget.WaitForExitAsync();
            return Exe is not null ? null : $"winget exited with code {winget.ExitCode}";
        }
        catch (Win32Exception)
        {
            Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true });
            return "Install Tailscale from the page that opened, then sign in here";
        }
    }

    public static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException($"Tailscale self-test failed: {what}");
        }
        var status = ParseStatus("""
            {"BackendState":"Running","Self":{"UserID":111,"HostName":"desk","OS":"windows","TailscaleIPs":["fd7a:115c:a1e0::1","100.80.1.2"],"Online":true},
             "Peer":{"k1":{"UserID":111,"HostName":"laptop","OS":"windows","TailscaleIPs":["100.80.1.3"],"Online":true},
                     "k2":{"UserID":222,"HostName":"friend","OS":"linux","TailscaleIPs":["100.80.1.4"],"Online":true},
                     "k3":{"UserID":111,"HostName":"old","OS":"windows","TailscaleIPs":["100.80.1.5"],"Online":false}},
             "User":{"111":{"LoginName":"me@example.com"},"222":{"LoginName":"them@example.com"}}}
            """);
        Check(status.SignedIn && status.Self is { Name: "desk", UserId: "111" } && status.Self.Ip?.ToString() == "100.80.1.2" && status.Login == "me@example.com", "this PC and its account");
        Check(status.Mine.Select(d => (d.Name, d.Online)).SequenceEqual([("laptop", true), ("old", false)]), "the account's other PCs, another account's left out");
        Check(!ParseStatus("""{"BackendState":"NeedsLogin","Self":{"UserID":0,"HostName":"desk"}}""").SignedIn && !ParseStatus("""{"BackendState":"Running"}""").SignedIn, "signed out");
        Check(ParseWhoIs("""{"Node":{},"UserProfile":{"ID":111,"LoginName":"me@example.com"}}""") == "111" && ParseWhoIs("{}") is null && ParseWhoIs("nope") is null, "whois");
        var ip = IPAddress.Parse("100.80.1.3");
        Check(MayPair(ip, "111", "111") && !MayPair(ip, "222", "111") && !MayPair(ip, null, "111") && !MayPair(ip, "", "") &&
              !MayPair(IPAddress.Parse("192.168.1.3"), "111", "111") && !MayPair(null, "111", "111"), "pairing: only this account, only on Tailscale");
        Check(ParsePair(PairJson("K", "desk")) == new Pairing("K", "desk") && ParsePair("""{"key":""}""") is null && ParsePair("x") is null, "the pairing reply");
        Check(LoginUrl("To authenticate, visit:\n\n\thttps://login.tailscale.com/a/abc123\n") == "https://login.tailscale.com/a/abc123" &&
              LoginUrl("https://evil.example/login.tailscale.com/a/x") is null, "the sign-in page");
        const string notEnabled = "Serve is not enabled on your tailnet.\nTo enable, visit:\n\n\thttps://login.tailscale.com/f/serve?node=n123abc\n";
        Check(ServeEnableUrl(notEnabled) == "https://login.tailscale.com/f/serve?node=n123abc" &&
              ServeFailure(notEnabled) == "Serve is not enabled on your tailnet." &&
              ServeEnableUrl("Error: listener already exists for port 443") is null && ServeEnableUrl("visit https://evil.example/login.tailscale.com/f/serve") is null && ServeFailure("\r\nError: listener already exists\r\n") == "Error: listener already exists" &&
              ServeFailure("") == "", "why tailscale serve failed");
        const string web = """
            {"BackendState":"Running","Self":{"UserID":111,"HostName":"home","DNSName":"home.tail1234.ts.net."},
             "CurrentTailnet":{"Name":"me@example.com","MagicDNSSuffix":"tail1234.ts.net","MagicDNSEnabled":true},
             "User":{"111":{"LoginName":"me@example.com"}}%CERTS%}
            """;
        var https = ParseStatus(web.Replace("%CERTS%", ""","CertDomains":["home.tail1234.ts.net"]"""));
        Check(https is { DnsName: "home.tail1234.ts.net", MagicDnsSuffix: "tail1234.ts.net", CertDomains: ["home.tail1234.ts.net"] } && WebAppProblem(https) is null,
            "the phone app: this PC's name, the tailnet's suffix, its certificate");
        Check(WebAppProblem(ParseStatus(web.Replace("%CERTS%", ""))) == "Turn on HTTPS certificates for your tailnet in the Tailscale admin console (DNS page)",
            "the phone app without HTTPS certificates");
        Check(WebAppProblem(null) == "Tailscale is not installed" &&
              WebAppProblem(ParseStatus("""{"BackendState":"NeedsLogin","Self":{"UserID":0,"HostName":"home"}}""")) == "Tailscale is not signed in" &&
              WebAppProblem(ParseStatus("""{"BackendState":"Running","Self":{"UserID":111,"HostName":"home"},"CertDomains":["home.tail1234.ts.net"]}""")) ==
                  "Turn on MagicDNS for your tailnet in the Tailscale admin console (DNS page)",
            "the phone app without Tailscale, signed out, or without MagicDNS");
    }
}
