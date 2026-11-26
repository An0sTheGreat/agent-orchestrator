using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace AgentOrchestrator;

// Settings > Models > Local API for my apps (LocalApi.cs): an OpenAI-compatible server on 127.0.0.1:AppSettings.LocalApiPort for
// this PC's own programs, behind a bearer key (ApiKeys under LocalApi.KeyName: read from the store, never left in the environment).
public partial class MainWindow
{
    private LocalApi.Server? _localApi;
    private string _localApiState = "Off";

    internal string? LocalApiKey() => ApiKeys.Get(LocalApi.KeyName);

    // Test seams (--webapp-smoke): in place of the engine's --api-target run, and of the picture job.
    internal Func<string, bool, CancellationToken, Task<LocalApi.Target>>? _localApiTargetTest;
    internal Func<ImageSettings, CancellationToken, Task<List<ImageShare.Picture>>>? _localApiGenerateTest;
    private int _localApiStreams; // app streams to a local worker now (Interlocked): ChatModelsInUse

    private void InitLocalApi()
    {
        if (_settings.LocalApi)
            StartLocalApi();
        else
            RefreshLocalApi();
    }

    private void LocalApiToggle_Click(object sender, RoutedEventArgs e)
    {
        if (LocalApiToggle.IsChecked == true)
            StartLocalApi();
        else
        {
            StopLocalApi();
            _settings.LocalApi = false;
            _settings.Save();
            RefreshLocalApi();
        }
    }

    // The switch stays on when the port is taken (the state says why), so the next start tries again.
    private void StartLocalApi()
    {
        StopLocalApi();
        if (LocalApiKey() is not { Length: > 0 })
            NewLocalApiKey();
        var port = _settings.LocalApiPort is > 0 and < 65536 ? _settings.LocalApiPort : LocalApi.DefaultPort;
        var server = new LocalApi.Server(port, LocalApiKey, LocalApiRoute);
        if (server.Start())
        {
            _localApi = server;
            _localApiState = $"Listening on http://127.0.0.1:{port}/v1";
        }
        else
            _localApiState = $"Port {port} is in use by another program";
        _settings.LocalApi = true;
        _settings.Save();
        RefreshLocalApi();
    }

    // Idempotent.
    private void StopLocalApi()
    {
        _localApi?.Dispose();
        _localApi = null;
        _localApiState = "Off";
    }

    private void RefreshLocalApi()
    {
        LocalApiToggle.IsChecked = _settings.LocalApi;
        LocalApiStateText.Text = _localApiState;
        LocalApiKeyBox.Text = _localApi is not null ? LocalApiKey() ?? "" : "";
    }

    // ApiKeys.Save also sets the variable; this key stays out of the environment (the workers' processes inherit it).
    private static void NewLocalApiKey()
    {
        ApiKeys.Save(LocalApi.KeyName, "Default", ModelShare.NewKey());
        Environment.SetEnvironmentVariable(LocalApi.KeyName, null);
    }

    private void LocalApiCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard(LocalApiKeyBox.Text, "API key copied", "No API key yet");

    // The key is read on every request: the old one stops working at once.
    private void LocalApiNewKey_Click(object sender, RoutedEventArgs e)
    {
        NewLocalApiKey();
        RefreshLocalApi();
        Toast("New key: your apps need the new one");
    }

    private async Task LocalApiRoute(HttpListenerContext ctx)
    {
        var (method, path) = (ctx.Request.HttpMethod, ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "");
        if (method == "GET" && path == "/v1/models")
        {
            var data = await Dispatcher.InvokeAsync(() =>
            {
                ScanImageFolder();
                return _cards.Select(c => new { id = c.Role, @object = "model", owned_by = "agent-orchestrator", name = c.Title, kind = "chat" })
                    .Concat(ImageShare.Models(_imgLocal).Select(m => new { id = m.Id, @object = "model", owned_by = "agent-orchestrator", name = m.Name, kind = "image" }))
                    .ToList();
            });
            await LocalApiReply(ctx, 200, JsonSerializer.SerializeToUtf8Bytes(new { @object = "list", data }));
            return;
        }
        if (method == "POST" && path == "/v1/chat/completions")
            await LocalApiChat(ctx);
        else if (method == "POST" && path == "/v1/images/generations")
            await LocalApiImages(ctx);
        else
            await LocalApiReply(ctx, 404, LocalApi.Error("Unknown route"));
    }

