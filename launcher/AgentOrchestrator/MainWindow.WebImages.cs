using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator;

// The phone app's Images tab: the gallery, the form's models and last-used settings, generate into the Images page's queue,
// the queue's status and the running job's step preview, and a picture's settings. Generate builds the settings from the
// request (PicturesToMake reads the page's controls), then goes the page's way: saved, shown on the page, queued, run.
public partial class MainWindow
{
    private byte[]? _imgPreviewPng; // the stage's latest step preview (ShowImagePreview), and how many came so far
    private int _imgPreviewFrame;

    private static object WebImageSettings(ImageSettings s) => new
    {
        model = ImageKey(s), prompt = s.Prompt, negative = s.Negative, steps = s.Steps, cfg = s.Cfg, aspect = s.Aspect,
        width = s.CustomWidth, height = s.CustomHeight, unlocked = s.SizeUnlocked, count = s.Batch, seed = s.Seed, lowVram = s.LowVram,
    };

    private sealed record WebImageModel(string Id, string Name, string Note, string Source, bool Negative, int Steps, double Cfg, int Multiple, ImageShare.Model? Pick);

    // The composer's choices (this PC's, then the primary PC's while connected) with what the form needs of each, then the page's
    // OpenRouter model when it has one (the composer offers none; choosing another stays on the PC).
    private List<WebImageModel> WebImageModels(List<ImageShare.Model>? shared)
    {
        ScanImageFolder();
        var own = ImageShare.Models(_imgLocal);
        var models = ChatImageChoices(shared)
            .Select(c => (c, m: (c.Primary ? shared : own)?.FirstOrDefault(m => m.Id == c.Id)))
            .Where(x => x.m is not null)
            .Select(x => new WebImageModel(x.c.Key, x.c.Name, x.c.Note, x.c.Primary ? "shared" : "local", x.m!.Negative, x.m.Steps, x.m.Cfg, x.m.Multiple, x.m))
            .ToList();
        if (_settings.ImageGen.RemoteModel is { Length: > 0 } remote)
            models.Add(new("openrouter:" + remote, remote, "OpenRouter", "remote", false, 0, 0, 16, null));
        return models;
    }

    private (int, object) WebImages(List<ImageShare.Model>? shared) => (200, new
    {
        settings = WebImageSettings(_settings.ImageGen),
        models = WebImageModels(shared).Select(m => new { id = m.Id, name = m.Name, note = m.Note, source = m.Source, negative = m.Negative, steps = m.Steps, cfg = m.Cfg, multiple = m.Multiple }).ToList(),
        aspects = ImageGen.Presets.Select(p => new { name = p.Name, ratio = p.Ratio.Length > 0 ? p.Ratio : "Custom" }).ToList(),
        maxBatch = ImageGen.MaxBatch,
        pictures = GalleryPictures().Select(p => new { path = p, url = "/api/file?path=" + Uri.EscapeDataString(p), time = File.GetLastWriteTimeUtc(p) }).ToList(),
    });

    private (int, object) WebImagesStatus() => (200, new
    {
        busy = ImageBusy, text = _imgStatus?.Text ?? "",
        progress = ImageBusy && _imgProgress is { IsIndeterminate: false } bar ? bar.Value : (double?)null,
        frame = ImageBusy && _imgStagePreview ? _imgPreviewFrame : 0,
        queue = _imgQueue.Select((p, i) => new { index = i, prompt = p.Run.Prompt, seed = p.Run.Seed }).ToList(),
        latest = GalleryPictures().FirstOrDefault(),
    });

    private (int, object) WebImagesPreview() =>
        ImageBusy && _imgStagePreview && _imgPreviewPng is { } png ? (200, new WebServer.Response(200, "image/png", png)) : WebNotFound;

