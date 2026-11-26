using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentOrchestrator;

// The composers' Image Gen chip (Chat and Code): a dropdown (the whole chip) with the chat's own image model, size and steps (AppSettings.ChatImage and ChatImageSteps, apart from the Images page's) and the
// keep-both switch (AppSettings.ImageKeepBoth). Image Gen is always on: once a model is chosen, the chat model can paint. The size controls are the Images page's (ImageSizeControls), one more set.
public partial class MainWindow
{
    internal Popup? _imageChipPopup; // the dropdown open last (--ui-smoke)
    private ImageSizeControls? _chipSize;
    private DateTime _imageChipClosedAt;

    // The chat's pick resolves to a model the request can carry (the primary PC's only while connected): what CurrentExtras sends.
    private bool ChatImageReady => ImageGen.ChatModel(_settings.ChatImage) is not null && (_settings.ChatImage.SharedModel.Length == 0 || _settings.ShareHost.Length > 0);

    private int ChatImageMultiple() => ImageGen.ChatModel(_settings.ChatImage) is ({ } recipe, _) ? recipe.Multiple : 16; // OpenRouter: as ImageGen.ChatRun

    private void RefreshImageChip()
    {
        var tip = "Choose an image model so the model can make pictures as it answers";
        if (ImageGen.ChatModel(_settings.ChatImage) is var (_, name))
            tip = ChatImageReady && ImageGen.ChatRequest(_settings.ChatImage, _settings.ImageKeepBoth) is { } r ? $"Image Gen: {name}, {r.Width} × {r.Height}"
                : $"Image Gen: {name} is on the primary PC, which is not connected";
        foreach (var chip in new[] { ChatImageGenToggle, CodeImageGenToggle })
            (chip.IsChecked, chip.ToolTip) = (true, tip); // always on: lit as the other chips that are on
    }

    private void ImageGenToggle_Click(object sender, RoutedEventArgs e)
    {
        var chip = (ToggleButton)sender;
        chip.IsChecked = true; // a click opens the dropdown, never turns it off
        OpenImageChip(chip);
    }