    // The request's JSON object; null with the reply sent (413 over LocalApi.MaxBody, 400 not a JSON object).
    private static async Task<JsonObject?> LocalApiBody(HttpListenerContext ctx)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        for (int read; buffer.Length <= LocalApi.MaxBody && (read = await ctx.Request.InputStream.ReadAsync(chunk)) > 0;)
            buffer.Write(chunk, 0, read);
        if (buffer.Length > LocalApi.MaxBody)
        {
            await LocalApiReply(ctx, 413, LocalApi.Error("The body is over 25 MB"));
            return null;
        }
        buffer.Position = 0;
        try
        {
            if (JsonNode.Parse(buffer) is JsonObject body)
                return body;
        }
        catch (JsonException) { }
        await LocalApiReply(ctx, 400, LocalApi.Error("The body is not a JSON object"));
        return null;
    }

    private static readonly HttpClient LocalApiHttp = new() { Timeout = Timeout.InfiniteTimeSpan }; // a stream has no overall cap
    private readonly SemaphoreSlim _localApiTargets = new(1, 1); // one engine target at a time: two app calls never load at once

    // POST /v1/chat/completions: the worker the model names, made ready by the engine, then the request passed through unchanged
    // but for the worker's defaults under it and its real model id; the reply streams back as it comes.
    private async Task LocalApiChat(HttpListenerContext ctx)
    {
        if (await LocalApiBody(ctx) is not { } body)
            return;
        var model = body["model"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : null;
        var (role, roles, timeout) = await Dispatcher.InvokeAsync(() =>
        {
            var role = LocalApi.ResolveWorker(model, _cards.Select(c => (c.Role, c.Title)));
            var config = role is null ? "" : ActiveConfigPath();
            string text;
            try { text = config.Length > 0 ? File.ReadAllText(config) : ""; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { text = ""; }
            return (role, string.Join(", ", _cards.Select(c => c.Role)), role is null ? 0 : LocalApi.TimeoutSeconds(text, role));
        });
        if (role is null)
        {
            await LocalApiReply(ctx, 400, LocalApi.Error($"No worker named \"{model}\": use one of {roles}"));
            return;
        }
        LocalApi.Target target;
        try { target = await LocalApiTargetAsync(role); }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            if (e.Message.StartsWith("busy:", StringComparison.Ordinal))
            {
                ctx.Response.AddHeader("Retry-After", "10");
                await LocalApiReply(ctx, 503, LocalApi.Error("AO is busy with another model; retry when its turn ends", "server_error"));
            }
            else
                await LocalApiReply(ctx, 400, LocalApi.Error(e.Message)); // the engine's own text: a key name at most, never a value
            return;
        }
        // AO's own llama-server: no key. A remote's key comes from its variable; one written in config.json is not sent (its 401 comes back).
        var local = Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var url) && url.IsLoopback && target.KeyEnv.Length == 0;
        var secret = local || target.KeyEnv.Length == 0 ? null : Environment.GetEnvironmentVariable(target.KeyEnv);
        using var request = new HttpRequestMessage(HttpMethod.Post, target.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(LocalApi.MergeBody(target.Defaults, body, target.Model).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (local || secret is { Length: > 0 })
            request.Headers.Authorization = new("Bearer", local ? "no-key" : secret);
        if (local)
            Interlocked.Increment(ref _localApiStreams);
        try
        {
            HttpResponseMessage upstream;
            try
            {
                using var headers = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                upstream = await LocalApiHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                var why = e is TaskCanceledException ? $"no answer within {timeout} seconds" : LocalApi.Scrub(e.Message, secret);
                await Dispatcher.InvokeAsync(() => Log($"Local API: {role} failed: {why}"));
                await LocalApiReply(ctx, 502, LocalApi.Error($"The worker's API failed: {why}", "server_error"));
                return;
            }
            // disposing the upstream reply (on return, or the app hanging up) closes its connection: the worker stops
            using (upstream)
            {
                var response = ctx.Response;
                response.StatusCode = (int)upstream.StatusCode;
                response.ContentType = upstream.Content.Headers.ContentType?.ToString();
                await using var source = await upstream.Content.ReadAsStreamAsync();
                if (!upstream.IsSuccessStatusCode)
                {
                    var error = Encoding.UTF8.GetBytes(LocalApi.Scrub(await new StreamReader(source).ReadToEndAsync(), secret));
                    response.ContentLength64 = error.Length;
                    await response.OutputStream.WriteAsync(error);
                    return;
                }
                response.SendChunked = true;
                var buffer = new byte[16384];
                for (int read; (read = await source.ReadAsync(buffer)) > 0;)
                {
                    try
                    {
                        await response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                        await response.OutputStream.FlushAsync();
                    }
                    catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { return; } // the app hung up
                }
            }
        }
        catch (Exception e)
        {
            await Dispatcher.InvokeAsync(() => Log($"Local API: {role} failed: {LocalApi.Scrub(e.Message, secret)}"));
            throw;
        }
        finally
        {
            if (local)
                Interlocked.Decrement(ref _localApiStreams);
        }
    }

    // The worker's target from the engine (--api-target, at most 10 minutes), one at a time. While the models work (a turn, a run,
    // Load models) it may only reuse a loaded server ("busy:" otherwise); else the GPU is freed for the chat model as Load models does.
    private async Task<LocalApi.Target> LocalApiTargetAsync(string role)
    {
        await _localApiTargets.WaitAsync();
        string? temp = null;
        try
        {
            using var cap = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var (busy, info) = await Dispatcher.InvokeAsync(() =>
            {
                var busy = ModelsWorking || _loadingRole is not null;
                if (_localApiTargetTest is not null)
                    return (busy, null);
                if (_workflowRoot is null)
                    throw new InvalidOperationException("The engine folder is not set");
                var prepared = LauncherLogic.PrepareChatFolder(SelectedProjectPath(), _workflowRoot, CurrentWorkers(), out _);
                temp = prepared.IsTemp ? prepared.Path : null;
                var info = LauncherLogic.BuildApiTargetStartInfo(LauncherLogic.FindPython(), _workflowRoot, prepared.Path, role, busy);
                if (!busy)
                    FreeGpuForChat();
                ApplyRuntime(info);
                return (busy, (System.Diagnostics.ProcessStartInfo?)info);
            });
            if (_localApiTargetTest is { } test)
                return await test(role, busy, cap.Token);
            using var process = new System.Diagnostics.Process { StartInfo = info! };
            string? last = null, error = null;
            process.OutputDataReceived += (_, a) => { if (a.Data is { Length: > 0 } line) last = line; };
            process.ErrorDataReceived += (_, a) => { if (a.Data is { } line && line.StartsWith("ERROR:", StringComparison.Ordinal)) error = line[6..].Trim(); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try { await process.WaitForExitAsync(cap.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (Exception x) when (x is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                throw new InvalidOperationException("The engine took over 10 minutes to make the worker ready");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException(error ?? $"The engine exited with code {process.ExitCode}");
            return LocalApi.ParseTarget(last ?? "") ?? throw new InvalidOperationException("The engine did not report the worker's target");
        }
        finally
        {
            _localApiTargets.Release();
            if (temp is not null)
                try { Directory.Delete(temp, true); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        }
    }

    // POST /v1/images/generations: one picture job through the Images page (its status line, its GPU rule), in a temp folder; while
    // another picture is being made the call waits its turn. Local image models only; the page's own settings are left as they are.
    private async Task LocalApiImages(HttpListenerContext ctx)
    {
        if (await LocalApiBody(ctx) is not { } body)
            return;
        string? Text(string name) => body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        double? Number(string name) => body[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        var size = LocalApi.ParseSize(Text("size"));
        var refused = Text("response_format") is { } format && format != "b64_json" ? "Only response_format b64_json is supported"
            : string.IsNullOrWhiteSpace(Text("prompt")) ? "The prompt is empty"
            : body["size"] is not null && size is null ? "size must be \"WxH\", each from 64 to 4096" : null;
        if (refused is not null)
        {
            await LocalApiReply(ctx, 400, LocalApi.Error(refused));
            return;
        }
        // ponytail: HttpListener reports an app that hung up only when a write fails, so a waiting or running call is not given up early
        while (true)
        {
            var (done, status, reply) = await await Dispatcher.InvokeAsync(async () =>
            {
                if (ImageBusy) // checked and taken on the UI thread at once
                    return (false, 0, Array.Empty<byte>());
                ScanImageFolder();
                if (ImagesView.Children.Count == 0)
                    BuildImagesPage();
                var page = ReadImageSettings();
                var (pick, pickLocal) = ImageChoice(_imgRecipe.SelectedIndex); // the page's local pick, also while it shows a remote or shared model
                var id = Text("model") ?? (pickLocal.Length > 0 ? "local:" + Path.GetFileName(pickLocal) : pick.Id);
                if (ImageShare.Resolve(id, _imgLocal) is not var (recipe, local))
                    return (true, 400, LocalApi.Error($"No such image model: {id}"));
                var run = ImageShare.Run(new ImageSettings
                {
                    Prompt = Text("prompt")!, Negative = Text("negative_prompt") ?? "", Aspect = "Custom", Width = size?.W ?? page.Width, Height = size?.H ?? page.Height,
                    Steps = (int)Math.Clamp(Number("steps") ?? page.Steps, 1, 100), Cfg = Number("cfg_scale") ?? page.Cfg, Seed = (long)Math.Max(Number("seed") ?? -1, -1),
                    Batch = (int)Math.Clamp(Number("n") ?? 1, 1, ImageGen.MaxBatch), Sampler = page.Sampler, Scheduler = page.Scheduler, LowVram = page.LowVram,
                }, recipe, local);
                var pictures = _localApiGenerateTest is { } test ? await test(run, CancellationToken.None)
                    : await GenerateInTempAsync(run, "an app", ct => FreeGpuForImageAsync(run, ct), CancellationToken.None);
                return pictures is null ? (true, 500, LocalApi.Error(_imgStatus.Text, "server_error"))
                    : (true, 200, JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        data = pictures.Select(p => new { b64_json = p.Png, revised_prompt = (string?)null, seed = p.Seed }),
                    }));
            });
            if (done)
            {
                await LocalApiReply(ctx, status, reply);
                return;
            }
            await Task.Delay(500);
        }
    }

    private static async Task LocalApiReply(HttpListenerContext ctx, int status, byte[] body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body);
    }

    // --webapp-smoke: the switch starts and stops the server on a free port with its key; /v1/models needs the key; New key replaces it;
    // a port in use is reported; the key never reaches the environment.
    internal async Task<bool> LocalApiSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: local api: {what}"); return false; }
        void Click(bool on)
        {
            LocalApiToggle.IsChecked = on;
            LocalApiToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        static int FreePort()
        {
            using var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }
        var (on, port) = (_settings.LocalApi, _settings.LocalApiPort);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        async Task<(int, string)> Get(int at, string? key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{at}/v1/models");
            if (key is not null)
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var response = await http.SendAsync(request);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        HttpListener? blocker = null;
        try
        {
            var p = _settings.LocalApiPort = FreePort();
            Click(true);
            var key = LocalApiKeyBox.Text;
            if (_localApi is null || LocalApiStateText.Text != $"Listening on http://127.0.0.1:{p}/v1" || key.Length != 43 || !AppSettings.Load().LocalApi ||
                Environment.GetEnvironmentVariable(LocalApi.KeyName) is not null || key != LocalApiKey())
                return Fail($"on: running {_localApi is not null}, state {LocalApiStateText.Text}, key length {key.Length}, env {Environment.GetEnvironmentVariable(LocalApi.KeyName) is not null}");
            if ((await Get(p, null)).Item1 != 401 || (await Get(p, "wrong")).Item1 != 401)
                return Fail("/v1/models without the key");
            var (status, body) = await Get(p, key);
            if (status != 200)
                return Fail($"/v1/models with the key: {status}");
            using (var doc = JsonDocument.Parse(body))
            {
                var chat = doc.RootElement.GetProperty("data").EnumerateArray().Where(m => m.GetProperty("kind").GetString() == "chat").Select(m => m.GetProperty("id").GetString()).ToList();
                if (!_cards.Select(c => c.Role).All(chat.Contains) || chat.Count != _cards.Count)
                    return Fail($"the workers listed: {string.Join(",", chat)}");
            }
            LocalApiNewKey_Click(this, new RoutedEventArgs());
            var fresh = LocalApiKeyBox.Text;
            if (fresh == key || fresh.Length != 43 || (await Get(p, key)).Item1 != 401 || (await Get(p, fresh)).Item1 != 200)
                return Fail("New key");
            if (!await LocalApiRoutesSmoke(p, fresh))
                return false;

            Click(false);
            if (_localApi is not null || LocalApiStateText.Text != "Off" || AppSettings.Load().LocalApi)
                return Fail($"off: {LocalApiStateText.Text}");
            try { await Get(p, fresh); return Fail("still answering when off"); } catch (HttpRequestException) { }

            var taken = _settings.LocalApiPort = FreePort();
            blocker = new HttpListener();
            blocker.Prefixes.Add($"http://127.0.0.1:{taken}/");
            blocker.Start();
            Click(true);
            if (_localApi is not null || LocalApiStateText.Text != $"Port {taken} is in use by another program")
                return Fail($"port in use: {LocalApiStateText.Text}");
            Click(false);
            if (LocalApiStateText.Text != "Off")
                return Fail($"off after in use: {LocalApiStateText.Text}");
            return true;
        }
        finally
        {
            blocker?.Close();
            StopLocalApi();
            (_settings.LocalApi, _settings.LocalApiPort) = (on, port);
            _settings.Save();
            RefreshLocalApi();
        }
    }

    // --webapp-smoke, while the server listens on port with key: chat through a fake upstream (streamed as it comes, the worker's key sent,
    // an error relayed scrubbed, the busy rule, an app hanging up), and pictures through a fake job.
    private async Task<bool> LocalApiRoutesSmoke(int port, string key)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: local api: {what}"); return false; }
        string[] lines = ["data: {\"n\":1}\n\n", "data: {\"n\":2}\n\n", "data: [DONE]\n\n"];
        var (fakeAuth, fakeBody, fakeStatus, lastWritten) = ("", "", 200, false);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var fakePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var fake = new HttpListener();
        fake.Prefixes.Add($"http://127.0.0.1:{fakePort}/");
        fake.Start();
        _ = Task.Run(async () =>
        {
            while (fake.IsListening)
            {
                HttpListenerContext c;
                try { c = await fake.GetContextAsync(); } catch (Exception) { return; }
                try
                {
                    fakeAuth = c.Request.Headers["Authorization"] ?? "";
                    fakeBody = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
                    c.Response.StatusCode = fakeStatus;
                    if (fakeStatus != 200)
                    {
                        c.Response.ContentType = "application/json";
                        await c.Response.OutputStream.WriteAsync("bad key sk-smoke"u8.ToArray());
                        continue;
                    }
                    lastWritten = false;
                    c.Response.ContentType = "text/event-stream";
                    c.Response.SendChunked = true;
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (i > 0)
                            await Task.Delay(200);
                        lastWritten = i == lines.Length - 1;
                        await c.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(lines[i]));
                        await c.Response.OutputStream.FlushAsync();
                    }
                }
                catch (Exception) { } // the API hung up
                finally { try { c.Response.Close(); } catch (Exception) { } }
            }
        });
        var target = new LocalApi.Target($"http://127.0.0.1:{fakePort}/v1", "real-id", "AO_SMOKE_KEY", JsonNode.Parse("""{"temperature":0.4}""")!.AsObject());
        var asked = new List<(string Role, bool OnlyLoaded)>();
        ImageSettings? generated = null;
        var settingsBefore = JsonSerializer.Serialize(_settings.ImageGen);
        _localApiTargetTest = (role, onlyLoaded, _) =>
        {
            asked.Add((role, onlyLoaded));
            return onlyLoaded ? throw new InvalidOperationException($"busy: {role} is not loaded and loading it would swap out another model") : Task.FromResult(target);
        };
        _localApiGenerateTest = (run, _) =>
        {
            generated = run;
            return Task.FromResult(new List<ImageShare.Picture> { new("cGljdHVyZQ==", 7) });
        };
        Environment.SetEnvironmentVariable("AO_SMOKE_KEY", "sk-smoke");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        Task<HttpResponseMessage> Post(string path, string json)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
        var planner = _cards.First().Role;
        var chat = $$"""{"model":"{{planner}}","stream":true,"temperature":1,"messages":[{"role":"user","content":"hi"}]}""";
        try
        {
            using (var response = await Post("/v1/chat/completions", chat))
            {
                if ((int)response.StatusCode != 200 || response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                    return Fail($"chat: {(int)response.StatusCode} {response.Content.Headers.ContentType} {await response.Content.ReadAsStringAsync()}");
                using var stream = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[4096];
                var first = await stream.ReadAsync(buffer);
                if (first == 0 || lastWritten)
                    return Fail("chat: the first chunk came only after the last was written");
                using var all = new MemoryStream();
                all.Write(buffer, 0, first);
                await stream.CopyToAsync(all);
                if (Encoding.UTF8.GetString(all.ToArray()) != string.Concat(lines))
                    return Fail($"chat: the stream changed: {Encoding.UTF8.GetString(all.ToArray())}");
            }
            var sent = JsonNode.Parse(fakeBody)!;
            if (fakeAuth != "Bearer sk-smoke" || sent["model"]?.GetValue<string>() != "real-id" || sent["temperature"]?.GetValue<double>() != 1 || asked.Count != 1 || asked[0] != (planner, false))
                return Fail($"chat upstream: {fakeAuth} {fakeBody} {string.Join(",", asked)}");

            using (var response = await Post("/v1/chat/completions", chat.Replace(planner, "nope")))
            {
                var text = await response.Content.ReadAsStringAsync();
                if ((int)response.StatusCode != 400 || !_cards.All(c => text.Contains(c.Role)))
                    return Fail($"unknown worker: {(int)response.StatusCode} {text}");
            }

            fakeStatus = 401;
            using (var response = await Post("/v1/chat/completions", chat))
            {
                var text = await response.Content.ReadAsStringAsync();
                if ((int)response.StatusCode != 401 || text != "bad key ***")
                    return Fail($"upstream error: {(int)response.StatusCode} {text}");
            }
            fakeStatus = 200;

            _chatBusy = true;
            using (var response = await Post("/v1/chat/completions", chat))
                if ((int)response.StatusCode != 503 || response.Headers.RetryAfter?.Delta != TimeSpan.FromSeconds(10) || asked[^1] != (planner, true))
                    return Fail($"busy: {(int)response.StatusCode} {response.Headers.RetryAfter} {asked[^1]}");
            _chatBusy = false;

            target = target with { KeyEnv = "" }; // AO's own llama-server: a local stream
            using (var response = await Post("/v1/chat/completions", chat))
            {
                using var stream = await response.Content.ReadAsStreamAsync();
                if ((int)response.StatusCode != 200 || await stream.ReadAsync(new byte[4096]) == 0 || Volatile.Read(ref _localApiStreams) != 1 || !ChatModelsInUse ||
                    fakeAuth != "Bearer no-key")
                    return Fail($"local stream: {(int)response.StatusCode}, streams {_localApiStreams}, {fakeAuth}");
            } // the app hangs up after the first chunk
            using (var response = await Post("/v1/chat/completions", chat))
                if ((int)response.StatusCode != 200 || await response.Content.ReadAsStringAsync() != string.Concat(lines))
                    return Fail($"after a hang-up: {(int)response.StatusCode}");
            for (var wait = 0; wait < 20 && Volatile.Read(ref _localApiStreams) != 0; wait++)
                await Task.Delay(100);
            if (Volatile.Read(ref _localApiStreams) != 0)
                return Fail($"streams left counted: {_localApiStreams}");

            using (var response = await Post("/v1/images/generations", """{"prompt":"a cat","size":"512x512","n":1}"""))
            {
                var text = await response.Content.ReadAsStringAsync();
                var picture = (int)response.StatusCode == 200 ? JsonNode.Parse(text)?["data"]?[0] : null;
                if (picture?["b64_json"]?.GetValue<string>() != "cGljdHVyZQ==" || generated is not { Width: 512, Height: 512, Batch: 1, Prompt: "a cat" })
                    return Fail($"images: {(int)response.StatusCode} {text}");
            }
            using (var response = await Post("/v1/images/generations", """{"prompt":"a cat","response_format":"url"}"""))
                if ((int)response.StatusCode != 400)
                    return Fail($"images as url: {(int)response.StatusCode}");
            using (var response = await Post("/v1/images/generations", """{"prompt":" "}"""))
                if ((int)response.StatusCode != 400)
                    return Fail($"images with no prompt: {(int)response.StatusCode}");
            if (JsonSerializer.Serialize(_settings.ImageGen) != settingsBefore)
                return Fail("images changed the page's settings");
            return true;
        }
        finally
        {
            (_localApiTargetTest, _localApiGenerateTest, _chatBusy) = (null, null, false);
            Environment.SetEnvironmentVariable("AO_SMOKE_KEY", null);
            fake.Stop();
        }
    }
}
