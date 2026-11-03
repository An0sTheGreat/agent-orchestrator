using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator;

// The remote access password: PBKDF2-SHA256 with a random salt, "pbkdf2-sha256$<iterations>$<salt>$<hash>" (base64).
internal static class RemoteAuth
{
    private const int Iterations = 600_000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (stored?.Split('$') is not ["pbkdf2-sha256", var count, var salt, var hash] || !int.TryParse(count, out var iterations) || iterations < 1)
            return false;
        try
        {
            var expected = Convert.FromBase64String(hash);
            return expected.Length == 32 && CryptographicOperations.FixedTimeEquals(
                Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), iterations, HashAlgorithmName.SHA256, 32), expected);
        }
        catch (FormatException) { return false; }
    }
}

// Wrong passwords: after 5 in a row each further try is refused until a wait that doubles (5 s, 10 s, ... up to 5 min);
// a correct one resets it. Global: every request comes from 127.0.0.1 through the tunnel.
internal sealed class RemoteLockout(Func<DateTime> now)
{
    private int _failures;
    private DateTime _until = DateTime.MinValue;

    public TimeSpan Wait() => _until > now() ? _until - now() : TimeSpan.Zero;

    public void Failed()
    {
        if (++_failures >= 5)
            _until = now() + TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, _failures - 5)));
    }

    public void Succeeded() => (_failures, _until) = (0, DateTime.MinValue);
}

// The remote page's server: http://127.0.0.1:<port>/ only (the tunnel is the way in from outside). "/" is the page;
// /api/login signs in (a random session cookie, kept in memory); every other /api call needs the cookie and goes to `api`
// (MainWindow.Remote.cs), which answers (status, JSON body). A new password hash or Stop signs every device out.
internal sealed class RemoteServer
{
    public sealed record Response(int Status, string Type, byte[] Body, string? Cookie = null);

    public delegate Task<(int Status, object Body)> Api(string method, string path, JsonElement body);

    private const int MaxBody = 1 << 20;
    private const string CookieName = "ao_session";
    private static readonly Lazy<byte[]> Page = new(() =>
    {
        using var stream = typeof(RemoteServer).Assembly.GetManifestResourceStream("remote.html")!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    private readonly Func<string?> _hash;
    private readonly Api _api;
    private readonly RemoteLockout _lockout;
    private readonly object _lock = new();
    private readonly HashSet<string> _sessions = new(); // SHA-256 of each token: a lookup leaks nothing about a real token
    private string? _sessionsHash; // the password hash the sessions were signed in with
    private HttpListener? _listener;

    public RemoteServer(Func<string?> hash, Api api, Func<DateTime>? now = null) =>
        (_hash, _api, _lockout) = (hash, api, new RemoteLockout(now ?? (() => DateTime.UtcNow)));

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
        _listener?.Close();
        (_listener, Port) = (null, null);
        lock (_lock)
            _sessions.Clear();
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
        var response = context.Response;
        try
        {
            var body = await ReadBody(context.Request);
            var r = body is null ? Json(413, new { error = "Too large" })
                : await Handle(context.Request.HttpMethod, context.Request.Url?.AbsolutePath ?? "/", context.Request.Headers["Cookie"], body);
            response.StatusCode = r.Status;
            response.ContentType = r.Type;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'";
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            if (r.Cookie is { } cookie)
                response.Headers.Add("Set-Cookie", cookie);
            response.ContentLength64 = r.Body.Length;
            await response.OutputStream.WriteAsync(r.Body);
        }
        catch (Exception) { } // the device went away, or the server stopped: nothing to answer
        finally
        {
            try { response.Close(); } catch (Exception) { }
        }
    }

    // The request's body as text; null when it is over MaxBody.
    private static async Task<string?> ReadBody(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxBody)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > MaxBody)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public async Task<Response> Handle(string method, string path, string? cookie, string body)
    {
        if (path == "/")
            return method == "GET" ? new Response(200, "text/html; charset=utf-8", Page.Value) : Json(405, new { error = "Method not allowed" });
        if (!path.StartsWith("/api/"))
            return Json(404, new { error = "Not found" });
        JsonElement json = default;
        try
        {
            if (body.Length > 0)
                json = JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException) { return Json(400, new { error = "Not JSON" }); }
        if (path == "/api/login")
            return method == "POST" ? Login(json) : Json(405, new { error = "Method not allowed" });
        if (!SignedIn(cookie))
            return Json(401, new { error = "Sign in first" });
        try
        {
            var (status, result) = await _api(method, path, json);
            return Json(status, result);
        }
        catch (Exception e) { return Json(500, new { error = e.Message }); }
    }