    private void ImageGenCaret_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // the caret sits inside the chip: its click must not reach the chip's own handler
        OpenImageChip(sender == ChatImageGenCaret ? ChatImageGenToggle : CodeImageGenToggle);
    }

    private void OpenImageChip(ToggleButton chip)
    {
        if (_imageChipPopup is { IsOpen: true } open)
        {
            open.IsOpen = false;
            return;
        }
        if (DateTime.UtcNow - _imageChipClosedAt < TimeSpan.FromMilliseconds(400)) // the press that closed it: its release must not reopen it
            return;
        TextBlock Text(string text, string brush = "Muted", double size = 12)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }
        TextBlock Head(string text, double top, double left = 0) => new() { Text = text.ToUpperInvariant(), Style = (Style)FindResource("SectionHeader"), Margin = new Thickness(left, top, 0, 4) };

        // STEPS: empty shows the model's own greyed; a typed number (1-100) is the chat's. Not for OpenRouter models, which take none.
        var steps = new TextBox { Tag = "ChatImageSteps", Width = 70, Text = _settings.ChatImageSteps > 0 ? _settings.ChatImageSteps.ToString() : "", VerticalContentAlignment = VerticalAlignment.Center };
        steps.SetResourceReference(Control.BackgroundProperty, "Field");
        steps.SetResourceReference(Control.BorderBrushProperty, "Border");
        var stepsHint = Text("", size: 12);
        (stepsHint.IsHitTestVisible, stepsHint.Margin, stepsHint.Tag) = (false, new Thickness(6, 0, 0, 0), "ChatImageStepsHint");
        var stepsLabel = new TextBlock { Text = "Steps", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(stepsLabel, Dock.Left);
        var stepsRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0), Children = { stepsLabel, new Grid { HorizontalAlignment = HorizontalAlignment.Left, Children = { steps, stepsHint } } } };
        void ShowSteps()
        {
            var recipe = ImageGen.ChatModel(_settings.ChatImage)?.Recipe;
            stepsRow.Visibility = recipe is null && _settings.ChatImage.RemoteModel.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            stepsHint.Text = (recipe?.Steps ?? 8).ToString();
            stepsHint.Visibility = steps.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            steps.ToolTip = $"Sampling steps for the chat's pictures; empty: the model's own ({stepsHint.Text})";
        }
        steps.TextChanged += (_, _) =>
        {
            var digits = new string(steps.Text.Where(char.IsAsciiDigit).ToArray()); // typed or pasted
            if (digits != steps.Text)
            {
                steps.Text = digits; // raises TextChanged again
                steps.CaretIndex = digits.Length;
                return;
            }
            _settings.ChatImageSteps = int.TryParse(digits, out var v) ? Math.Clamp(v, 1, 100) : 0;
            _settings.Save();
            ShowSteps();
        };
        steps.LostKeyboardFocus += (_, _) => steps.Text = _settings.ChatImageSteps > 0 ? _settings.ChatImageSteps.ToString() : ""; // shows the clamped value
        ShowSteps();

        // MODEL: this PC's (the recipes and the models folder's), the primary PC's while connected; the pick is marked
        ScanImageFolder();
        List<ImageShare.Model>? shared = null;
        var sharedNote = "";
        var rows = new StackPanel();
        void Pick(ChatImageChoice m)
        {
            PickChatImage(m);
            _chipSize?.Show(); // the new model's multiple
            ShowSteps(); // the new model's own steps
            ShowRows();
            RefreshExtras();
        }
        // note: right of the name, which gets the room first; tip: the tooltip's extra (the recipe's own note)
        void Row(string tag, string name, string note, bool current, Action click, string tip = "")
        {
            var check = Text(current ? "" : "", "AccentLight", 11);
            (check.FontFamily, check.Width) = (new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), 18);
            var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left);
            var cells = new DockPanel { Children = { check, label } };
            if (note.Length > 0)
            {
                var n = Text(note, size: 11);
                (n.TextWrapping, n.TextTrimming, n.TextAlignment, n.Margin) = (TextWrapping.NoWrap, TextTrimming.CharacterEllipsis, TextAlignment.Right, new Thickness(8, 0, 0, 0));
                cells.Children.Add(n);
            }
            var row = new Button { Content = cells, Tag = tag, Style = (Style)FindResource("GhostButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 4, 8, 4), FontSize = 12, ToolTip = string.Join(" · ", new[] { name, tip, note }.Where(t => t.Length > 0)) };
            row.Click += (_, _) => click();
            rows.Children.Add(row);
        }
        void ShowRows()
        {
            var c = _settings.ChatImage;
            var (choices, current) = (ChatImageChoices(shared), ChatImageKey());
            rows.Children.Clear();
            rows.Children.Add(Head("This PC", 0, 6));
            foreach (var m in choices.Where(m => !m.Primary))
                Row("chatimg:" + m.Id, m.Name, m.Note, m.Key.Equals(current, StringComparison.OrdinalIgnoreCase), () => Pick(m), m.Tip);
            if (_settings.ShareHost.Length > 0)
            {
                rows.Children.Add(Head("Primary PC", 8, 6));
                if (shared is null || shared.Count == 0)
                {
                    var note = Text(sharedNote, size: 11);
                    note.Margin = new Thickness(6, 0, 0, 0);
                    rows.Children.Add(note);
                }
                foreach (var m in choices.Where(m => m.Primary))
                    Row("chatimgshared:" + m.Id, m.Name, m.Note, m.Key == current, () => Pick(m));
            }
            if (c.SharedModel.Length == 0 && c.RemoteModel.Length > 0) // chosen before; OpenRouter models are not offered here
            {
                rows.Children.Add(Head("OpenRouter", 8, 6));
                Row("chatimgremote:" + c.RemoteModel, c.RemoteModel, "", true, () => { });
            }
        }
        async void LoadShared()
        {
            var host = _settings.ShareHost;
            sharedNote = $"Loading the models of {host}…";
            try
            {
                shared = await FetchSharedImageModels(host, 30);
                sharedNote = $"{host} has no image models";
            }
            catch (Exception x) when (x is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
            {
                sharedNote = x is TaskCanceledException ? $"No answer from {host} in 30 s" : x.Message;
            }
            ShowRows();
        }
        if (_settings.ShareHost.Length > 0)
            LoadShared(); // shows "Loading…" until it answers
        ShowRows();
        var models = new ScrollViewer { Content = rows, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(-6, 0, -6, 0) };
        var hide = HideMissingSwitch("ChatImageHideMissing", () =>
        {
            ShowRows();
            if (_imgHideMissing is { } page) // the Images page's, when built: the same setting
            {
                page.IsChecked = _settings.ImageHideMissing;
                RefreshImageRecipes();
            }
        });
        hide.Margin = new Thickness(0, 6, 0, 0);

        // SIZE: the page's controls and behaviour; every change is the chat's size
        var size = _chipSize = new ImageSizeControls("ChatImage", ChatImageMultiple);
        size.Load(_settings.ChatImage);
        size.Changed += () =>
        {
            var (aspect, unlocked, w, h, cw, ch) = size.Read();
            var c = _settings.ChatImage;
            (c.Aspect, c.SizeUnlocked, c.Width, c.Height, c.CustomWidth, c.CustomHeight) = (aspect, unlocked, w, h, cw, ch);
            _settings.Save();
            RefreshExtras();
        };
        var aspectLabel = new TextBlock { Text = "Aspect ratio", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(aspectLabel, Dock.Left);
        size.SizeRow.Margin = new Thickness(0, 8, 0, 0);
        foreach (var field in new Control[] { size.Aspect, size.WidthBox, size.HeightBox }) // as on the Images page
        {
            field.SetResourceReference(Control.BackgroundProperty, "Field");
            field.SetResourceReference(Control.BorderBrushProperty, "Border");
        }

        // the keep-both switch
        var keep = new ToggleButton { Style = (Style)FindResource("ToggleSwitch"), Tag = "ChatImageKeepBoth", IsChecked = _settings.ImageKeepBoth, Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(keep, "Keep both models loaded");
        keep.Click += (_, _) =>
        {
            _settings.ImageKeepBoth = keep.IsChecked == true;
            _settings.Save();
            RefreshExtras();
        };
        DockPanel.SetDock(keep, Dock.Right);
        var keepHint = Text("Off: the chat model leaves the graphics card while the picture is made, and comes back after. On: both stay loaded (needs enough VRAM).", size: 11);
        keepHint.Margin = new Thickness(0, 4, 0, 0);
        var line = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 10) };
        line.SetResourceReference(Border.BackgroundProperty, "Border");

        var card = new Border
        {
            BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 12), Width = 340,
            Child = new StackPanel
            {
                Children =
                {
                    Head("Model", 0), models, hide,
                    Head("Size", 12), new DockPanel { Children = { aspectLabel, size.AspectRow } }, size.SizeRow, stepsRow,
                    line,
                    new DockPanel { Children = { keep, new TextBlock { Text = "Keep both models loaded", FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } },
                    keepHint,
                },
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        var popup = _imageChipPopup = new Popup { Child = card, PlacementTarget = chip, Placement = PlacementMode.Top, VerticalOffset = -6, AllowsTransparency = true,
            StaysOpen = false, PopupAnimation = PopupAnimation.Fade };
        // the size boxes take the keyboard, and StaysOpen=false closes only while the popup holds the mouse: a press in the window closes it (as ChooseImageModel)
        void OnPress(object sender, MouseButtonEventArgs e)
        {
            _imageChipClosedAt = DateTime.UtcNow;
            popup.IsOpen = false;
        }
        // Esc closes it, with the keyboard in the dropdown or still in the window (the chip)
        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
        }
        PreviewMouseDown += OnPress;
        PreviewKeyDown += OnKey;
        popup.Closed += (_, _) =>
        {
            PreviewMouseDown -= OnPress;
            PreviewKeyDown -= OnKey;
            if (Mouse.LeftButton == MouseButtonState.Pressed)
                _imageChipClosedAt = DateTime.UtcNow;
        };
        card.PreviewKeyDown += OnKey;
        popup.IsOpen = true;
    }

    // One model the chip offers. Key: the pick's own name (ChatImageKey; the primary PC's prefixed "primary:"), what the phone sends.
    // Note: right of the name (this PC's: what the Images page says of its files); Tip: the tooltip's extra; Downloaded: null on the primary PC.
    internal sealed record ChatImageChoice(string Key, string Id, string Name, string Note, string Tip, bool? Downloaded, bool Primary);

    // The chat's pick as a ChatImageChoice key: the primary PC's, OpenRouter's (never offered), a models folder file, else the recipe ("" = none).
    private string ChatImageKey() => ImageKey(_settings.ChatImage);

    private static string ImageKey(ImageSettings c)
    {
        return c.SharedModel.Length > 0 ? "primary:" + c.SharedModel : c.RemoteModel.Length > 0 ? "openrouter:" + c.RemoteModel
            : c.LocalModel.Length > 0 ? "local:" + Path.GetFileName(c.LocalModel) : c.RecipeId;
    }

    // The chip's models (call ScanImageFolder first): this PC's, less those not downloaded with the switch on and those another row
    // shows (the pick stays), then the primary PC's while connected (shared: its list; null while not fetched).
    private List<ChatImageChoice> ChatImageChoices(List<ImageShare.Model>? shared)
    {
        var current = ChatImageKey();
        var choices = new List<ChatImageChoice>();
        foreach (var m in ImageShare.Models(_imgLocal))
        {
            // what the Images page's list says of a model's files: picking one marked Download downloads it on first use, as there
            var (files, downloaded) = ("", (bool?)null);
            if (ImageShare.Resolve(m.Id, _imgLocal) is var (recipe, path))
            {
                var missing = MissingImageFiles(recipe, path).Sum(f => f.Size);
                var gb = $"{missing / (double)(1L << 30):0.0} GB";
                (files, downloaded) = (path.Length == 0 ? missing == 0 ? "Downloaded" : $"Download ({gb})" : missing == 0 ? "on this PC" : $"on this PC, needs {gb} of parts", missing == 0);
            }
            if (!m.Id.Equals(current, StringComparison.OrdinalIgnoreCase) && (_settings.ImageHideMissing && files.StartsWith("Download (", StringComparison.Ordinal) || Shown(m.Id)))
                continue; // not downloaded and the switch on, or the model shown by another row (the current pick stays)
            choices.Add(new(m.Id, m.Id, m.Name, files, m.Note, downloaded, false));
        }
        if (_settings.ShareHost.Length > 0)
            choices.AddRange((shared ?? []).Select(m => new ChatImageChoice("primary:" + m.Id, m.Id, m.Name, m.Note, "", null, true)));
        return choices;
        // a recipe whose model is on this PC as another file, or a folder file that is a recipe's own (as the Images page skips them)
        bool Shown(string id) => ImageShare.Resolve(id, _imgLocal) is var (recipe, path)
            && (path.Length == 0 ? RecipeCovered(recipe) : IsRecipeFile(_imgLocal.First(l => l.Path == path)));
    }

    // A chip row's click (and the phone's pick): the chat's image model is that one alone, saved.
    private void PickChatImage(ChatImageChoice m)
    {
        var c = _settings.ChatImage;
        (c.SharedModel, c.RemoteModel, c.RemoteProvider, c.LocalModel) = ("", "", "", "");
        if (m.Primary)
            (c.SharedModel, c.RecipeId) = (m.Id, m.Id);
        else
            (c.RecipeId, c.LocalModel) = ImageShare.Resolve(m.Id, _imgLocal) is var (recipe, path) ? (recipe.Id, path) : (m.Id, "");
        _settings.Save();
    }

    // The primary PC's list (the chip's dropdown fetches it at each opening; the phone's composer takes the last one fetched).
    private (string Host, List<ImageShare.Model> Models)? _imageShared;
    internal Func<string, int, Task<List<ImageShare.Model>>> _imageSharedFetch = FetchImageModels; // --webapp-smoke swaps it, never the network

    private async Task<List<ImageShare.Model>> FetchSharedImageModels(string host, int seconds)
    {
        var models = await _imageSharedFetch(host, seconds);
        _imageShared = (host, models);
        return models;
    }

    private static async Task<List<ImageShare.Model>> FetchImageModels(string host, int seconds)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) };
        return await ImageShare.FetchModelsAsync(http, ModelShare.ImagesUrl(host), Environment.GetEnvironmentVariable(ModelShare.KeyEnv) ?? "", CancellationToken.None);
    }

    // "Hide models not downloaded" (AppSettings.ImageHideMissing), one setting for the Images page's list and the chip's dropdown.
    private ToggleButton? _imgHideMissing; // the Images page's switch

    private DockPanel HideMissingSwitch(string tag, Action changed)
    {
        var toggle = new ToggleButton { Style = (Style)FindResource("ToggleSwitch"), Tag = tag, IsChecked = _settings.ImageHideMissing, Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(toggle, "Hide models not downloaded");
        toggle.ToolTip = "Hide models not downloaded yet (the chosen one stays)";
        toggle.Click += (_, _) =>
        {
            _settings.ImageHideMissing = toggle.IsChecked == true;
            _settings.Save();
            changed();
        };
        DockPanel.SetDock(toggle, Dock.Right);
        return new DockPanel { Children = { toggle, new TextBlock { Text = "Hide not downloaded", FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } };
    }

    // The size controls: the aspect ratio list with its ↑↓ swap, the width and height boxes and the lock. The Images page has one set
    // (_imgSize) and the Image Gen chip's dropdown another; both run this code. Multiple: the chosen model's (a typed size rounds to it).
    private sealed class ImageSizeControls
    {
        public readonly ComboBox Aspect, WidthBox, HeightBox;
        public readonly TextBlock WidthHint, HeightHint;
        public readonly Button Lock;
        public readonly DockPanel AspectRow, SizeRow; // [aspect list  ↑↓] and [⇆ width  ⇅ height  lock]
        public bool Unlocked; // the lock open: width and height as typed (ImageSettings.SizeUnlocked)
        public bool Applying; // Load sets the controls: no commits meanwhile
        private bool _flipped; // the swap button: the presets show and give their ratios flipped (portrait)
        private int _lastRatio = 1; // the preset whose recommended size Custom keeps showing
        private readonly Func<int> _multiple;
        public event Action? Changed; // the user changed the size (not Load)

        public ImageSizeControls(string tag, Func<int> multiple)
        {
            _multiple = multiple;
            Aspect = new ComboBox { Tag = tag + "Aspect" };
            Aspect.SelectionChanged += (_, _) => AspectChanged();
            var swap = new Button { Content = "↑↓", Tag = tag + "SwapSize", ToolTip = "Swap width and height (portrait or landscape)", Template = ParseTemplate(RoundButtonXaml), Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 0, 0) };
            swap.Click += (_, _) => Swap();
            DockPanel.SetDock(swap, Dock.Right);
            AspectRow = new DockPanel { Children = { swap, Aspect } };
            Lock = new Button { Tag = tag + "SizeLock", Template = ParseTemplate(RoundButtonXaml), Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 0, 0),
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), VerticalAlignment = VerticalAlignment.Center };
            Lock.Click += (_, _) => { Unlocked = !Unlocked; ShowLock(); Changed?.Invoke(); };
            DockPanel.SetDock(Lock, Dock.Right);
            var (w, h) = (SizeBox(tag + "Width", true, out WidthBox, out WidthHint), SizeBox(tag + "Height", false, out HeightBox, out HeightHint));
            (w.Margin, h.Margin) = (new Thickness(0, 0, 8, 0), new Thickness(8, 0, 0, 0));
            SizeRow = new DockPanel { Children = { Lock, new UniformGrid { Rows = 1, Children = { w, h } } } };
            ShowLock();
        }

        // Width and height: editable lists. Empty shows the recommended value greyed where typed text starts (the caret sits at its left
        // edge); a typed or picked value is rounded to the model's multiple when the box loses focus, and with a ratio chosen the other follows.
        private DockPanel SizeBox(string tag, bool width, out ComboBox box, out TextBlock hint)
        {
            var b = box = new ComboBox { Tag = tag, MinWidth = 70, IsEditable = true, IsTextSearchEnabled = false, Template = ParseTemplate(EditableComboXaml), ItemsSource = ImageGen.SizeChoices,
                ToolTip = $"Any {(width ? "width" : "height")}, rounded to the model's multiple; empty: the aspect ratio's recommended one" };
            var h = hint = new TextBlock { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Tag = tag + "Hint" };
            h.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            b.Loaded += (_, _) =>
            {
                // the template's text box sits at the combo's left padding; WPF draws text 2 px inside a text box
                (h.FontSize, h.FontFamily, h.Margin) = (b.FontSize, b.FontFamily, new Thickness(b.Padding.Left + 2, 0, 0, 0));
                h.Visibility = b.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            b.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) =>
            {
                var t = (TextBox)e.OriginalSource;
                var digits = new string(t.Text.Where(char.IsAsciiDigit).ToArray()); // typed or pasted
                if (digits != t.Text)
                {
                    var caret = Math.Max(0, t.CaretIndex - (t.Text.Length - digits.Length));
                    t.Text = digits; // raises TextChanged again
                    t.CaretIndex = caret;
                    return;
                }
                h.Visibility = t.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            }));
            b.LostKeyboardFocus += (_, _) =>
            {
                if (!b.IsKeyboardFocusWithin && !b.IsDropDownOpen)
                    Commit(width);
            };
            b.DropDownClosed += (_, _) => Commit(width); // a value picked from the list
            b.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    Commit(width);
            };
            var icon = new TextBlock { Text = width ? "⇆" : "⇅", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = width ? "Width" : "Height" };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            DockPanel.SetDock(icon, Dock.Left);
            return new DockPanel { Children = { icon, new Grid { Children = { b, h } } } };
        }

        public void Load(ImageSettings s)
        {
            Applying = true;
            var preset = Array.FindIndex(ImageGen.Presets, p => p.Ratio.Length > 0 && (p.Ratio == s.Aspect || ImageGen.Flip(p.Ratio) == s.Aspect));
            (_flipped, _lastRatio) = (preset > 0 && ImageGen.Presets[preset].Ratio != s.Aspect, Math.Max(preset, 1));
            Aspect.ItemsSource = Labels();
            Aspect.SelectedIndex = s.Aspect == "Custom" ? 0 : _lastRatio; // unknown: Square
            var before = s.CustomWidth == 0 && s.CustomHeight == 0 && s.LongSide != ImageGen.RecommendedLongSide; // saved with the old long side list: its size, typed
            Unlocked = s.SizeUnlocked;
            ShowLock();
            WidthBox.Text = before ? s.Width.ToString() : s.CustomWidth > 0 ? s.CustomWidth.ToString() : "";
            HeightBox.Text = before ? s.Height.ToString() : s.CustomHeight > 0 ? s.CustomHeight.ToString() : "";
            Applying = false;
            Show();
            if (!Unlocked && Ratio() is { Length: > 0 } ratio && Typed(WidthBox) is > 0 and var w && Typed(HeightBox) is > 0 and var h &&
                !ImageGen.Matches(w, h, ratio, Recommended().Multiple))
                Aspect.SelectedIndex = 0; // a saved pair that is not its ratio: Custom
        }

        // The settings' size fields: Custom saves both sizes (its recommended one is not restored).
        public (string Aspect, bool Unlocked, int Width, int Height, int CustomWidth, int CustomHeight) Read()
        {
            var (multiple, rw, rh) = Recommended();
            var (cw, ch) = (Typed(WidthBox), Typed(HeightBox));
            int w = ImageGen.Pick(cw, rw, Unlocked ? 1 : multiple), h = ImageGen.Pick(ch, rh, Unlocked ? 1 : multiple);
            var custom = Aspect.SelectedIndex == 0;
            return (custom ? "Custom" : Ratio(), Unlocked, w, h, custom || cw > 0 ? w : 0, custom || ch > 0 ? h : 0);
        }

        public void Show()
        {
            if (Applying || Aspect.SelectedIndex < 0)
                return;
            var (_, w, h) = Recommended();
            (WidthHint.Text, HeightHint.Text) = (w.ToString(), h.ToString());
        }

        // The chosen preset's (Custom: the last preset's) width and height, and the multiple a typed size rounds to.
        private (int Multiple, int Width, int Height) Recommended()
        {
            var multiple = _multiple();
            var (w, h) = ImageGen.Size(PresetRatio(Aspect.SelectedIndex > 0 ? Aspect.SelectedIndex : _lastRatio), ImageGen.RecommendedLongSide, multiple);
            return (multiple, w, h);
        }

        private string PresetRatio(int index) => _flipped ? ImageGen.Flip(ImageGen.Presets[index].Ratio) : ImageGen.Presets[index].Ratio;
        private string Ratio() => Aspect.SelectedIndex > 0 ? PresetRatio(Aspect.SelectedIndex) : ""; // "" = Custom
        private List<string> Labels() => ImageGen.Presets.Select(p => ImageGen.PresetLabel(p, _flipped)).ToList();

        // digits only (the boxes drop anything else) and far below int.MaxValue in practice; 0 = empty
        private static int Typed(ComboBox box) => int.TryParse(box.Text, out var v) ? v : 0;

        // A box is done (focus left, Enter, or a value picked): round it and, with a ratio chosen, make the other follow. A pair that
        // cannot keep the ratio (one side clamped at 4096) turns the preset to Custom. Clearing a linked box clears both: the recommended size.
        public void Commit(bool width)
        {
            if (Applying || Aspect.SelectedIndex < 0)
                return;
            var (box, other) = width ? (WidthBox, HeightBox) : (HeightBox, WidthBox);
            var multiple = Unlocked ? 1 : Recommended().Multiple;
            var typed = Typed(box);
            var value = ImageGen.Pick(typed, 0, multiple);
            box.Text = typed > 0 ? value.ToString() : "";
            if (!Unlocked && Ratio() is { Length: > 0 } ratio)
            {
                other.Text = typed > 0 ? ImageGen.Linked(value, ratio, width, multiple).ToString() : "";
                if (typed > 0 && !ImageGen.Matches(Typed(WidthBox), Typed(HeightBox), ratio, multiple))
                    Aspect.SelectedIndex = 0;
            }
            Changed?.Invoke();
        }

        // The lock beside the size boxes: closed, width and height keep the aspect ratio and round to the model's multiple; open, they
        // are taken as typed (kept to 64-4096). Closing it again fits the typed sizes to the chosen ratio.
        private void ShowLock()
        {
            Lock.Content = Unlocked ? "" : ""; // Unlock, Lock
            Lock.ToolTip = Unlocked ? "Unlocked: any width and height, as typed. Click to keep the aspect ratio"
                : "Locked: width and height keep the aspect ratio and round to the model's multiple. Click to type any size";
            System.Windows.Automation.AutomationProperties.SetName(Lock, Unlocked ? "Aspect ratio unlocked" : "Aspect ratio locked");
            if (!Unlocked && !Applying && WidthBox is not null)
                AspectChanged();
        }

        private void AspectChanged()
        {
            if (Applying || Aspect.SelectedIndex < 0)
                return;
            if (Aspect.SelectedIndex > 0) // a preset: typed sizes take its ratio (from the width first); Custom keeps them as they are
            {
                _lastRatio = Aspect.SelectedIndex;
                if (Typed(WidthBox) > 0)
                    Commit(true);
                else if (Typed(HeightBox) > 0)
                    Commit(false);
            }
            Show();
            Changed?.Invoke();
        }

        // The ↑↓ button: width and height trade places, and the presets show their flipped ratios ("Photo (2:3)").
        private void Swap()
        {
            var index = Aspect.SelectedIndex;
            _flipped = !_flipped;
            Applying = true;
            Aspect.ItemsSource = Labels();
            Aspect.SelectedIndex = index;
            (WidthBox.Text, HeightBox.Text) = (HeightBox.Text, WidthBox.Text);
            Applying = false;
            Show();
            Changed?.Invoke();
        }
    }
}
