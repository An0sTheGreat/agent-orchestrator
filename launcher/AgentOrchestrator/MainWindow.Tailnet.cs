using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentOrchestrator;

// Tailscale in the profile card (Tailscale.cs): install, sign in (in the browser) and out, and this account's PCs. Every PC
// signed in to the same account finds the others: one not sharing its models and not connected (or whose saved Tailscale host
// stops answering) asks each of the account's online PCs for its share key (GET /share/pair, ServePairAsync), at startup,
// after a sign-in and every minute, and connects to the first that shares, as a pasted connection code does.
public partial class MainWindow
{
    private Tailscale.State? _tailnet; // the last status read; null = Tailscale not installed or not answering
    private bool _tailnetBusy;
    private readonly DispatcherTimer _tailnetTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    private void InitTailnet()
    {
        _tailnetTimer.Tick += async (_, _) => await TailnetTickAsync();
        _tailnetTimer.Start();
        _ = TailnetTickAsync();
    }

    // The status again, then the automatic connection when this PC needs one.
    private async Task TailnetTickAsync()
    {
        if (_tailnetBusy)
            return;
        _tailnetBusy = true;
        try
        {
            _tailnet = await Tailscale.StatusAsync();
            if (_tailnet is { SignedIn: true } state && !_settings.ShareModels && !await SharedHostAnswersAsync())
                await PairAsync(state);
            await SyncProfileAsync();
        }
        finally { _tailnetBusy = false; }
    }