    // A generated picture's settings file, for "Use these settings"; 404 for a path outside the images folder or without one.
    private static (int, object) WebImagesPicture(string? path)
    {
        if (WebPicturePath(path, ["images"]) is not { } full || Path.ChangeExtension(full, ".json") is var sidecar && !File.Exists(sidecar))
            return WebNotFound;
        try { return JsonSerializer.Deserialize<ImageSettings>(File.ReadAllText(sidecar)) is { } s ? (200, WebImageSettings(s)) : WebNotFound; }
        catch (Exception x) when (x is IOException or JsonException or UnauthorizedAccessException) { return WebNotFound; }
    }

    // The page's Generate with the phone's settings: the same refusals as PicturesToMake (400 with its words), the model one of the
    // form's, the numbers within the page's ranges; what the phone does not set (sampler, scheduler, strength, start image, providers)
    // stays the page's. Saved and shown on the page, queued, and the queue started when no job runs.
    private (int, object) WebImagesGenerate(JsonElement body, List<ImageShare.Model>? shared)
    {
        var s = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("settings", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
        if (s.ValueKind != JsonValueKind.Object)
            return (400, new { error = "Expected {settings}" });
        double? Number(string name) => s.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : null;
        bool? Flag(string name) => s.TryGetProperty(name, out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False ? f.GetBoolean() : null;
        var prompt = WebField(s, "prompt").Trim();
        if (prompt.Length == 0)
            return (400, new { error = "Write a prompt first" });
        var key = WebField(s, "model");
        if (WebImageModels(shared).FirstOrDefault(m => key.Length > 0 && m.Id.Equals(key, StringComparison.OrdinalIgnoreCase)) is not { } model)
            return (400, new { error = "Choose an image model first" });
        var aspect = WebField(s, "aspect") is { Length: > 0 } a ? a : "1:1";
        if (aspect != "Custom" && !IsChatImageRatio(aspect))
            return (400, new { error = "No such aspect ratio" });
        var page = _settings.ImageGen;
        if (model.Source != "remote" && page.InitImage.Length > 0 && !File.Exists(page.InitImage))
            return (400, new { error = $"The start image is gone: {page.InitImage}" });

        var run = JsonSerializer.Deserialize<ImageSettings>(JsonSerializer.Serialize(page))!; // the page's own fields kept
        (run.SharedModel, run.RemoteModel) = ("", "");
        if (model.Source == "shared")
            (run.SharedModel, run.RecipeId, run.LocalModel) = (model.Pick!.Id, model.Pick.Id, "");
        else if (model.Source == "remote")
            run.RemoteModel = page.RemoteModel; // RecipeId, LocalModel and providers as the page has them (its local pick kept for the way back)
        else
            (run.RecipeId, run.LocalModel) = ImageShare.Resolve(model.Pick!.Id, _imgLocal) is var (recipe, local) ? (recipe.Id, local) : (model.Pick.Id, "");
        if (model.Source != "remote")
            run.RemoteProvider = "";
        var typed = (w: (int)Math.Clamp(Number("width") ?? 0, 0, 4096), h: (int)Math.Clamp(Number("height") ?? 0, 0, 4096));
        (run.Prompt, run.Negative, run.Aspect, run.SizeUnlocked) = (prompt, model.Negative ? WebField(s, "negative").Trim() : "", aspect, Flag("unlocked") ?? false);
        (run.CustomWidth, run.CustomHeight) = typed;
        (run.Width, run.Height) = (typed.w > 0 ? typed.w : 1024, typed.h > 0 ? typed.h : 1024); // Custom's size; a ratio's comes from it below
        var sized = ImageGen.ChatRun(run, ImageGen.ChatModel(run) is var (paints, _) ? paints : null, "", null, 0, false); // the page's rounding and range
        (run.Width, run.Height, run.CustomWidth, run.CustomHeight) = (sized.Width, sized.Height, typed.w > 0 ? sized.Width : 0, typed.h > 0 ? sized.Height : 0);
        run.Steps = (int)Math.Clamp(Number("steps") ?? model.Steps, 1, 100);
        run.Cfg = Math.Round(Math.Clamp(Number("cfg") ?? model.Cfg, 0, 20) * 2) / 2; // the page's 0.5 steps
        run.Batch = (int)Math.Clamp(Number("count") ?? 1, 1, ImageGen.MaxBatch);
        run.Seed = Number("seed") is >= 0 and var seed ? (long)seed : -1;
        run.LowVram = Flag("lowVram") ?? page.LowVram;

        if (ImagesView.Children.Count == 0)
            BuildImagesPage(); // the job paints through the page's status and Low VRAM controls
        _settings.ImageGen = run;
        _settings.Save();
        ApplyImageSettings(run);
        var pictures = PicturesFor(run);
        _imgQueue.AddRange(pictures);
        RefreshQueue();
        if (!ImageBusy)
            _ = RunImageJob(RunQueueAsync);
        return (200, new { queued = pictures.Count });
    }

    // The phone's Cancel stops everything, as the page's: the waiting pictures and the one being made.
    private (int, object) WebImagesCancel()
    {
        _imgQueue.Clear();
        RefreshQueue();
        CancelImageGen();
        return (200, new { ok = true });
    }

    private (int, object) WebImagesUnqueue(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("index", out var i) || !i.TryGetInt32(out var index) || index < 0 || index >= _imgQueue.Count)
            return WebNotFound;
        _imgQueue.RemoveAt(index);
        RefreshQueue();
        return (200, new { ok = true });
    }

    // --webapp-smoke (while on), the job never run (a stand-in token holds ImageBusy, so generate only queues): the gallery lists a
    // picture written into images\ with its URL; preview is 404 while idle; an empty prompt or unknown model is 400; generate queues
    // one picture a count with seeds counting up, which status lists; unqueue takes one out (out of range 404); cancel empties the
    // queue; a picture's settings file is read back, one without, outside images\ or beside it is 404.
    private async Task<bool> WebImagesSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: images: {what}"); return false; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("Tailscale-User-Login", "smoke@example.com");
        async Task<(int Status, JsonNode? Body)> Call(string path, object? post = null)
        {
            using var response = post is null ? await http.GetAsync($"http://127.0.0.1:{port}{path}")
                : await http.PostAsync($"http://127.0.0.1:{port}{path}", new StringContent(JsonSerializer.Serialize(post), System.Text.Encoding.UTF8, "application/json"));
            var text = await response.Content.ReadAsStringAsync();
            try { return ((int)response.StatusCode, JsonNode.Parse(text)); }
            catch (JsonException) { return ((int)response.StatusCode, null); }
        }
        string Seeds(JsonNode? status) => string.Join(",", status?["queue"]?.AsArray().Select(q => q?["seed"]?.ToString()) ?? []);
        var page = JsonSerializer.Serialize(_settings.ImageGen);
        Directory.CreateDirectory(ImageGen.ImagesFolder);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");
        var picture = Path.Combine(ImageGen.ImagesFolder, "smoke-images-fox.png");
        var bare = Path.Combine(ImageGen.ImagesFolder, "smoke-images-bare.png");
        var outside = Path.Combine(AppSettings.DataRoot, "smoke-images-outside.png");
        var written = new[] { bare, picture, outside };
        foreach (var file in written)
        {
            File.WriteAllBytes(file, png);
            File.SetLastWriteTimeUtc(file, file == bare ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(1)); // the fox is the newest
        }
        foreach (var file in new[] { picture, outside })
            File.WriteAllText(Path.ChangeExtension(file, ".json"), JsonSerializer.Serialize(new ImageSettings { Prompt = "a smoke fox", Seed = 42, Steps = 6, Batch = 1 }));
        try
        {
            if (ImageBusy)
                return Fail("an image job is already running");
            if (await Call("/api/images") is not (200, { } gallery) || gallery["settings"]?["prompt"] is null || gallery["maxBatch"]?.GetValue<int>() != 20 ||
                gallery["aspects"]?.AsArray().Count is not > 1 || gallery["models"]?.AsArray() is not { Count: > 0 } models ||
                gallery["pictures"]?.AsArray().FirstOrDefault() is not { } first || first["path"]?.ToString() != picture ||
                first["url"]?.ToString() != "/api/file?path=" + Uri.EscapeDataString(picture))
                return Fail("the gallery and form: " + (await Call("/api/images")).Body?.ToJsonString());
            if (models[0]?["source"]?.ToString() != "local" || models[0]?["multiple"]?.GetValue<int>() is not > 0)
                return Fail("a model's fields: " + models[0]?.ToJsonString());
            if ((await Call("/api/images/preview")).Status != 404)
                return Fail("the preview while idle was not 404");
            var model = models[0]!["id"]!.ToString();
            if (await Call("/api/images/generate", new { settings = new { prompt = "  ", model } }) is not (400, { } empty) || empty["error"]?.ToString() != "Write a prompt first")
                return Fail("an empty prompt was not 400");
            if ((await Call("/api/images/generate", new { settings = new { prompt = "a fox", model = "no such model" } })).Status != 400)
                return Fail("an unknown model was not 400");

            _imgCts = new CancellationTokenSource(); // a job running: generate queues behind it
            if (await Call("/api/images/generate", new { settings = new { prompt = "a fox", model, count = 3, seed = 7, steps = 500 } }) is not (200, { } queued) ||
                queued["queued"]?.GetValue<int>() != 3 || _settings.ImageGen is not { Prompt: "a fox", Steps: 100, Batch: 3, Seed: 7 })
                return Fail($"generate: saved {JsonSerializer.Serialize(_settings.ImageGen)}");
            if (await Call("/api/images/status") is not (200, { } busy) || busy["busy"]?.GetValue<bool>() != true || Seeds(busy) != "7,8,9" ||
                busy["queue"]![0]!["prompt"]?.ToString() != "a fox" || busy["latest"]?.ToString() != picture || busy["frame"]?.GetValue<int>() != 0)
                return Fail("the status while queued: " + (await Call("/api/images/status")).Body?.ToJsonString());
            if ((await Call("/api/images/unqueue", new { index = 1 })).Status != 200 || Seeds((await Call("/api/images/status")).Body) != "7,9")
                return Fail("unqueue the second: " + Seeds((await Call("/api/images/status")).Body));
            if ((await Call("/api/images/unqueue", new { index = 2 })).Status != 404 || (await Call("/api/images/unqueue", new { index = -1 })).Status != 404)
                return Fail("unqueue out of range was not 404");
            var stand = _imgCts;
            if ((await Call("/api/images/cancel", new { })).Status != 200 || _imgQueue.Count != 0 || !stand.IsCancellationRequested)
                return Fail($"cancel: {_imgQueue.Count} left");
            _imgCts = null;

            if (await Call("/api/images/picture?path=" + Uri.EscapeDataString(picture)) is not (200, { } read) || read["prompt"]?.ToString() != "a smoke fox" ||
                read["seed"]?.GetValue<long>() != 42 || read["steps"]?.GetValue<int>() != 6)
                return Fail("a picture's settings: " + (await Call("/api/images/picture?path=" + Uri.EscapeDataString(picture))).Body?.ToJsonString());
            foreach (var path in new[] { bare, outside, Path.Combine(ImageGen.ImagesFolder, "..", Path.GetFileName(outside)), @"images\smoke-images-fox.png", Path.ChangeExtension(picture, ".json"), "" })
                if ((await Call("/api/images/picture?path=" + Uri.EscapeDataString(path))).Status != 404)
                    return Fail($"a picture's settings for {path} was not 404");
            return true;
        }
        finally
        {
            _imgCts = null;
            _imgQueue.Clear();
            RefreshQueue();
            _settings.ImageGen = JsonSerializer.Deserialize<ImageSettings>(page)!;
            _settings.Save();
            foreach (var file in written.SelectMany(f => new[] { f, Path.ChangeExtension(f, ".json") }).Where(File.Exists))
                File.Delete(file);
        }
    }
}
