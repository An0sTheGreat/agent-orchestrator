using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AgentOrchestrator;

// One file of a Hugging Face repo to download: its path inside the repo ("sub/x.gguf") and its size in bytes.
public sealed record HubDownloadFile(string Name, long Size);

// Progress of one DownloadAsync call: the current file's bytes, the whole download's bytes, and a moving-average speed.
public sealed record HubProgress(string File, long FileBytes, long FileTotal, long DoneBytes, long TotalBytes, double BytesPerSecond);

// HfCache: huggingface_hub's cache layout under the hub cache folder, which the model picker and the engine read
// (LauncherLogic.FindLocalGgufs, orchestrator.py gguf_file). Flat: root\<repo name>\<file>.
public enum HubLayout { HfCache, Flat }

// Resumable HTTP downloads from Hugging Face. Each file goes to <target>.incomplete, resumes with a Range request, and is
// renamed when its size matches. The token goes only to the configured host, never to the CDN a download redirects to.
// The HttpClient must not follow redirects itself (AllowAutoRedirect = false): the hub's redirect carries the file's
// SHA-256 (X-Linked-Etag), which names the blob.
internal sealed class HubDownloader(HttpClient http, string root, HubLayout layout, string? token = null, string host = "https://huggingface.co")
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private const string Refused = "Hugging Face refused the download (gated model or token needed)";

    // The wait before retry n (1-3) of a failed file; the self-test shortens it.
    public static Func<int, TimeSpan> Backoff = attempt => TimeSpan.FromSeconds(attempt switch { 1 => 1, 2 => 3, _ => 8 });

    private readonly string _base = host.TrimEnd('/');
    private readonly Uri _host = new(host);

    public static string RepoFolder(string root, string repo) => Path.Combine(root, "models--" + repo.Replace("/", "--"));

    public static bool IsDownloaded(string root, HubLayout layout, string repo, string file)
    {
        if (layout == HubLayout.Flat)
            return SizeOf(Path.Combine(root, repo.Split('/')[^1], Local(file))) >= 0;
        var snapshots = Path.Combine(RepoFolder(root, repo), "snapshots");
        return Directory.Exists(snapshots) && Directory.EnumerateDirectories(snapshots).Any(d => SizeOf(Path.Combine(d, Local(file))) >= 0);
    }

    public static string? EnvToken()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
        return Env("HF_TOKEN") ?? Env("HUGGING_FACE_HUB_TOKEN");
    }

    public static string? CheckDiskSpace(string root, long bytesNeeded)
    {
        if (bytesNeeded <= 0)
            return null;
        long free;
        string drive;
        try
        {
            var info = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            (free, drive) = (info.AvailableFreeSpace, info.Name);
        }
        // ponytail: a network share (\\server\share) is not checked; a full share fails the download with an IOException
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
        const long margin = 1L << 30;
        static string Gb(long bytes) => $"{bytes / (double)(1L << 30):0.0} GB";
        return free - margin >= bytesNeeded ? null
            : $"Not enough disk space on {drive}: the download needs {Gb(bytesNeeded)} plus 1 GB spare, and {Gb(free)} is free.";
    }

    public async Task DownloadAsync(string repo, string revisionSha, IReadOnlyList<HubDownloadFile> files, IProgress<HubProgress>? progress, CancellationToken ct)
    {
        // repo, revision and file names come from the Hugging Face API, and become paths here
        if (repo.Split('/') is not [var org, var repoName])
            throw new ArgumentException($"Not a repo: {repo}");
        Segment(org, "repo");
        Segment(repoName, "repo");
        Segment(revisionSha, "revision");
        foreach (var file in files)
            foreach (var part in file.Name.Split('/'))
                Segment(part, "file name");

        var meter = new Meter(progress, files.Sum(f => f.Size));
        var todo = new List<(HubDownloadFile File, Uri Url, string Target, string? Snapshot)>();
        long missing = 0;
        foreach (var file in files)
        {
            var url = new Uri($"{_base}/{Escape(repo)}/resolve/{Uri.EscapeDataString(revisionSha)}/{Escape(file.Name)}");
            string target;
            string? snapshot = null;
            if (layout == HubLayout.Flat)
                target = Path.Combine(root, repoName, Local(file.Name));
            else
            {
                snapshot = Path.Combine(RepoFolder(root, repo), "snapshots", revisionSha, Local(file.Name));
                if (SizeOf(snapshot) == file.Size)
                {
                    meter.Done += file.Size;
                    continue;
                }
                var blob = "";
                await Retry(async idle => blob = await BlobName(url, idle.Token).ConfigureAwait(false), ct).ConfigureAwait(false);
                target = Path.Combine(RepoFolder(root, repo), "blobs", blob);
            }
            if (SizeOf(target) == file.Size)
            {
                if (snapshot is not null)
                    Link(target, snapshot);
                meter.Done += file.Size;
                continue;
            }
            var have = Math.Max(0, SizeOf(target + ".incomplete"));
            meter.Done += have;
            missing += Math.Max(0, file.Size - have);
            todo.Add((file, url, target, snapshot));
        }
        if (CheckDiskSpace(root, missing) is { } full)
            throw new IOException(full);

        foreach (var (file, url, target, snapshot) in todo)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await Retry(idle => Fetch(url, file, target, meter, idle, ct), ct).ConfigureAwait(false);
            if (snapshot is not null)
                Link(target, snapshot);
        }
        if (layout == HubLayout.HfCache)
        {
            var refs = Path.Combine(RepoFolder(root, repo), "refs");
            Directory.CreateDirectory(refs);
            File.WriteAllText(Path.Combine(refs, "main"), revisionSha);
        }
        meter.Report();
    }

    // The blob's name: the file's SHA-256 from the hub's redirect (X-Linked-Etag), else its ETag, as huggingface_hub names it.
    private async Task<string> BlobName(Uri url, CancellationToken cancel)
    {
        using var response = await Send(HttpMethod.Head, url, 0, crossHost: false, cancel).ConfigureAwait(false);
        Ensure(response);
        var tag = (response.Headers.TryGetValues("X-Linked-Etag", out var linked) ? linked.FirstOrDefault()
            : response.Headers.TryGetValues("ETag", out var etag) ? etag.FirstOrDefault() : null)
            ?? throw new InvalidDataException($"Hugging Face sent no ETag for {url}");
        return Segment((tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag).Trim('"'), "ETag");
    }

    private async Task Fetch(Uri url, HubDownloadFile file, string target, Meter meter, CancellationTokenSource idle, CancellationToken ct)
    {
        var part = target + ".incomplete";
        var have = Math.Max(0, SizeOf(part));
        using var response = await Send(HttpMethod.Get, url, have, crossHost: true, idle.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable) // 416: nothing after `have`; complete when it is the size
        {
            Ensure(response);
            var append = response.StatusCode == HttpStatusCode.PartialContent;
            if (append && response.Content.Headers.ContentRange?.From != have)
                throw new InvalidDataException($"{file.Name}: Hugging Face resumed at the wrong place");
            if (!append) // a 200 to a Range request: the whole file again
            {
                meter.Add(file, 0, -have);
                have = 0;
            }
            await using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            await using var sink = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 0, useAsync: true);
            var buffer = new byte[1 << 20];
            for (int read; (read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) > 0;)
            {
                await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                idle.CancelAfter(IdleTimeout);
                have += read;
                meter.Add(file, have, read);
                ct.ThrowIfCancellationRequested();
            }
        }
        if (have != file.Size)
            throw new InvalidDataException($"{file.Name}: received {have:N0} bytes, expected {file.Size:N0}; the partial download is kept");
        File.Move(part, target, overwrite: true);
    }

    // Follows redirects itself so the token goes to the configured host only. crossHost false stops at a redirect to
    // another host (the HEAD wants the hub's own headers).
    private async Task<HttpResponseMessage> Send(HttpMethod method, Uri uri, long from, bool crossHost, CancellationToken cancel)
    {
        for (var hop = 0; ; hop++)
        {
            var request = new HttpRequestMessage(method, uri);
            if (token is not null && SameHost(uri))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (from > 0)
                request.Headers.Range = new RangeHeaderValue(from, null);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location)
                return response;
            var next = new Uri(uri, location);
            if (!crossHost && !SameHost(next))
                return response;
            response.Dispose();
            if (hop == 10)
                throw new HttpRequestException("Too many redirects from Hugging Face");
            uri = next;
        }
    }

    private bool SameHost(Uri uri) =>
        string.Equals(uri.GetLeftPart(UriPartial.Authority), _host.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private static void Ensure(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (code is 401 or 403)
            throw new HttpRequestException(Refused, null, response.StatusCode);
        if (code == 404)
            throw new HttpRequestException("File not found on Hugging Face", null, response.StatusCode);
        if (code >= 400)
            throw new HttpRequestException($"Hugging Face answered {code} {response.ReasonPhrase}", null, response.StatusCode);
    }

    // Up to 3 retries (Backoff) for network errors, 5xx and 30 s without data; the cancel token and other errors end it.
    private static async Task Retry(Func<CancellationTokenSource, Task> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(IdleTimeout);
                try
                {
                    await work(idle).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException e) when (!ct.IsCancellationRequested && attempt > 3)
                {
                    throw new TimeoutException("Hugging Face sent no data for 30 seconds", e);
                }
                catch (Exception e) when (attempt <= 3 && !ct.IsCancellationRequested &&
                    (e is IOException or TimeoutException or OperationCanceledException ||
                     e is HttpRequestException { StatusCode: null or >= HttpStatusCode.InternalServerError }))
                {
                }
            }
            await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
        }
    }

    // snapshots\<sha>\<file> pointing at the blob: a relative symlink, else a hard link, else the blob moved there.
    private static void Link(string blob, string snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
        File.Delete(snapshot); // an unfinished or broken entry; a finished one was skipped before this
        try
        {
            File.CreateSymbolicLink(snapshot, Path.GetRelativePath(Path.GetDirectoryName(snapshot)!, blob));
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // no symlink privilege (Developer Mode off)
        if (!CreateHardLinkW(snapshot, blob, IntPtr.Zero))
            File.Move(blob, snapshot); // ponytail: a moved blob is not shared with a later revision, which downloads it again
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr securityAttributes);

    // A file's size through a symlink; -1 when it (or the link's target) is missing.
    private static long SizeOf(string path)
    {
        FileSystemInfo info = new FileInfo(path);
        if (info.LinkTarget is not null)
            info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
        return info is FileInfo { Exists: true } file ? file.Length : -1;
    }

    private static string Local(string name) => name.Replace('/', Path.DirectorySeparatorChar);
    private static string Escape(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string Segment(string s, string what) =>
        s.Length > 0 && s is not "." and not ".." && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? s
            : throw new ArgumentException($"Not a safe {what}: {s}");

    // Reports at most 4 times a second, with the speed averaged over the last 5 seconds.
    private sealed class Meter(IProgress<HubProgress>? progress, long total)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Queue<(double Seconds, long Done)> _window = new();
        private double _last = double.NegativeInfinity;
        private (string Name, long Bytes, long Size) _file = ("", 0, 0);
        public long Done;

        public void Add(HubDownloadFile file, long fileBytes, long delta)
        {
            Done += delta;
            _file = (file.Name, fileBytes, file.Size);
            if (_clock.Elapsed.TotalSeconds - _last >= 0.25)
                Report();
        }

        public void Report()
        {
            if (progress is null)
                return;
            var now = _last = _clock.Elapsed.TotalSeconds;
            _window.Enqueue((now, Done));
            while (now - _window.Peek().Seconds > 5)
                _window.Dequeue();
            var (then, before) = _window.Peek();
            progress.Report(new HubProgress(_file.Name, _file.Bytes, _file.Size, Done, total, now > then ? Math.Max(0, (Done - before) / (now - then)) : 0));
        }
    }

    // --self-test: a fake hub (redirects to a CDN host, Range/206/416, X-Linked-Etag) and a temp folder.
    internal static void HubDownloadSelfTest()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-hub-{Guid.NewGuid():N}");
        var backoff = Backoff;
        Backoff = _ => TimeSpan.Zero;
        try { Task.Run(() => SelfTestAsync(temp)).GetAwaiter().GetResult(); } // off the UI thread: no context to deadlock on
        finally
        {
            Backoff = backoff;
            try { Directory.Delete(temp, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task SelfTestAsync(string temp)
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException($"Hub download self-test: {what}");
        }
        static byte[] Bytes(int size, int seed)
        {
            var data = new byte[size];
            new Random(seed).NextBytes(data);
            return data;
        }
        const string repo = "org/m-GGUF", sha = "0123abcd", main = "m-Q4_K_M.gguf", nested = "sub/mmproj-m-F16.gguf";
        var (a, b) = (Bytes(300_000, 1), Bytes(200_000, 2));
        var files = new Dictionary<string, byte[]> { [main] = a, [nested] = b };
        HubDownloadFile[] both = [new(main, a.Length), new(nested, b.Length)];
        var none = CancellationToken.None;

        // the hub cache layout, with a token
        var hub = new FakeHub(files);
        var root = Path.Combine(temp, "cache");
        var seen = new List<HubProgress>();
        await new HubDownloader(new HttpClient(hub), root, HubLayout.HfCache, "secret", "https://hub.test").DownloadAsync(repo, sha, both, new Recorder(seen), none);
        var repoDir = RepoFolder(root, repo);
        Check(repoDir == Path.Combine(root, "models--org--m-GGUF"), "RepoFolder naming");
        Check(File.Exists(Path.Combine(repoDir, "blobs", FakeHub.Sha(a))), "the blob is named by its SHA-256 (X-Linked-Etag)");
        Check(File.ReadAllText(Path.Combine(repoDir, "refs", "main")) == sha, "refs/main holds the revision");
        Check(File.ReadAllBytes(Path.Combine(repoDir, "snapshots", sha, main)).SequenceEqual(a) &&
            File.ReadAllBytes(Path.Combine(repoDir, "snapshots", sha, "sub", "mmproj-m-F16.gguf")).SequenceEqual(b), "the snapshot files read back");
        Check(hub.HubAuth > 0 && hub.CdnAuth == 0, $"the token goes to the hub host only (hub {hub.HubAuth}, CDN {hub.CdnAuth})");
        Check(LauncherLogic.FindLocalGgufs("", root) is [{ Repo: repo, Variant: "Q4_K_M" }], "the model picker finds the download");
        Check(seen.Count > 0 && seen[^1].DoneBytes == a.Length + b.Length && seen[^1].TotalBytes == a.Length + b.Length, "progress reaches the total");
        Check(IsDownloaded(root, HubLayout.HfCache, repo, nested) && !IsDownloaded(root, HubLayout.HfCache, repo, "other.gguf") &&
            !IsDownloaded(root, HubLayout.HfCache, "org/none", main), "IsDownloaded in the hub cache");
        var requests = hub.Log.Count;
        await new HubDownloader(new HttpClient(hub), root, HubLayout.HfCache, "secret", "https://hub.test").DownloadAsync(repo, sha, both, null, none);
        Check(hub.Log.Count == requests, "a finished file is skipped without a request");

        // flat layout; a cancel keeps the partial file and the next call resumes it with a Range request
        var flatHub = new FakeHub(files);
        var flatRoot = Path.Combine(temp, "flat");
        var flat = new HubDownloader(new HttpClient(flatHub), flatRoot, HubLayout.Flat, host: "https://hub.test");
        using (var cts = new CancellationTokenSource())
        {
            flatHub.OnRead = at => { if (at >= 100_000) cts.Cancel(); };
            var cancelled = false;
            try { await flat.DownloadAsync(repo, sha, [both[0]], null, cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "a cancel throws OperationCanceledException");
        }
        flatHub.OnRead = null;
        var part = Path.Combine(flatRoot, "m-GGUF", main + ".incomplete");
        var kept = File.Exists(part) ? new FileInfo(part).Length : 0;
        Check(kept is > 0 and < 300_000, $"a cancel keeps the partial file ({kept} bytes)");
        await flat.DownloadAsync(repo, sha, both, null, none);
        Check(flatHub.Log.Contains($"GET cdn.test {kept}"), "the resume sends a Range request");
        Check(File.ReadAllBytes(Path.Combine(flatRoot, "m-GGUF", main)).SequenceEqual(a) && !File.Exists(part) &&
            File.ReadAllBytes(Path.Combine(flatRoot, "m-GGUF", "sub", "mmproj-m-F16.gguf")).SequenceEqual(b), "the resumed flat download matches");
        Check(!Directory.Exists(RepoFolder(flatRoot, repo)), "the flat layout has no hub folders");
        Check(IsDownloaded(flatRoot, HubLayout.Flat, repo, main) && !IsDownloaded(flatRoot, HubLayout.Flat, repo, "other.gguf"), "IsDownloaded in a flat folder");

        // a 200 reply to a Range request starts the file again
        var restartHub = new FakeHub(files) { IgnoreRange = true };
        var restartRoot = Path.Combine(temp, "restart");
        var blobs = Path.Combine(RepoFolder(restartRoot, repo), "blobs");
        Directory.CreateDirectory(blobs);
        File.WriteAllBytes(Path.Combine(blobs, FakeHub.Sha(a) + ".incomplete"), Enumerable.Repeat((byte)0xEE, 1000).ToArray());
        await new HubDownloader(new HttpClient(restartHub), restartRoot, HubLayout.HfCache, host: "https://hub.test").DownloadAsync(repo, sha, [both[0]], null, none);
        Check(restartHub.Log.Contains("GET cdn.test 1000") &&
            File.ReadAllBytes(Path.Combine(RepoFolder(restartRoot, repo), "snapshots", sha, main)).SequenceEqual(a), "a 200 reply to a Range request restarts the file");

        // a size mismatch throws and keeps the partial file
        var wrongRoot = Path.Combine(temp, "wrong");
        string? error = null;
        try { await new HubDownloader(new HttpClient(new FakeHub(files)), wrongRoot, HubLayout.Flat, host: "https://hub.test").DownloadAsync(repo, sha, [new(main, a.Length + 5)], null, none); }
        catch (InvalidDataException e) { error = e.Message; }
        var wrongPart = Path.Combine(wrongRoot, "m-GGUF", main + ".incomplete");
        Check(error is not null && File.Exists(wrongPart) && new FileInfo(wrongPart).Length == a.Length && !File.Exists(Path.Combine(wrongRoot, "m-GGUF", main)),
            "a size mismatch throws and keeps the partial file");

        // refusals and missing files
        error = null;
        try { await new HubDownloader(new HttpClient(new FakeHub(files) { Refuse = true }), Path.Combine(temp, "gated"), HubLayout.HfCache, host: "https://hub.test").DownloadAsync(repo, sha, both, null, none); }
        catch (HttpRequestException e) { error = e.Message; }
        Check(error == Refused, $"a 401 gives the gated message ({error})");
        error = null;
        try { await new HubDownloader(new HttpClient(new FakeHub(files)), Path.Combine(temp, "missing"), HubLayout.Flat, host: "https://hub.test").DownloadAsync(repo, sha, [new("gone.gguf", 5)], null, none); }
        catch (HttpRequestException e) { error = e.Message; }
        Check(error == "File not found on Hugging Face", $"a 404 gives the not-found message ({error})");

        // a 503 is retried
        var flaky = new FakeHub(files);
        flaky.FailGets.Enqueue(HttpStatusCode.ServiceUnavailable);
        await new HubDownloader(new HttpClient(flaky), Path.Combine(temp, "flaky"), HubLayout.Flat, host: "https://hub.test").DownloadAsync(repo, sha, [both[0]], null, none);
        Check(flaky.Log.Count(l => l.StartsWith("GET hub.test", StringComparison.Ordinal)) == 2 &&
            File.ReadAllBytes(Path.Combine(temp, "flaky", "m-GGUF", main)).SequenceEqual(a), "a 503 is retried");

        // the token from the environment
        var saved = (Environment.GetEnvironmentVariable("HF_TOKEN"), Environment.GetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN"));
        try
        {
            Environment.SetEnvironmentVariable("HF_TOKEN", null);
            Environment.SetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN", null);
            var absent = EnvToken();
            Environment.SetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN", "old");
            var fallback = EnvToken();
            Environment.SetEnvironmentVariable("HF_TOKEN", "new");
            Check(absent is null && fallback == "old" && EnvToken() == "new", "EnvToken prefers HF_TOKEN, then HUGGING_FACE_HUB_TOKEN");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HF_TOKEN", saved.Item1);
            Environment.SetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN", saved.Item2);
        }
        Check(CheckDiskSpace(temp, 1) is null && CheckDiskSpace(temp, long.MaxValue) is { } message && message.Contains("disk space"), "CheckDiskSpace");
    }

    private sealed class Recorder(List<HubProgress> seen) : IProgress<HubProgress>
    {
        public void Report(HubProgress value) => seen.Add(value);
    }

    // hub.test answers like huggingface.co (302 to the CDN with X-Linked-Etag); cdn.test serves the bytes with Range support.
    private sealed class FakeHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public readonly List<string> Log = [];
        public readonly Queue<HttpStatusCode> FailGets = new();
        public int HubAuth, CdnAuth;
        public bool IgnoreRange, Refuse;
        public Action<long>? OnRead;

        public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var range = request.Headers.Range?.Ranges.First().From;
            var auth = request.Headers.Authorization is not null;
            Log.Add($"{request.Method} {uri.Host} {range}");
            HttpResponseMessage Reply(HttpStatusCode code) => new(code) { RequestMessage = request };
            if (uri.Host == "hub.test")
            {
                HubAuth += auth ? 1 : 0;
                if (Refuse)
                    return Task.FromResult(Reply(HttpStatusCode.Unauthorized));
                if (request.Method == HttpMethod.Get && FailGets.TryDequeue(out var fail))
                    return Task.FromResult(Reply(fail));
                var path = Uri.UnescapeDataString(uri.AbsolutePath);
                path = path[(path.IndexOf("/resolve/", StringComparison.Ordinal) + "/resolve/".Length)..];
                if (!files.TryGetValue(path[(path.IndexOf('/') + 1)..], out var known))
                    return Task.FromResult(Reply(HttpStatusCode.NotFound));
                var moved = Reply(HttpStatusCode.Found);
                moved.Headers.Location = new Uri("https://cdn.test/" + Sha(known));
                moved.Headers.Add("X-Linked-Etag", $"\"{Sha(known)}\"");
                moved.Headers.ETag = new EntityTagHeaderValue("\"hub-etag\"");
                return Task.FromResult(moved);
            }
            CdnAuth += auth ? 1 : 0;
            var data = files.Values.First(d => Sha(d) == uri.AbsolutePath[1..]);
            var from = IgnoreRange ? 0 : (int)(range ?? 0);
            if (from >= data.Length && from > 0)
            {
                var beyond = Reply(HttpStatusCode.RequestedRangeNotSatisfiable);
                beyond.Content = new ByteArrayContent([]);
                beyond.Content.Headers.ContentRange = new ContentRangeHeaderValue(data.Length);
                return Task.FromResult(beyond);
            }
            var ok = Reply(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
            ok.Content = new StreamContent(new Trickle(data[from..], OnRead));
            if (from > 0)
                ok.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
            ok.Headers.ETag = new EntityTagHeaderValue("\"cdn-etag\"");
            return Task.FromResult(ok);
        }
    }

    // Serves 64 KB per read and reports its position, so a test can cancel part-way.
    private sealed class Trickle(byte[] data, Action<long>? onRead) : MemoryStream(data, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = Read(buffer.Span[..Math.Min(buffer.Length, 1 << 16)]);
            onRead?.Invoke(Position);
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
