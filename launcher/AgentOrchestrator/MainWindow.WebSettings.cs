using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AgentOrchestrator;

// The phone app's Settings tab (MainWindow.WebApp.cs routes /api/settings): every section read from the PC's own state, and
// each write made through the desktop Settings page's own control and handler (the control set, its handler run), so the
// page shows the change and the same refresh calls run. Never a dialog: where the desktop refuses, the phone gets a 409.
public partial class MainWindow
{
    private static readonly (string Key, string Name)[] WebThemes =
        [("Flat", "Flat"), ("Gold", "Track Gold"), ("Glass", "Liquid Glass"), ("Oled", "OLED"), ("Graphite", "Graphite"),
         ("Parchment", "Parchment"), ("ParchmentDark", "Dark Parchment"), ("Beautiful", "Beautiful UI")];

    private object WebSettings() => new
    {
        general = new
        {
            startWithWindows = StartsWithWindows(), notifyWhenDone = _settings.NotifyWhenDone, linkOpen = _settings.LinkOpen,
            followTabs = _settings.FollowTabs is >= 3 and <= 10 ? _settings.FollowTabs : 11, computerUse = _settings.ComputerUse,
            computerApps = _settings.ComputerApps, stats = StatsSummary.Text, phoneApp = new { on = _settings.WebAppOn, address = WebAppAddress ?? "" },
        },
        appearance = new
        {
            theme = _settings.Theme, themes = WebThemes.Select(t => new { key = t.Key, name = t.Name }), accent = AccentKey,
            accents = AccentColors.Select(a => new { key = a.Key, name = a.Label, color = a.Accent }), centerChat = _settings.CenterChat,
            readingSize = _settings.ReadingSize, emoji = _settings.Emoji, contextPercent = _settings.ContextPercent,
        },
        models = new { keepLoaded = _settings.ChatKeepLoaded, multiModel = _settings.MultiModel, share = new { on = _settings.ShareModels, network = ShareNetworkNow() } },
        agents = new
        {
            workflow = _settings.WorkflowMode, codeEngine = _settings.CodeEngine, fullAccess = _settings.FullAccess, superAccess = _settings.SuperAccess,
            sandboxNetwork = _settings.SandboxNetwork,
        },
        plugins = new { ponytail = _settings.Ponytail },
        about = new { version = LauncherLogic.VersionText(), pc = Environment.MachineName },
    };