    // ponytail: one lock around the PBKDF2 check, so logins run one at a time (also slows guessing); fine for one user.
    private Response Login(JsonElement json)
    {
        lock (_lock)
        {
            ForgetStaleSessions();
            if (_lockout.Wait() is var wait && wait > TimeSpan.Zero)
            {
                var seconds = (int)Math.Ceiling(wait.TotalSeconds);
                return Json(429, new { error = $"Too many wrong passwords. Try again in {seconds} s.", wait = seconds });
            }
            var password = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
            if (!RemoteAuth.Verify(password, _hash()))
            {
                _lockout.Failed();
                return Json(401, new { error = "Wrong password" });
            }
            _lockout.Succeeded();
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            _sessions.Add(Digest(token));
            return Json(200, new { ok = true }) with { Cookie = $"{CookieName}={token}; Path=/; HttpOnly; Secure; SameSite=Strict" };
        }
    }

    private bool SignedIn(string? cookie)
    {
        var token = cookie?.Split(';').Select(c => c.Trim()).FirstOrDefault(c => c.StartsWith(CookieName + "="))?[(CookieName.Length + 1)..];
        lock (_lock)
        {
            ForgetStaleSessions();
            return token is { Length: > 0 } && _sessions.Contains(Digest(token));
        }
    }

    private void ForgetStaleSessions()
    {
        var hash = _hash();
        if (hash == _sessionsHash)
            return;
        _sessionsHash = hash;
        _sessions.Clear();
    }

    private static string Digest(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static Response Json(int status, object body) => new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(body));

    // --self-test: hashing, the lockout's timing on a stepped clock, and routing with auth.
    internal static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException($"Remote access self-test: {what}");
        }
        var hash = RemoteAuth.HashPassword("open sesame");
        Check(RemoteAuth.Verify("open sesame", hash) && !RemoteAuth.Verify("open sesamE", hash) && !RemoteAuth.Verify("open sesame", null) &&
            !RemoteAuth.Verify("open sesame", "pbkdf2-sha256$1$x$y") && hash != RemoteAuth.HashPassword("open sesame"), "hash and verify");

        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lockout = new RemoteLockout(() => now);
        for (var i = 0; i < 4; i++)
            lockout.Failed();
        Check(lockout.Wait() == TimeSpan.Zero, "four failures lock nothing");
        var waits = new List<double>();
        for (var i = 0; i < 9; i++)
        {
            lockout.Failed();
            waits.Add(lockout.Wait().TotalSeconds);
            now += lockout.Wait();
        }
        Check(waits.SequenceEqual([5, 10, 20, 40, 80, 160, 300, 300, 300]), "lockout doubling: " + string.Join(",", waits));
        lockout.Failed();
        now += TimeSpan.FromSeconds(299);
        Check(lockout.Wait() == TimeSpan.FromSeconds(1), "lockout counts down");
        lockout.Succeeded();
        Check(lockout.Wait() == TimeSpan.Zero, "a success resets");

        var server = new RemoteServer(() => hash, (method, path, _) => Task.FromResult((200, (object)new { method, path })), () => now);
        Response Call(string method, string path, string? cookie = null, string body = "") => server.Handle(method, path, cookie, body).GetAwaiter().GetResult();
        Check(Call("GET", "/api/conversations").Status == 401 && Call("POST", "/api/send", "ao_session=forged").Status == 401, "no session: 401");
        var wrong = Call("POST", "/api/login", body: """{"password":"nope"}""");
        Check(wrong.Status == 401 && wrong.Cookie is null, "wrong password");
        var login = Call("POST", "/api/login", body: """{"password":"open sesame"}""");
        Check(login is { Status: 200, Cookie: { } set } && set.StartsWith("ao_session=") && set.Contains("HttpOnly") && set.Contains("Secure") &&
            set.Contains("SameSite=Strict") && set.Contains("Path=/"), "login sets the cookie");
        var session = login.Cookie!.Split(';')[0];
        var ok = Call("GET", "/api/conversations", "other=1; " + session);
        Check(ok.Status == 200 && Encoding.UTF8.GetString(ok.Body).Contains("/api/conversations"), "a session reaches the API");
        Check(Call("GET", "/").Type.StartsWith("text/html") && Call("GET", "/nope").Status == 404 && Call("POST", "/api/stop", session, "{").Status == 400 &&
            Call("GET", "/api/login").Status == 405, "routing");
        hash = RemoteAuth.HashPassword("changed");
        Check(Call("GET", "/api/conversations", session).Status == 401, "a new password signs out");
    }
}
