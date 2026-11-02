using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;

namespace AgentOrchestrator;

// The composer's "+": files, photos and folders sent with the next message, as Claude's attach menu. Text files (and a Chat
// folder's text files) go into the message itself, after AttachMarker; photos go in the request's "images", which the engine
// sends to the model with that message only. In Code, "Add folder" adds a folder the lead may read (or edit).
public partial class MainWindow
{
    internal const string AttachMarker = "\n\n<attachments>\n";
    private const int FileMaxBytes = 200_000, FolderMaxBytes = 300_000, FolderMaxFiles = 400;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", "dist", "build", "__pycache__", ".venv", "venv", ".vs", ".idea", ".next", "target" };

    private readonly List<string> _chatAttachments = new(), _codeAttachments = new();
    private List<string> _turnImages = new(); // the photos of the message being sent, for its request

    private List<string> AttachmentsOf(Conversation c) => c.IsCode ? _codeAttachments : _chatAttachments;

    private const int MaxImages = 20; // per message
    internal Func<string[]?>? _attachPick; // --ui-smoke: the files "Add files or photos" picks, without the dialog

    // The app's compact menu (as Effort's), above the "+".
    private void Plus_Click(object sender, RoutedEventArgs e) => PlusMenu((Button)sender, ConvOf(sender)).IsOpen = true;

    private ContextMenu PlusMenu(Button plus, Conversation c)
    {
        var menu = NewPresetMenu(plus);
        menu.Placement = PlacementMode.Top;
        var files = PresetItem("Add files or photos", () => AddFiles(c));
        files.ToolTip = $"Or paste (Ctrl+V) or drop them on the message box; up to {MaxImages} photos per message";
        var folder = PresetItem("Add folder", () => AddFolder(c));
        folder.ToolTip = c.IsCode ? "A folder the lead may read or edit, besides the project folder" : "Its text files go with your message";
        menu.Items.Add(files);
        menu.Items.Add(folder);
        return menu;
    }

