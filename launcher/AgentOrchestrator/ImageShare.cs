using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator;

// The image half of sharing this PC's models (ModelShare.cs, docs/superpowers/specs/2026-11-19-share-models-design.md): a
// minimal HTTP/1.1 server on ModelShare.ImagePort (a raw TcpListener: HttpListener on a non-loopback address needs a URL ACL,
// which needs admin). GET /images/models lists the recipes and the local image models; POST /images/generate runs one
// (MainWindow.Share.cs). Every request needs the share key as a bearer token; one request per connection, then it closes.
internal static class ImageShare
{
    public const int MaxBody = 1 << 20, MaxHead = 16 << 10;
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    public const string ModelsPath = "/images/models", GeneratePath = "/images/generate";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);
    public sealed record Response(int Status, string Json);
    public sealed class HttpError(int status, string message) : Exception(message) { public int Status => status; }

    // What the other PC's picker lists; Id goes back as ImageSettings.RecipeId.
    public sealed record Model(string Id, string Name, string Note, bool Negative, int Multiple, int Steps, double Cfg, string Sampler);
    public sealed record Picture(string Png, long Seed);
    private sealed record ModelList(List<Model> Models);
    private sealed record PictureList(List<Picture> Images);
    private sealed record ErrorReply(string Error);

    // The request line, the headers and a Content-Length body (chunked bodies are not read). HttpError: 400, 413 or 431.
    public static async Task<Request> ReadRequestAsync(Stream stream, CancellationToken ct)
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
        var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
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
        long length = 0;
        if (headers.TryGetValue("Content-Length", out var text) && !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out length))
            throw new HttpError(400, "Bad Content-Length");
        if (length > MaxBody)
            throw new HttpError(413, $"The body is over {MaxBody} bytes");
        var body = new byte[length];
        var have = (int)Math.Min(filled - end - 4, length);
        buffer.AsSpan(end + 4, have).CopyTo(body);
        try { await stream.ReadExactlyAsync(body.AsMemory(have), ct); }
        catch (EndOfStreamException) { throw new HttpError(400, "The body is shorter than its Content-Length"); }
        return new(first[0], first[1].Split('?')[0], headers, body);
    }

    // 401 without the key (checked first, so the paths stay hidden), 404 for an unknown path, 405 for the wrong method; null = go on.
    public static Response? Check(Request request, string key)
    {
        var given = request.Headers.GetValueOrDefault("Authorization") is { } auth && auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..].Trim() : "";
        if (key.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key)))
            return Error(401, "Wrong or missing share key");
        var method = request.Path switch { ModelsPath => "GET", GeneratePath => "POST", _ => null };
        return method is null ? Error(404, $"No such path: {request.Path}") : request.Method == method ? null : Error(405, $"{request.Path} takes {method}");
    }

    public static Response Error(int status, string message) => new(status, JsonSerializer.Serialize(new ErrorReply(message), Web));

    public static byte[] Bytes(Response response)
    {
        var reason = response.Status switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict",
            413 => "Content Too Large", 431 => "Request Header Fields Too Large", 503 => "Service Unavailable", _ => "Internal Server Error",
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
        Width = Math.Clamp(s.Width, 64, 2048), Height = Math.Clamp(s.Height, 64, 2048), Steps = Math.Clamp(s.Steps, 1, 60), Cfg = Math.Clamp(s.Cfg, 0, 20),
        Seed = s.Seed >= 0 ? s.Seed : Random.Shared.Next(), Sampler = ImageGen.Samplers.Contains(s.Sampler) ? s.Sampler : "", Batch = Math.Clamp(s.Batch, 1, 4), LowVram = s.LowVram,
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

    // ponytail: a task per connection, no cap; the server is on the Tailscale address only, add a cap if that ever widens.
    public sealed class Server : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; }

        public Server(IPAddress ip, int port, Func<string> key, Func<Request, CancellationToken, Task<Response>> handle)
        {
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
                Response response;
                try
                {
                    Request request;
                    using (var read = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                    {
                        read.CancelAfter(ReadTimeout); // a stalled client is dropped
                        request = await ReadRequestAsync(stream, read.Token);
                    }
                    response = Check(request, key()) ?? await handle(request, _stop.Token);
                }
                catch (HttpError x) { response = Error(x.Status, x.Message); }
                catch (Exception x) when (x is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { return; }
                catch (Exception x) { response = Error(500, x.Message); }
                try { await stream.WriteAsync(Bytes(response), _stop.Token); }
                catch (Exception x) when (x is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            }
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
        Assert(run is { RecipeId: "moss-image-sft", LocalModel: "", InitImage: "", RemoteModel: "", SharedModel: "", Prompt: "a fox", Batch: 4, Steps: 1, Width: 2048, Sampler: "" } && run.Seed >= 0,
            "a request runs only the resolved model, with no start image and clamped numbers");

        // the server over loopback: the key gates it, the handler answers
        using var server = new Server(IPAddress.Loopback, 0, () => "k", (r, _) => Task.FromResult(new Response(200, ModelsJson([models[0]]))));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var url = $"http://127.0.0.1:{server.Port}{ModelsPath}";
        Assert((int)http.GetAsync(url).GetAwaiter().GetResult().StatusCode == 401, "the server refuses a request without the key");
        using var get = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Authorization = new("Bearer", "k") } };
        using var ok = http.SendAsync(get).GetAwaiter().GetResult();
        Assert((int)ok.StatusCode == 200 && ParseModels(ok.Content.ReadAsStringAsync().GetAwaiter().GetResult()) is [{ Id: "z-image-turbo" }], "the server answers with the key");
    }
}
