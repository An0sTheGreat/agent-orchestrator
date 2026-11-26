using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator;

// The tailnet phone app's server: http://127.0.0.1:<port>/ only, published on the tailnet by `tailscale serve`, which adds
// Tailscale-User-Login to every request it proxies. Every path, the page and its files included, needs that header naming
// the PC's own Tailscale user (`ownerLogin`); anything else gets 403. "/" and the embedded web\ files are the app;
// /api/* goes to `api` (MainWindow.WebApp.cs), which answers (status, JSON body). Apart from RemoteServer (Cloudflare).
internal sealed class WebServer
{
    public record Response(int Status, string Type, byte[] Body);

    // What the API answers an events route with (it goes through every gate like any API answer): `Hello` first, then `Events`
    // until they end, the phone goes away or the server stops; `Life` is cancelled then (it ends the subscription).
    public sealed record EventStream(string Hello, System.Threading.Channels.ChannelReader<string> Events, CancellationTokenSource Life)
        : Response(200, "text/event-stream; charset=utf-8", []);

    // `fileName`: the request's X-File-Name header (/api/upload's file, URL-encoded), null without one.
    public delegate Task<(int Status, object Body)> WebApi(string method, string path, IReadOnlyDictionary<string, string> query, byte[] body, string contentType, string? fileName);

    private const int MaxBody = 25 << 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<string?> _owner;
    private readonly Func<IReadOnlyList<string>?> _origins;
    private readonly WebApi _api;
    private HttpListener? _listener;
    private readonly CancellationTokenSource _stopping = new(); // ends the open event streams

    internal TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(15); // an event stream's comment line, when nothing else was sent

    // --webapp-dev only (dev, never the app's own server): the login a request without Tailscale-User-Login is taken to have;
    // only this PC's connections reach Handle at all.
    internal string? DevLogin { get; init; }

    // `ownerLogin`: the PC's Tailscale login; `ownerOrigins`: https://<DnsName> of this PC and the owner's other PCs
    // (Tailscale.State.OwnerOrigins), which may call the API and whose pictures the page may show. Both are asked on every
    // request: null while Tailscale is not known.
    public WebServer(Func<string?> ownerLogin, Func<IReadOnlyList<string>?> ownerOrigins, WebApi api) =>
        (_owner, _origins, _api) = (ownerLogin, ownerOrigins, api);

    public int? Port { get; private set; }

    // The first free port from `port` (20 tries). False when none is.
    public bool Start(int port)
    {
        for (var p = port; p < port + 20 && p <= 65535; p++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{p}/");
            try { listener.Start(); }
            catch (HttpListenerException)
            {
                listener.Close();
                continue;
            }
            (_listener, Port) = (listener, p);
            _ = Task.Run(() => Loop(listener));
            return true;
        }
        return false;
    }

