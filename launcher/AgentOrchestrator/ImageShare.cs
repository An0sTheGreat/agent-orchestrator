using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator;

// The front door of sharing this PC's models (ModelShare.cs, docs/superpowers/specs/2026-11-19-share-models-design.md): a
// minimal HTTP/1.1 server on 127.0.0.1:ModelShare.ImagePort, the Cloudflare tunnel's target. GET /images/models lists the
// recipes and the local image models; POST /images/generate runs one (MainWindow.Share.cs); /v1/... is proxied to llama-server
// on 127.0.0.1:<upstream>. Every request needs the share key as a bearer token; one request per connection, then it closes.
internal static class ImageShare
{
    public const int MaxBody = 1 << 20, MaxHead = 16 << 10;
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    public const string ModelsPath = "/images/models", GeneratePath = "/images/generate", UnloadPath = "/share/unload", PresetsPath = "/share/presets", LoadPath = "/share/load", ChatPrefix = "/v1/";
    // GET without the key: the key itself, for a PC signed in to this PC's Tailscale account (Server.Pair, Tailscale.MayPair).
    public const string PairPath = "/share/pair";
    // GET: this PC's profile (name, picture, when it last changed); POST: a newer one from a connected PC (Profile, MainWindow.Tailnet.cs).
    public const string ProfilePath = "/share/profile";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Query: what follows the path's "?" ("" = none). Remote: the caller's address (set by Server).
    public sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body, string Query = "", IPAddress? Remote = null);
    public sealed record Response(int Status, string Json);
    public sealed class HttpError(int status, string message) : Exception(message) { public int Status => status; }

    // What the other PC's picker lists; Id goes back as ImageSettings.RecipeId.
    public sealed record Model(string Id, string Name, string Note, bool Negative, int Multiple, int Steps, double Cfg, string Sampler);
    public sealed record Picture(string Png, long Seed);
    private sealed record ModelList(List<Model> Models);
    private sealed record PictureList(List<Picture> Images);
    // Status: set when the error rides in a 200 reply whose head went out before the outcome was known (/images/generate).
    private sealed class Sync<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); } // the self-test's, without a context

    private sealed record ErrorReply(string Error, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Status = null);

    // Cloudflare drops a reply that has not started within ~100 s (524): /images/generate sends its 200 head at once and a
    // space every Heartbeat until the outcome (JSON allows leading whitespace), and an error goes in the body with its status.
    internal static TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    // While it runs, a {"progress","status"} line goes out each ProgressTick the primary PC's status changed (Server.Progress), so
    // the other PC shows the real step and percentage; the outcome is the last line.
    internal static TimeSpan ProgressTick = TimeSpan.FromSeconds(1);
    public sealed record ProgressLine(double Progress, string Status);
    public static string ProgressJson(double fraction, string status) => JsonSerializer.Serialize(new ProgressLine(Math.Clamp(fraction, 0, 1), status), Web);

    // The request line, the headers and a Content-Length body (chunked bodies are not read). HttpError: 400, 413 or 431.
    public static async Task<Request> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var (request, _, rest) = await ReadHeadAsync(stream, ct);
        return await ReadBodyAsync(stream, request, rest, ct);
    }

    // The request (no body yet), its head's lines as sent (Latin-1: byte for byte), and the bytes read past the head.
    public static async Task<(Request Request, string[] Lines, byte[] Remainder)> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHead];
        int filled = 0, end;
        while ((end = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8)) < 0)
        {
            if (filled == buffer.Length)
                throw new HttpError(431, "The request headers are too large");
            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0)
                throw new HttpError(400, "Incomplete request");
            filled += read;
        }
        var lines = Encoding.Latin1.GetString(buffer, 0, end).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !first[1].StartsWith('/'))
            throw new HttpError(400, "Bad request line");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                throw new HttpError(400, "Bad header");
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var target = first[1].Split('?', 2);
        return (new(first[0], target[0], headers, [], target.Length > 1 ? target[1] : ""), lines, buffer[(end + 4)..filled]);
    }

    public static async Task<Request> ReadBodyAsync(Stream stream, Request request, byte[] rest, CancellationToken ct)
    {
        long length = 0;
        if (request.Headers.TryGetValue("Content-Length", out var text) && !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out length))
            throw new HttpError(400, "Bad Content-Length");
        if (length > MaxBody)
            throw new HttpError(413, $"The body is over {MaxBody} bytes");
        var body = new byte[length];
        var have = (int)Math.Min(rest.Length, length);
        rest.AsSpan(0, have).CopyTo(body);
        try { await stream.ReadExactlyAsync(body.AsMemory(have), ct); }
        catch (EndOfStreamException) { throw new HttpError(400, "The body is shorter than its Content-Length"); }
        return request with { Body = body };
    }

    // 401 without the key (checked first, so the paths stay hidden), 404 for an unknown path, 405 for the wrong method; null = go on.
    public static Response? Check(Request request, string key)
    {
        if (Unauthorized(request, key) is { } refused)
            return refused;
        var method = request.Path switch { ModelsPath => "GET", GeneratePath => "POST", UnloadPath => "POST", PresetsPath => "GET", LoadPath or ProfilePath => request.Method == "POST" ? "POST" : "GET", _ => null };
        return method is null ? Error(404, $"No such path: {request.Path}") : request.Method == method ? null : Error(405, $"{request.Path} takes {method}");
    }

    // 401 unless the bearer token is the key (constant-time compare); null = go on.
    public static Response? Unauthorized(Request request, string key)
    {
        var given = request.Headers.GetValueOrDefault("Authorization") is { } auth && auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..].Trim() : "";
        if (key.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key)))
            return Error(401, "Wrong or missing share key");
        return null;
    }

    // A /v1/ request to llama-server on 127.0.0.1:upstream: its head as sent (Connection: close in place of the client's), then
    // whatever the client sends, unchanged; llama-server's reply goes back as it arrives (SSE streaming) until it closes the
    // connection. 502 when llama-server is not listening (it is restarting); null = answered.
    public static async Task<Response?> ProxyAsync(Stream client, string[] lines, byte[] rest, int upstream, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        try { await tcp.ConnectAsync(IPAddress.Loopback, upstream, ct); }
        catch (SocketException) { return Error(502, "llama-server is not running on the primary PC (it restarts by itself): try again shortly"); }
        var server = tcp.GetStream();
        var head = string.Join("\r\n", lines.Where((line, i) => i == 0 || !line[..line.IndexOf(':')].Trim().Equals("Connection", StringComparison.OrdinalIgnoreCase)));
        await server.WriteAsync(Encoding.Latin1.GetBytes(head + "\r\nConnection: close\r\n\r\n"), ct);
        await server.WriteAsync(rest, ct);
        _ = Upload();
        await server.CopyToAsync(client, ct);
        return null;

        async Task Upload() // the rest of the body; it ends with an error when the connection closes
        {
            try { await client.CopyToAsync(server, ct); }
            catch (Exception x) when (x is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }
    }

    public static Response Error(int status, string message) => new(status, JsonSerializer.Serialize(new ErrorReply(message), Web));

    public static byte[] Bytes(Response response)
    {
        var reason = response.Status switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict",
            413 => "Content Too Large", 431 => "Request Header Fields Too Large", 502 => "Bad Gateway", 503 => "Service Unavailable", _ => "Internal Server Error",
        };
        var body = Encoding.UTF8.GetBytes(response.Json);
        var head = $"HTTP/1.1 {response.Status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        return [.. Encoding.ASCII.GetBytes(head), .. body];
    }

    // The primary PC's Images page list: the recipes, then the image models in its folder (by file name).
    public static List<Model> Models(IEnumerable<ImageGen.LocalModel> locals) =>
    [
        .. ImageGen.Recipes.Select(r => new Model(r.Id, r.Name, r.Note, r.Negative, r.Multiple, r.Steps, r.Cfg, r.Sampler)),
        .. locals.Select(m => new Model("local:" + Path.GetFileName(m.Path), $"{Path.GetFileName(m.Path)} · {m.Recipe.Name}", "", m.Recipe.Negative, m.Recipe.Multiple,
            m.Recipe.Steps, m.Recipe.Cfg, m.Recipe.Sampler)),
    ];

    // A Model id back to the recipe and local model path ("" = the recipe's download); null = unknown.
    public static (ImageRecipe Recipe, string Local)? Resolve(string id, IEnumerable<ImageGen.LocalModel> locals) =>
        id.StartsWith("local:", StringComparison.Ordinal)
            ? locals.FirstOrDefault(m => Path.GetFileName(m.Path).Equals(id[6..], StringComparison.OrdinalIgnoreCase)) is { } m ? (m.Recipe, m.Path) : null
            : ImageGen.Recipes.FirstOrDefault(r => r.Id == id) is { } r ? (r, "") : null;

    // What the primary PC runs for a request: the model from the resolved id only (never a path from the body), no start image,
    // the numbers within the page's ranges, a seed chosen when none was sent.
    public static ImageSettings Run(ImageSettings s, ImageRecipe recipe, string local) => new()
    {
        RecipeId = recipe.Id, LocalModel = local, Prompt = s.Prompt.Trim(), Negative = s.Negative, Aspect = s.Aspect, LongSide = s.LongSide,
        Width = Math.Clamp(s.Width, 64, 4096), Height = Math.Clamp(s.Height, 64, 4096), Steps = Math.Clamp(s.Steps, 1, 100), Cfg = Math.Clamp(s.Cfg, 0, 20),
        Seed = s.Seed >= 0 ? s.Seed : Random.Shared.Next(), Sampler = ImageGen.Samplers.Contains(s.Sampler) ? s.Sampler : "", Scheduler = ImageGen.Schedulers.Contains(s.Scheduler) ? s.Scheduler : "", Batch = Math.Clamp(s.Batch, 1, 4), LowVram = s.LowVram,
    };

    public static string ModelsJson(List<Model> models) => JsonSerializer.Serialize(new ModelList(models), Web);
    public static List<Model> ParseModels(string json) => JsonSerializer.Deserialize<ModelList>(json, Web)?.Models ?? [];
    public static string PicturesJson(List<Picture> pictures) => JsonSerializer.Serialize(new PictureList(pictures), Web);
    public static List<Picture> ParsePictures(string json) => JsonSerializer.Deserialize<PictureList>(json, Web)?.Images ?? [];

    // The other PC's message for a failed call.
    public static string ErrorText(int status, string body)
    {
        if (status == 401)
            return "The primary PC refused this PC's key: connect again with a new connection code (Settings > Models)";
        if (status == 409)
            return "The primary PC is busy generating another picture: try again when it is done";
        string? message = null;
        try { message = JsonSerializer.Deserialize<ErrorReply>(body, Web)?.Error; }
        catch (JsonException) { }
        return string.IsNullOrEmpty(message) ? $"The primary PC returned {status}" : "The primary PC: " + message;
    }

    // The other PC's calls to baseUrl (ModelShare.ImagesUrl). A refusal throws InvalidOperationException(ErrorText), a connection
    // or DNS failure InvalidOperationException(CannotReach).
    public const string CannotReach = "Cannot reach the primary PC: is sharing on there? Its address changes each time sharing starts; paste the new code under Settings > Models.";

    // The primary PC unloads its shared chat model (the other PC's Unload models button).
    public static async Task UnloadAsync(HttpClient http, string baseUrl, string key, CancellationToken ct) =>
        await SendAsync(http, new(HttpMethod.Post, baseUrl + UnloadPath) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }, key, ct);

    // The primary PC's model presets for its shared models (ModelShare.PresetsJson), as sent.
    public static Task<string> FetchPresetsAsync(HttpClient http, string baseUrl, string key, CancellationToken ct) =>
        SendAsync(http, new(HttpMethod.Get, baseUrl + PresetsPath), key, ct);

    // The primary PC's saved load options for one of its shared models (ModelShare.LoadJson), and saving changed ones there.
    public static async Task<Dictionary<string, string>> FetchLoadAsync(HttpClient http, string baseUrl, string key, string model, CancellationToken ct) =>
        ModelShare.ParseLoad(await SendAsync(http, new(HttpMethod.Get, $"{baseUrl}{LoadPath}?model={Uri.EscapeDataString(model)}"), key, ct));

    public static async Task SaveLoadAsync(HttpClient http, string baseUrl, string key, string model, IReadOnlyDictionary<string, string> options, CancellationToken ct) =>
        await SendAsync(http, new(HttpMethod.Post, baseUrl + LoadPath)
            { Content = new StringContent(JsonSerializer.Serialize(new { model, options }), Encoding.UTF8, "application/json") }, key, ct);

    // The sidebar's profile as the PCs pass it on: Png = the picture as base64 PNG ("" = none), Changed = when it was last edited (UTC).
    public sealed record Profile(string Name, string Png, DateTime Changed);
    public static string ProfileJson(Profile profile) => JsonSerializer.Serialize(profile, Web);
    public static Profile? ParseProfile(string json)
    {
        try { return JsonSerializer.Deserialize<Profile>(json, Web) is { Name: not null, Png: not null } p ? p : null; }
        catch (JsonException) { return null; }
    }

    // Which way a connected PC syncs: "pull" the primary PC's newer profile, "push" its own newer one, "" = nothing to do. Equal
    // times (two profiles set before syncing existed carry none) with different profiles: the primary PC's wins.
    public static string ProfileSync(DateTime mine, DateTime theirs, bool differ = false) =>
        theirs > mine ? "pull" : mine > theirs ? "push" : differ ? "pull" : "";

    public static async Task<Profile?> FetchProfileAsync(HttpClient http, string baseUrl, string key, CancellationToken ct) =>
        ParseProfile(await SendAsync(http, new(HttpMethod.Get, baseUrl + ProfilePath), key, ct));

    public static async Task SendProfileAsync(HttpClient http, string baseUrl, string key, Profile profile, CancellationToken ct) =>
        await SendAsync(http, new(HttpMethod.Post, baseUrl + ProfilePath) { Content = new StringContent(ProfileJson(profile), Encoding.UTF8, "application/json") }, key, ct);

    public static async Task<List<Model>> FetchModelsAsync(HttpClient http, string baseUrl, string key, CancellationToken ct) =>
        ParseModels(await SendAsync(http, new(HttpMethod.Get, baseUrl + ModelsPath), key, ct));

    // The reply is 200 from its first byte (Server.HeartbeatAsync): progress lines as the primary PC works (reported to progress),
    // then the outcome on the last line; a failure is {"error","status"} there.
    public static async Task<List<Picture>> GenerateAsync(HttpClient http, string baseUrl, string key, ImageSettings settings, CancellationToken ct,
        IProgress<ProgressLine>? progress = null)
    {
        string body = "";
        using (var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + GeneratePath) { Content = new StringContent(JsonSerializer.Serialize(settings), Encoding.UTF8, "application/json") })
        {
            request.Headers.Authorization = new("Bearer", key);
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(ErrorText((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct)));
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.Trim().Length == 0)
                        continue;
                    if (line.TrimStart().StartsWith("{\"progress\"", StringComparison.Ordinal))
                    {
                        try { if (JsonSerializer.Deserialize<ProgressLine>(line, Web) is { } p) progress?.Report(p); }
                        catch (JsonException) { }
                        continue;
                    }
                    body = line;
                }
            }
            catch (HttpRequestException x) when (x.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
            {
                throw new InvalidOperationException(CannotReach, x);
            }
        }
        ErrorReply? failed = null;
        try { failed = JsonSerializer.Deserialize<ErrorReply>(body, Web); }
        catch (JsonException) { }
        if (failed is { Error.Length: > 0 })
            throw new InvalidOperationException(ErrorText(failed.Status ?? 500, body));
        return ParsePictures(body);
    }

    private static async Task<string> SendAsync(HttpClient http, HttpRequestMessage request, string key, CancellationToken ct)
    {
        using (request)
        {
            request.Headers.Authorization = new("Bearer", key);
            try
            {
                using var response = await http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                return response.IsSuccessStatusCode ? body : throw new InvalidOperationException(ErrorText((int)response.StatusCode, body));
            }
            catch (HttpRequestException x) when (x.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
            {
                throw new InvalidOperationException(CannotReach, x);
            }
        }
    }

    // ponytail: a task per connection, no cap; a request without the key is dropped after ReadTimeout or answered 401 at once.
    // Add a cap if the tunnel address leaking ever becomes a load problem.
    public sealed class Server : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; }

        private readonly int _upstream;
        // The current {"progress","status"} line of the generation in progress (null: none yet); read from the accept loop's threads.
        public Func<string?>? Progress { get; set; }
        // GET /share/pair, answered without the key (it checks the caller itself); null = 401 like any path without the key.
        public Func<Request, Task<Response>>? Pair { get; set; }

        // upstream: llama-server's port on 127.0.0.1, for /v1/.
        public Server(IPAddress ip, int port, int upstream, Func<string> key, Func<Request, CancellationToken, Task<Response>> handle)
        {
            _upstream = upstream;
            _listener = new TcpListener(ip, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptAsync(key, handle)); // off the caller's dispatcher: the handler marshals UI work itself
        }

        private async Task AcceptAsync(Func<string> key, Func<Request, CancellationToken, Task<Response>> handle)
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception x) when (x is OperationCanceledException or ObjectDisposedException) { return; }
                catch (SocketException) when (!_stop.IsCancellationRequested) { continue; }
                catch (SocketException) { return; }
                _ = ServeAsync(client, key, handle);
            }
        }

        private async Task ServeAsync(TcpClient client, Func<string> key, Func<Request, CancellationToken, Task<Response>> handle)
        {
            using (client)
            {
                var stream = client.GetStream();
                Response? response;
                try
                {
                    Request request;
                    string[] lines;
                    byte[] rest;
                    using (var read = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                    {
                        read.CancelAfter(ReadTimeout); // a stalled client is dropped
                        (request, lines, rest) = await ReadHeadAsync(stream, read.Token);
                        if (!request.Path.StartsWith(ChatPrefix, StringComparison.Ordinal))
                            request = await ReadBodyAsync(stream, request, rest, read.Token);
                    }
                    var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                    request = request with { Remote = remote is { IsIPv4MappedToIPv6: true } ? remote.MapToIPv4() : remote };
                    response = request.Path.StartsWith(ChatPrefix, StringComparison.Ordinal)
                        ? Unauthorized(request, key()) ?? await ProxyAsync(stream, lines, rest, _upstream, _stop.Token)
                        : request.Path == PairPath && Pair is { } pair
                        ? request.Method == "GET" ? await pair(request) : Error(405, $"{PairPath} takes GET")
                        : Check(request, key()) ?? (request.Path == GeneratePath
                            ? await GenerateUntilGoneAsync(client.Client, stream, request, handle)
                            : await handle(request, _stop.Token));
                    if (response is null)
                        return;
                }
                catch (HttpError x) { response = Error(x.Status, x.Message); }
                catch (Exception x) when (x is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { return; }
                catch (Exception x) { response = Error(500, x.Message); }
                try { await stream.WriteAsync(Bytes(response), _stop.Token); }
                catch (Exception x) when (x is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            }
        }

        // The generation, cancelled when the other PC hangs up (its Cancel closes the connection): else the primary PC kept
        // downloading and generating for nobody and answered every later request 409 busy until it was done.
        private async Task<Response?> GenerateUntilGoneAsync(Socket socket, Stream stream, Request request, Func<Request, CancellationToken, Task<Response>> handle)
        {
            using var gone = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            return await HeartbeatAsync(stream, handle(request, gone.Token), Progress, _stop.Token, () =>
            {
                if (!Closed(socket))
                    return false;
                gone.Cancel();
                return true;
            });
        }

        // The other PC sent its whole request before this, so readable with nothing to read is its close (or a reset).
        internal static bool Closed(Socket socket)
        {
            try { return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0; }
            catch (Exception x) when (x is SocketException or ObjectDisposedException) { return true; }
        }

        // The 200 head now; while the handler runs, a progress line whenever the status changed (checked each ProgressTick) and a
        // blank line at least every Heartbeat; then its JSON on the last line (an error as {"error","status"}). Null: the reply is written.
        private static async Task<Response?> HeartbeatAsync(Stream stream, Task<Response> work, Func<string?>? progress, CancellationToken ct, Func<bool>? hungUp = null)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/x-ndjson; charset=utf-8\r\nConnection: close\r\n\r\n"), ct);
            string? sent = null;
            var quiet = System.Diagnostics.Stopwatch.StartNew();
            while (await Task.WhenAny(work, Task.Delay(ProgressTick, ct)) != work)
            {
                if (hungUp?.Invoke() == true)
                    return null; // the handler sees its token cancelled and stops
                var line = progress?.Invoke();
                if (line is not null && line != sent)
                    await stream.WriteAsync(Encoding.UTF8.GetBytes((sent = line) + "\n"), ct);
                else if (quiet.Elapsed < Heartbeat)
                    continue;
                else
                    await stream.WriteAsync("\n"u8.ToArray(), ct);
                quiet.Restart();
            }
            await stream.WriteAsync("\n"u8.ToArray(), ct);
            Response result;
            try { result = await work; }
            catch (HttpError x) { result = Error(x.Status, x.Message); }
            catch (Exception x) when (x is not OperationCanceledException) { result = Error(500, x.Message); }
            var json = result.Status == 200 ? result.Json
                : JsonSerializer.Serialize(new ErrorReply(JsonSerializer.Deserialize<ErrorReply>(result.Json, Web)?.Error ?? $"failed ({result.Status})", result.Status), Web);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(json), ct);
            return null;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    public static void SelfTest()
    {
        static void Assert(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("ImageShare: " + what);
        }
        var when = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);
        Assert(ParseProfile(ProfileJson(new("An0s", "iVBO", when))) is { Name: "An0s", Png: "iVBO" } back && back.Changed == when &&
               ParseProfile("{}") is null && ParseProfile("nope") is null, "the profile round trip");
        Assert(ProfileSync(when, when.AddMinutes(1)) == "pull" && ProfileSync(when.AddMinutes(1), when) == "push" && ProfileSync(when, when) == "" &&
               ProfileSync(when, when, differ: true) == "pull" && ProfileSync(when.AddMinutes(1), when, differ: true) == "push", "which way the profile syncs");
        Assert(Check(new("POST", ProfilePath, new() { ["Authorization"] = "Bearer k" }, []), "k") is null &&
               Check(new("GET", ProfilePath, new() { ["Authorization"] = "Bearer k" }, []), "k") is null &&
               Check(new("GET", ProfilePath, new(), []), "k")?.Status == 401, "the profile path needs the key");
        static Request Parse(string text) => ReadRequestAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None).GetAwaiter().GetResult();
        static int Refused(string text)
        {
            try { Parse(text); return 0; }
            catch (HttpError x) { return x.Status; }
        }

        var post = Parse("POST /images/generate?x=1 HTTP/1.1\r\nHost: a\r\nauthorization:  Bearer k \r\nContent-Length: 5\r\n\r\nhello");
        Assert(post is { Method: "POST", Path: GeneratePath } && post.Headers["AUTHORIZATION"] == "Bearer k" && Encoding.UTF8.GetString(post.Body) == "hello", "a POST with a body");
        Assert(Parse("GET /images/models HTTP/1.1\r\n\r\n") is { Method: "GET", Path: ModelsPath, Body.Length: 0 }, "a GET without a body");
        Assert(Refused($"POST /images/generate HTTP/1.1\r\nContent-Length: {MaxBody + 1}\r\n\r\n") == 413, "an oversized body is refused");
        Assert(Refused("POST /images/generate HTTP/1.1\r\nContent-Length: 9\r\n\r\nshort") == 400, "a short body");
        Assert(Refused("GET /images/models\r\n\r\n") == 400 && Refused("GET /x HTTP/1.1\r\nno colon\r\n\r\n") == 400 && Refused("GET /x HTTP/1.1\r\n") == 400, "bad requests");
        Assert(Refused("GET /x HTTP/1.1\r\nX: " + new string('a', MaxHead) + "\r\n\r\n") == 431, "too large headers");

        Request R(string method, string path, string? auth) =>
            new(method, path, auth is null ? new(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = auth }, []);
        Assert(Check(R("GET", ModelsPath, null), "key")?.Status == 401 && Check(R("GET", ModelsPath, "Bearer nope"), "key")?.Status == 401 &&
            Check(R("GET", ModelsPath, "key"), "key")?.Status == 401 && Check(R("GET", ModelsPath, "Bearer "), "")?.Status == 401, "401 without the key");
        Assert(Check(R("GET", "/images/other", null), "key")?.Status == 401, "401 before 404");
        Assert(Check(R("GET", "/images/other", "Bearer key"), "key")?.Status == 404, "404 for an unknown path");
        Assert(Check(R("POST", ModelsPath, "Bearer key"), "key")?.Status == 405 && Check(R("GET", GeneratePath, "Bearer key"), "key")?.Status == 405, "405 for a wrong method");
        Assert(Check(R("GET", ModelsPath, "Bearer key"), "key") is null && Check(R("POST", GeneratePath, "Bearer key"), "key") is null, "the key lets a request through");
        Assert(Check(R("GET", PresetsPath, null), "key")?.Status == 401 && Check(R("GET", PresetsPath, "Bearer key"), "key") is null, "/share/presets takes the key");
        Assert(Check(R("GET", LoadPath, null), "key")?.Status == 401 && Check(R("POST", LoadPath, "Bearer nope"), "key")?.Status == 401 &&
            Check(R("GET", LoadPath, "Bearer key"), "key") is null && Check(R("POST", LoadPath, "Bearer key"), "key") is null && Check(R("PUT", LoadPath, "Bearer key"), "key")?.Status == 405,
            "/share/load: GET and POST with the key, 401 without");
        Assert(Parse("GET /share/load?model=Qwen%20%C2%B7%20Q4 HTTP/1.1\r\n\r\n") is { Path: LoadPath, Query: "model=Qwen%20%C2%B7%20Q4" } && Parse("GET /x HTTP/1.1\r\n\r\n").Query == "",
            "the query"); 

        var moss = ImageGen.Recipe("moss-image-sft");
        ImageGen.LocalModel[] locals = [new(moss, @"D:\m\MOSS-Image-SFT-Q8_0.gguf"), new(ImageGen.Recipe("sdxl"), @"E:\x\sd_xl_turbo.safetensors")];
        var models = Models(locals);
        Assert(models.Count == ImageGen.Recipes.Length + 2 && models[^2] is { Id: "local:MOSS-Image-SFT-Q8_0.gguf", Name: "MOSS-Image-SFT-Q8_0.gguf · MOSS-Image SFT", Negative: true, Steps: 20 },
            "the recipes and the local models");
        foreach (var m in models)
            Assert(Resolve(m.Id, locals) is var (recipe, local) && (m.Id.StartsWith("local:") ? local.EndsWith(m.Id[6..]) && recipe == locals.First(l => l.Path == local).Recipe
                : local == "" && recipe.Id == m.Id), $"the id round trip: {m.Id}");
        Assert(Resolve("local:nope.gguf", locals) is null && Resolve("nope", locals) is null && Resolve(@"local:D:\m\MOSS-Image-SFT-Q8_0.gguf", locals) is null, "unknown ids");
        Assert(ParseModels(ModelsJson(models)).SequenceEqual(models), "the models JSON round trip");
        Assert(ModelsJson([models[0]]) == """{"models":[{"id":"z-image-turbo","name":"Z-Image Turbo","note":"Recommended · runs on 4-6 GB VRAM","negative":false,"multiple":16,"steps":8,"cfg":1,"sampler":""}]}"""
            .Replace("·", "\\u00B7"), "the models JSON shape");
        Assert(PicturesJson([new("AA==", 7)]) == """{"images":[{"png":"AA==","seed":7}]}""" && ParsePictures(PicturesJson([new("AA==", 7)])) is [{ Png: "AA==", Seed: 7 }], "the pictures JSON");
        Assert(Error(409, "busy").Json == """{"error":"busy"}""", "the error JSON");
        Assert(ErrorText(401, "").Contains("new connection code") && ErrorText(409, "").Contains("busy") && ErrorText(500, """{"error":"sd-cli failed"}""") == "The primary PC: sd-cli failed" &&
            ErrorText(502, "<html>") == "The primary PC returned 502", "error texts");
        var reply = Encoding.ASCII.GetString(Bytes(Error(401, "x")));
        Assert(reply.StartsWith("HTTP/1.1 401 Unauthorized\r\n") && reply.Contains("Connection: close\r\n") && reply.EndsWith("\r\n\r\n{\"error\":\"x\"}") &&
            reply.Contains($"Content-Length: {"{\"error\":\"x\"}".Length}\r\n"), "the response bytes");

        var run = Run(new() { Prompt = " a fox ", InitImage = @"C:\secret.png", LocalModel = @"C:\other.gguf", RemoteModel = "x/y", SharedModel = "z", Batch = 9, Steps = 0, Width = 99999, Sampler = "nope", Seed = -1 }, moss, "");
        Assert(run is { RecipeId: "moss-image-sft", LocalModel: "", InitImage: "", RemoteModel: "", SharedModel: "", Prompt: "a fox", Batch: 4, Steps: 1, Width: 4096, Sampler: "" } && run.Seed >= 0,
            "a request runs only the resolved model, with no start image and clamped numbers");

        // the server over loopback: the key gates it, the handler answers
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        // the fake primary PC: generate knows z-image-turbo only, as ServeImageShareAsync resolves RecipeId
        using var server = new Server(IPAddress.Loopback, 0, upstreamPort, () => "k", (r, _) => Task.FromResult(r.Path == ModelsPath ? new Response(200, ModelsJson([models[0]]))
            : JsonSerializer.Deserialize<ImageSettings>(r.Body) is { RecipeId: "z-image-turbo", Prompt: "a fox" } ? new(200, PicturesJson([new("AA==", 7)]))
            : JsonSerializer.Deserialize<ImageSettings>(r.Body) is { Prompt: "busy" } ? Error(409, "busy") : Error(400, "No such model: nope")));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var url = $"http://127.0.0.1:{server.Port}{ModelsPath}";
        Assert((int)http.GetAsync(url).GetAwaiter().GetResult().StatusCode == 401, "the server refuses a request without the key");
        using var get = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Authorization = new("Bearer", "k") } };
        using var ok = http.SendAsync(get).GetAwaiter().GetResult();
        Assert((int)ok.StatusCode == 200 && ParseModels(ok.Content.ReadAsStringAsync().GetAwaiter().GetResult()) is [{ Id: "z-image-turbo" }], "the server answers with the key");

        // the other PC's client: the list, a generation, and each failure as its message
        static string Thrown(Func<Task> call)
        {
            try { Task.Run(call).GetAwaiter().GetResult(); return ""; } // off the dispatcher: the client's awaits would wait for it
            catch (InvalidOperationException x) { return x.Message; }
        }
        var baseUrl = $"http://127.0.0.1:{server.Port}";
        Assert(Task.Run(() => FetchModelsAsync(http, baseUrl, "k", default)).GetAwaiter().GetResult() is [{ Id: "z-image-turbo", Multiple: 16 }], "the client lists the models");
        Assert(Task.Run(() => GenerateAsync(http, baseUrl, "k", new() { RecipeId = "z-image-turbo", Prompt = "a fox" }, default)).GetAwaiter().GetResult() is [{ Png: "AA==", Seed: 7 }], "the client generates");
        Assert(Thrown(() => GenerateAsync(http, baseUrl, "k", new() { RecipeId = "nope", Prompt = "a fox" }, default)) == "The primary PC: No such model: nope", "an unknown model: ErrorText");
        Assert(Thrown(() => FetchModelsAsync(http, baseUrl, "wrong", default)) == ErrorText(401, ""), "a wrong key: ErrorText");
        Assert(Thrown(() => GenerateAsync(http, baseUrl, "k", new() { RecipeId = "z-image-turbo", Prompt = "busy" }, default)) == ErrorText(409, ""), "a 409 in the body: busy");
        // a generation slower than the heartbeat: its reply starts with the head and spaces at once, and still parses
        var heartbeat = Heartbeat;
        Heartbeat = TimeSpan.FromMilliseconds(40);
        try
        {
            var tick = ProgressTick;
            ProgressTick = TimeSpan.FromMilliseconds(20);
            using var slow = new Server(IPAddress.Loopback, 0, upstreamPort, () => "k", async (_, ct) => { await Task.Delay(300, ct); return new Response(200, PicturesJson([new("AA==", 9)])); })
                { Progress = () => ProgressJson(0.5, "Step 1/2") };
            using var raw = new TcpClient();
            raw.Connect(IPAddress.Loopback, slow.Port);
            var wire = raw.GetStream();
            wire.Write(Encoding.ASCII.GetBytes($"POST {GeneratePath} HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer k\r\nContent-Length: 2\r\n\r\n{{}}"));
            var started = System.Diagnostics.Stopwatch.StartNew();
            var head = new byte[64];
            var read = wire.Read(head);
            Assert(started.ElapsedMilliseconds < 250 && Encoding.ASCII.GetString(head, 0, read).StartsWith("HTTP/1.1 200"), "a slow generation's head goes out before the outcome");
            var reported = new List<ProgressLine>();
            Assert(Task.Run(() => GenerateAsync(http, $"http://127.0.0.1:{slow.Port}", "k", new() { Prompt = "x" }, default, new Sync<ProgressLine>(reported.Add))).GetAwaiter().GetResult() is [{ Seed: 9 }],
                "a slow generation's reply parses after its progress lines");
            Assert(reported.Count > 0 && reported[0] == new ProgressLine(0.5, "Step 1/2"), "the primary PC's progress reaches the other PC");
            // the other PC cancels (closes the connection): the generation's token is cancelled, so the primary PC is free again
            var stopped = new TaskCompletionSource();
            using var endless = new Server(IPAddress.Loopback, 0, upstreamPort, () => "k", async (_, ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { stopped.TrySetResult(); }
                return Error(500, "unreachable");
            });
            using (var quitter = new TcpClient())
            {
                quitter.Connect(IPAddress.Loopback, endless.Port);
                var q = quitter.GetStream();
                q.Write(Encoding.ASCII.GetBytes($"POST {GeneratePath} HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer k\r\nContent-Length: 2\r\n\r\n{{}}"));
                q.Read(new byte[64]);
            }
            Assert(stopped.Task.Wait(TimeSpan.FromSeconds(3)), "the other PC hanging up cancels the generation");
            ProgressTick = tick;
        }
        finally { Heartbeat = heartbeat; }
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        Assert(Thrown(() => FetchModelsAsync(http, $"http://127.0.0.1:{closedPort}", "k", default)) == CannotReach, "nothing listening: CannotReach");

        // /v1/ through the proxy: 401 without the key (llama-server never asked); with it the request arrives unchanged but for
        // Connection: close, and the reply streams: the client reads the first chunk before the fake llama-server sends the second.
        Assert((int)http.GetAsync($"http://127.0.0.1:{server.Port}/v1/models").GetAwaiter().GetResult().StatusCode == 401, "the proxy refuses a request without the key");
        const string sent = "{\"stream\":true}";
        var gotFirst = new TaskCompletionSource();
        var fake = Task.Run(async () =>
        {
            using var llama = await upstream.AcceptTcpClientAsync();
            var s = llama.GetStream();
            var head = new MemoryStream();
            var buffer = new byte[4096];
            while (!Encoding.Latin1.GetString(head.ToArray()).EndsWith(sent))
                head.Write(buffer, 0, await s.ReadAsync(buffer) is > 0 and var n ? n : throw new IOException("the request ended early"));
            await s.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\ndata: one\n\n"u8.ToArray());
            var streamed = await Task.WhenAny(gotFirst.Task, Task.Delay(5000)) == gotFirst.Task;
            await s.WriteAsync("data: two\n\n"u8.ToArray());
            return (Encoding.Latin1.GetString(head.ToArray()), streamed);
        });
        using var chat = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/v1/chat/completions?x=1")
            { Headers = { Authorization = new("Bearer", "k"), Connection = { "keep-alive" } }, Content = new StringContent(sent) };
        using var answer = http.SendAsync(chat, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        using var body = answer.Content.ReadAsStream();
        var got = "";
        var chunk = new byte[4096];
        for (int n; !got.Contains("two") && (n = body.Read(chunk)) > 0;)
        {
            got += Encoding.UTF8.GetString(chunk, 0, n);
            if (got.Contains("data: one"))
                gotFirst.TrySetResult();
        }
        var (forwarded, streamedFirst) = fake.GetAwaiter().GetResult();
        Assert((int)answer.StatusCode == 200 && got == "data: one\n\ndata: two\n\n", $"the proxied reply: {got}");
        Assert(forwarded.StartsWith("POST /v1/chat/completions?x=1 HTTP/1.1\r\n") && forwarded.Contains("\r\nAuthorization: Bearer k\r\n") &&
            forwarded.Contains("\r\nConnection: close\r\n") && !forwarded.Contains("keep-alive", StringComparison.OrdinalIgnoreCase) && forwarded.EndsWith("\r\n\r\n" + sent),
            $"the forwarded request: {forwarded}");
        Assert(streamedFirst, "the proxy streams: the first chunk reached the client before the second was sent");
    }
}
