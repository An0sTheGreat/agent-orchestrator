using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator;

// The local OpenAI-compatible API: http://127.0.0.1:<port>/ only, for this PC's own programs. Every request passes the gate
// (a loopback caller, Host 127.0.0.1:<port> or localhost:<port>, then "Authorization: Bearer <key>") before its body is read.
internal static class LocalApi
{
    internal const int DefaultPort = 8830;
    internal const string KeyName = "AO_LOCAL_API_KEY";
    internal const int MaxBody = 25 << 20;

    // "Bearer <key>" only (the scheme as written), compared in constant time; never with an empty key.
    internal static bool KeyMatches(string? authorization, string key) =>
        key.Length > 0 && authorization is not null && authorization.StartsWith("Bearer ", StringComparison.Ordinal) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization[7..].Trim()), Encoding.UTF8.GetBytes(key));

    // http.sys hands this listener any Host (its prefix is an IP), so a page elsewhere could rebind its name to 127.0.0.1.
    internal static bool HostAllowed(string? host, int port) =>
        string.Equals(host, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) || string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase);

    // OpenAI's error shape.
    internal static byte[] Error(string message, string type = "invalid_request_error") =>
        JsonSerializer.SerializeToUtf8Bytes(new { error = new { message, type } });

    // The worker a request's model names: its id first, then its title, case-insensitive; null when none.
    internal static string? ResolveWorker(string? model, IEnumerable<(string Role, string Title)> workers)
    {
        var list = workers.ToList();
        return list.FirstOrDefault(w => string.Equals(w.Role, model, StringComparison.OrdinalIgnoreCase)).Role
            ?? list.FirstOrDefault(w => string.Equals(w.Title, model, StringComparison.OrdinalIgnoreCase)).Role;
    }

    // The worker's request defaults, the app's top-level fields over them, and the worker's model.
    internal static JsonObject MergeBody(JsonObject defaults, JsonObject app, string model)
    {
        var body = defaults.DeepClone().AsObject();
        foreach (var (name, value) in app)
            body[name] = value?.DeepClone();
        body["model"] = model;
        return body;
    }

    internal static string Scrub(string text, string? secret) => string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "***");

    // "<w>x<h>", each 64..4096; null otherwise.
    internal static (int W, int H)? ParseSize(string? size) =>
        size?.Split('x') is [var w, var h] && int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var width) &&
        int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out var height) && width is >= 64 and <= 4096 && height is >= 64 and <= 4096
            ? (width, height) : null;

    // The worker's timeout_seconds in config.json's text (a role with none of its own falls back to the planner's, as the engine's
    // built-in roles do); 600 when unset or unreadable.
    internal static int TimeoutSeconds(string configJson, string role)
    {
        try
        {
            var config = JsonNode.Parse(configJson);
            var worker = config?["roles"]?[role] ?? config?["custom_workers"]?.AsArray().FirstOrDefault(w => w?["id"]?.GetValue<string>() == role) ?? config?["roles"]?["planner"];
            return worker?["timeout_seconds"]?.GetValue<int>() is > 0 and var seconds ? seconds : 600;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return 600; }
    }

    // Where a request goes: the engine's `--api-target` line. KeyEnv names the key's variable ("" for none), never its value.
    internal sealed record Target(string BaseUrl, string Model, string KeyEnv, JsonObject Defaults);

    // null when the line is not the target JSON (the engine's "ERROR: ..." among others).
    internal static Target? ParseTarget(string stdoutLastLine)
    {
        try
        {
            return JsonNode.Parse(stdoutLastLine) is JsonObject o && o["base_url"] is JsonValue url && url.TryGetValue<string>(out var baseUrl) &&
                o["model"] is JsonValue m && m.TryGetValue<string>(out var model) && o["key_env"] is JsonValue k && k.TryGetValue<string>(out var keyEnv) &&
                o["defaults"] is JsonObject defaults
                ? new Target(baseUrl, model, keyEnv, defaults.DeepClone().AsObject()) : null;
        }
        catch (JsonException) { return null; }
    }

    // The listener: `route` sees only requests that passed the gate, and reads their body itself (up to MaxBody).
    internal sealed class Server(int port, Func<string?> key, Func<HttpListenerContext, Task> route) : IDisposable
    {
        private HttpListener? _listener;

        // False when the port is taken.
        public bool Start()
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); }
            catch (HttpListenerException)
            {
                listener.Close();
                return false;
            }
            _listener = listener;
            _ = Task.Run(() => Loop(listener));
            return true;
        }

        public void Dispose()
        {
            _listener?.Close();
            _listener = null;
        }

        private async Task Loop(HttpListener listener)
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                _ = Serve(context);
            }
        }

        private async Task Serve(HttpListenerContext context)
        {
            var (request, response) = (context.Request, context.Response);
            try
            {
                // http.sys routes on the Host header, so a LAN device could reach the port: only this PC's connections count.
                if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address) || !HostAllowed(request.Headers["Host"], port))
                    await Write(response, 403, Error("Only this PC's programs may call the local API"));
                else if (!KeyMatches(request.Headers["Authorization"], key() ?? ""))
                    await Write(response, 401, Error("Wrong or missing API key"));
                else
                    await route(context);
            }
            catch (Exception)
            {
                // the route failed before answering (no detail: it may name a provider's key); or the caller went away
                try { await Write(response, 500, Error("The local API failed", "server_error")); } catch (Exception) { }
            }
            finally
            {
                try { response.Close(); } catch (Exception) { }
            }
        }

        private static async Task Write(HttpListenerResponse response, int status, byte[] body)
        {
            response.StatusCode = status;
            response.ContentType = "application/json";
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body);
        }
    }

    // --self-test: the key, host and worker checks, the body merge, scrubbing, sizes, the engine's target and the live gate.
    internal static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException($"Local API self-test: {what}");
        }
        Check(KeyMatches("Bearer k1", "k1") && !KeyMatches("Bearer k1x", "k1") && !KeyMatches("Bearer k", "k1") && !KeyMatches("bearer k1", "k1") &&
            !KeyMatches("k1", "k1") && !KeyMatches(null, "k1") && !KeyMatches("Bearer ", ""), "bearer key");
        Check(HostAllowed("127.0.0.1:8830", 8830) && HostAllowed("localhost:8830", 8830) && !HostAllowed("evil.com:8830", 8830) &&
            !HostAllowed("127.0.0.1:8831", 8830) && !HostAllowed(null, 8830), "host");
        var error = JsonNode.Parse(Error("x"))!;
        Check(error["error"]?["message"]?.GetValue<string>() == "x" && error["error"]?["type"]?.GetValue<string>() == "invalid_request_error", "error shape");
        (string, string)[] workers = [("planner", "planner"), ("w1", "my coder")];
        Check(ResolveWorker("My Coder", workers) == "w1" && ResolveWorker("PLANNER", workers) == "planner" && ResolveWorker("nope", workers) is null &&
            ResolveWorker(null, workers) is null, "worker by id or title");
        var merged = MergeBody(JsonNode.Parse("""{"temperature":0.4,"top_p":0.9}""")!.AsObject(), JsonNode.Parse("""{"temperature":1,"messages":[]}""")!.AsObject(), "gguf");
        Check(merged["temperature"]?.GetValue<double>() == 1 && merged["top_p"]?.GetValue<double>() == 0.9 && merged["model"]?.GetValue<string>() == "gguf" &&
            merged["messages"] is JsonArray { Count: 0 }, "merged body: " + merged.ToJsonString());
        Check(Scrub("bad key sk-1 sk-1", "sk-1") == "bad key *** ***" && Scrub("x", "") == "x" && Scrub("x", null) == "x", "scrub");
        Check(TimeoutSeconds("""{"roles":{"planner":{"timeout_seconds":90},"w":{"timeout_seconds":30}},"custom_workers":[{"id":"c","timeout_seconds":45}]}""", "w") == 30 &&
            TimeoutSeconds("""{"roles":{"planner":{"timeout_seconds":90}},"custom_workers":[{"id":"c","timeout_seconds":45}]}""", "c") == 45 &&
            TimeoutSeconds("""{"roles":{"planner":{"timeout_seconds":90}}}""", "reviewer") == 90 && TimeoutSeconds("{}", "w") == 600 &&
            TimeoutSeconds("not json", "w") == 600 && TimeoutSeconds("""{"roles":{"w":{"timeout_seconds":"x"}}}""", "w") == 600, "worker timeout");
        Check(ParseSize("1024x768") == (1024, 768) && ParseSize("10x10") is null && ParseSize("abc") is null && ParseSize("5000x5000") is null &&
            ParseSize(null) is null, "size");
        Check(ParseTarget("""{"base_url":"http://127.0.0.1:8080/v1","model":"gguf","key_env":"OPENAI_API_KEY","defaults":{"temperature":0.4}}""") is
            { BaseUrl: "http://127.0.0.1:8080/v1", Model: "gguf", KeyEnv: "OPENAI_API_KEY" } target && target.Defaults["temperature"]?.GetValue<double>() == 0.4 &&
            ParseTarget("ERROR: x") is null && ParseTarget("""{"model":"gguf"}""") is null, "engine target");

        // the server over loopback: the gate answers before the route
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var server = new Server(port, () => "k", async context =>
        {
            context.Response.StatusCode = 200;
            await context.Response.OutputStream.WriteAsync("ok"u8.ToArray());
        });
        Check(server.Start(), "server starts");
        using (var taken = new Server(port, () => "k", _ => Task.CompletedTask))
            Check(!taken.Start(), "a taken port: false");
        using var http = new HttpClient();
        (int Status, string Type, string Body) Call(string? auth, string? host = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/v1/models");
            if (auth is not null)
                request.Headers.TryAddWithoutValidation("Authorization", auth);
            if (host is not null)
                request.Headers.Host = host;
            using var response = http.Send(request);
            return ((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType ?? "", response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        Check(Call(null) is (401, "application/json", var missing) && JsonNode.Parse(missing)?["error"]?["type"]?.GetValue<string>() == "invalid_request_error",
            "no key: 401 with the error JSON");
        Check(Call("Bearer x").Status == 401, "wrong key: 401");
        Check(Call("Bearer k") is (200, _, "ok"), "right key: the route");
        Check(Call("Bearer k", "evil.com") is (403, "application/json", _), "foreign host: 403");
    }
}