    // Ctrl+V in a message box: copied files, or a copied picture (saved as a PNG in <data>\pasted), are attached; plain
    // text pastes as usual. Files dropped on the message box are attached too.
    private void InitAttachments()
    {
        foreach (var c in new[] { _chat, _code })
        {
            var conv = c;
            // With only a picture or files on the clipboard a text box says Paste cannot run, so Ctrl+V would never arrive
            CommandManager.AddPreviewCanExecuteHandler(c.Input, (_, e) =>
            {
                if (e.Command == ApplicationCommands.Paste && ClipboardHasAttachment())
                {
                    e.CanExecute = true;
                    e.Handled = true;
                }
            });
            CommandManager.AddPreviewExecutedHandler(c.Input, (_, e) =>
            {
                if (e.Command == ApplicationCommands.Paste && PasteAttachments(conv))
                    e.Handled = true;
            });
            var box = c.IsCode ? CodeComposer : ChatComposer;
            box.AllowDrop = true;
            box.PreviewDragOver += (_, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                    return;
                e.Effects = DragDropEffects.Copy;
                e.Handled = true; // the text box would refuse files
            };
            box.PreviewDrop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
                    return;
                e.Handled = true;
                foreach (var path in paths)
                    Attach(conv, path);
            };
        }
    }

    private bool PasteAttachments(Conversation c)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var path in Clipboard.GetFileDropList().Cast<string>())
                    Attach(c, path);
                return true;
            }
            if (Clipboard.GetData("PNG") is MemoryStream png) // Snipping Tool and browsers put the picture as PNG too: kept as is
            {
                Attach(c, SavePastedImage(png.ToArray()));
                return true;
            }
            if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image)
                return false;
            Attach(c, SavePastedImage(image));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            Toast($"Could not paste: {e.Message}", "#F7C873");
            return true;
        }
    }

    private static bool ClipboardHasAttachment()
    {
        try { return Clipboard.ContainsFileDropList() || Clipboard.ContainsImage() || Clipboard.ContainsData("PNG"); }
        catch (System.Runtime.InteropServices.COMException) { return false; } // another app holds the clipboard
    }

    internal static string SavePastedImage(byte[] png)
    {
        var file = PastedFile();
        File.WriteAllBytes(file, png);
        return file;
    }

    private static string PastedFile()
    {
        var folder = Path.Combine(AppSettings.DataRoot, "pasted");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"pasted-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.png");
    }

    internal static string SavePastedImage(System.Windows.Media.Imaging.BitmapSource image)
    {
        var file = PastedFile();
        using var stream = File.Create(file);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        png.Save(stream);
        return file;
    }
    private TextBlock Glyph(string glyph)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 13 };
        icon.SetResourceReference(StyleProperty, "RailGlyph");
        return icon;
    }

    // While a reply runs too: what is attached then goes with the queued (or next) message.
    private void AddFiles(Conversation c)
    {
        string[]? picked;
        if (_attachPick is { } pick)
            picked = pick();
        else
        {
            var dialog = new OpenFileDialog { Title = "Add files or photos", Multiselect = true,
                Filter = "All files|*.*|Photos|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp" };
            picked = dialog.ShowDialog(this) == true ? dialog.FileNames : null;
        }
        foreach (var file in picked ?? [])
            Attach(c, file);
    }

    private void AddFolder(Conversation c)
    {
        if (c.IsCode)
            AddCodeFolder_Click(AddCodeFolderButton, new RoutedEventArgs());
        else if (PickFolder("Add a folder to the message") is { } folder)
            Attach(c, Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
    }

    // Adds a file or folder (not twice); a file that is neither a photo nor readable text is refused here, not at send,
    // and so is a photo past MaxImages.
    private void Attach(Conversation c, string path)
    {
        var list = AttachmentsOf(c);
        if (list.Contains(path, StringComparer.OrdinalIgnoreCase))
            return;
        if (File.Exists(path) && ImageExtensions.Contains(Path.GetExtension(path)) && list.Count(p => ImageExtensions.Contains(Path.GetExtension(p))) >= MaxImages)
        {
            Toast($"Up to {MaxImages} photos per message; {Path.GetFileName(path)} was not added", "#F7C873");
            return;
        }
        if (File.Exists(path) && !ImageExtensions.Contains(Path.GetExtension(path)) && ReadText(path, FileMaxBytes) is null)
        {
            Toast($"{Path.GetFileName(path)} isn't a text file or photo, so it can't be added", "#F7C873");
            return;
        }
        list.Add(path);
        RefreshAttachments(c);
        c.Input.Focus();
    }

    private void RefreshAttachments(Conversation c)
    {
        var panel = c.IsCode ? CodeAttachList : ChatAttachList;
        panel.Children.Clear();
        foreach (var path in AttachmentsOf(c))
        {
            var folder = Directory.Exists(path);
            var icon = Glyph(folder ? "" : ImageExtensions.Contains(Path.GetExtension(path)) ? "" : "");
            icon.Margin = new Thickness(0, 0, 6, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            var name = new TextBlock { Text = Path.GetFileName(path), MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var remove = new Button { Content = "×", Padding = new Thickness(6, 0, 4, 0), FontSize = 13, ToolTip = "Remove", Tag = "RemoveAttachment" };
            remove.SetResourceReference(StyleProperty, "GhostButton");
            remove.Click += (_, _) =>
            {
                AttachmentsOf(c).Remove(path);
                RefreshAttachments(c);
            };
            var chip = new Border { Padding = new Thickness(8, 3, 2, 3), Margin = new Thickness(0, 0, 6, 4), CornerRadius = new CornerRadius(8), ToolTip = path,
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, name, remove } } };
            chip.SetResourceReference(Border.BackgroundProperty, "Hover");
            panel.Children.Add(chip);
        }
        panel.Visibility = panel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshChatInput();
    }

    // The message to send: the typed text, then the attached text files and folders after AttachMarker. The photos go to
    // _turnImages. StartTurn clears the attachments once the message is sent. attachments: a queued message's own list.
    private string ComposeMessage(Conversation c, string typed, List<string>? attachments = null)
    {
        var list = attachments ?? AttachmentsOf(c);
        _turnImages = list.Where(p => File.Exists(p) && ImageExtensions.Contains(Path.GetExtension(p))).ToList();
        var body = new StringBuilder();
        foreach (var path in list)
            if (Directory.Exists(path))
                AppendFolder(body, path);
            else if (_turnImages.Contains(path))
                body.Append($"<image name=\"{Path.GetFileName(path)}\" />\n");
            else if (ReadText(path, FileMaxBytes) is { } text)
                body.Append($"<file name=\"{Path.GetFileName(path)}\">\n{text}\n</file>\n");
        return body.Length == 0 ? typed : (typed.Length > 0 ? typed : "(see the attachments)") + AttachMarker + body + "</attachments>";
    }

    // A folder's tree (junk folders skipped) and its text files' contents, up to FolderMaxBytes in all.
    private static void AppendFolder(StringBuilder body, string root)
    {
        var name = Path.GetFileName(root);
        var files = new List<string>();
        void Walk(string dir)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir).Order(StringComparer.OrdinalIgnoreCase))
                    if (files.Count < FolderMaxFiles)
                        files.Add(file);
                foreach (var sub in Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
                    if (!SkippedFolders.Contains(Path.GetFileName(sub)) && files.Count < FolderMaxFiles)
                        Walk(sub);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Walk(root);
        string Rel(string f) => Path.GetRelativePath(root, f).Replace('\\', '/');
        body.Append($"<folder name=\"{name}\">\n<tree>\n{string.Join("\n", files.Select(Rel))}{(files.Count >= FolderMaxFiles ? "\n(more files not listed)" : "")}\n</tree>\n");
        var budget = FolderMaxBytes;
        foreach (var file in files)
        {
            if (budget <= 0)
            {
                body.Append("(the remaining files' contents were left out: size limit)\n");
                break;
            }
            if (ImageExtensions.Contains(Path.GetExtension(file)) || ReadText(file, Math.Min(budget, FileMaxBytes)) is not { } text)
                continue;
            body.Append($"<file name=\"{name}/{Rel(file)}\">\n{text}\n</file>\n");
            budget -= text.Length;
        }
        body.Append("</folder>\n");
    }

    // A file's text, cut at max characters (noted); null for a binary file (a NUL byte in its start) or an unreadable one.
    internal static string? ReadText(string path, int max)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[Math.Min(8192, (int)Math.Min(stream.Length, int.MaxValue))];
            var read = stream.Read(head, 0, head.Length);
            if (head.AsSpan(0, read).Contains((byte)0))
                return null;
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[max];
            var n = reader.ReadBlock(buffer, 0, max);
            return new string(buffer, 0, n) + (reader.Peek() >= 0 ? "\n(cut: the file is longer)" : "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    // A sent message without its attachments: what the user typed.
    internal static string TypedText(string message) =>
        message.IndexOf(AttachMarker, StringComparison.Ordinal) is var at and >= 0 ? message[..at] : message;

    // A sent message as its bubble shows it: the typed text, then the attachments' names.
    internal static string BubbleText(string message)
    {
        var at = message.IndexOf(AttachMarker, StringComparison.Ordinal);
        if (at < 0)
            return message;
        var names = System.Text.RegularExpressions.Regex.Matches(message[at..], "^<(file|image|folder) name=\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Where(m => m.Groups[1].Value != "file" || !m.Groups[2].Value.Contains('/')) // a folder's files are listed under it
            .Select(m => m.Groups[2].Value + (m.Groups[1].Value == "folder" ? "/" : ""));
        return message[..at] + "\n\n📎 " + string.Join(", ", names);
    }

    // --ui-smoke: "Add files or photos" attaches while a reply runs (it goes with the queued message), a 21st photo is
    // refused, and the "+" opens the app's compact two-item menu.
    private bool AttachSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: attachments: {what}"); return false; }
        var folder = Path.Combine(AppSettings.DataRoot, "smoke-attach");
        Directory.CreateDirectory(folder);
        var photos = Enumerable.Range(1, MaxImages + 1).Select(i => Path.Combine(folder, $"p{i}.png")).ToArray();
        foreach (var photo in photos)
            File.WriteAllBytes(photo, [137, 80, 78, 71]);
        var (busy, pick) = (_chatBusy, _attachPick);
        try
        {
            _chatBusy = true;
            _attachPick = () => photos;
            AddFiles(_code);
            if (_codeAttachments.Count != MaxImages || _codeAttachments.Contains(photos[^1]))
                return Fail($"{_codeAttachments.Count} attached");
            var menu = PlusMenu(CodePlus, _code);
            if (menu.Style != (Style)FindResource(typeof(ContextMenu)) || menu.Placement != PlacementMode.Top ||
                menu.Items.OfType<MenuItem>().Select(i => (string)i.Header).SequenceEqual(["Add files or photos", "Add folder"]) is false)
                return Fail("the + menu");
            return true;
        }
        finally
        {
            (_chatBusy, _attachPick) = (busy, pick);
            _codeAttachments.Clear();
            RefreshAttachments(_code);
        }
    }
}