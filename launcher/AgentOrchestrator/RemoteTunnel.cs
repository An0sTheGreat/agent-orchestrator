using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// The Cloudflare quick tunnel (docs/remote-access.md): cloudflared forwards a random https://*.trycloudflare.com address
// to RemoteServer on 127.0.0.1. Online once its output names the address; when it exits while on, it restarts after a
// backoff (5 s doubling to 1 min). Stop kills it by its own pid with its tree, never by name. Its pid is kept in a file
// so a cloudflared left by an app instance that crashed is killed on the next start (only if that pid is cloudflared).
internal sealed partial class RemoteTunnel(string exe)
{
    internal enum Status { Off, Starting, Online, Failed }

    internal static string DefaultExe => Path.Combine(AppSettings.DataRoot, "tools", "cloudflared.exe");
    private static string PidFile => Path.Combine(AppSettings.DataRoot, "tools", "cloudflared.pid");

    internal string Exe => exe; // --ui-smoke points a tunnel at a fake cloudflared
    internal Status State { get; private set; }
    internal string? Url { get; private set; }
    internal string? Reason { get; private set; } // Failed: the last meaningful output line, or the exit code
    internal int? Pid { get { lock (_lock) return _process?.Id; } }
    internal event Action? Changed; // raised on any thread

    private readonly object _lock = new();
    private Process? _process;
    private bool _on;
    private int _port, _failures, _generation; // a new generation for every launch and stop: stale outputs and restarts are ignored

    // The quick tunnel's address; not api.trycloudflare.com (named in its errors) nor a longer host that starts like one.
    [GeneratedRegex(@"https://(?!api\.)[a-z0-9-]+\.trycloudflare\.com(?![a-z0-9-]|\.[a-z0-9])")]
    private static partial Regex UrlPattern();

    internal static string? ParseUrl(string line) => UrlPattern().Match(line) is { Success: true } m ? m.Value : null;

    internal static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(60, 5 << Math.Min(failures, 4)));

    internal void Start(int port)
    {
        int generation;
        lock (_lock)
        {
            if (_on)
                return;
            (_on, _port, _failures) = (true, port, 0);
            generation = ++_generation;
        }
        KillLeftover();
        Launch(generation);
    }

    internal void Stop()
    {
        Process? process;
        lock (_lock)
        {
            if (!_on)
                return;
            _on = false;
            _generation++;
            (process, _process) = (_process, null);
        }
        if (process is not null)
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch (Exception x) when (x is InvalidOperationException or Win32Exception) { } // already gone
        try { File.Delete(PidFile); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        Set(null, Status.Off, null, null);
    }

    private void Launch(int generation)
    {
        if (!File.Exists(exe))
        {
            Set(generation, Status.Failed, null, "cloudflared is not downloaded");
            return;
        }
        int port;
        lock (_lock)
            port = _port;
        if (!Set(generation, Status.Starting, null, null))
            return;
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // HttpListener answers 400 to a Host other than its 127.0.0.1 prefix, so the tunnel sends that one.
        foreach (var arg in new[] { "tunnel", "--url", $"http://127.0.0.1:{port}", "--no-autoupdate", "--http-host-header", $"127.0.0.1:{port}" })
            info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info };
        string? last = null;
        void Line(string? line)
        {
            if (line is null || !line.Any(char.IsLetter))
                return; // blank, or the banner's box
            last = line.Trim();
            if (ParseUrl(line) is { } url && Set(generation, Status.Online, url, null))
                lock (_lock)
                    _failures = 0; // online again: the next exit waits 5 s
        }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception x) when (x is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            Set(generation, Status.Failed, null, x.Message);
            return;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_lock)
            if (generation == _generation)
                _process = process;
            else
                try { process.Kill(entireProcessTree: true); } catch (Exception x) when (x is InvalidOperationException or Win32Exception) { } // stopped meanwhile
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PidFile)!);
            File.WriteAllText(PidFile, process.Id.ToString());
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        _ = Watch(process, generation, () => last);
    }

    // Exited while on: Failed with the reason, then a new launch after the backoff.
    private async Task Watch(Process process, int generation, Func<string?> last)
    {
        await process.WaitForExitAsync(); // and its output read to the end
        var code = process.ExitCode;
        process.Dispose();
        TimeSpan wait;
        lock (_lock)
        {
            if (generation != _generation || !_on)
                return;
            _process = null;
            wait = Backoff(_failures++);
            generation = ++_generation;
        }
        Set(generation, Status.Failed, null, $"{last() ?? $"cloudflared exited with code {code}"} (restarting in {wait.TotalSeconds:0} s)");
        await Task.Delay(wait);
        Launch(generation);
    }

    // False (and nothing changes) when `generation` is no longer current; null always applies.
    private bool Set(int? generation, Status state, string? url, string? reason)
    {
        lock (_lock)
        {
            if (generation is { } g && g != _generation)
                return false;
            (State, Url, Reason) = (state, url, reason);
        }
        Changed?.Invoke();
        return true;
    }

    private static void KillLeftover()
    {
        try
        {
            if (!File.Exists(PidFile) || !int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid))
                return;
            using var process = Process.GetProcessById(pid);
            if (process.ProcessName.Equals("cloudflared", StringComparison.OrdinalIgnoreCase))
                process.Kill(entireProcessTree: true);
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception) { } // gone already
    }

    // --self-test: the address parser on cloudflared's own lines, and the restart backoff.
    internal static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException($"Remote tunnel self-test: {what}");
        }
        (string Line, string? Url)[] lines =
        [
            ("2026-11-02T10:00:00Z INF Requesting new quick Tunnel on trycloudflare.com...", null),
            ("2026-11-02T10:00:01Z INF +--------------------------------------------------------------------------------------------+", null),
            ("2026-11-02T10:00:01Z INF |  Your quick Tunnel has been created! Visit it at (it may take some time to be reachable):  |", null),
            ("2026-11-02T10:00:01Z INF |  https://seasonal-deck-organisms-sf.trycloudflare.com                                      |", "https://seasonal-deck-organisms-sf.trycloudflare.com"),
            ("2026-11-02T10:00:01Z INF Thank you for trying Cloudflare Tunnel. Doing so, without a Cloudflare account, is a quick way to experiment. " +
                "https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/do-more-with-tunnels/trycloudflare/", null),
            ("2026-11-02T10:00:02Z ERR failed to request quick Tunnel: Post \"https://api.trycloudflare.com/tunnel\": dial tcp: i/o timeout", null),
            ("2026-11-02T10:00:03Z INF Registered tunnel connection connIndex=0 connection=8c3f event=0 ip=198.41.200.13 location=lax01 protocol=quic", null),
            ("see https://abc-1.trycloudflare.com.evil.example/x", null),
            ("https://abc-1.trycloudflare.com/path.", "https://abc-1.trycloudflare.com"),
        ];
        foreach (var (line, url) in lines)
            Check(ParseUrl(line) == url, $"parse {line}: {ParseUrl(line)}");
        var waits = Enumerable.Range(0, 7).Select(i => Backoff(i).TotalSeconds).ToList();
        Check(waits.SequenceEqual([5, 10, 20, 40, 60, 60, 60]), "backoff: " + string.Join(",", waits));
    }
}
