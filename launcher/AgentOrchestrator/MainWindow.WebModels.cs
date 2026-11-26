using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator;

// The phone app's Models and Agents screens (MainWindow.WebApp.cs routes /api/workers, /api/worker/{role}/..., /api/models/local,
// /api/favorites): the workers as the desktop lists them, a worker's options as its worker tab shows them, and every write
// through the desktop's own calls (AddCustomWorker, RenameTo, SetChatCard, RemoveWorker, SaveWorkerModel, AssignModel), so
// the desktop shows the change. Never a dialog: a save while the models work says when it takes effect instead.
public partial class MainWindow
{
    private const string WebBusyText = "Wait for the reply or run to finish";
    private const string WebLoadNote = "Takes effect from the next load";

    // A worker as its tab reads it: Path/Role the built-in role's config.json and roles.<role> (the planner's while COMPACTOR
    // follows MAIN), Current its configured values, Load its auto_load (for its GGUF), Inherited a custom worker's Implementer values.
    private sealed record WebWorkerState(RoleCard Card, string Path, JsonObject? Role, bool FollowsMain, LauncherLogic.RemoteRole? Remote,
        Dictionary<string, string> Current, JsonObject? Load, Dictionary<string, string>? Inherited, string Folder, string Hub, int Version);

    private sealed record WebGgufRead(Dictionary<string, string>? Meta, (long Model, long Vision) Files, long MtpHead);

    // UI thread; throws when config.json can't be read (as ModelSettingsBody's read).
    private WebWorkerState WebState(RoleCard card)
    {
        var (folder, hub, w) = (_settings.ModelsFolder ?? "", LauncherLogic.HubCache(), card.Config);
        if (card.IsCustom)
            return new(card, "", null, false, w.Remote, ModelOptions.Effective(w),
                w.ModelPath is { } p ? new JsonObject { ["model_path"] = p, ["gguf_variant"] = w.ModelVariant, ["gguf_file"] = w.GgufFile } : null,
                LauncherLogic.InheritedOptions(ActiveConfigPath(), Path.Combine(_workflowRoot ?? "", "config.json")), folder, hub, _localModelsVersion);
        var path = ActiveConfigPath();
        var roles = JsonNode.Parse(File.ReadAllText(path))!["roles"]!.AsObject();
        var follows = LauncherLogic.FollowsLead(card.Role, roles);
        var role = roles[follows ? "planner" : card.Role]!.AsObject();
        return new(card, path, role, follows, LauncherLogic.ReadRemoteRole(path, follows ? "planner" : card.Role), ModelOptions.Read(role),
            role["auto_load"]?.DeepClone() as JsonObject, null, folder, hub, _localModelsVersion);
    }

    // The worker's GGUF header and file sizes, off the UI thread; cached per load, folder and models version.
    // ponytail: never evicted; one small entry per model a worker pointed at while the app runs.
    private readonly ConcurrentDictionary<string, WebGgufRead> _webGguf = new();
    private Task<WebGgufRead> WebGguf(WebWorkerState s)
    {
        var key = $"{s.Version}|{s.Folder}|{s.Hub}|{s.Load?.ToJsonString()}";
        return _webGguf.TryGetValue(key, out var hit) ? Task.FromResult(hit) : Task.Run(() =>
        {
            var (meta, files, head) = VramEstimate.Read(LauncherLogic.ResolveGguf(s.Load, s.Folder, s.Hub));
            return _webGguf[key] = new(meta, files, head);
        });
    }

    // The installed models as the picker lists them (FindLocalGgufs + DescribeChoices), off the UI thread; cached until the
    // models version, folder or Hugging Face cache changes.
    private (int Version, string Folder, string Hub, List<PickerEntry> Entries)? _webLocal;
    private async Task<List<PickerEntry>> WebLocalEntries()
    {
        var (version, folder, hub) = await Dispatcher.InvokeAsync(() => (_localModelsVersion, _settings.ModelsFolder ?? "", LauncherLogic.HubCache()));
        if (_webLocal is { } cached && cached.Version == version && cached.Folder == folder && cached.Hub == hub)
            return cached.Entries;
        var choices = await FetchModelChoicesAsync();
        var entries = await Task.Run(() => DescribeChoices(choices, folder, hub));
        _webLocal = (version, folder, hub, entries);
        return entries;
    }

