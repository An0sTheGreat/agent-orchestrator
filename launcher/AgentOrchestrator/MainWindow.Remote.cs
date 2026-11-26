using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace AgentOrchestrator;

// The phone app's glue to the window (MainWindow.WebApp.cs). Every action runs on the UI thread through the desktop's own
// paths: open the conversation, put the text in its composer and SendOrQueue; Stop is StopWork; an answer resolves the open
// Choose popup as its button would.
public partial class MainWindow
{
    private bool _dialogsQuiet; // --ui-smoke: popups open without taking the focus

    // The open remote-answerable Choose popup (null: none), the conversation it is shown in, and a nonce an answer must
    // carry, so an answer meant for an earlier popup never resolves this one.
    // `Mirrored`: a PC popup the phone app shows while it is on; it may be answered remotely, but send and stop still wait for it.
    private sealed record RemotePrompt(string Conversation, string Nonce, string Title, string Message, (string Label, string Value)[] Options, Window Dialog, Action<string> Resolve,
        bool Mirrored = false);
    private RemotePrompt? _remotePrompt;

    // The remote prompt for conversation `id`, while nothing is open on top of it. A PC popup raised while no turn runs (mirrored
    // under "", only while the phone app is on) belongs to every conversation, so the phone sees and answers it wherever it is.
    private RemotePrompt? PromptFor(string id) => _remotePrompt is { } p && (p.Conversation == id || p.Conversation == "") && !Disabled(p.Dialog) ? p : null;

    // A popup the remote page cannot answer (or a mirrored one) is open at the PC: a remote action now would run inside its dialog loop.
    private bool DesktopDialogOpen() => _remotePrompt is { Mirrored: false } p ? Disabled(p.Dialog)
        : System.Windows.Interop.ComponentDispatcher.IsThreadModal || Disabled(this);

    // A modal on top disables its owner (WPF's ShowDialog every other window of the thread).
    private static bool Disabled(Window window) => new System.Windows.Interop.WindowInteropHelper(window).Handle is var h && h != 0 && !IsWindowEnabled(h);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    private (int, object) RemoteAct(string method, string path, JsonElement body)
    {
        string Field(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        return (method, path) switch
        {
            ("POST", "/api/send") => RemoteSend(Field("id"), Field("text")),
            ("POST", "/api/stop") => Field("id") != RunningId || RunningId is null ? (409, new { error = "Nothing is running there." })
                : DesktopDialogOpen() ? DialogOpen : Ok(() => StopWork_Click(this, new RoutedEventArgs())),
            ("POST", "/api/answer") => PromptFor(Field("id")) is { } p && p.Nonce == Field("nonce") && p.Options.Any(o => o.Value == Field("choice"))
                ? Ok(() => p.Resolve(Field("choice"))) : (409, new { error = "That request was already answered." }),
            _ => (404, new { error = "Not found" }),
        };

        static (int, object) Ok(Action act)
        {
            act();
            return (200, new { ok = true });
        }
    }

    private static readonly (int, object) DialogOpen = (409, new { error = "The app is showing a dialog on the PC." });

    private static string Kind(string kind) => kind == "chat" ? "chat" : "code";

    // A conversation as the phone app (MainWindow.WebApp.cs) shows it; null: no such one.
    // A streaming reply's text is what the desktop shows so far: the sections before its tool calls, then the current one.
    private (ConversationStore.Conversation Saved, bool Busy, IReadOnlyList<LauncherLogic.ChatMessage> Messages, int Queued, object? Prompt, object? Reply)? ConversationNow(string id)
    {
        if (_store.Get(id) is not { } saved)
            return null;
        var busy = id == RunningId;
        var live = busy && _chatBusy; // a reply streams in it: the turn's own view (the store holds a placeholder reply)
        var messages = live ? _turn.History.Concat(_pendingUser is { } user ? TurnMessages(user) : Enumerable.Empty<LauncherLogic.ChatMessage>()).ToList() : saved.Messages;
        var queued = id == _chatConv?.Id ? _chatQueue.Count : id == _codeConv?.Id ? _codeQueue.Count : 0;
        var prompt = PromptFor(id) is { } p
            ? new { title = p.Title, message = p.Message, nonce = p.Nonce, choices = p.Options.Select(o => new { label = o.Label, value = o.Value }).ToList() } : null;
        var reply = live && _reply is { } r
            ? new { text = string.Join("\n\n", r.Earlier.Append(r.Final ?? r.AnswerText.ToString()).Where(t => t.Length > 0)), status = r.Status.Visibility == Visibility.Visible ? r.Status.Text : "" } : null;
        return (saved, busy, messages, queued, prompt, reply);
    }

    // The desktop's own send: the conversation opens, the text goes through its composer (the desktop's draft and attached
    // files are set aside meanwhile, so they never go with it), and SendOrQueue sends it or queues it behind a reply.
    // attachments: the phone app's uploads (MainWindow.WebApp.cs has checked they are), sent with the text.
    private (int, object) RemoteSend(string id, string text, IReadOnlyList<string>? attachments = null)
    {
        var mine = new List<string>();
        foreach (var path in (attachments ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            if (AttachRefusal(mine, path) is { } why)
                return (400, new { error = why });
            else
                mine.Add(path);
        if (text.Trim().Length == 0 && mine.Count == 0)
            return (400, new { error = "Empty message" });
        if (text.TrimStart().StartsWith('/') && !System.Text.RegularExpressions.Regex.IsMatch(text, @"\A\s*/(image|compact)(\s|\z)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) // a slash command changes settings (RunLocalCommand): those stay at the PC; /image paints, /compact summarizes this conversation
            return (400, new { error = "Slash commands only work at the PC." });
        if (DesktopDialogOpen())
            return DialogOpen;
        Conversation c;
        if (id.Length == 0)
        {
            NewChat_Click(this, new RoutedEventArgs());
            c = _chat;
        }
        else
        {
            if (_store.Get(id) is not { } saved)
                return (404, new { error = "No such conversation" });
            if (saved.Kind == "chat")
                OpenChatConversation(id);
            else
                OpenCodeConversation(id);
            c = saved.Kind == "chat" ? _chat : _code;
            if ((c.IsCode ? _codeConv : _chatConv)?.Id != id)
                return (409, new { error = "The app cannot open that conversation now (a reply or run is using the models)." });
        }
        var (draft, files) = (c.Input.Text, AttachmentsOf(c).ToList());
        AttachmentsOf(c).Clear();
        AttachmentsOf(c).AddRange(mine);
        c.Input.Text = text;
        SendOrQueue(c);
        var sent = c.Input.Text != text || text.Length == 0 && AttachmentsOf(c).Count == 0; // sent or queued: the composer took it
        c.Input.Text = draft;
        AttachmentsOf(c).RemoveAll(mine.Contains); // not taken: they never stay in the desktop's composer
        if (AttachmentsOf(c).Count == 0)
            AttachmentsOf(c).AddRange(files);
        RefreshAttachments(c);
        return sent ? (200, new { id = (c.IsCode ? _codeConv : _chatConv)?.Id, queued = QueueOf(c).Count > 0 })
            : (409, new { error = "The app did not send it; see the desktop for why." });
    }

    // ~8 px a module, black on the white quiet zone; the Image scales it to 180 px with NearestNeighbor, so it stays crisp.
    private static BitmapImage QrCode(string text)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(PngByteQRCodeHelper.GetQRCode(text, QRCodeGenerator.ECCLevel.M, 8));
        image.EndInit();
        image.Freeze();
        return image;
    }
}