    // A saved host from a Cloudflare code is left alone; a Tailscale one is asked for its models with the saved key.
    private async Task<bool> SharedHostAnswersAsync()
    {
        if (_settings.ShareHost is not { Length: > 0 } host)
            return false;
        if (!ModelShare.IsTailscaleHost(host) || ApiKeys.Get(ModelShare.KeyEnv) is not { Length: > 0 } key)
            return true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await RemoteCatalog.FetchModelsAsync(http, ModelShare.ChatUrl(host), key, CancellationToken.None);
            return true;
        }
        catch (Exception x) when (x is HttpRequestException or TaskCanceledException or InvalidOperationException) { return false; }
    }

    // A PC connected to a primary PC: the newer profile wins, the primary PC's taken here or this one's sent there, so every
    // connected PC shows the same name and picture within a minute of a change on any of them.
    private async Task SyncProfileAsync()
    {
        if (_settings.ShareModels || _settings.ShareHost is not { Length: > 0 } host || ApiKeys.Get(ModelShare.KeyEnv) is not { Length: > 0 } key)
            return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = ModelShare.ImagesUrl(host);
            var mine = OwnProfile();
            if (await ImageShare.FetchProfileAsync(http, url, key, CancellationToken.None) is { } theirs)
            switch (ImageShare.ProfileSync(mine.Changed, theirs.Changed, differ: mine.Name.Trim() != theirs.Name.Trim() || mine.Png != theirs.Png))
            {
                case "pull":
                    TakeProfile(theirs);
                    break;
                case "push":
                    await ImageShare.SendProfileAsync(http, url, key, mine, CancellationToken.None);
                    break;
            }
        }
        catch (Exception x) when (x is HttpRequestException or TaskCanceledException or InvalidOperationException) { } // an older primary PC (404) or not reachable: next minute
        try
        {
            // The token usage too: this PC's own goes there, every PC's comes back (UsageStats.Receive).
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var reply = await ImageShare.ExchangeUsageAsync(http, ModelShare.ImagesUrl(host), key, UsageStats.ReportJson(Environment.MachineName, _stats.Own()), CancellationToken.None);
            if (_stats.Take(reply, Environment.MachineName))
            {
                _stats.Save();
                RefreshProfile();
            }
        }
        catch (Exception x) when (x is HttpRequestException or TaskCanceledException or InvalidOperationException) { } // as above
    }

    // The account's online PCs, each asked for its key; the first that shares becomes this PC's primary PC.
    private async Task PairAsync(Tailscale.State state)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        foreach (var peer in state.Mine.Where(p => p is { Online: true, Ip: not null }))
        {
            Tailscale.Pairing? pairing;
            try
            {
                using var reply = await http.GetAsync(ModelShare.ImagesUrl(peer.Ip!.ToString()) + ImageShare.PairPath);
                pairing = reply.IsSuccessStatusCode ? Tailscale.ParsePair(await reply.Content.ReadAsStringAsync()) : null;
            }
            catch (Exception x) when (x is HttpRequestException or TaskCanceledException) { continue; }
            if (pairing is null)
                continue;
            var host = peer.Ip.ToString();
            ApiKeys.Save(ModelShare.KeyEnv, ShareLabel, pairing.Key);
            _settings.ShareHost = RemoteCatalog.ShareHost = host;
            RefreshWorkerTab(); // the Run on the primary PC switch follows (kept while edits are unsaved)
            _settings.Save();
            var name = pairing.Name.Length > 0 ? pairing.Name : peer.Name;
            ShareNote($"Connected to {name} ({host}) through Tailscale · {await CopySharedPresetsAsync(http, host, pairing.Key)}");
            Log($"Share models: connected to {name} ({host}) through Tailscale");
            Toast($"Connected to {name}'s models through Tailscale");
            return;
        }
    }

    // GET /share/pair on the sharing PC (ImageShare.Server.Pair): the key, for a caller whose Tailscale address belongs to this
    // PC's own signed-in account; 401 for anyone else.
    private async Task<ImageShare.Response> ServePairAsync(ImageShare.Request request)
    {
        var own = await Tailscale.StatusAsync();
        var caller = request.Remote is { } ip && ModelShare.IsTailscale(ip) ? await Tailscale.WhoIsAsync(ip) : null;
        if (!Tailscale.MayPair(request.Remote, caller, own is { SignedIn: true } ? own.Self!.UserId : null) ||
            Environment.GetEnvironmentVariable(ModelShare.OwnKeyEnv) is not { Length: > 0 } key)
            return ImageShare.Error(401, "Only a PC signed in to this PC's Tailscale account can pair");
        await Dispatcher.InvokeAsync(() => Log($"Share models: paired with {request.Remote} through Tailscale"));
        return new(200, Tailscale.PairJson(key, own!.Self!.Name));
    }

    // The profile card's Tailscale part, from the last status read.
    private FrameworkElement TailnetSection(Action close)
    {
        TextBlock Text(string text, string brush = "Muted", double size = 11)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }
        Button Action(string label, Func<Task> click)
        {
            var b = new Button { Content = label, Padding = new Thickness(10, 4, 10, 4), FontSize = 11.5, Margin = new Thickness(0, 4, 6, 0) };
            b.SetResourceReference(StyleProperty, "GhostButton");
            b.Click += async (_, _) => { close(); await click(); };
            return b;
        }
        var panel = new StackPanel { Margin = new Thickness(2, 14, 2, 0) };
        var title = Text("Connect your PCs · Tailscale", "TextPrimary", 12);
        title.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(title);
        var buttons = new WrapPanel();
        if (Tailscale.Exe is null)
        {
            panel.Children.Add(Text("Sign in to Tailscale with the same account on each PC and they connect by themselves: the one sharing its models (Settings > Models) serves the others."));
            buttons.Children.Add(Action("Install Tailscale", async () =>
            {
                Toast("Installing Tailscale…");
                Toast(await Tailscale.InstallAsync() is { } why ? why : "Tailscale is installed: sign in from your profile", null);
                await TailnetTickAsync();
            }));
        }
        else if (_tailnet is not { SignedIn: true } state)
        {
            panel.Children.Add(Text("Sign in with the same account on each PC and they connect by themselves: the one sharing its models (Settings > Models) serves the others."));
            buttons.Children.Add(Action("Sign in with Tailscale", SignInTailnetAsync));
        }
        else
        {
            panel.Children.Add(Text($"Signed in as {(state.Login.Length > 0 ? state.Login : "your Tailscale account")} · this PC {state.Self!.Name}" +
                (state.Self.Ip is { } ip ? $" ({ip})" : "")));
            var mine = state.Mine.OrderByDescending(p => p.Online).ThenBy(p => p.Name).ToList();
            if (mine.Count == 0)
                panel.Children.Add(Text("No other PC on this account yet: sign in with it on your other PCs."));
            foreach (var pc in mine)
            {
                var shared = pc.Ip?.ToString() == _settings.ShareHost && _settings.ShareHost.Length > 0;
                var row = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 1, 0, 1), TextTrimming = TextTrimming.CharacterEllipsis };
                row.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = pc.Online ? new SolidColorBrush(Color.FromRgb(0x3C, 0xBB, 0x72)) : Brushes.Gray });
                var name = new System.Windows.Documents.Run(pc.Name);
                name.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextPrimary");
                row.Inlines.Add(name);
                var note = new System.Windows.Documents.Run($"  {pc.Ip}{(pc.Online ? "" : " · offline")}{(shared ? " · connected, sharing its models" : "")}");
                note.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
                row.Inlines.Add(note);
                panel.Children.Add(row);
            }
            buttons.Children.Add(Action("Connect now", async () =>
            {
                await TailnetTickAsync();
                if (!_settings.ShareModels && !await SharedHostAnswersAsync())
                    Toast("No PC on this account is sharing its models: turn on sharing in Settings > Models on the primary PC", "#FF667A");
            }));
            buttons.Children.Add(Action("Sign out", async () =>
            {
                Toast(await Tailscale.SignOutAsync() is { Length: > 0 } why ? $"Not signed out: {why}" : "Signed out of Tailscale");
                await TailnetTickAsync();
            }));
        }
        panel.Children.Add(buttons);
        return panel;
    }

    private async Task SignInTailnetAsync()
    {
        Toast("Finish signing in to Tailscale in your browser");
        var why = await Tailscale.SignInAsync(disconnected: _tailnet?.Backend == "Stopped");
        await TailnetTickAsync();
        if (why is not null && _tailnet is not { SignedIn: true })
            Toast($"Not signed in: {why}", "#FF667A");
        else
            Toast(_settings.ShareHost.Length > 0 ? "Signed in to Tailscale and connected" : "Signed in to Tailscale");
    }
}