    public void Stop()
    {
        _stopping.Cancel();
        _listener?.Close();
        (_listener, Port) = (null, null);
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
            var path = request.Url?.AbsolutePath ?? "/";
            var origins = _origins() ?? [];
            response.Headers["Content-Security-Policy"] = Csp(origins);
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            if (path.StartsWith("/api/"))
                response.Headers["Cache-Control"] = "no-store";
            else if (path == "/sw.js")
                response.Headers["Cache-Control"] = "no-cache"; // the browser revalidates the service worker on every load, so updates reach phones
            if (AllowedOrigin(request.Headers["Origin"], origins) is { } origin)
            {
                response.Headers["Access-Control-Allow-Origin"] = origin;
                response.Headers["Vary"] = "Origin";
                if (request.HttpMethod == "OPTIONS")
                {
                    response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE";
                    response.Headers["Access-Control-Allow-Headers"] = AllowHeaders;
                    response.Headers["Access-Control-Max-Age"] = "600";
                }
            }
            // http.sys routes on the Host header, so a LAN device naming 127.0.0.1 could reach the port and forge the
            // header: only this PC's connections (tailscaled's Serve proxy) count.
            if (!RemoteServer.IsAllowedClient(request.RemoteEndPoint.Address))
            {
                response.StatusCode = 403;
                return;
            }
            var query = request.QueryString.AllKeys.OfType<string>().ToDictionary(k => k, k => request.QueryString[k] ?? "");
            // identity first: a non-owner's body is never read (Handle answers it 403 without one)
            var login = request.Headers["Tailscale-User-Login"] ?? DevLogin;
            var body = path.StartsWith("/api/") && IsOwner(login, _owner()) ? await ReadBody(request) : [];
            var r = await Handle(request.HttpMethod, path, login, query, body, request.ContentType ?? "",
                request.Headers["Origin"], request.Headers["Sec-Fetch-Site"], request.Headers["Host"], request.Headers["X-File-Name"], request.Headers["Accept"]);
            if (r is EventStream stream)
            {
                using (stream.Life)
                    try { await StreamEvents(response, stream, KeepAlive, _stopping.Token); }
                    finally { stream.Life.Cancel(); } // ends the subscription
                return;
            }
            response.StatusCode = r.Status;
            response.ContentType = r.Type;
            response.ContentLength64 = r.Body.Length;
            await response.OutputStream.WriteAsync(r.Body);
        }
        catch (Exception) { } // the phone went away, or the server stopped: nothing to answer
        finally
        {
            try { response.Close(); } catch (Exception) { }
        }
    }

    // Server-Sent Events: the hello, then each event as it comes, one write and flush per frame (with chunked encoding, one
    // chunk each), and `: keepalive` after `keepAlive` without one; until the events end, the phone goes away (a write fails)
    // or `ct` is cancelled.
    private static async Task StreamEvents(HttpListenerResponse response, EventStream stream, TimeSpan keepAlive, CancellationToken ct)
    {
        response.StatusCode = 200;
        response.ContentType = stream.Type;
        response.SendChunked = true;
        async Task Send(byte[] frame)
        {
            await response.OutputStream.WriteAsync(frame, ct);
            await response.OutputStream.FlushAsync(ct);
        }
        await Send(SseFrame(stream.Hello));
        while (true)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(keepAlive);
            try
            {
                if (!await stream.Events.WaitToReadAsync(wait.Token))
                    return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await Send(KeepAliveFrame);
                continue;
            }
            while (stream.Events.TryRead(out var json))
                await Send(SseFrame(json));
        }
    }

    private static readonly byte[] KeepAliveFrame = Encoding.UTF8.GetBytes(": keepalive\n\n");

    // The request's body; null when it is over MaxBody.
    private static async Task<byte[]?> ReadBody(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxBody)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > MaxBody)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // `login`: the request's Tailscale-User-Login; `body`: null when it was over the limit.
    // `origin`, `fetchSite`, `host`, `accept`: the request's Origin, Sec-Fetch-Site, Host and Accept headers.
    public async Task<Response> Handle(string method, string path, string? login, IReadOnlyDictionary<string, string> query, byte[]? body, string contentType,
        string? origin = null, string? fetchSite = null, string? host = null, string? fileName = null, string? accept = null)
    {
        // A browser opening the page while signed out of Tailscale (or as someone else) gets signedout.html's instructions
        // with the 403: a static page, no data; anything else gets the plain 403, whose SignedOut tells the page to reload (and so
        // get signedout.html), which no other 403 says.
        if (!IsOwner(login, _owner()))
            return method == "GET" && path == "/" && accept?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true
                ? Embedded(403, Asset("/signedout.html")!.Value) : Json(403, new { error = "SignedOut" });
        if (method == "OPTIONS")
            return new Response(204, "text/plain", []);
        if (path.StartsWith("/api/"))
        {
            // Serve adds the owner's login to whatever the owner's device sends, so a page on another site could call the
            // API as the owner (CSRF): only this page and the user's other PCs may; tools that send no Origin still can.
            // --webapp-dev's page is plain http on 127.0.0.1, so there (only) its own http origin is this page too.
            var known = origin is not null && (string.Equals(origin, "https://" + host, StringComparison.OrdinalIgnoreCase) || AllowedOrigin(origin, _origins() ?? []) is not null ||
                DevLogin is not null && string.Equals(origin, "http://" + host, StringComparison.OrdinalIgnoreCase));
            if (!known && (fetchSite == "cross-site" || origin is not null && method is not ("GET" or "HEAD")))
                return Json(403, new { error = "Cross-site request refused" });
            if (body is null)
                return Json(413, new { error = path == "/api/upload" ? "The file is over 25 MB" : "Too large (25 MB at most)" });
            try
            {
                var (status, result) = await _api(method, path, query, body, contentType, fileName);
                return result as Response ?? Json(status, result); // a Response is sent as it is (/api/file's bytes)
            }
            catch (Exception e) { return Json(500, new { error = e.Message }); }
        }
        if (Asset(path) is not var (resource, type))
            return Json(404, new { error = "Not found" });
        if (method != "GET")
            return Json(405, new { error = "Method not allowed" });
        return Embedded(200, (resource, type));
    }

    private static Response Embedded(int status, (string Resource, string Type) asset)
    {
        using var stream = typeof(WebServer).Assembly.GetManifestResourceStream(asset.Resource)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return new Response(status, asset.Type, copy.ToArray());
    }

    private static Response Json(int status, object body) => new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions));

    public static bool IsOwner(string? headerLogin, string? owner) =>
        !string.IsNullOrEmpty(headerLogin) && !string.IsNullOrEmpty(owner) && string.Equals(headerLogin, owner, StringComparison.OrdinalIgnoreCase);

    // `origin` when it is one of the owner's PCs (`origins`: https://<DnsName>, the default port), else null: another
    // account's device or a shared-in node on the same tailnet is a foreign site like any other.
    public static string? AllowedOrigin(string? origin, IReadOnlyList<string> origins) =>
        origin is not null && origins.Contains(origin, StringComparer.OrdinalIgnoreCase) ? origin : null;

    // The page may call (connect-src) and show pictures from (img-src: another PC's /api/file) the owner's PCs, no other site.
    internal static string Csp(IReadOnlyList<string> origins)
    {
        var sources = "'self' " + string.Join(' ', origins);
        return "default-src 'self'; style-src-attr 'unsafe-inline'" + (origins.Count == 0 ? "" : $"; connect-src {sources}; img-src {sources}");
    }

    // The embedded file for a request path ("/" is index.html) and its content type; null for anything not embedded.
    public static (string Resource, string Type)? Asset(string path)
    {
        if (!path.StartsWith('/') || path.Contains("..") || path.Contains('\\'))
            return null;
        var resource = "web" + (path == "/" ? "/index.html" : path);
        return typeof(WebServer).Assembly.GetManifestResourceInfo(resource) is null ? null : (resource, ContentType(resource));
    }

    internal static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".ttf" => "font/ttf",
        ".webmanifest" => "application/manifest+json",
        ".png" => "image/png",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    // The app shell's identity: the first 12 hex digits of a SHA-256 over every embedded web/ file's name and bytes (in name
    // order). /api/hello sends it as `shell`; main.js registers sw.js?v=<shell>, whose cache is named by it, so every publish
    // that changes a web/ file installs a new worker and cache (the csproj Version does not change with a publish).
    internal static string ShellHash(IEnumerable<(string Name, byte[] Bytes)> files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, bytes) in files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes($"{name}\n{bytes.Length}\n"));
            sha.AppendData(bytes);
        }
        return Convert.ToHexString(sha.GetHashAndReset())[..12].ToLowerInvariant();
    }

    public static readonly string Shell = ShellHash(typeof(WebServer).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("web/", StringComparison.Ordinal)).Select(n => (n, Embedded(200, (n, "")).Body)));

    // The request headers another of the owner's PCs' pages may send: the upload's file name rides in X-File-Name.
    internal const string AllowHeaders = "Content-Type, X-File-Name";

    public static byte[] SseFrame(string json) => Encoding.UTF8.GetBytes($"data: {json}\n\n");
    // --self-test: the identity gate, the owner's PCs' origins, the CSP, asset lookup, routing and the SSE frame.
    internal static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException($"Web server self-test: {what}");
        }
        Check(IsOwner("me@x.com", "ME@x.com") && !IsOwner(null, "me@x.com") && !IsOwner("me@x.com", null) && !IsOwner("a@x.com", "b@x.com") &&
            !IsOwner("", ""), "owner check");
        string[] mine = ["https://pc.tail1234.ts.net", "https://laptop.tail1234.ts.net"]; // this PC and the owner's laptop
        Check(AllowedOrigin("https://laptop.tail1234.ts.net", mine) == "https://laptop.tail1234.ts.net" &&
            AllowedOrigin("https://theirs.tail1234.ts.net", mine) is null && // another account's device (or a shared-in node) on the tailnet
            AllowedOrigin("http://laptop.tail1234.ts.net", mine) is null && AllowedOrigin("https://evil.com", mine) is null &&
            AllowedOrigin("https://laptop.tail1234.ts.net.evil.com", mine) is null && AllowedOrigin("https://a.laptop.tail1234.ts.net", mine) is null &&
            AllowedOrigin("https://laptop.tail1234.ts.net:8443", mine) is null && AllowedOrigin("https://laptop.tail1234.ts.net", []) is null &&
            AllowedOrigin(null, mine) is null, "the owner's PCs' origins only");
        const string sources = "'self' https://pc.tail1234.ts.net https://laptop.tail1234.ts.net";
        Check(Csp(mine).Contains("default-src 'self'") && Csp(mine).Contains("; connect-src " + sources + ";") && Csp(mine).EndsWith("; img-src " + sources) &&
            !Csp(mine).Contains('*') && Csp(mine).Contains("style-src-attr 'unsafe-inline'") && Csp([]).Contains("style-src-attr 'unsafe-inline'") &&
            !Csp([]).Contains("connect-src") && !Csp([]).Contains("img-src"), "content security policy: " + Csp(mine));
        Check(Asset("/") is ("web/index.html", var html) && html.StartsWith("text/html") && Asset("/styles.css") is (_, var css) && css.StartsWith("text/css") &&
            Asset("/assets/fonts/Inter-Regular.ttf") is (_, "font/ttf") && Asset("/manifest.webmanifest") is (_, "application/manifest+json") &&
            Asset("/icons/icon-192.png") is (_, "image/png") && ContentType("x.json") == "application/json" &&
            Asset("/sw.js") is (_, var sw) && sw.StartsWith("text/javascript"), "assets");
        Check(AllowHeaders.Split(", ").Order().SequenceEqual(["Content-Type", "X-File-Name"]), "preflight headers: " + AllowHeaders);
        (string, byte[])[] files = [("web/a.js", [1, 2]), ("web/b.css", [3])], edited = [("web/a.js", [1, 2]), ("web/b.css", [4])];
        Check(ShellHash(files) == ShellHash(files.Reverse()) && ShellHash(files) != ShellHash(edited) &&
            ShellHash([("web/a", [1])]) != ShellHash([("web/a\u0001", [])]) && ShellHash(files).Length == 12 &&
            Shell.Length == 12 && Shell != typeof(WebServer).Assembly.GetName().Version?.ToString(3), "shell hash: " + Shell);
        Check(Asset("/../remote.html") is null && Asset("/web/index.html") is null && Asset("/x\\y") is null && Asset("/missing.js") is null &&
            Asset("") is null, "refused assets");
        Check(SseFrame("{\"e\":\"delta\"}").SequenceEqual(Encoding.UTF8.GetBytes("data: {\"e\":\"delta\"}\n\n")), "SSE frame");

        var server = new WebServer(() => "me@x.com", () => mine, (method, path, query, body, type, _) =>
            Task.FromResult((200, (object)new { method, path, q = query.GetValueOrDefault("q"), length = body.Length, type })));
        Response Call(string method, string path, string? login = "Me@x.com", byte[]? body = null, string? origin = null, string? site = null) =>
            server.Handle(method, path, login, new Dictionary<string, string> { ["q"] = "1" }, body ?? [], "application/json", origin, site, "pc.tail1234.ts.net")
                .GetAwaiter().GetResult();
        Check(Call("POST", "/api/send", origin: "https://evil.com", site: "cross-site").Status == 403 && Call("POST", "/api/send", origin: "https://evil.com").Status == 403 &&
            Call("DELETE", "/api/x", origin: "https://evil.com").Status == 403 && Call("GET", "/api/x", origin: "https://evil.com", site: "cross-site").Status == 403 &&
            Call("GET", "/api/x", site: "cross-site").Status == 403 && Call("POST", "/api/send", origin: "https://theirs.tail1234.ts.net", site: "same-site").Status == 403,
            "cross-site API calls (another account's device on the tailnet too): 403");
        Check(Call("POST", "/api/send", origin: "https://pc.tail1234.ts.net", site: "same-origin").Status == 200 &&
            Call("POST", "/api/send", origin: "https://laptop.tail1234.ts.net", site: "same-site").Status == 200 && Call("POST", "/api/send").Status == 200,
            "same origin, another tailnet PC and no Origin reach the API");
        var noSuffix = new WebServer(() => "me@x.com", () => null, (_, _, _, _, _, _) => Task.FromResult((200, (object)new { })));
        Check(noSuffix.Handle("POST", "/api/x", "me@x.com", new Dictionary<string, string>(), [], "", "https://PC.tail1234.ts.net", "same-origin", "pc.tail1234.ts.net")
            .GetAwaiter().GetResult().Status == 200, "same origin by the Host header, the owner's PCs unknown");
        Check(Call("GET", "/", null).Status == 403 && Call("GET", "/", "other@x.com").Status == 403 && Call("GET", "/api/conversations", null).Status == 403 &&
            Call("OPTIONS", "/api/hello", null).Status == 403 && Call("GET", "/missing.js", null).Status == 403, "no owner header: 403 everywhere");
        const string browser = "text/html,application/xhtml+xml,*/*;q=0.8";
        Response Page(string path, string? login) => server.Handle("GET", path, login, new Dictionary<string, string>(), [], "", accept: browser).GetAwaiter().GetResult();
        foreach (var login in new[] { null, "other@x.com" })
            Check(Page("/", login) is { Status: 403 } signedOut && signedOut.Type.StartsWith("text/html") &&
                Encoding.UTF8.GetString(signedOut.Body).Contains("<title>Signed out · Agent Orchestrator</title>") &&
                Page("/api/hello", login) is { Status: 403 } refused && refused.Type.StartsWith("application/json") && Page("/styles.css", login).Type.StartsWith("application/json"),
                "signed out: the page GET gets signedout.html with its 403, anything else the plain 403");
        // The page reloads (to get signedout.html) on this body only, so the other 403s must not send it.
        Check(Encoding.UTF8.GetString(Call("GET", "/api/hello", null).Body) == """{"error":"SignedOut"}""" &&
            !Encoding.UTF8.GetString(Call("POST", "/api/send", origin: "https://evil.com").Body).Contains("SignedOut"), "signed out: the API's 403 says SignedOut, a refused origin does not");
        // --webapp-dev's page is http://127.0.0.1:<port>: its own origin reaches the API there, never on the app's server.
        var dev = new WebServer(() => "me@x.com", () => null, (_, _, _, _, _, _) => Task.FromResult((200, (object)new { }))) { DevLogin = "me@x.com" };
        Response Local(WebServer s, string origin) => s.Handle("POST", "/api/x", "me@x.com", new Dictionary<string, string>(), [], "", origin, "same-origin", "127.0.0.1:48741")
            .GetAwaiter().GetResult();
        Check(Local(dev, "http://127.0.0.1:48741").Status == 200 && Local(dev, "http://127.0.0.1:9999").Status == 403 && Local(dev, "http://evil.com").Status == 403 &&
            Local(noSuffix, "http://127.0.0.1:48741").Status == 403, "the dev page's own http origin reaches the API under --webapp-dev only");
        Check(Call("GET", "/") is { Status: 200 } page && page.Type.StartsWith("text/html") && page.Body.Length > 1000, "the page");
        var api = Call("POST", "/api/send", body: [1, 2, 3]);
        Check(api.Status == 200 && api.Type.StartsWith("application/json") &&
            Encoding.UTF8.GetString(api.Body) == """{"method":"POST","path":"/api/send","q":"1","length":3,"type":"application/json"}""", "the API: " + Encoding.UTF8.GetString(api.Body));
        Check(server.Handle("POST", "/api/send", "me@x.com", new Dictionary<string, string>(), null, "").GetAwaiter().GetResult().Status == 413, "too large: 413");
        Check(Call("OPTIONS", "/api/hello").Status == 204 && Call("GET", "/missing.js").Status == 404 && Call("POST", "/").Status == 405, "routing");
        var failing = new WebServer(() => "me@x.com", () => null, (_, _, _, _, _, _) => throw new InvalidOperationException("boom"));
        Check(failing.Handle("GET", "/api/x", "me@x.com", new Dictionary<string, string>(), [], "").GetAwaiter().GetResult() is { Status: 500 } boom &&
            Encoding.UTF8.GetString(boom.Body).Contains("boom"), "an API exception: 500");
    }
}