    // {key, value}: one writable setting (the GET's leaf names; share.on / share.network, or share / network).
    private (int, object) WebSettingsSet(JsonElement body)
    {
        var key = WebField(body, "key");
        var value = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("value", out var v) ? v : default;
        bool? flag = value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : null;
        (int, object) Bad() => (400, new { error = $"Bad value for {key}" });
        (int, object) Ok() => (200, new { ok = true, settings = WebSettings() });
        const string busy = "Wait for the reply or run to finish";

        // A switch: its control set and clicked, as a click on the page does; refused while the page has it locked.
        (int, object) Press(ToggleButton toggle)
        {
            if (flag is not { } on)
                return Bad();
            if (!toggle.IsEnabled)
                return (409, new { error = "Not available on the PC right now" });
            toggle.IsChecked = on;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return Ok();
        }
        // A choice: its value checked, then the control showing it selected (its handler writes it).
        (int, object) Choose(string[] allowed, Action<string> select)
        {
            if (text is null || !allowed.Contains(text))
                return Bad();
            select(text);
            return Ok();
        }
        void Radio(Panel options, string tag) => options.Children.OfType<RadioButton>().First(r => (string)r.Tag == tag).IsChecked = true;

        switch (key)
        {
            case "startWithWindows":
                if (Press(StartupToggle) is { Item1: not 200 } refused)
                    return refused;
                return StartupToggle.IsChecked == flag ? Ok() : (409, new { error = "Could not change Start with Windows" });
            case "notifyWhenDone": return Press(NotifyToggle);
            case "linkOpen": return Choose(["", "app", "default"], t => SelectTagged(LinkOpenBox, t));
            case "followTabs":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var tabs) || tabs is < 3 or > 11)
                    return Bad();
                _settings.FollowTabs = tabs == 11 ? 0 : tabs; // the slider saves only once the window has loaded: written here
                _settings.Save();
                FollowTabsSlider.Value = tabs;
                return Ok();
            case "computerUse": return Press(ComputerToggle);
            case "theme": return Choose(WebThemes.Select(t => t.Key).ToArray(), t => ((RadioButton)FindName($"Theme{t}Radio")).IsChecked = true);
            case "accent": return Choose(AccentColors.Select(a => a.Key).ToArray(), SetAccent);
            case "centerChat": return Press(CenterChatToggle);
            case "readingSize": return Choose(["compact", "comfortable", "large"], t => SelectTagged(ReadingSizeBox, t));
            case "emoji": return Choose(["none", "less", "standard", "more"], t => SelectTagged(EmojiBox, t));
            case "contextPercent": return Press(ContextPercentToggle);
            case "keepLoaded": return Press(ChatKeepToggle);
            case "multiModel": return Choose(["never", "fit", "always"], t => Radio(MultiModelOptions, t));
            case "share" or "share.on":
                if (flag == true && !_settings.ShareModels && ShareNetworkNow() == ModelShare.Cloudflare && !File.Exists(_shareTunnel.Exe))
                    return (409, new { error = "Set up Share models on the PC first: it needs cloudflared downloaded" });
                if (Press(ShareToggle) is { Item1: not 200 } stopped)
                    return stopped;
                return _settings.ShareModels == flag ? Ok() : (409, new { error = _shareState }); // StartShare's reason (llama-server, Tailscale)
            case "network" or "share.network":
                if (text is not (ModelShare.Tailscale or ModelShare.Cloudflare))
                    return Bad();
                if (text == ModelShare.Cloudflare && _settings.ShareModels && !File.Exists(_shareTunnel.Exe))
                    return (409, new { error = "Set up Share models on the PC first: it needs cloudflared downloaded" });
                var network = text == ModelShare.Tailscale ? ShareTailscaleRadio : ShareCloudflareRadio;
                network.IsChecked = true;
                network.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return Ok();
            case "workflow" or "codeEngine":
                var allowed = key == "workflow" ? new[] { "dual", "tri", "solo" } : ["", "pi"];
                if (text is null || !allowed.Contains(text))
                    return Bad();
                if (text != (key == "workflow" ? _settings.WorkflowMode : _settings.CodeEngine) && (_process is not null || _chatBusy))
                    return (409, new { error = busy });
                (key == "workflow" ? text switch { "tri" => WorkflowTriRadio, "solo" => WorkflowSoloRadio, _ => WorkflowDualRadio }
                    : text == "pi" ? EnginePiRadio : EngineBuiltinRadio).IsChecked = true;
                return Ok();
            case "fullAccess": return Press(FullAccessToggle);
            case "superAccess": return Press(SuperAccessToggle);
            case "sandboxNetwork": return Press(SandboxNetworkToggle);
            case "ponytail": return Choose(["", "lite", "full", "ultra"], t => Radio(PonytailLevels, t));
            default: return (400, new { error = $"Unknown setting {key}" });
        }
    }

    // ResetStats_Click after its confirm (the phone confirms on its side).
    private (int, object) WebResetStats()
    {
        ResetStats();
        return (200, new { ok = true, settings = WebSettings() });
    }

    // The Settings routes (WebAppSmoke, while on), on the smoke's own data folder; Start with Windows is not written (its Run
    // key is the real registry's): every section; a switch and a choice persist to settings.json; a theme applies to the
    // window and its accent follows; workflow while a reply runs is refused as the desktop does; an unknown key and a bad
    // value are 400; reset-stats zeroes the usage summary.
    private async Task<bool> WebSettingsSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: settings: {what}"); return false; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        async Task<(int Status, JsonNode? Body)> Send(string path, string? json = null)
        {
            using var request = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{port}{path}")
                { Content = json is null ? null : new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Add("Tailscale-User-Login", "smoke@example.com");
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
        }
        Task<(int Status, JsonNode? Body)> Set(string key, string value) => Send("/api/settings", $$"""{"key":"{{key}}","value":{{value}}}""");

        if (await Send("/api/settings") is not { Status: 200, Body: { } all } ||
            new[] { "general", "appearance", "models", "agents", "plugins", "about" }.FirstOrDefault(s => all[s] is not JsonObject) is { } missing)
            return Fail($"GET: a section is missing ({(await Send("/api/settings")).Body})");
        if (all["general"]!["stats"]?.ToString() != StatsSummary.Text || all["appearance"]!["themes"]!.AsArray().Count != 8 || all["about"]!["pc"]?.ToString() != Environment.MachineName)
            return Fail($"GET: {all}");

        var notify = !_settings.NotifyWhenDone;
        var n = await Set("notifyWhenDone", notify ? "true" : "false");
        if (n.Status != 200 || n.Body?["ok"]?.GetValue<bool>() != true ||
            n.Body["settings"]?["general"]?["notifyWhenDone"]?.GetValue<bool>() != notify || AppSettings.Load().NotifyWhenDone != notify || NotifyToggle.IsChecked != notify)
            return Fail($"notifyWhenDone: {n.Body}");
        var e = await Set("emoji", "\"more\"");
        if (e.Status != 200 || e.Body?["settings"]?["appearance"]?["emoji"]?.ToString() != "more" ||
            AppSettings.Load().Emoji != "more" || (EmojiBox.SelectedItem as ComboBoxItem)?.Tag as string != "more")
            return Fail($"emoji: {e.Body}");

        var background = ((SolidColorBrush)Resources["AppBackground"]).Color;
        var theme = _settings.Theme == "Parchment" ? "Oled" : "Parchment";
        var t = await Set("theme", $"\"{theme}\"");
        if (t.Status != 200 || t.Body?["settings"]?["appearance"] is not { } look ||
            look["theme"]?.ToString() != theme || look["accent"]?.ToString() != AccentKey || look["accent"]?.ToString() != (theme == "Parchment" ? "Clay" : "Purple") ||
            AppSettings.Load().Theme != theme || ((SolidColorBrush)Resources["AppBackground"]).Color == background)
            return Fail($"theme: {t.Body}");

        _chatBusy = true;
        try
        {
            var other = _settings.WorkflowMode == "solo" ? "dual" : "solo";
            var w = await Set("workflow", $"\"{other}\"");
            if (w.Status != 409 || w.Body?["error"]?.ToString() != "Wait for the reply or run to finish" || _settings.WorkflowMode == other)
                return Fail($"workflow while busy: {w.Status} {w.Body}");
        }
        finally { _chatBusy = false; }

        if ((await Set("nonsense", "true")).Status != 400 || (await Set("emoji", "\"lots\"")).Status != 400 || (await Set("notifyWhenDone", "\"yes\"")).Status != 400 ||
            (await Set("followTabs", "12")).Status != 400 || _settings.Emoji != "more")
            return Fail("an unknown key or a bad value was not 400");

        _stats.Lifetime = 123_456;
        _stats.LongestTaskSeconds = 90;
        RefreshProfile();
        var before = StatsSummary.Text;
        var r = await Send("/api/settings/reset-stats", "{}");
        if (r.Status != 200 || _stats.Lifetime != 0 || _stats.LongestTaskSeconds != 0 ||
            r.Body?["settings"]?["general"]?["stats"]?.ToString() is not { } after || after == before || after != StatsSummary.Text)
            return Fail($"reset-stats: {r.Status} {r.Body}");
        return true;
    }
}