    // Reads the worker on the UI thread and its GGUF off it, then runs act on the UI thread with them and its defaults (Base:
    // ModelOptions.Defaults, none for a remote worker). Unknown role: 404.
    private async Task<(int, object)> WebWithWorker(string role, Func<WebWorkerState, Dictionary<string, string>, WebGgufRead, (int, object)> act)
    {
        WebWorkerState? s;
        try { s = await Dispatcher.InvokeAsync(() => CardFor(role) is { } card ? WebState(card) : null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException)
        {
            return (500, new { error = "Could not read config.json: " + e.Message });
        }
        if (s is null)
            return WebNotFound;
        var gguf = await WebGguf(s);
        var bases = s.Remote is not null ? new Dictionary<string, string>() : ModelOptions.Defaults(gguf.Meta, s.Load?["model_path"] is not null, s.Inherited);
        return await Dispatcher.InvokeAsync(() => act(s, bases, gguf));
    }

    private string WebSource(LauncherLogic.RemoteRole? remote) => remote is null ? "local"
        : _settings.ShareHost is { Length: > 0 } host && remote.BaseUrl.Trim().TrimEnd('/') == ModelShare.ChatUrl(host) ? "primary" : "api";

    // Whether the key a remote worker names is there (never the key itself).
    private static bool WebHasKey(string keyEnv) =>
        keyEnv.Trim() is { Length: > 0 } name && (ApiKeys.Has(name) || Environment.GetEnvironmentVariable(name) is { Length: > 0 });

    private static string WebPickerGroup(string group) => group == PickerGroups[1] ? "vision" : group == PickerGroups[2] ? "image" : "text";

    private (int, object) WebWorkers() => (200, new
    {
        busy = ModelsWorking,
        workers = _cards.Select(c =>
        {
            LauncherLogic.RemoteRole? remote;
            try { remote = WebState(c).Remote; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException) { remote = null; }
            return new
            {
                role = c.Role, title = c.Title, custom = c.IsCustom, kind = c.IsCustom ? c.Config.Kind : "", shown = c.Role == "planner" || _settings.ChatCards.Contains(c.Role),
                source = WebSource(remote), model = c.ModelName, quant = c.Quant, problem = c.SetupProblem ?? "",
            };
        }).ToList(),
    });

    private (int, object) WebAddWorker() => ModelsWorking ? (409, new { error = WebBusyText }) : (200, new { role = AddCustomWorker(select: false).Role });

    // GET /api/worker/{role}: options as the worker tab's editors show them (value: ModelOptions.Prefill; default: Base).
    private async Task<(int, object)> WebWorker(string role)
    {
        var entries = await WebLocalEntries();
        List<ModelPreset>? shared;
        try { shared = await WebPrimaryPresets(role); }
        catch (Exception e) when (WebNetFailure(e)) { shared = []; } // ponytail: the primary PC unreachable lists no presets; the preset routes say why
        return await WebWithWorker(role, (s, bases, _) => (200, WebWorkerBody(s, bases, entries, shared)));
    }

    private object WebWorkerBody(WebWorkerState s, Dictionary<string, string> bases, List<PickerEntry> entries, List<ModelPreset>? shared)
    {
        var (card, r, shown) = (s.Card, s.Remote, ModelOptions.Prefill(s.Current, bases));
        var (path, variant, file) = (s.Load?["model_path"]?.ToString(), s.Load?["gguf_variant"]?.ToString(), s.Load?["gguf_file"]?.ToString());
        var label = r is null && path is not null
            ? entries.FirstOrDefault(e => e.Choice.LoadPath == path && e.Choice.Variant == variant && (file is null || e.Choice.File == file))?.Choice.Label ?? "" : "";
        return new
        {
            role = card.Role, title = card.Title, followsMain = s.FollowsMain, source = WebSource(r), // custom: the object below, null for a built-in
            model = new { name = card.ModelName, quant = card.Quant, label },
            remote = r is null ? null : new
            {
                service = RemoteCatalog.ServiceFor(r.BaseUrl)?.Id ?? "custom", baseUrl = r.BaseUrl, model = r.Model, keyEnv = r.KeyEnv, hasKey = WebHasKey(r.KeyEnv),
                provider = r.Provider, sortPrice = r.SortPrice, maxInput = r.MaxInput, maxOutput = r.MaxOutput,
            },
            options = ModelOptions.All.Select(o => (Option: o, Group: SettingsGroupOf(o.Key))).Select(x => new
            {
                key = x.Option.Key, label = x.Option.Label, group = x.Group, kind = x.Option.Kind.ToString(), value = shown[x.Option.Key],
                @default = bases.GetValueOrDefault(x.Option.Key, ""), min = x.Option.Min == double.MinValue ? (double?)null : x.Option.Min,
                max = x.Option.Max >= int.MaxValue ? (double?)null : x.Option.Max, choices = x.Option.Values,
                help = ModelOptions.Descriptions.GetValueOrDefault(x.Option.Key, x.Option.Help), advanced = AdvancedGroups.Contains(x.Group),
                apiOnly = ModelOptions.ApiOnly.Contains(x.Option.Key), load = x.Option.Target == "auto_load", // hidden for a Remote API worker, as on the desktop
            }).ToList(),
            custom = card.IsCustom ? new { kind = card.Config.Kind, description = card.Config.Description, systemPrompt = card.Config.SystemPrompt } : null,
            presets = WebPresetsBody(s, shown, shared),
        };
    }

    // POST /api/worker/{role}/...: remove, rename and shown on the UI thread; the rest read the worker's GGUF first.
    private async Task<(int, object)> WebWorkerPost(string rest, JsonElement body)
    {
        var cut = rest.IndexOf('/');
        if (cut <= 0)
            return WebNotFound;
        var (role, action) = (rest[..cut], rest[(cut + 1)..]);
        return action switch
        {
            "primary" => await WebSaved(role, await Dispatcher.InvokeAsync(() => WebUsePrimary(role, WebField(body, "model").Trim()))),
            "remote" => await WebSaved(role, await Dispatcher.InvokeAsync(() => WebApplyRemote(role, body))),
            "preset/apply" or "preset/save" or "preset/delete" => await WebPreset(role, action["preset/".Length..], body),
            "remove" or "rename" or "shown" => await Dispatcher.InvokeAsync(() => CardFor(role) is not { } card ? WebNotFound
                : action == "remove" ? WebRemoveWorker(card) : action == "rename" ? WebRenameWorker(card, body) : WebShowWorker(card, body)),
            "save" => await WebSaved(role, await WebWithWorker(role, (s, bases, _) => WebSaveWorker(s, bases, body))),
            "vram" => await WebWithWorker(role, (s, bases, gguf) => WebVram(s, bases, gguf, body)),
            "model" => await WebPickModel(role, WebField(body, "label")),
            "local" => await WebSaved(role, await WebWithWorker(role, (s, _, _) => WebToLocal(s))),
            _ => WebNotFound,
        };
    }

    private static readonly (int, object) WebOk = (200, new { ok = true });

    private (int, object) WebRemoveWorker(RoleCard card)
    {
        if (!card.IsCustom)
            return (403, new { error = "A built-in worker can't be removed" });
        if (ModelsWorking)
            return (409, new { error = WebBusyText });
        HoldWorkerTab(() =>
        {
            var selected = card.IsSelected;
            RemoveWorker(card);
            if (selected) // its tab is gone: LEAD's shows, as when a shown tab is hidden
                SelectWorker(CardFor("planner"));
        });
        Log($"{card.Title}: removed");
        return WebOk;
    }

    private (int, object) WebRenameWorker(RoleCard card, JsonElement body)
    {
        if (!card.IsCustom)
            return (403, new { error = "A built-in worker can't be renamed" });
        if (RenameTo(card, WebField(body, "title")) is { } problem)
            return (400, new { error = problem });
        WebRefreshTab(card);
        return WebOk;
    }

    private (int, object) WebShowWorker(RoleCard card, JsonElement body)
    {
        if (card.Role == "planner")
            return (403, new { error = "LEAD is always shown in Chat" });
        if (WebFlag(body, "shown") is not { } shown)
            return (400, new { error = "Bad value for shown" });
        SetChatCard(card.Role, shown, select: false);
        return WebOk;
    }

    private static bool? WebFlag(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    // body.fields as strings (null: not an object; absent: none).
    private static Dictionary<string, string>? WebFields(JsonElement body) =>
        body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("fields", out var f) ? new()
        : f.ValueKind != JsonValueKind.Object ? null
        : f.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind switch { JsonValueKind.String => p.Value.GetString()!, JsonValueKind.Null => "", _ => p.Value.GetRawText() });

    // The worker tab shows the phone's change, unless the desktop has unsaved edits there (RefreshWorkerTab without force): then
    // the last write wins, by design. The phone's save is on disk, the tab keeps its edits, and the desktop's Save overwrites the
    // phone's values with them. Only the routes that force the tab's rebuild (primary, remote) refuse while it is dirty.
    private void WebRefreshTab(RoleCard card) => HoldWorkerTab(() =>
    {
        PrefillCards();
        if (card.IsSelected)
            RefreshWorkerTab();
    });

    // A write's answer: {ok, note, worker} (the worker read again), or the write's refusal as it is.
    private async Task<(int, object)> WebSaved(string role, (int Status, object Body) result)
    {
        if (result.Status != 200 || result.Body is not string note)
            return result;
        var worker = await WebWorker(role);
        return (200, new { ok = true, note, worker = worker.Item2 });
    }

    // The desktop Save's data part (SaveWorkerModel): every field the tab would post (Prefill) with the phone's laid over it.
    private (int, object) WebSaveWorker(WebWorkerState s, Dictionary<string, string> bases, JsonElement body)
    {
        if (WebFields(body) is not { } phone)
            return (400, new { error = "fields must be an object" });
        if (phone.Keys.FirstOrDefault(k => ModelOptions.All.All(o => o.Key != k)) is { } unknown)
            return (400, new { errors = new Dictionary<string, string> { [unknown] = "Unknown option" } });
        var (card, w) = (s.Card, s.Card.Config);
        if (!card.IsCustom && !string.Equals(ActiveConfigPath(), s.Path, StringComparison.OrdinalIgnoreCase))
            return (409, new { error = "The project changed on the PC: open the worker again" });
        var shown = ModelOptions.Prefill(s.Current, bases);
        var fields = new Dictionary<string, string>(shown);
        foreach (var (key, value) in phone)
            fields[key] = value;
        if (s.Remote is not null) // a remote worker's load options keep their values (unchecked and unshown there; the desktop's Save keeps them too)
            foreach (var o in ModelOptions.All.Where(o => o.Target == "auto_load"))
                fields[o.Key] = s.Current.GetValueOrDefault(o.Key, "");
        WorkerEdit? edit = null;
        if (card.IsCustom)
        {
            var c = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("custom", out var given) && given.ValueKind == JsonValueKind.Object ? given : default;
            string Text(string name, string now) => c.ValueKind == JsonValueKind.Object && c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : now;
            edit = new(Text("title", card.Title), Text("kind", w.Kind), Text("description", w.Description), Text("systemPrompt", w.SystemPrompt));
            if (edit.Kind is not ("specialist" or "reviewer"))
                return (400, new { errors = new Dictionary<string, string> { ["Kind"] = "Kind is specialist or reviewer" } });
        }
        // COMPACTOR on MAIN's model keeps following it until a value of its own is saved
        else if (s.FollowsMain && fields.All(p => shown[p.Key] == p.Value))
            return (200, ModelsWorking ? WebLoadNote : "");
        Dictionary<string, string> errors;
        try
        {
            errors = SaveWorkerModel(card, s.Path, s.Role, fields, null, s.Remote, s.Remote, bases, edit,
                card.IsCustom || s.FollowsMain ? "" : s.Role!["chat_system_prompt"]?.ToString() ?? "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException)
        {
            Log($"Could not update config.json: {e.Message}");
            return (500, new { error = "Could not update config.json: " + e.Message });
        }
        if (errors.Count > 0)
            return (400, new { errors });
        WebRefreshTab(card);
        return (200, ModelsWorking ? WebLoadNote : "");
    }

    // The GPU estimate for the shown values with the phone's laid over them (bytes; nulls when the model's GGUF says too little).
    private (int, object) WebVram(WebWorkerState s, Dictionary<string, string> bases, WebGgufRead gguf, JsonElement body)
    {
        if (WebFields(body) is not { } phone)
            return (400, new { error = "fields must be an object" });
        _gpuMemory ??= Task.Run(LauncherLogic.GpuMemoryBytes);
        var options = ModelOptions.Prefill(s.Current, bases);
        foreach (var (key, value) in phone)
            options[key] = value;
        var estimate = s.Remote is not null ? null
            : VramEstimate.WithWhole(gguf.Meta, gguf.Files.Model, gguf.Files.Vision, options.ToDictionary(p => p.Key, p => p.Value.Trim()), gguf.MtpHead);
        return (200, new
        {
            gpu = estimate is { } e ? new { weights = e.Gpu.Weights, kv = e.Gpu.Kv, compute = e.Gpu.Compute, vision = e.Gpu.Vision, total = e.Gpu.Total } : null,
            whole = estimate?.Whole,
            gpuTotal = _gpuMemory is { IsCompletedSuccessfully: true, Result: > 0 } t ? t.Result : null,
        });
    }

    // A local model for the worker: a built-in role's Save with only the choice; a custom worker's AssignModel (an empty Save
    // would drop its options).
    private async Task<(int, object)> WebPickModel(string role, string label)
    {
        var entry = (await WebLocalEntries()).FirstOrDefault(e => e.Choice.Label == label);
        return await WebSaved(role, await WebWithWorker(role, (s, _, _) =>
        {
            if (ModelsWorking)
                return (409, new { error = WebBusyText });
            if (entry is null)
                return (400, new { error = "That model isn't on the PC" });
            if (entry.Group == PickerGroups[2])
                return (400, new { error = "An image model can't run as a worker" });
            if (s.Card.IsCustom)
            {
                AssignModel(s.Card, entry.Choice);
                WebRefreshTab(s.Card);
                return (200, "");
            }
            return WebSaveRole(s, entry.Choice);
        }));
    }

    // Switch to Local: an API or primary-PC worker back on its local model (the desktop Save's toLocal).
    private (int, object) WebToLocal(WebWorkerState s)
    {
        if (ModelsWorking)
            return (409, new { error = WebBusyText });
        if (s.Remote is null)
            return (200, "");
        if (!s.Card.IsCustom)
            return WebSaveRole(s, null);
        var w = s.Card.Config;
        // its stored values as they are (nothing inherited is dropped or added), Remote cleared and its local model restored
        var errors = SaveWorkerModel(s.Card, "", null, ModelOptions.Prefill(s.Current, new Dictionary<string, string>()), null, null, s.Remote, new(),
            new(s.Card.Title, w.Kind, w.Description, w.SystemPrompt), "");
        if (errors.Count > 0)
            return (400, new { errors });
        WebRefreshTab(s.Card);
        return (200, "");
    }

    // A built-in role's Save with no option changes: a model choice, or (choice null) Switch to Local.
    private (int, object) WebSaveRole(WebWorkerState s, ModelChoice? choice)
    {
        if (!string.Equals(ActiveConfigPath(), s.Path, StringComparison.OrdinalIgnoreCase))
            return (409, new { error = "The project changed on the PC: open the worker again" });
        Dictionary<string, string> errors;
        try { errors = SaveWorkerModel(s.Card, s.Path, s.Role, new(), choice, null, s.Remote, new(), null, s.FollowsMain ? "" : s.Role!["chat_system_prompt"]?.ToString() ?? ""); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException)
        {
            Log($"Could not update config.json: {e.Message}");
            return (500, new { error = "Could not update config.json: " + e.Message });
        }
        if (errors.Count > 0)
            return (400, new { errors });
        WebRefreshTab(s.Card);
        return (200, "");
    }

    private async Task<(int, object)> WebLocalModels()
    {
        var entries = await WebLocalEntries();
        var favorites = await Dispatcher.InvokeAsync(() => _settings.LocalFavorites.ToHashSet());
        return (200, new
        {
            models = entries.Select(e => new
            {
                label = e.Choice.Label, display = e.Name, group = WebPickerGroup(e.Group), size = e.Bytes, quant = e.Quant,
                favorite = favorites.Contains(e.Choice.Label), pickable = e.Group != PickerGroups[2],
            }).ToList(),
        });
    }

    // {kind: local|remote, id, on}: a starred local model (its label) or remote one ("<service id>|<model id>").
    private (int, object) WebFavorite(JsonElement body)
    {
        var (kind, id) = (WebField(body, "kind"), WebField(body, "id").Trim());
        if (kind is not ("local" or "remote") || id.Length == 0 || WebFlag(body, "on") is not { } on)
            return (400, new { error = "Send kind (local or remote), id and on" });
        var list = kind == "local" ? _settings.LocalFavorites : _settings.RemoteFavorites;
        list.Remove(id);
        if (on)
            list.Add(id);
        _settings.Save();
        return WebOk;
    }

    // ---- the primary PC, Remote API services and presets: keys stay on the PC (only hasKey goes out); lists load with the stored key.
    private static bool WebNetFailure(Exception e) => e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or JsonException;

    private static string? WebKey(string name) => name.Trim() is not { Length: > 0 } n ? null
        : ApiKeys.Get(n) is { Length: > 0 } key ? key : Environment.GetEnvironmentVariable(n) is { Length: > 0 } env ? env : null;

    private async Task<(int, object)> WebPrimaryModels()
    {
        if (await Dispatcher.InvokeAsync(() => _settings.ShareHost) is not { Length: > 0 } host)
            return (404, new { error = "No primary PC is set" });
        try
        {
            var models = await RemoteCatalog.FetchModelsAsync(_remoteHttp, ModelShare.ChatUrl(host), WebKey(ModelShare.KeyEnv), CancellationToken.None);
            return (200, new { host, models = models.Select(m => new { id = m.Id }).ToList() });
        }
        catch (Exception e) when (WebNetFailure(e)) { return (502, new { error = "Could not list the primary PC's models: " + e.Message }); }
    }

    // A primary PC model for the worker, as Model Settings' UsePrimaryModel (no unsaved-edits dialog: the phone has none open).
    private (int, object) WebUsePrimary(string role, string model)
    {
        if (CardFor(role) is not { } card)
            return WebNotFound;
        if (_settings.ShareHost is not { Length: > 0 } host)
            return (404, new { error = "No primary PC is set" });
        if (CodeBusy)
            return (409, new { error = WebBusyText });
        if (WebWorkerEditsRefusal() is { } dirty)
            return dirty;
        if (model.Length == 0)
            return (400, new { error = "Send the model's id" });
        return ApplyRemoteRole(card, new(ModelShare.ChatUrl(host), model, ModelShare.KeyEnv, ""), "in Settings › Models") is { } problem ? (400, new { error = problem }) : (200, "");
    }

    // ApplyRemoteRole rebuilds the worker tab, discarding its edits: the desktop asks first (DiscardWorkerEdits), the phone refuses.
    private (int, object)? WebWorkerEditsRefusal() => WorkerEditsPending ? (409, new { error = "Save or discard the worker settings on the PC first" }) : null;

    private (int, object) WebRemoteServices() => (200, new
    {
        services = RemoteCatalog.Services.Select(s => new
        {
            id = s.Id, name = s.Name, baseUrl = s.BaseUrl, keyEnv = s.KeyEnv, hasKey = WebHasKey(s.KeyEnv), hasProviders = s.HasProviders, modelsNeedKey = s.ModelsNeedKey,
        }).ToList(),
        favorites = _settings.RemoteFavorites.ToList(),
    });

    // A service's model list with its stored key (a custom URL's: none); vision is per provider on OpenRouter, so null here.
    // ponytail: a custom baseUrl may be any address the PC reaches, a LAN one too; the route is the owner's only and sends no key.
    private async Task<(int, object)> WebRemoteModels(string id, string baseUrl)
    {
        if (RemoteCatalog.Services.FirstOrDefault(s => s.Id == id) is not { } service)
            return (400, new { error = "Unknown service" });
        var custom = service.BaseUrl.Length == 0;
        var url = custom ? baseUrl.Trim() : service.BaseUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (400, new { error = "Send the custom server's http(s) address as baseUrl" });
        var key = custom ? null : WebKey(service.KeyEnv);
        if (service.ModelsNeedKey && !custom && key is null)
            return (409, new { error = $"Add a key for {service.Name} on the PC" });
        try
        {
            var models = await RemoteCatalog.FetchModelsAsync(_remoteHttp, url, key, CancellationToken.None);
            return (200, new
            {
                models = models.Select(m => new { id = m.Id, name = m.Name, vision = (bool?)null, priceIn = m.InputPerM, priceOut = m.OutputPerM, context = m.ContextLength }).ToList(),
            });
        }
        catch (Exception e) when (WebNetFailure(e)) { return (502, new { error = $"Could not load {service.Name}'s models: {e.Message}" }); }
    }

    // OpenRouter's providers for a model; tag is what the worker's provider order lists.
    private async Task<(int, object)> WebRemoteProviders(string model)
    {
        if (model.Trim().Length == 0)
            return (400, new { error = "Send the model's id" });
        try
        {
            var rows = await RemoteCatalog.FetchProvidersAsync(_remoteHttp, model, CancellationToken.None, WebKey("OPENROUTER_API_KEY"));
            return (200, new
            {
                providers = rows.Select(r => new
                {
                    name = r.Name, tag = r.Tag, priceIn = r.InputPerM, priceOut = r.OutputPerM, latency = r.LatencySeconds, tps = r.ThroughputTps, uptime = r.Uptime,
                    quant = r.Quantization, vision = r.Vision,
                }).ToList(),
            });
        }
        catch (Exception e) when (WebNetFailure(e)) { return (502, new { error = "Could not load OpenRouter's providers: " + e.Message }); }
    }

    // {service, baseUrl (custom only), model, provider, sortPrice, maxInput, maxOutput (services with providers only)} -> ApplyRemoteRole.
    // The key variable never comes from the phone: a catalog service's own; a custom server's the worker's current one while the
    // address stays the same, else none, which is refused (a new custom server is set up on the PC, where its key is chosen).
    private (int, object) WebApplyRemote(string role, JsonElement body)
    {
        if (CardFor(role) is not { } card)
            return WebNotFound;
        if (RemoteCatalog.Services.FirstOrDefault(s => s.Id == WebField(body, "service")) is not { } service)
            return (400, new { error = "Unknown service" });
        if (CodeBusy) // as the Remote API page's Apply
            return (409, new { error = WebBusyText });
        if (WebWorkerEditsRefusal() is { } dirty)
            return dirty;
        string Text(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : WebField(body, name).Trim();
        var custom = service.BaseUrl.Length == 0;
        var url = custom ? Text("baseUrl").TrimEnd('/') : service.BaseUrl;
        var keyEnv = service.KeyEnv;
        if (custom)
        {
            LauncherLogic.RemoteRole? now;
            try { now = WebState(card).Remote; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException) { now = null; }
            keyEnv = now is not null && now.BaseUrl.Trim().TrimEnd('/') == url ? now.KeyEnv : "";
            if (keyEnv.Trim().Length == 0)
                return (400, new { error = "A new custom server is set up on the PC's Remote API page, where its key variable is chosen" });
        }
        var remote = service.HasProviders // the price settings are OpenRouter's
            ? new LauncherLogic.RemoteRole(url, Text("model"), keyEnv, "", Text("provider"), WebFlag(body, "sortPrice") == true, Text("maxInput"), Text("maxOutput"))
            : new LauncherLogic.RemoteRole(url, Text("model"), keyEnv, "");
        return ApplyRemoteRole(card, remote, "on the PC's Remote API page") is { } problem ? (400, new { error = problem }) : (200, "");
    }

    // The primary PC's presets when the worker runs there (null: it doesn't); throws when they can't be read.
    private async Task<List<ModelPreset>?> WebPrimaryPresets(string role)
    {
        var host = await Dispatcher.InvokeAsync(() =>
        {
            try { return CardFor(role) is { } card && WebSource(WebState(card).Remote) == "primary" ? _settings.ShareHost : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException) { return null; }
        });
        if (host is null)
            return null;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        return ModelShare.ParsePresets(await ImageShare.FetchPresetsAsync(http, ModelShare.ImagesUrl(host), WebKey(ModelShare.KeyEnv) ?? "", CancellationToken.None));
    }

    // The model a worker's presets are for (its remote model id, else the card's name) and its quantization ("" remote or none).
    private static (string Model, string Quant) WebPresetModel(WebWorkerState s, Dictionary<string, string> shown) => s.Remote is { } r ? (r.Model.Trim(), "")
        : (s.Card.ModelName, shown.GetValueOrDefault("gguf_variant", "").Trim() is { Length: > 0 } v ? v : s.Card.Quant is "—" ? "" : s.Card.Quant);

    private object WebPresetsBody(WebWorkerState s, Dictionary<string, string> shown, List<ModelPreset>? shared)
    {
        var (model, quant) = WebPresetModel(s, shown);
        var active = shared is null && _settings.ActiveModelPresets.GetValueOrDefault(s.Card.Role) is { Length: > 0 } name
            && _settings.ModelPresets.Any(p => PresetMine(p, model, quant) && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? name : "";
        return new
        {
            active,
            groups = ModelPresetGroups(shared ?? _settings.ModelPresets, model, quant, s.Remote is not null || shared is not null).Where(g => g.Items.Count > 0)
                .Select(g => new { name = g.Name, items = g.Items.Select(p => new { name = p.Name, model = p.Model, quant = p.Quant }).ToList() }).ToList(),
        };
    }

    // The named preset, in the menu's order (this model's, other quantizations', other models').
    private static ModelPreset? WebFindPreset(IReadOnlyList<ModelPreset> presets, string model, string quant, bool mineOnly, string name) =>
        ModelPresetGroups(presets, model, quant, mineOnly).SelectMany(g => g.Items).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // apply {name} -> {fields, systemPrompt ("": none, keep the current), model (the preset's model when another's, else null)}; save {name, fields} / delete {name} -> {ok, presets}. On the primary PC: its presets, changed there.
    private async Task<(int, object)> WebPreset(string role, string action, JsonElement body)
    {
        var name = WebField(body, "name").Trim();
        if (name.Length == 0)
            return (400, new { error = "Send the preset's name" });
        List<ModelPreset>? shared;
        try { shared = await WebPrimaryPresets(role); }
        catch (Exception e) when (WebNetFailure(e)) { return (502, new { error = "Could not read the primary PC's presets: " + e.Message }); }
        var host = "";
        var done = await WebWithWorker(role, (s, bases, _) => { host = _settings.ShareHost; return WebPresetAct(s, bases, action, name, body, shared); });
        if (done.Item2 is not string change) // done here, or refused
            return done;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            shared = ModelShare.ParsePresets(await ImageShare.ChangePresetAsync(http, ModelShare.ImagesUrl(host), WebKey(ModelShare.KeyEnv) ?? "", change, CancellationToken.None));
        }
        catch (Exception e) when (WebNetFailure(e)) { return (502, new { error = $"The preset was not {(action == "save" ? "saved" : "deleted")} on the primary PC: {e.Message}" }); }
        return await WebWithWorker(role, (s, bases, _) => (200, new { ok = true, presets = WebPresetsBody(s, ModelOptions.Prefill(s.Current, bases), shared) }));
    }

    // UI thread. A string answer is the change to send to the primary PC.
    private (int, object) WebPresetAct(WebWorkerState s, Dictionary<string, string> bases, string action, string name, JsonElement body, List<ModelPreset>? shared)
    {
        var shown = ModelOptions.Prefill(s.Current, bases);
        var (model, quant) = WebPresetModel(s, shown);
        var mineOnly = s.Remote is not null || shared is not null;
        var (card, w) = (s.Card, s.Card.Config);
        object Listed() => new { ok = true, presets = WebPresetsBody(s, shown, shared) };
        if (action == "save")
        {
            if (WebFields(body) is not { } phone)
                return (400, new { error = "fields must be an object" });
            if (phone.Keys.FirstOrDefault(k => ModelOptions.All.All(o => o.Key != k)) is { } unknown)
                return (400, new { errors = new Dictionary<string, string> { [unknown] = "Unknown option" } });
            foreach (var (key, value) in phone)
                shown[key] = value;
            // as the desktop's ShownOptions / ShownPrompt: the options shown but the quantization, the prompt unless it is the default
            var options = shown.Where(p => p.Key != "gguf_variant" && p.Value.Trim().Length > 0).ToDictionary(p => p.Key, p => p.Value);
            var prompt = card.IsCustom ? (w.SystemPrompt.Trim().Length > 0 && w.SystemPrompt != LauncherLogic.DefaultSystemPrompt(w.Kind) ? w.SystemPrompt : "")
                : s.FollowsMain ? "" : s.Role?["chat_system_prompt"]?.ToString() ?? "";
            if (shared is not null)
                return (200, ModelShare.PresetChangeJson("save", name, model, options, prompt));
            var same = _settings.ModelPresets.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && PresetSameModel(p, model) && !PresetOtherQuant(p, model, quant));
            PutModelPreset(card.Role, new ModelPreset { Name = name, Model = model, Quant = quant, Options = options, SystemPrompt = prompt }, same); // a same name: replaced (the phone asked)
            return (200, Listed());
        }
        if (WebFindPreset(shared ?? _settings.ModelPresets, model, quant, mineOnly, name) is not { } preset)
            return (404, new { error = "No such preset" });
        if (action == "delete")
        {
            if (shared is not null)
                return (200, ModelShare.PresetChangeJson("delete", preset.Name, preset.Model, new Dictionary<string, string>(), ""));
            RemoveModelPreset(preset);
            return (200, Listed());
        }
        // apply: values only; load options only with a local model to load (as the desktop's Apply)
        var load = s.Remote is null && s.Load?["model_path"] is not null;
        var fields = preset.Options.Where(p => p.Value.Trim().Length > 0 && ModelOptions.All.FirstOrDefault(o => o.Key == p.Key) is { } o && (load || o.Target != "auto_load"))
            .ToDictionary(p => p.Key, p => p.Value);
        if (shared is null) // the primary PC's is not a local preset Save to could overwrite
            SetActiveModelPreset(card.Role, preset.Name);
        return (200, new { fields, systemPrompt = preset.SystemPrompt.Trim().Length > 0 ? preset.SystemPrompt : "",
            model = preset.Model.Length > 0 && !PresetSameModel(preset, model) ? preset.Model : null });
    }

    // The routes above (WebAppSmoke, while on), on the smoke's data folder with a fake catalog (RemoteApiFakeHandler: no network):
    // services and key status, a service needing a key with none 409, models and providers, a custom worker put on a stub service (the
    // key variable the worker's, never the phone's; refused while busy or with unsaved desktop edits) and back to local, primary models with no primary PC 404, and a preset saved, listed, applied and deleted. The primary PC's presets
    // (ImageShare to ModelShare.ImagesUrl, a fixed https host or port 8782) are not stubbed.
    private async Task<bool> WebModelsSmoke2(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: models 2: {what}"); return false; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        async Task<(int Status, JsonNode? Body)> Send(string path, string? json = null)
        {
            using var request = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{port}{path}")
                { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("Tailscale-User-Login", "smoke@example.com");
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
        }
        if (!ActiveConfigPath().StartsWith(AppSettings.DataRoot, StringComparison.OrdinalIgnoreCase))
            return Fail($"the active config is not the smoke's: {ActiveConfigPath()}");
        var (remoteHttp, shareHost, mistral) = (_remoteHttp, _settings.ShareHost, Environment.GetEnvironmentVariable("MISTRAL_API_KEY"));
        string? custom = null;
        try
        {
            _remoteHttp = new HttpClient(new RemoteApiFakeHandler());
            _settings.ShareHost = "";
            Environment.SetEnvironmentVariable("MISTRAL_API_KEY", null); // this process only

            var sv = await Send("/api/remote/services");
            var services = sv.Body?["services"]?.AsArray();
            if (sv.Status != 200 || sv.Body?["favorites"] is not JsonArray || services?.FirstOrDefault(x => x!["id"]!.ToString() == "openrouter") is not { } or ||
                or["hasProviders"]?.GetValue<bool>() != true || or["keyEnv"]?.ToString() != "OPENROUTER_API_KEY" ||
                services.FirstOrDefault(x => x!["id"]!.ToString() == "mistral")?["hasKey"]?.GetValue<bool>() != false)
                return Fail($"services: {sv.Status} {sv.Body}");
            var nokey = await Send("/api/remote/models?service=mistral");
            if (nokey.Status != 409 || nokey.Body?["error"]?.ToString() != "Add a key for Mistral on the PC")
                return Fail($"models with no key: {nokey.Status} {nokey.Body}");
            var ms = await Send("/api/remote/models?service=openrouter");
            if (ms.Status != 200 || ms.Body?["models"]?.AsArray() is not { Count: 3 } models || models[0]?["id"]?.ToString() != "openai/gpt-4o-mini" || models[0]?["context"] is null)
                return Fail($"models: {ms.Status} {ms.Body}");
            var pv = await Send("/api/remote/providers?model=z-ai/glm-5.3-flash");
            if (pv.Status != 200 || pv.Body?["providers"]?.AsArray() is not { Count: > 0 } rows || rows[0]?["tag"] is null || rows[0]?["priceIn"] is null)
                return Fail($"providers: {pv.Status} {pv.Body}");
            if ((await Send("/api/primary/models")).Status != 404)
                return Fail("primary models with no primary PC were not 404");

            custom = (await Send("/api/workers/add", "{}")).Body?["role"]?.ToString();
            var w = "/api/worker/" + custom;
            // the key variable is never the phone's: a new custom address is refused, the same one keeps the worker's
            var fresh = await Send(w + "/remote", """{"service":"custom","baseUrl":"http://127.0.0.1:9/v1","model":"stub-model","keyEnv":"SMOKE_PHONE_KEY"}""");
            if (fresh.Status != 400 || CardFor(custom)!.Config.Remote is not null)
                return Fail($"a new custom server from the phone: {fresh.Status} {fresh.Body}");
            CardFor(custom)!.Config.Remote = new("http://127.0.0.1:9/v1", "old-model", "SMOKE_KEEP_KEY", "");
            var ap = await Send(w + "/remote", """{"service":"custom","baseUrl":"http://127.0.0.1:9/v1/","model":"stub-model","keyEnv":"SMOKE_PHONE_KEY","provider":"x","maxInput":"1"}""");
            if (ap.Status != 200 || ap.Body?["worker"]?["source"]?.ToString() != "api" || ap.Body["worker"]?["remote"]?["model"]?.ToString() != "stub-model" ||
                CardFor(custom)!.Config.Remote is not { KeyEnv: "SMOKE_KEEP_KEY", Provider: "", MaxInput: "" } || (await Send(w)).Body?["model"]?["name"] is null)
                return Fail($"apply remote (keyEnv {CardFor(custom)!.Config.Remote?.KeyEnv}): {ap.Status} {ap.Body}");
            var moved = await Send(w + "/remote", """{"service":"custom","baseUrl":"https://phone.example.invalid/v1","model":"stub-model","keyEnv":"SMOKE_PHONE_KEY"}""");
            if (moved.Status != 400 || CardFor(custom)!.Config.Remote is not { KeyEnv: "SMOKE_KEEP_KEY", BaseUrl: "http://127.0.0.1:9/v1" })
                return Fail($"another custom address from the phone: {moved.Status} {moved.Body}");
            _chatBusy = true;
            try
            {
                var busy = await Send(w + "/remote", """{"service":"openrouter","model":"openai/gpt-4o-mini"}""");
                if (busy.Status != 409 || busy.Body?["error"]?.ToString() != WebBusyText || CardFor(custom)!.Config.Remote?.Model != "stub-model")
                    return Fail($"remote while busy: {busy.Status} {busy.Body}");
            }
            finally { _chatBusy = false; }
            var (shownWas, dirtyWas) = (_shownWorker, _workerDirty);
            try
            {
                (_shownWorker, _workerDirty) = (_shownWorker ?? CardFor("planner"), () => true); // the desktop's worker tab with unsaved edits
                var dirty = await Send(w + "/remote", """{"service":"openrouter","model":"openai/gpt-4o-mini"}""");
                if (dirty.Status != 409 || dirty.Body?["error"]?.ToString() != "Save or discard the worker settings on the PC first" || CardFor(custom)!.Config.Remote?.Model != "stub-model")
                    return Fail($"remote with unsaved desktop edits: {dirty.Status} {dirty.Body}");
            }
            finally { (_shownWorker, _workerDirty) = (shownWas, dirtyWas); }
            if ((await Send(w + "/remote", """{"service":"nope","model":"x"}""")).Status != 400 || (await Send("/api/worker/nope/remote", """{"service":"openrouter","model":"x"}""")).Status != 404)
                return Fail("a bad service or role was not refused");

            var ps = await Send(w + "/preset/save", """{"name":"Smoke preset","fields":{"temperature":"0.55"}}""");
            if (ps.Status != 200 || ps.Body?["presets"]?["active"]?.ToString() != "Smoke preset" ||
                ps.Body["presets"]?["groups"]?.AsArray().FirstOrDefault(g => g!["name"]!.ToString() == "THIS MODEL")?["items"]?.AsArray()
                    .Any(i => i!["name"]!.ToString() == "Smoke preset" && i["model"]!.ToString() == "stub-model") != true ||
                (await Send(w)).Body?["presets"]?["groups"]?.AsArray().Count is not > 0)
                return Fail($"preset save: {ps.Status} {ps.Body}");
            var pa = await Send(w + "/preset/apply", """{"name":"smoke preset"}""");
            if (pa.Status != 200 || pa.Body?["fields"]?["temperature"]?.ToString() != "0.55" || ((JsonObject)pa.Body["fields"]!).ContainsKey("max_seq_length") ||
                pa.Body["systemPrompt"]?.ToString() != "" || pa.Body is not JsonObject applied || !applied.ContainsKey("model") || applied["model"] is not null)
                return Fail($"preset apply: {pa.Status} {pa.Body}");
            var pd = await Send(w + "/preset/delete", """{"name":"Smoke preset"}""");
            if (pd.Status != 200 || _settings.ModelPresets.Any(p => p.Name == "Smoke preset") || pd.Body?["presets"]?["groups"]?.AsArray().Count != 0 ||
                (await Send(w + "/preset/delete", """{"name":"Smoke preset"}""")).Status != 404)
                return Fail($"preset delete: {pd.Status} {pd.Body}");

            var lo = await Send(w + "/local", "{}");
            if (lo.Status != 200 || lo.Body?["worker"]?["source"]?.ToString() != "local")
                return Fail($"back to local: {lo.Status} {lo.Body}");
            return true;
        }
        finally
        {
            (_remoteHttp, _settings.ShareHost) = (remoteHttp, shareHost);
            Environment.SetEnvironmentVariable("MISTRAL_API_KEY", mistral);
            _settings.ModelPresets.RemoveAll(p => p.Name == "Smoke preset");
            if (custom is not null && CardFor(custom) is { } left)
                RemoveWorker(left);
        }
    }

    // The routes above (WebAppSmoke, while on), on the smoke's own data folder (its worker config, a models folder with one
    // stand-in GGUF, an empty Hugging Face cache): the list (LEAD first); add, rename (a bad title 400), shown; a worker's options;
    // a sampling value saved to config.json (LEAD) and settings.json (a custom worker) and read back, a bad one 400; the GPU
    // estimate with no model; the local list; a model picked; a stand-in remote worker's key status and Switch to Local; a
    // favorite on and off; add while busy 409; remove (a built-in 403); unknown roles 404.
    private async Task<bool> WebModelsSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: models: {what}"); return false; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        async Task<(int Status, JsonNode? Body)> Send(string path, string? json = null)
        {
            using var request = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{port}{path}")
                { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("Tailscale-User-Login", "smoke@example.com");
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
        }
        async Task<JsonArray> Workers() => (await Send("/api/workers")).Body!["workers"]!.AsArray();
        static JsonNode? Option(JsonNode? worker, string key) => worker?["options"]?.AsArray().FirstOrDefault(o => o!["key"]!.ToString() == key);

        if (!ActiveConfigPath().StartsWith(AppSettings.DataRoot, StringComparison.OrdinalIgnoreCase))
            return Fail($"the active config is not the smoke's: {ActiveConfigPath()}");
        var (folder, hubWas) = (_settings.ModelsFolder, Environment.GetEnvironmentVariable("HF_HUB_CACHE"));
        var models = Path.Combine(AppSettings.DataRoot, "smoke-models");
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(Path.Combine(AppSettings.DataRoot, "smoke-hub"));
        File.WriteAllBytes(Path.Combine(models, "smoke-model.gguf"), [0x47, 0x47, 0x55, 0x46, 3, 0, 0, 0]); // a header and nothing else
        string? custom = null;
        try
        {
            _settings.ModelsFolder = models;
            Environment.SetEnvironmentVariable("HF_HUB_CACHE", Path.Combine(AppSettings.DataRoot, "smoke-hub"));

            var list = await Send("/api/workers");
            if (list.Status != 200 || list.Body?["busy"] is null || list.Body["workers"]?.AsArray() is not [{ } lead, ..] || lead["role"]?.ToString() != "planner" ||
                lead["shown"]?.GetValue<bool>() != true || lead["source"] is null || lead["model"] is null || lead["problem"] is null)
                return Fail($"list: {list.Status} {list.Body}");
            // VOICE, the calls' agent, comes right after COMPACTOR, and its page answers
            var roles = list.Body["workers"]!.AsArray().Select(x => x?["role"]?.ToString()).ToList();
            if (roles.IndexOf("compactor") < 0 || roles.IndexOf("voice") != roles.IndexOf("compactor") + 1 ||
                await Send("/api/worker/voice") is not { Status: 200, Body: { } voiceWorker } || voiceWorker["options"] is not JsonArray)
                return Fail($"voice in the list: {string.Join(",", roles)}");

            var a = await Send("/api/workers/add", "{}");
            custom = a.Body?["role"]?.ToString();
            if (a.Status != 200 || custom is null || (await Workers()).FirstOrDefault(w => w!["role"]!.ToString() == custom) is not { } added || added["custom"]?.GetValue<bool>() != true)
                return Fail($"add: {a.Status} {a.Body}");
            var w = "/api/worker/" + custom;
            if ((await Send(w + "/rename", """{"title":"Phone worker"}""")).Status != 200 ||
                (await Workers()).First(x => x!["role"]!.ToString() == custom)!["title"]?.ToString() != "Phone worker")
                return Fail("rename");
            if ((await Send(w + "/rename", """{"title":"  "}""")).Status != 400 || (await Send("/api/worker/planner/rename", """{"title":"X"}""")).Status != 403)
                return Fail("a blank title or a built-in's rename was not refused");
            foreach (var on in new[] { true, false })
                if ((await Send(w + "/shown", $$"""{"shown":{{(on ? "true" : "false")}}}""")).Status != 200 || _settings.ChatCards.Contains(custom) != on ||
                    (await Workers()).First(x => x!["role"]!.ToString() == custom)!["shown"]?.GetValue<bool>() != on)
                    return Fail($"shown {on}");

            var lw = await Send("/api/worker/planner");
            if (lw.Status != 200 || Option(lw.Body, "temperature") is not { } temperature || Option(lw.Body, "max_seq_length")?["load"]?.GetValue<bool>() != true ||
                temperature["load"]?.GetValue<bool>() != false ||
                temperature["group"]?.ToString() != "Sampling" || lw.Body!["custom"] is not null || lw.Body["presets"]?["active"]?.ToString() != "")
                return Fail($"worker: {lw.Status} {lw.Body}");

            var s = await Send("/api/worker/planner/save", """{"fields":{"temperature":"0.42"}}""");
            var planner = JsonNode.Parse(File.ReadAllText(ActiveConfigPath()))!["roles"]!["planner"]!.AsObject();
            if (s.Status != 200 || s.Body?["ok"]?.GetValue<bool>() != true || s.Body["note"] is null || ModelOptions.Read(planner)["temperature"] != "0.42" ||
                Option(s.Body["worker"], "temperature")?["value"]?.ToString() != "0.42")
                return Fail($"save LEAD: {s.Status} {s.Body}");
            var cs = await Send(w + "/save", """{"fields":{"temperature":"0.37"},"custom":{"title":"Phone worker","kind":"reviewer","description":"From the phone.","systemPrompt":"Be brief."}}""");
            var saved = AppSettings.Load().Workers.FirstOrDefault(x => x.Id == custom);
            if (cs.Status != 200 || saved?.ModelOptions.GetValueOrDefault("temperature") != "0.37" || saved.Kind != "reviewer" || saved.SystemPrompt != "Be brief." ||
                Option(cs.Body?["worker"], "temperature")?["value"]?.ToString() != "0.37" || cs.Body?["worker"]?["custom"]?["description"]?.ToString() != "From the phone.")
                return Fail($"save custom: {cs.Status} {cs.Body}");
            var bad = await Send("/api/worker/planner/save", """{"fields":{"temperature":"hot"}}""");
            if (bad.Status != 400 || bad.Body?["errors"]?["temperature"] is null)
                return Fail($"a bad value: {bad.Status} {bad.Body}");

            var v = await Send(w + "/vram", """{"fields":{"max_seq_length":"8192"}}""");
            if (v.Status != 200 || v.Body is not JsonObject vram || !vram.ContainsKey("gpu") || vram["gpu"] is not null || vram["whole"] is not null || !vram.ContainsKey("gpuTotal"))
                return Fail($"vram with no model: {v.Status} {v.Body}");

            var l = await Send("/api/models/local");
            // other smokes (--ui-smoke) may have put models of their own in this data folder: find ours by label
            var model = l.Body?["models"]?.AsArray().OfType<JsonNode>().FirstOrDefault(x => x["label"]?.ToString() == "smoke-model");
            if (l.Status != 200 || model is null || model["group"]?.ToString() != "text" ||
                model["pickable"]?.GetValue<bool>() != true || model["favorite"]?.GetValue<bool>() != false || model["size"] is null || model["quant"] is null)
                return Fail($"local models: {l.Status} {l.Body}");
            var m = await Send(w + "/model", """{"label":"smoke-model"}""");
            if (m.Status != 200 || CardFor(custom)?.Config.ModelPath != "smoke-model" || m.Body?["worker"]?["model"]?["label"]?.ToString() != "smoke-model" ||
                AppSettings.Load().Workers.First(x => x.Id == custom).ModelOptions.GetValueOrDefault("temperature") != "0.37")
                return Fail($"model: {m.Status} {m.Body}");
            if ((await Send(w + "/model", """{"label":"nope"}""")).Status != 400)
                return Fail("an unknown model was not 400");

            CardFor(custom)!.Config.Remote = new("https://api.example.invalid/v1", "m", "SMOKE_NO_SUCH_KEY", "");
            var r = await Send(w);
            if (r.Body?["source"]?.ToString() != "api" || r.Body["remote"]?["hasKey"]?.GetValue<bool>() != false || r.Body["remote"]?["service"]?.ToString() != "custom" ||
                r.Body["remote"]?["keyEnv"]?.ToString() != "SMOKE_NO_SUCH_KEY")
                return Fail($"a remote worker: {r.Body}");
            var lo = await Send(w + "/local", "{}");
            if (lo.Status != 200 || CardFor(custom)!.Config.Remote is not null || lo.Body?["worker"]?["source"]?.ToString() != "local")
                return Fail($"local: {lo.Status} {lo.Body}");

            foreach (var on in new[] { true, false })
                if ((await Send("/api/favorites", $$"""{"kind":"local","id":"smoke-model","on":{{(on ? "true" : "false")}}}""")).Status != 200 ||
                    AppSettings.Load().LocalFavorites.Contains("smoke-model") != on ||
                    (await Send("/api/models/local")).Body?["models"]?.AsArray().FirstOrDefault(x => x?["label"]?.ToString() == "smoke-model")?["favorite"]?.GetValue<bool>() != on)
                    return Fail($"favorite {on}");
            if ((await Send("/api/favorites", """{"kind":"other","id":"x","on":true}""")).Status != 400)
                return Fail("a bad favorite was not 400");

            _chatBusy = true;
            try
            {
                var busy = await Send("/api/workers/add", "{}");
                if (busy.Status != 409 || busy.Body?["error"]?.ToString() != WebBusyText || (await Send(w + "/remove", "{}")).Status != 409)
                    return Fail($"add or remove while busy: {busy.Status} {busy.Body}");
            }
            finally { _chatBusy = false; }

            if ((await Send(w + "/remove", "{}")).Status != 200 || (await Workers()).Any(x => x!["role"]!.ToString() == custom) || CardFor(custom) is not null)
                return Fail("remove");
            if ((await Send("/api/worker/planner/remove", "{}")).Status != 403 || CardFor("planner") is null)
                return Fail("a built-in's remove was not 403");
            if ((await Send("/api/worker/nope")).Status != 404 || (await Send("/api/worker/nope/save", "{}")).Status != 404 ||
                (await Send("/api/worker/nope/rename", """{"title":"x"}""")).Status != 404 || (await Send("/api/worker/planner/nope", "{}")).Status != 404)
                return Fail("an unknown role or action was not 404");
            return true;
        }
        finally
        {
            _settings.ModelsFolder = folder;
            Environment.SetEnvironmentVariable("HF_HUB_CACHE", hubWas);
            if (custom is not null && CardFor(custom) is { } left)
                RemoveWorker(left);
        }
    }
}
