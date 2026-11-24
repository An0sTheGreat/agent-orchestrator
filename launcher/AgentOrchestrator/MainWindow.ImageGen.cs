using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AgentOrchestrator;

// The Images page (the sidebar's Images): local image generation with stable-diffusion.cpp's sd-server (ImageGen.cs).
// Settings on the left, the current picture and the gallery (<data>\images\*.png, each with its settings as <name>.json)
// on the right. Generate downloads the runtime and the recipe's model files first when they are missing.
public partial class MainWindow
{
    private CancellationTokenSource? _imgCts; // the download or generation in progress; Cancel and OnClosing cancel it
    private ComboBox _imgRecipe = null!, _imgAspect = null!, _imgWidth = null!, _imgHeight = null!,_imgBatch = null!, _imgSampler = null!, _imgScheduler = null!;
    private TextBox _imgPrompt = null!, _imgNegative = null!, _imgSeed = null!;
    private bool _imgUnlocked; // the size lock open: width and height as typed (ImageSettings.SizeUnlocked)
    private Button _imgSizeLock = null!;
    private bool _imgFlipped; // the swap button: the presets show and give their ratios flipped (portrait)
    private int _imgLastRatio = 1; // the preset whose recommended size Custom keeps showing
    private FrameworkElement _imgNegativeRow = null!;
    private Slider _imgSteps = null!, _imgCfg = null!, _imgStrength = null!;
    private CheckBox _imgLowVram = null!;
    private string _imgInit = "";
    private TextBlock _imgWidthHint = null!, _imgHeightHint = null!,_imgRuntimeText = null!, _imgStatus = null!, _imgVramHint = null!, _imgInitText = null!;
    private Border _imgVramHintRow = null!; // the hint's row under the stage: shown for local runs only (the hint itself while llama-server runs)
    private Button _imgRuntimeButton = null!, _imgGenerate = null!, _imgCancel = null!;
    private ProgressBar _imgProgress = null!;
    private Image _imgResult = null!;
    private string? _imgResultPath;
    private StackPanel _imgGallery = null!;
    private TextBlock _imgGalleryHead = null!, _imgZoomText = null!, _imgPlaceholder = null!;
    private ScrollViewer _imgStage = null!;
    private readonly ScaleTransform _imgScale = new();
    private Point? _imgPanFrom;
    private (double X, double Y) _imgPanOffset;
    private bool _imgApplying; // Apply sets the controls: no recipe reset meanwhile
    // The user's models folder (AppSettings.ImageModelsFolder, else the Hugging Face cache): its image models follow the
    // recipes in the picker, and its files stand in for a recipe's downloads (a text encoder already there).
    private List<ImageGen.LocalModel> _imgLocal = [];
    private List<string> _imgFolderFiles = [];
    private TextBlock _imgFolderText = null!;
    // OpenRouter instead of a local model: the panel shown, the model picked (id, name) and its providers in the order ticked.
    private bool _imgRemoteMode;
    private StackPanel _imgLocalPanel = null!, _imgRemotePanel = null!;
    private Button _imgRemoteModelButton = null!, _imgRemoteProvidersButton = null!;
    private (string Id, string Name) _imgRemoteModel = ("", "");
    private List<string> _imgRemoteProviders = [];
    // The primary PC (sharing its models, MainWindow.Share.cs) instead: its panel, the model id picked (kept before the list
    // loads) and its /images/models list. Offered only while connected (AppSettings.ShareHost).
    private bool _imgSharedMode;
    private StackPanel _imgSharedPanel = null!;
    private ComboBox _imgSharedModel = null!;
    private TextBlock _imgSharedNote = null!;
    private RadioButton _imgConnLocal = null!, _imgConnRemote = null!; // the header's CONNECTION: this PC or the primary PC
    private TextBlock _imgModelBadge = null!;
    private string _imgSharedId = "";
    private List<ImageShare.Model> _imgShared = [];
    private ImageShare.Model? SharedPick => _imgShared.FirstOrDefault(m => m.Id == _imgSharedId);
    private readonly List<UIElement> _imgLocalOnly = []; // the controls only a local run reads (hidden in OpenRouter mode)
    private static string ImageKeyEnv => RemoteCatalog.Services.First(x => x.Id == "openrouter").KeyEnv;
    internal Action<DialogShell>? _imgDialogTest; // --ui-smoke: gets the popup instead of showing it

    private string ImageModelsFolder => _settings.ImageModelsFolder.Length > 0 ? _settings.ImageModelsFolder : LauncherLogic.HubCache();

    private void ScanImageFolder()
    {
        try { (_imgLocal, _imgFolderFiles) = ImageGen.ScanFolder(ImageModelsFolder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { (_imgLocal, _imgFolderFiles) = ([], []); }
    }

    // The picker's item at index: a recipe (downloaded) or a model from the folder (its recipe and path).
    private (ImageRecipe Recipe, string Local) ImageChoice(int index) =>
        index >= ImageGen.Recipes.Length && index - ImageGen.Recipes.Length < _imgLocal.Count
            ? (_imgLocal[index - ImageGen.Recipes.Length].Recipe, _imgLocal[index - ImageGen.Recipes.Length].Path)
            : (ImageGen.Recipes[Math.Clamp(index, 0, ImageGen.Recipes.Length - 1)], "");

    private void OpenImages()
    {
        CloseRemoteApi();
        CloseModelHub();
        CloseModelSettings();
        CloseSkills();
        ScanImageFolder();
        if (ImagesView.Children.Count == 0)
            BuildImagesPage();
        RefreshImageRuntime();
        RefreshImageRecipes();
        RefreshGallery();
        ShowImageSource(); // connected or disconnected since
        if (_imgSharedMode)
            LoadSharedModels();
        Reveal(CodeView, false);
        Reveal(ChatView, false);
        Reveal(ImagesView, true);
    }

    private void CloseImages() => Reveal(ImagesView, false);

    private void CancelImageGen()
    {
        _imgCts?.Cancel(); // the generation's job is cancelled on sd-server, which keeps its models loaded
    }

    private bool ImageBusy => _imgCts is not null;

    // The page never scrolls: a header (title, the CONNECTION pill, the model badge), then the settings card on the left and the
    // stage with the gallery on the right, both reaching the bottom. The card's prompt boxes are about four lines (the card's spare
    // height sits above Generate); View more gives them that spare height (Advanced open or a short window shrinks them); the stage
    // takes what the gallery leaves.
    private void BuildImagesPage()
    {
        TextBlock Muted(string text, double size = 12)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return t;
        }
        TextBlock Head(string text, double bottom = 6) =>
            new() { Text = text.ToUpperInvariant(), Style = (Style)FindResource("SectionHeader"), Margin = new Thickness(0, 0, 0, bottom), VerticalAlignment = VerticalAlignment.Center };
        TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        // a label left of its control, on one line
        DockPanel Inline(string label, UIElement control)
        {
            var l = Label(label);
            l.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(l, Dock.Left);
            return new DockPanel { Children = { l, control } };
        }
        // cells side by side in equal columns; a collapsed cell gives its column to the others
        UniformGrid Pair(double top, params FrameworkElement[] cells)
        {
            for (var i = 0; i < cells.Length; i++)
                cells[i].Margin = new Thickness(i == 0 ? 0 : 8, 0, i == cells.Length - 1 ? 0 : 8, 0);
            var grid = new UniformGrid { Rows = 1, Margin = new Thickness(0, top, 0, 0) };
            foreach (var c in cells)
                grid.Children.Add(c);
            return grid;
        }
        Border Badge(TextBlock text)
        {
            (text.FontSize, text.FontFamily, text.TextWrapping, text.TextTrimming) = (11, AppFonts.Mono, TextWrapping.NoWrap, TextTrimming.CharacterEllipsis);
            var badge = new Border { Child = text, Padding = new Thickness(10, 3, 10, 3), CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(Border.BackgroundProperty, "Hover");
            badge.SetResourceReference(Border.BorderBrushProperty, "Border");
            return badge;
        }
        Slider NewSlider(double min, double max, double step, string format, out FrameworkElement row)
        {
            var value = new TextBlock { Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, _) => value.Text = slider.Value.ToString(format);
            DockPanel.SetDock(value, Dock.Right);
            row = new DockPanel { Children = { value, slider } };
            return slider;
        }
        // Steps and Guidance: the label with a small box at the right of its row, the slider under it with its range; box and slider set each other
        FrameworkElement SliderCell(string label, double min, double max, double step, string format, string tag, out Slider slider)
        {
            var s = slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true };
            var box = new TextBox { Width = 48, Height = 24, Padding = new Thickness(4, 0, 4, 0), TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, Tag = tag };
            s.ValueChanged += (_, _) => box.Text = s.Value.ToString(format);
            // a typed value goes to the slider's step ("." or "," as the decimal point, no thousands), then into its range; the box shows what is used
            void Commit()
            {
                if (double.TryParse(box.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    s.Value = Math.Clamp(Math.Round(v / step, MidpointRounding.AwayFromZero) * step, min, max);
                box.Text = s.Value.ToString(format);
            }
            box.LostKeyboardFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            DockPanel.SetDock(box, Dock.Right);
            var (lo, hi) = (Muted(min.ToString(format), 10), Muted(max.ToString(format), 10));
            DockPanel.SetDock(hi, Dock.Right);
            return new StackPanel
            {
                Children =
                {
                    new DockPanel { Margin = new Thickness(0, 0, 0, 2), Children = { box, Label(label) } }, s,
                    new DockPanel { LastChildFill = false, Children = { hi, lo } },
                },
            };
        }
        var radius = (CornerRadius)FindResource("ControlRadius");
        // a card section: a line under it; a band is slightly darker (as Model settings' top band)
        Border Section(UIElement child, bool band = false, bool last = false)
        {
            var b = new Border { Child = child, Padding = new Thickness(18, band ? 6 : 10, 18, band ? 6 : 10), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1) };
            b.SetResourceReference(Border.BorderBrushProperty, "Border");
            if (band)
                b.Background = new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0));
            return b;
        }

        // the runtime: in Advanced (hidden as a whole in OpenRouter mode, and on the primary PC)
        _imgRuntimeText = Muted("", 11.5);
        (_imgRuntimeText.TextWrapping, _imgRuntimeText.TextTrimming, _imgRuntimeText.VerticalAlignment) = (TextWrapping.NoWrap, TextTrimming.CharacterEllipsis, VerticalAlignment.Center);
        _imgRuntimeButton = new Button { Tag = "ImagesRuntime", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(9, 3, 9, 3), FontSize = 11, Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(_imgRuntimeButton, Dock.Right);
        _imgRuntimeButton.Click += async (_, _) => await RunImageJob(InstallImageRuntimeAsync);
        var runtime = new StackPanel { Children = { new DockPanel { Children = { _imgRuntimeButton, _imgRuntimeText } } } };
        _imgLocalOnly.Add(runtime);

        _imgRecipe = new ComboBox { Tag = "ImagesRecipe" };
        _imgRecipe.SelectionChanged += (_, _) =>
        {
            if (_imgApplying || _imgRecipe.SelectedIndex < 0)
                return;
            var (recipe, local) = ImageChoice(_imgRecipe.SelectedIndex);
            var now = ReadImageSettings(); // the aspect and sizes stay with a new model; its own steps, guidance and sampler come in
            ApplyImageSettings(new ImageSettings
            {
                Aspect = now.Aspect, CustomWidth = now.CustomWidth, CustomHeight = now.CustomHeight, Width = now.Width, Height = now.Height,
                RecipeId = recipe.Id, LocalModel = local, Prompt = _imgPrompt.Text, Negative = _imgNegative.Text, Steps = recipe.Steps, Cfg = recipe.Cfg, Sampler = recipe.Sampler,
                Seed = ReadSeed(), Batch = _imgBatch.SelectedIndex + 1, LowVram = _imgLowVram.IsChecked == true, Strength = _imgStrength.Value, InitImage = _imgInit,
            });
        };
        // The source: the local model (the picker and its folder), an OpenRouter one, or the primary PC's (the header's Remote PC).
        // The panels under MODEL swap; the band above shows the same source's note and links. The local pick is never touched on the way.
        Button SourceButton(string text, string tag, Action click)
        {
            var b = new Button { Content = text, Tag = tag, Style = (Style)FindResource("GhostButton"), Padding = new Thickness(9, 3, 9, 3), FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            b.Click += (_, _) => click();
            return b;
        }
        _imgLocalPanel = new StackPanel { Children = { _imgRecipe } };
        _imgRemoteModelButton = SourceButton("", "ImagesRemoteModelButton", ChooseImageModel);
        _imgRemoteProvidersButton = SourceButton("", "ImagesRemoteProvidersButton", ChooseImageProviders);
        (_imgRemoteModelButton.Margin, _imgRemoteModelButton.FontSize, _imgRemoteProvidersButton.FontSize) = (new Thickness(0), 12, 12);
        _imgRemotePanel = new StackPanel { Visibility = Visibility.Collapsed, Orientation = Orientation.Horizontal, Children = { _imgRemoteModelButton, _imgRemoteProvidersButton } };
        _imgSharedModel = new ComboBox { Tag = "ImagesSharedModel", ToolTip = "Runs on the primary PC this PC is connected to (Settings > Models)" };
        _imgSharedModel.SelectionChanged += (_, _) =>
        {
            if (_imgApplying || _imgSharedModel.SelectedIndex < 0)
                return;
            var m = _imgShared[_imgSharedModel.SelectedIndex];
            _imgSharedId = m.Id;
            (_imgSteps.Value, _imgCfg.Value, _imgSampler.SelectedIndex) = (m.Steps, m.Cfg, Array.IndexOf(ImageGen.Samplers, m.Sampler) + 1);
            ShowImageSource(); // the negative row
            ShowImageSize(); // the multiple
        };
        _imgSharedNote = new TextBlock();
        _imgSharedPanel = new StackPanel { Visibility = Visibility.Collapsed, Children = { _imgSharedModel } };
        _imgFolderText = new TextBlock { Tag = "ImagesFolder" };
        var chooseFolder = SourceButton("Choose folder…", "ImagesChooseFolder", () =>
        {
            var dialog = new OpenFolderDialog { Title = "Folder with image models", InitialDirectory = Directory.Exists(ImageModelsFolder) ? ImageModelsFolder : "" };
            if (dialog.ShowDialog(this) != true)
                return;
            _settings.ImageModelsFolder = dialog.FolderName;
            _settings.Save();
            ScanImageFolder();
            RefreshImageRecipes();
            Toast(_imgLocal.Count == 0 ? "No image models in that folder" : $"{_imgLocal.Count} image model{(_imgLocal.Count == 1 ? "" : "s")} found");
        });
        chooseFolder.ToolTip = "A folder with image models you already have (searched with its subfolders); only image models are listed";
        // MODEL INFO band: per source, its note as a badge and its links at the right (shown with that source's panel)
        var bandNotes = new Grid();
        var bandLinks = new Grid { HorizontalAlignment = HorizontalAlignment.Right };
        void BandFor(StackPanel panel, TextBlock note, params Button[] links)
        {
            var badge = Badge(note);
            badge.HorizontalAlignment = HorizontalAlignment.Left;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var link in links)
                row.Children.Add(link);
            foreach (var part in new FrameworkElement[] { badge, row })
                part.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = panel });
            bandNotes.Children.Add(badge);
            bandLinks.Children.Add(row);
        }
        _imgFolderText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _imgSharedNote.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        BandFor(_imgLocalPanel, _imgFolderText, chooseFolder, SourceButton("Use OpenRouter…", "ImagesUseRemoteApi", () => { _imgRemoteMode = true; ShowImageSource(); }));
        BandFor(_imgRemotePanel, Muted($"OpenRouter · key in {ImageKeyEnv}"), SourceButton("Switch to Local", "ImagesSwitchToLocal", () => { _imgRemoteMode = false; ShowImageSource(); }));
        BandFor(_imgSharedPanel, _imgSharedNote, SourceButton("Switch to Local", "ImagesSharedToLocal", () => { _imgSharedMode = false; ShowImageSource(); }));
        var bandHead = Head("Model info", 0);
        bandHead.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(bandHead, Dock.Left);
        DockPanel.SetDock(bandLinks, Dock.Right);
        var band = Section(new DockPanel { Children = { bandHead, bandLinks, bandNotes } }, band: true);
        band.CornerRadius = new CornerRadius(radius.TopLeft, radius.TopRight, 0, 0);

        var modelSection = Section(Inline("Model", new StackPanel { Children = { _imgLocalPanel, _imgRemotePanel, _imgSharedPanel } }));

        // PROMPTS: positive and negative side by side, about four lines each; View more lets them fill the height the card leaves
        _imgPrompt = new TextBox { Tag = "ImagesPrompt", Padding = new Thickness(10, 4, 10, 4), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
        _imgNegative = new TextBox { Padding = new Thickness(10, 4, 10, 4), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
        // View more / View less: at the right of the positive prompt's header (always shown, the negative cell is not), for both boxes;
        // on the header line because a line of its own would cost a short window's Advanced its last rows
        var promptsMore = new Button { Content = "View more ▾", Tag = "ImagesPromptsMore", Style = (Style)FindResource("BareButton"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0), Height = 15, Margin = new Thickness(8, 0, 0, 4), ToolTip = "Taller prompt boxes, using the card's spare height" };
        promptsMore.SetResourceReference(ForegroundProperty, "Muted");
        DockPanel PromptCell(string label, TextBox box, Button? link = null)
        {
            var head = new DockPanel { Children = { Head(label, 4) } };
            if (link is not null)
            {
                DockPanel.SetDock(link, Dock.Right);
                head.Children.Insert(0, link);
            }
            DockPanel.SetDock(head, Dock.Top);
            return new DockPanel { Children = { head, AttachPromptFade(box) } };
        }
        var positiveCell = PromptCell("Positive prompt", _imgPrompt, promptsMore);
        var negativeCell = PromptCell("Negative prompt", _imgNegative);
        negativeCell.Tag = "ImagesNegative";
        _imgNegativeRow = negativeCell;
        var promptsSection = Section(Pair(0, positiveCell, negativeCell));
        // compact: the row stops where the boxes are CompactPrompt tall and the spare row above Generate takes the rest; View more lifts the cap
        const double CompactPrompt = 288; // about twelve lines; a short window gives less (SizeAdvancedRow keeps them two)
        var cardGrid = new Grid();
        var promptsRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
        var spareRow = new RowDefinition();
        var promptsExpanded = false;
        void SizePromptsRow()
        {
            var chrome = promptsSection.ActualHeight - _imgPrompt.ActualHeight;
            promptsRow.MaxHeight = promptsExpanded || _imgPrompt.ActualHeight <= 0 ? double.PositiveInfinity : chrome + CompactPrompt;
            spareRow.Height = promptsExpanded ? new GridLength(0) : new GridLength(0.001, GridUnitType.Star); // the prompts' 1 is served first, up to their cap
        }
        promptsSection.SizeChanged += (_, _) => SizePromptsRow();
        promptsMore.Click += (_, _) =>
        {
            promptsExpanded = !promptsExpanded;
            promptsMore.Content = promptsExpanded ? "View less ▴" : "View more ▾";
            SizePromptsRow();
        };
        SizePromptsRow();

        // the parameters (no header of their own: the line above sets them off, and the page has no height to spare)
        var steps = SliderCell("Steps", 1, 100, 1, "0", "ImagesStepsBox", out _imgSteps);
        var guidance = SliderCell("Guidance", 0, 20, 0.5, "0.0", "ImagesGuidanceBox", out _imgCfg);
        var stepsRow = Pair(0, steps, guidance);
        _imgLocalOnly.AddRange([stepsRow, steps, guidance]);
        _imgAspect = new ComboBox { Tag = "ImagesAspect" };
        _imgAspect.SelectionChanged += (_, _) => ImageAspectChanged();
        var swap = new Button { Content = "↑↓", Tag = "ImagesSwapSize", ToolTip = "Swap width and height (portrait or landscape)", Template = ParseTemplate(RoundButtonXaml), Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 0, 0) };
        swap.Click += (_, _) => SwapImageSize();
        DockPanel.SetDock(swap, Dock.Right);
        var aspectRow = Inline("Aspect ratio", new DockPanel { Children = { swap, _imgAspect } });
        aspectRow.Margin = new Thickness(0, 8, 0, 0);
        // Width and height: editable lists. Empty shows the recommended value greyed where typed text starts (the caret sits at its left
        // edge); a typed or picked value is rounded to the model's multiple when the box loses focus, and with a ratio chosen the other follows.
        var editable = ParseTemplate(EditableComboXaml);
        DockPanel SizeBox(string tag, bool width, out ComboBox box, out TextBlock hint)
        {
            var b = box = new ComboBox { Tag = tag, MinWidth = 70, IsEditable = true, IsTextSearchEnabled = false, Template = editable, ItemsSource = ImageGen.SizeChoices,
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
                    CommitImageSize(width);
            };
            b.DropDownClosed += (_, _) => CommitImageSize(width); // a value picked from the list
            b.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    CommitImageSize(width);
            };
            var icon = Muted(width ? "⇆" : "⇅");
            (icon.VerticalAlignment, icon.Margin, icon.ToolTip) = (VerticalAlignment.Center, new Thickness(0, 0, 6, 0), width ? "Width" : "Height");
            DockPanel.SetDock(icon, Dock.Left);
            return new DockPanel { Children = { icon, new Grid { Children = { b, h } } } };
        }
        _imgSizeLock = new Button { Tag = "ImagesSizeLock", Template = ParseTemplate(RoundButtonXaml), Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 0, 0),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), VerticalAlignment = VerticalAlignment.Center };
        _imgSizeLock.Click += (_, _) => { _imgUnlocked = !_imgUnlocked; ShowSizeLock(); };
        ShowSizeLock();
        DockPanel.SetDock(_imgSizeLock, Dock.Right);
        var sizeBoxes = new DockPanel { Margin = new Thickness(0, 8, 0, 0),
            Children = { _imgSizeLock, Pair(0, SizeBox("ImagesWidth", true, out _imgWidth, out _imgWidthHint), SizeBox("ImagesHeight", false, out _imgHeight, out _imgHeightHint)) } };
        _imgLocalOnly.Add(sizeBoxes); // OpenRouter is sent the nearest aspect ratio only, not this size
        _imgBatch = new ComboBox { ItemsSource = new[] { 1, 2, 3, 4 } };
        // the style's padding filled a fixed 32 px and hid the text: a slim padding, as the other small boxes
        _imgSeed = new TextBox { Tag = "ImagesSeed", ToolTip = "Empty: a random seed (recorded with the picture)", VerticalContentAlignment = VerticalAlignment.Center,
            Height = 32, Padding = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var seedCell = Inline("Seed", _imgSeed);
        _imgLocalOnly.Add(seedCell);
        var countRow = Pair(8, Inline("Images", _imgBatch), seedCell);
        var paramsSection = Section(new StackPanel { Children = { stepsRow, aspectRow, sizeBoxes, countRow } });

        // ADVANCED SETTINGS band (Sampler, Scheduler, start image, Low VRAM, the runtime): hidden as one in OpenRouter mode
        var advanced = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        var toggle = new Button { Content = "▸  ADVANCED SETTINGS", Tag = "ImagesAdvanced", Style = (Style)FindResource("BareButton"), HorizontalAlignment = HorizontalAlignment.Left, FontSize = 11 };
        toggle.SetResourceReference(ForegroundProperty, "Muted");
        // open, the band shares the card's leftover height with the prompts: all of its content when there is room, else what is left
        // once the prompts have their two lines, scrolling inside (Generate under it always shows). The two lines are kept here, not
        // as the prompts row's MinHeight: the Grid settles a binding MinHeight first and would hand the rest to the spare row.
        var advancedRow = new RowDefinition { Height = GridLength.Auto };
        Border advancedSection = null!;
        void SizeAdvancedRow()
        {
            var open = advanced.Visibility == Visibility.Visible;
            var chrome = advancedSection.Padding.Top + advancedSection.Padding.Bottom + advancedSection.BorderThickness.Bottom + toggle.ActualHeight;
            var p = _imgPrompt;
            var promptsMin = promptsSection.ActualHeight - p.ActualHeight + 2 * p.FontSize * p.FontFamily.LineSpacing + p.Padding.Top + p.Padding.Bottom + p.BorderThickness.Top + p.BorderThickness.Bottom + 1;
            var room = cardGrid.ActualHeight - cardGrid.RowDefinitions.Where(r => r.Height.IsAuto && r != advancedRow).Sum(r => r.ActualHeight) - promptsMin;
            // a fixed height, not a star: beside the prompts' 1* and the spare row's 0.001* a 1000* row trips the Grid's star rounding
            // (the prompts stopped short and the spare row took the difference)
            advancedRow.Height = open ? new GridLength(Math.Max(chrome, Math.Min(chrome + advanced.Margin.Top + advanced.ActualHeight, room))) : GridLength.Auto;
        }
        advanced.SizeChanged += (_, _) => SizeAdvancedRow();
        cardGrid.SizeChanged += (_, _) => SizeAdvancedRow();
        promptsSection.SizeChanged += (_, _) => SizeAdvancedRow(); // its chrome, unknown until the boxes have a height
        toggle.Click += (_, _) =>
        {
            var show = advanced.Visibility != Visibility.Visible;
            advanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = (show ? "▾" : "▸") + "  ADVANCED SETTINGS";
            SizeAdvancedRow();
        };
        _imgSampler = new ComboBox { ItemsSource = ImageGen.Samplers.Prepend("Default").ToList() };
        _imgScheduler = new ComboBox { ItemsSource = ImageGen.Schedulers.Prepend("Default").ToList(), Tag = "ImagesScheduler",
            ToolTip = "How the noise level steps down over the steps; Default is the model's own" };
        _imgLowVram = new CheckBox { Content = "Low VRAM", ToolTip = "One model on the GPU at a time: runs the text encoder, then swaps in the image model, then the decoder (weights wait in RAM). Slower to load; needed when the models together are bigger than the GPU", VerticalAlignment = VerticalAlignment.Center };
        _imgLowVram.SetResourceReference(ForegroundProperty, "TextPrimary");
        _imgInitText = Muted("None", 11.5);
        (_imgInitText.VerticalAlignment, _imgInitText.TextWrapping, _imgInitText.TextTrimming) = (VerticalAlignment.Center, TextWrapping.NoWrap, TextTrimming.CharacterEllipsis);
        var pick = new Button { Content = "Start image…", ToolTip = "Start from an image (img2img)", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 4, 0) };
        var clear = new Button { Content = "✕", ToolTip = "Clear the start image", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 0) };
        pick.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
            if (dialog.ShowDialog(this) == true)
                SetInitImage(dialog.FileName);
        };
        clear.Click += (_, _) => SetInitImage("");
        DockPanel.SetDock(pick, Dock.Left);
        DockPanel.SetDock(clear, Dock.Left);
        _imgStrength = NewSlider(0, 1, 0.05, "0.00", out var strengthRow);
        strengthRow.ToolTip = "Strength: how far from the start image";
        advanced.ColumnDefinitions.Add(new ColumnDefinition());
        advanced.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var _ in new[] { 0, 1, 2 })
            advanced.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Cell(FrameworkElement element, int row, int column)
        {
            (element.Margin, element.VerticalAlignment) = (new Thickness(column == 0 ? 0 : 8, row == 0 ? 0 : 6, column == 0 ? 8 : 0, 0), VerticalAlignment.Top);
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            advanced.Children.Add(element);
        }
        Cell(Inline("Sampler", _imgSampler), 0, 0);
        Cell(Inline("Scheduler", _imgScheduler), 0, 1);
        Cell(new DockPanel { Children = { pick, clear, _imgInitText } }, 1, 0);
        Cell(Inline("Strength", strengthRow), 1, 1);
        Cell(_imgLowVram, 2, 0);
        Cell(runtime, 2, 1);
        DockPanel.SetDock(toggle, Dock.Top);
        advancedSection = Section(new DockPanel { Children = { toggle, new ScrollViewer { Content = advanced, Tag = "ImagesAdvancedScroll", VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false } } }, band: true);
        _imgLocalOnly.Add(advancedSection);

        _imgGenerate = new Button { Content = "Generate", Tag = "ImagesGenerate", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 7, 16, 7) };
        _imgGenerate.Click += async (_, _) => await RunImageJob(GenerateImagesAsync);
        var generateSection = Section(_imgGenerate, last: true);

        // the card: its sections top to bottom, the prompts (at least two lines) and Advanced sharing the rest, the spare row (null) over Generate at the foot
        var sections = new FrameworkElement?[] { band, modelSection, promptsSection, paramsSection, advancedSection, null, generateSection };
        for (var i = 0; i < sections.Length; i++)
        {
            cardGrid.RowDefinitions.Add(sections[i] == promptsSection ? promptsRow : sections[i] == advancedSection ? advancedRow
                : sections[i] is null ? spareRow : new RowDefinition { Height = GridLength.Auto });
            if (sections[i] is not { } section)
                continue;
            Grid.SetRow(section, i);
            cardGrid.Children.Add(section);
        }
        foreach (var field in new Control[] { _imgRecipe, _imgSharedModel, _imgPrompt, _imgNegative, _imgAspect, _imgWidth, _imgHeight, _imgBatch, _imgSeed, _imgSampler, _imgScheduler,
            (Control)((DockPanel)((StackPanel)steps).Children[0]).Children[0], (Control)((DockPanel)((StackPanel)guidance).Children[0]).Children[0] })
        {
            field.SetResourceReference(Control.BackgroundProperty, "Field");
            field.SetResourceReference(Control.BorderBrushProperty, "Border");
        }
        var card = new Border { Child = cardGrid, BorderThickness = new Thickness(1), Tag = "ImagesCard", ClipToBounds = true };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        // right: the stage (the picture centred and fitted; the wheel zooms at the cursor, a drag pans, a double-click fits it
        // again), the progress under it, and the gallery as a strip along the bottom (a click puts a picture on the stage)
        _imgResult = new Image { Stretch = Stretch.Uniform, Tag = "ImagesResult", LayoutTransform = _imgScale };
        RenderOptions.SetBitmapScalingMode(_imgResult, BitmapScalingMode.HighQuality);
        _imgStage = new ScrollViewer
        {
            Content = _imgResult, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
        };
        // at 100% the picture fits the stage; zooming scales that
        _imgResult.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = _imgStage });
        _imgResult.SetBinding(HeightProperty, new System.Windows.Data.Binding(nameof(ActualHeight)) { Source = _imgStage });
        _imgStage.PreviewMouseWheel += (_, e) =>
        {
            if (_imgResult.Source is null)
                return;
            e.Handled = true;
            ZoomStage(e.Delta > 0 ? 1.15 : 1 / 1.15, e.GetPosition(_imgResult), e.GetPosition(_imgStage));
        };
        _imgStage.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                FitStage();
                e.Handled = true;
                return;
            }
            if (_imgStage.ScrollableWidth <= 0 && _imgStage.ScrollableHeight <= 0)
                return;
            _imgPanFrom = e.GetPosition(_imgStage);
            _imgPanOffset = (_imgStage.HorizontalOffset, _imgStage.VerticalOffset);
            _imgStage.Cursor = Cursors.SizeAll;
            _imgStage.CaptureMouse();
            e.Handled = true;
        };
        _imgStage.PreviewMouseMove += (_, e) =>
        {
            if (_imgPanFrom is not { } from || e.LeftButton != MouseButtonState.Pressed)
                return;
            var at = e.GetPosition(_imgStage);
            _imgStage.ScrollToHorizontalOffset(_imgPanOffset.X - (at.X - from.X));
            _imgStage.ScrollToVerticalOffset(_imgPanOffset.Y - (at.Y - from.Y));
        };
        void EndPan()
        {
            _imgPanFrom = null;
            _imgStage.Cursor = null;
            if (_imgStage.IsMouseCaptured)
                _imgStage.ReleaseMouseCapture();
        }
        _imgStage.PreviewMouseLeftButtonUp += (_, _) => EndPan();
        _imgStage.LostMouseCapture += (_, _) => EndPan();
        _imgPlaceholder = Muted("Generated pictures appear here", 13);
        _imgPlaceholder.HorizontalAlignment = HorizontalAlignment.Center;
        _imgPlaceholder.VerticalAlignment = VerticalAlignment.Center;
        _imgPlaceholder.IsHitTestVisible = false;
        // the stage's corner: zoom, Fit, Full size
        _imgZoomText = Muted("100%", 11.5);
        _imgZoomText.Tag = "ImagesZoom";
        _imgZoomText.VerticalAlignment = VerticalAlignment.Center;
        _imgZoomText.Margin = new Thickness(4, 0, 8, 0);
        _imgZoomText.MinWidth = 34;
        Button StageButton(string text, string tip, Action click)
        {
            var button = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("GhostButton"), Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0, 0, 4, 0), FontSize = 11.5 };
            button.Click += (_, _) => click();
            return button;
        }
        var tools = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(4),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    _imgZoomText, StageButton("Fit", "Fit the picture to the stage (or double-click it)", FitStage),
                    StageButton("Full size", "Open the picture over the whole window", () => { if (_imgResultPath is { } p) ShowImage(p); }),
                },
            },
        };
        tools.SetResourceReference(Border.BackgroundProperty, "Card");
        tools.SetResourceReference(Border.BorderBrushProperty, "Border");
        tools.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Image.Source)) { Source = _imgResult, Converter = new NullToCollapsed() });
        var stage = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(12), ClipToBounds = true, Child = new Grid { Children = { _imgStage, _imgPlaceholder, tools } } };
        stage.SetResourceReference(Border.BackgroundProperty, "Card");
        stage.SetResourceReference(Border.BorderBrushProperty, "Border");
        stage.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");

        stage.SetResourceReference(Border.BackgroundProperty, "Panel");

        _imgProgress = new ProgressBar { Height = 4, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        _imgStatus = Muted("", 12);
        _imgStatus.Tag = "ImagesStatus";
        _imgStatus.VerticalAlignment = VerticalAlignment.Center;
        _imgCancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 3, 12, 3), Visibility = Visibility.Collapsed, Margin = new Thickness(10, 0, 0, 0) };
        _imgCancel.Click += (_, _) => CancelImageGen();
        DockPanel.SetDock(_imgCancel, Dock.Right);
        var statusRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0), Children = { _imgCancel, _imgStatus } };
        // a warning, so always in view under the status (not in Advanced); local runs only
        _imgVramHint = Muted("Chat models are loaded and using VRAM; image generation may be slower or run out of memory.", 11.5);
        _imgVramHint.Margin = new Thickness(0, 4, 0, 0);
        _imgVramHintRow = new Border { Child = _imgVramHint };
        _imgGallery = new StackPanel { Orientation = Orientation.Horizontal, Tag = "ImagesGallery" };
        _imgGalleryHead = Muted("", 11);
        DockPanel.SetDock(_imgGalleryHead, Dock.Right);
        var deleteAll = new Button { Content = "Delete all", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(10, 2, 10, 2), FontSize = 11,
            Margin = new Thickness(0, 0, 10, 0), Tag = "ImagesDeleteAll", ToolTip = "Overwrite and delete every picture in the gallery" };
        DockPanel.SetDock(deleteAll, Dock.Right);
        deleteAll.Click += (_, _) =>
        {
            var all = Directory.Exists(ImageGen.ImagesFolder) ? Directory.GetFiles(ImageGen.ImagesFolder, "*.png") : [];
            if (all.Length == 0 || SidebarAsk("Delete all pictures", $"Delete all {all.Length} picture{(all.Length == 1 ? "" : "s")} for good? They are overwritten, not moved to the Recycle Bin, so they can't be brought back.", "Delete all") is null)
                return;
            ShredPictures(all);
        };
        // one row of thumbnails, always in view; the wheel scrolls it sideways
        var galleryScroll = new ScrollViewer
        {
            Content = _imgGallery, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Height = 122, Margin = new Thickness(0, 8, 0, 0),
        };
        galleryScroll.PreviewMouseWheel += (_, e) =>
        {
            galleryScroll.ScrollToHorizontalOffset(galleryScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        var galleryCard = new Border
        {
            BorderThickness = new Thickness(1), Padding = new Thickness(18, 12, 18, 10), Margin = new Thickness(0, 12, 0, 0),
            Child = new StackPanel { Children = { new DockPanel { Children = { _imgGalleryHead, deleteAll, Head("Gallery", 0) } }, galleryScroll } },
        };
        galleryCard.SetResourceReference(Border.BackgroundProperty, "Card");
        galleryCard.SetResourceReference(Border.BorderBrushProperty, "Border");
        galleryCard.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        var right = new Grid { Tag = "ImagesRight", Margin = new Thickness(16, 0, 0, 0) };
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        foreach (var _ in new[] { 1, 2, 3, 4 })
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var (element, row) in new (UIElement, int)[] { (stage, 0), (_imgProgress, 1), (statusRow, 2), (_imgVramHintRow, 3), (galleryCard, 4) })
        {
            Grid.SetRow(element, row);
            right.Children.Add(element);
        }

        // the header: the title left, CONNECTION (this PC or the primary PC) in a pill exactly centred, the model in use right
        var title = new TextBlock { Text = "Image Generation", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        RadioButton Segment(string text, string tag, string tip, Action click)
        {
            var b = new RadioButton { Content = text, Tag = tag, GroupName = "ImagesConnection", Style = (Style)FindResource("HeaderSeg"), ToolTip = tip };
            ToolTipService.SetShowOnDisabled(b, true);
            b.Click += (_, _) => click();
            return b;
        }
        _imgConnLocal = Segment("LOCAL", "ImagesConnectionLocal", "Generate on this PC", () => { _imgSharedMode = _imgRemoteMode = false; ShowImageSource(); });
        _imgConnRemote = Segment("REMOTE PC", "ImagesConnectionRemote", "", () => { (_imgSharedMode, _imgRemoteMode) = (true, false); ShowImageSource(); LoadSharedModels(); });
        var segments = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _imgConnLocal, _imgConnRemote } } };
        segments.SetResourceReference(Border.BorderBrushProperty, "Border");
        segments.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        var connectionHead = Head("Connection", 3);
        (connectionHead.FontSize, connectionHead.Margin) = (9, new Thickness(4, 0, 0, 3));
        var pill = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(30), Padding = new Thickness(20, 6, 20, 6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Children = { connectionHead, segments } } };
        pill.SetResourceReference(Border.BackgroundProperty, "Panel");
        pill.SetResourceReference(Border.BorderBrushProperty, "Border");
        _imgModelBadge = new TextBlock { Tag = "ImagesModelBadge", MaxWidth = 320 };
        _imgModelBadge.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var modelBadge = Badge(_imgModelBadge);
        modelBadge.HorizontalAlignment = HorizontalAlignment.Right;
        var header = new Grid { MinHeight = 56, Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (element, column) in new (UIElement, int)[] { (title, 0), (pill, 1), (modelBadge, 2) })
        {
            Grid.SetColumn(element, column);
            header.Children.Add(element);
        }

        // the card about 575 wide, narrower (to 420) as the window narrows, the right column the rest
        var page = new Grid { Tag = "ImagesPage", Margin = new Thickness(19, 8, 19, 16) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition());
        var leftColumn = new ColumnDefinition { Width = new GridLength(575) };
        page.ColumnDefinitions.Add(leftColumn);
        page.ColumnDefinitions.Add(new ColumnDefinition());
        page.SizeChanged += (_, e) => leftColumn.Width = new GridLength(Math.Clamp(e.NewSize.Width - 560, 420, 575));
        Grid.SetColumnSpan(header, 2);
        Grid.SetRow(card, 1);
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 1);
        page.Children.Add(header);
        page.Children.Add(card);
        page.Children.Add(right);
        ImagesView.Children.Add(page);
        HookImageDrop();
        RefreshImageRecipes(); // the items first: Apply selects one
        ApplyImageSettings(_settings.ImageGen);
    }

    private long ReadSeed() => long.TryParse(_imgSeed.Text.Trim(), out var seed) && seed >= 0 ? seed : -1;

    private void SetInitImage(string path)
    {
        _imgInit = path;
        _imgInitText.Text = path.Length > 0 ? Path.GetFileName(path) : "None";
        _imgInitText.ToolTip = path.Length > 0 ? path : null;
    }

    private void ApplyImageSettings(ImageSettings s)
    {
        _imgApplying = true;
        var local = _imgLocal.FindIndex(m => s.LocalModel.Length > 0 && string.Equals(m.Path, s.LocalModel, StringComparison.OrdinalIgnoreCase));
        var recipe = local >= 0 ? _imgLocal[local].Recipe : ImageGen.Recipe(s.RecipeId);
        _imgSharedMode = s.SharedModel.Length > 0;
        _imgRemoteMode = !_imgSharedMode && s.RemoteModel.Length > 0;
        if (_imgRemoteMode)
            (_imgRemoteModel, _imgRemoteProviders) = ((s.RemoteModel, ""), s.RemoteProvider.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList());
        if (_imgSharedMode)
        {
            _imgSharedId = s.SharedModel;
            _imgSharedModel.SelectedIndex = _imgShared.FindIndex(m => m.Id == s.SharedModel);
        }
        else // in shared mode RecipeId is the primary PC's id: the local pick stays as it was
            _imgRecipe.SelectedIndex = local >= 0 ? ImageGen.Recipes.Length + local : Array.IndexOf(ImageGen.Recipes, recipe); // the local pick is kept in OpenRouter mode too, for the way back
        ShowImageSource();
        _imgPrompt.Text = s.Prompt;
        _imgNegative.Text = s.Negative;
        var preset = Array.FindIndex(ImageGen.Presets, p => p.Ratio.Length > 0 && (p.Ratio == s.Aspect || ImageGen.Flip(p.Ratio) == s.Aspect));
        (_imgFlipped, _imgLastRatio) = (preset > 0 && ImageGen.Presets[preset].Ratio != s.Aspect, Math.Max(preset, 1));
        _imgAspect.ItemsSource = AspectLabels();
        _imgAspect.SelectedIndex = s.Aspect == "Custom" ? 0 : _imgLastRatio; // unknown: Square
        var before = s.CustomWidth == 0 && s.CustomHeight == 0 && s.LongSide != ImageGen.RecommendedLongSide; // saved with the old long side list: its size, typed
        _imgUnlocked = s.SizeUnlocked;
        ShowSizeLock();
        _imgWidth.Text = before ? s.Width.ToString() : s.CustomWidth > 0 ? s.CustomWidth.ToString() : "";
        _imgHeight.Text = before ? s.Height.ToString() : s.CustomHeight > 0 ? s.CustomHeight.ToString() : "";
        _imgSteps.Value = s.Steps;
        _imgCfg.Value = s.Cfg;
        _imgSeed.Text = s.Seed >= 0 ? s.Seed.ToString() : "";
        _imgBatch.SelectedIndex = Math.Clamp(s.Batch, 1, 4) - 1;
        _imgSampler.SelectedIndex = Array.IndexOf(ImageGen.Samplers, s.Sampler) + 1; // not found: Default
        _imgScheduler.SelectedIndex = Array.IndexOf(ImageGen.Schedulers, s.Scheduler) + 1;
        _imgLowVram.IsChecked = s.LowVram;
        _imgStrength.Value = s.Strength;
        SetInitImage(s.InitImage);
        _imgApplying = false;
        ShowImageSize();
        if (!_imgUnlocked && ImageRatio() is { Length: > 0 } ratio && TypedSize(_imgWidth) is > 0 and var w && TypedSize(_imgHeight) is > 0 and var h &&
            !ImageGen.Matches(w, h, ratio, RecommendedImageSize().Multiple))
            _imgAspect.SelectedIndex = 0; // a saved pair that is not its ratio: Custom
    }

    private ImageSettings ReadImageSettings()
    {
        var (recipe, local) = ImageChoice(_imgRecipe.SelectedIndex);
        var negative = _imgSharedMode && SharedPick is { } shared ? shared.Negative : recipe.Negative;
        var (multiple, rw, rh) = RecommendedImageSize();
        var (cw, ch) = (TypedSize(_imgWidth), TypedSize(_imgHeight));
        int w = ImageGen.Pick(cw, rw, _imgUnlocked ? 1 : multiple), h = ImageGen.Pick(ch, rh, _imgUnlocked ? 1 : multiple);
        var custom = _imgAspect.SelectedIndex == 0; // saves both sizes: Custom's recommended one is not restored
        return new()
        {
            RecipeId = _imgSharedMode ? _imgSharedId : recipe.Id, LocalModel = _imgSharedMode ? "" : local, SharedModel = _imgSharedMode ? _imgSharedId : "",
            Prompt = _imgPrompt.Text.Trim(), Negative = negative ? _imgNegative.Text.Trim() : "", Aspect = custom ? "Custom" : ImageRatio(),
            SizeUnlocked = _imgUnlocked, Width = w, Height = h, CustomWidth = custom || cw > 0 ? w : 0, CustomHeight = custom || ch > 0 ? h : 0, Steps = (int)_imgSteps.Value, Cfg = _imgCfg.Value, Seed = ReadSeed(), Batch = _imgBatch.SelectedIndex + 1,
            Sampler = _imgSampler.SelectedIndex > 0 ? ImageGen.Samplers[_imgSampler.SelectedIndex - 1] : "", LowVram = _imgLowVram.IsChecked == true,
            Scheduler = _imgScheduler.SelectedIndex > 0 ? ImageGen.Schedulers[_imgScheduler.SelectedIndex - 1] : "",
            Strength = Math.Round(_imgStrength.Value, 2), InitImage = _imgInit,
            RemoteModel = _imgRemoteMode ? _imgRemoteModel.Id.Trim() : "", RemoteProvider = _imgRemoteMode ? string.Join(", ", _imgRemoteProviders) : "",
        };
    }

    // A prompt box fills its cell (the page never scrolls; its own scrollbar reaches the rest): when its text is taller, the bottom
    // edge fades out (an opacity mask, so any theme's background shows through) until the end of the text is in view.
    private const double FadeHeight = 20;

    private FrameworkElement AttachPromptFade(TextBox box)
    {
        void Update()
        {
            if (!box.IsVisible || box.ViewportHeight <= 0)
                return; // hidden (the negative cell in OpenRouter mode) or not laid out yet: keep the state
            var tall = box.ExtentHeight > box.ViewportHeight + 1;
            var atEnd = box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 1; // the line being typed must not sit in the fade
            if (!tall || atEnd)
            {
                box.OpacityMask = null;
                return;
            }
            var fade = Math.Min(FadeHeight, box.ActualHeight / 2) / Math.Max(1, box.ActualHeight);
            box.OpacityMask = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 1 - fade), new GradientStop(Color.FromArgb(0x10, 0, 0, 0), 1),
            }, new Point(0, 0), new Point(0, 1));
        }
        box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Update()));
        box.TextChanged += (_, _) => box.Dispatcher.BeginInvoke(Update, System.Windows.Threading.DispatcherPriority.Loaded);
        box.SizeChanged += (_, _) => Update();
        box.IsVisibleChanged += (_, _) => box.Dispatcher.BeginInvoke(Update, System.Windows.Threading.DispatcherPriority.Loaded);
        return box;
    }

    // The panel for the source in use; the model button shows the name (the id until the list has been fetched), the providers button the ticks.
    // Never loads the primary PC's list: a failed load shows the source again, which would load again.
    private void ShowImageSource()
    {
        static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
        _imgSharedMode &= _settings.ShareHost.Length > 0; // disconnected since: back to Local
        _imgLocalPanel.Visibility = Shown(!_imgRemoteMode && !_imgSharedMode);
        _imgRemotePanel.Visibility = Shown(_imgRemoteMode);
        _imgSharedPanel.Visibility = Shown(_imgSharedMode);
        // the pill: neither segment in OpenRouter mode (LOCAL leaves it); both off while a job runs
        _imgConnLocal.IsEnabled = !ImageBusy;
        _imgConnRemote.IsEnabled = !ImageBusy && _settings.ShareHost.Length > 0;
        _imgConnRemote.ToolTip = _settings.ShareHost.Length > 0 ? $"Generate on the primary PC ({_settings.ShareHost})" : "Connect to a primary PC in Settings › Models";
        _imgConnLocal.IsChecked = !_imgSharedMode && !_imgRemoteMode;
        _imgConnRemote.IsChecked = _imgSharedMode;
        _imgVramHintRow.Visibility = Shown(!_imgRemoteMode && !_imgSharedMode);
        _imgModelBadge.Text = _imgSharedMode ? SharedPick?.Name ?? _imgSharedId
            : _imgRemoteMode ? (_imgRemoteModel.Name.Length > 0 ? _imgRemoteModel.Name : _imgRemoteModel.Id)
            : _imgRecipe.SelectedIndex < 0 ? "" : ImageChoice(_imgRecipe.SelectedIndex) is var (r, l) && l.Length > 0 ? Path.GetFileName(l) : r.Name;
        foreach (var control in _imgLocalOnly) // the primary PC takes the settings, but not this PC's runtime
            control.Visibility = Shown(!_imgRemoteMode && !(_imgSharedMode && control == ((FrameworkElement)_imgRuntimeText.Parent).Parent));
        _imgNegativeRow.Visibility = Shown(_imgSharedMode ? SharedPick?.Negative == true
            : !_imgRemoteMode && _imgRecipe.SelectedIndex >= 0 && ImageChoice(_imgRecipe.SelectedIndex).Recipe.Negative);
        _imgRemoteModelButton.Content = (_imgRemoteModel.Id.Length == 0 ? "Choose a model…" : _imgRemoteModel.Name.Length > 0 ? _imgRemoteModel.Name : _imgRemoteModel.Id) + " ▾";
        _imgRemoteProvidersButton.Content = "Providers: " + (_imgRemoteProviders.Count == 0 ? "any" : string.Join(", ", _imgRemoteProviders));
    }

    // The primary PC's /images/models into the picker, the pick kept by id; a failure shows in the note under it.
    private async void LoadSharedModels()
    {
        var host = _settings.ShareHost;
        _imgSharedNote.Text = $"Loading the models of {host}…";
        List<ImageShare.Model> models;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            models = await ImageShare.FetchModelsAsync(http, ModelShare.ImagesUrl(host), Environment.GetEnvironmentVariable(ModelShare.KeyEnv) ?? "", CancellationToken.None);
            _imgSharedNote.Text = models.Count == 0 ? $"{host} has no image models" : $"{models.Count} models on {host}";
        }
        catch (Exception x) when (x is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
        {
            models = [];
            _imgSharedNote.Text = x is TaskCanceledException ? $"No answer from {host} in 30 s" : x.Message;
        }
        _imgApplying = true;
        _imgShared = models;
        _imgSharedModel.ItemsSource = models.Select(m => m.Note.Length > 0 ? $"{m.Name} · {m.Note}" : m.Name).ToList();
        _imgSharedModel.SelectedIndex = models.FindIndex(m => m.Id == _imgSharedId);
        _imgApplying = false;
        ShowImageSource();
        ShowImageSize();
    }

    private void ShowImagePopup(DialogShell popup)
    {
        if (_imgDialogTest is { } test)
            test(popup);
        else
            popup.ShowDialog();
    }

    internal Popup? _imgModelPopupTest; // --ui-smoke: the model dropdown opened last
    private DateTime _imgModelPressClosedAt;

    // A dropdown under the model button: a filterable list of OpenRouter's image models, each with its price; a pick keeps its id and name and
    // closes it. A model with no id is skipped. A failed price lookup only leaves the prices as "—".
    private void ChooseImageModel()
    {
        // StaysOpen=false closes only while the popup holds the mouse, which the filter box takes: a press anywhere in the window closes it
        // (technique of ModelPicker). A press on the button closes it, and that same click's release must not open it again.
        if (_imgModelPopupTest is { IsOpen: true } open)
        {
            open.IsOpen = false;
            return;
        }
        if (DateTime.UtcNow - _imgModelPressClosedAt < TimeSpan.FromMilliseconds(400))
            return;
        var filter = new TextBox { Tag = "ImagesModelFilter", Margin = new Thickness(0, 0, 0, 8) };
        var status = RemoteMuted("Loading models…");
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var card = new Border
        {
            BorderThickness = new Thickness(1), Padding = new Thickness(8), MinWidth = 420, Width = Math.Max(_imgRemoteModelButton.ActualWidth, 420),
            Child = new StackPanel { Children = { filter, status, scroll } },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        var popup = _imgModelPopupTest = new Popup { Child = card, PlacementTarget = _imgRemoteModelButton, Placement = PlacementMode.Bottom, VerticalOffset = 4, AllowsTransparency = true, StaysOpen = false };
        void OnPress(object sender, MouseButtonEventArgs e)
        {
            _imgModelPressClosedAt = DateTime.UtcNow;
            popup.IsOpen = false;
        }
        popup.Opened += (_, _) => filter.Focus();
        void OnGone(object sender, DependencyPropertyChangedEventArgs e) // the page was switched away from (collapsed, not unloaded)
        {
            if (!_imgRemoteModelButton.IsVisible)
                popup.IsOpen = false;
        }
        popup.Closed += (_, _) =>
        {
            PreviewMouseDown -= OnPress;
            _imgRemoteModelButton.IsVisibleChanged -= OnGone;
            // closed by WPF itself with the button held down (our own press handler was not the one): its release must not reopen it
            if (Mouse.LeftButton == MouseButtonState.Pressed)
                _imgModelPressClosedAt = DateTime.UtcNow;
        };
        card.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
        };
        var models = new List<(string Id, string Name)>();
        var prices = new Dictionary<string, RemoteCatalog.ImagePrice>();
        void Show()
        {
            list.Children.Clear();
            foreach (var (id, name) in models.Where(m => $"{m.Name} {m.Id}".Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                var price = RemoteMuted(RemoteCatalog.ImagePriceText(prices.GetValueOrDefault(id)));
                price.VerticalAlignment = VerticalAlignment.Center;
                price.Margin = new Thickness(12, 0, 0, 0);
                var who = new StackPanel { Children = { new TextBlock { Text = name }, RemoteMuted(id) } };
                var cells = new DockPanel { Children = { price, who } };
                DockPanel.SetDock(price, Dock.Right);
                var row = new Button
                {
                    Content = cells, Tag = "imgmodel:" + id, Style = (Style)FindResource("GhostButton"),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 0, 2),
                };
                row.Click += (_, _) =>
                {
                    if (_imgRemoteModel.Id != id)
                        _imgRemoteProviders = []; // providers belong to a model
                    _imgRemoteModel = (id, name);
                    ShowImageSource();
                    popup.IsOpen = false;
                };
                list.Children.Add(row);
            }
        }
        async void Load()
        {
            try
            {
                var key = Environment.GetEnvironmentVariable(ImageKeyEnv);
                var priced = RemoteCatalog.FetchImagePricesAsync(_remoteHttp, key, CancellationToken.None);
                var got = await RemoteCatalog.FetchImageModelsAsync(_remoteHttp, key, CancellationToken.None); // the list never waits for the prices
                models = [.. got.Where(m => m.Id.Trim().Length > 0).Select(m => (m.Id.Trim(), m.Name.Trim().Length > 0 ? m.Name.Trim() : m.Id.Trim()))];
                status.Text = $"{models.Count} image models";
                Show();
                try { prices = await priced; }
                catch (Exception x) when (x is HttpRequestException or InvalidOperationException or TaskCanceledException) { return; } // prices are a nicety: the dashes stay
                var at = scroll.VerticalOffset;
                Show(); // the same rows, now priced (the filter text and focus are untouched)
                scroll.UpdateLayout();
                scroll.ScrollToVerticalOffset(at);
            }
            catch (Exception x) when (x is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                status.Text = $"Could not load the models ({x.Message}).";
            }
        }
        filter.TextChanged += (_, _) => Show();
        _imgRemoteModelButton.IsVisibleChanged += OnGone;
        Load();
        popup.IsOpen = true;
        if (popup.IsOpen)
            PreviewMouseDown += OnPress; // presses in the popup go to its own window, not this one
    }

    // The chosen model's providers, a tick each with its order number (tried in that order); ticking edits the page's choice at once.
    private void ChooseImageProviders()
    {
        var model = _imgRemoteModel.Id.Trim();
        if (model.Length == 0)
        {
            Toast("Choose a model first");
            return;
        }
        var status = RemoteMuted("Loading providers…");
        status.Margin = new Thickness(0, 8, 0, 0);
        var list = new StackPanel();
        var done = new Button { Content = "Done", Tag = "ImagesProvidersDone", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 5, 16, 5), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var popup = new DialogShell(this, $"Providers for {model}", new StackPanel
        {
            MinWidth = 460,
            Children = { RemoteMuted("Tick the providers to use; they are tried in the order you tick them. None ticked: OpenRouter chooses."), status, new ScrollViewer { Content = list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, done },
        });
        var orderMarks = new Dictionary<string, TextBlock>();
        void Marks()
        {
            foreach (var (tag, mark) in orderMarks)
                mark.Text = _imgRemoteProviders.IndexOf(tag) is >= 0 and var at ? $"#{at + 1}" : "";
            ShowImageSource();
        }
        async void Load()
        {
            try
            {
                var rows = (await RemoteCatalog.FetchImageProvidersAsync(_remoteHttp, model, CancellationToken.None, Environment.GetEnvironmentVariable(ImageKeyEnv))).Where(r => r.ProviderTag.Length > 0).ToList();
                if (model != _imgRemoteModel.Id.Trim())
                    return; // another model was picked while this loaded: its ticks are not ours to clear
                if (rows.Count > 0)
                    _imgRemoteProviders.RemoveAll(t => rows.All(r => r.ProviderTag != t)); // a provider gone since it was ticked
                status.Text = rows.Count == 0 ? $"No providers listed for {model}: OpenRouter chooses." : $"{rows.Count} providers · USD, in each provider's own unit";
                foreach (var r in rows)
                {
                    var tag = r.ProviderTag;
                    var mark = new TextBlock { FontWeight = FontWeights.SemiBold, Width = 28, TextAlignment = TextAlignment.Right };
                    mark.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
                    orderMarks[tag] = mark;
                    var who = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = r.ProviderName, FontWeight = FontWeights.SemiBold }, RemoteMuted(tag) } };
                    who.Children[1].SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
                    var cost = RemoteMuted(RemoteCatalog.ImageCostText(r.CostPerImage, r.Unit));
                    cost.Width = 110;
                    cost.TextAlignment = TextAlignment.Right;
                    var row = new ToggleButton { Content = new DockPanel { Children = { mark, cost, who } }, Tag = "provider:" + tag, IsChecked = _imgRemoteProviders.Contains(tag), Height = 34, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 0, 1), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    DockPanel.SetDock(mark, Dock.Right);
                    DockPanel.SetDock(cost, Dock.Right);
                    row.SetResourceReference(StyleProperty, "NavToggle");
                    row.Click += (_, _) =>
                    {
                        if (row.IsChecked == true && !_imgRemoteProviders.Contains(tag))
                            _imgRemoteProviders.Add(tag);
                        else if (row.IsChecked != true)
                            _imgRemoteProviders.Remove(tag);
                        Marks();
                    };
                    list.Children.Add(row);
                }
                Marks();
            }
            catch (Exception x) when (x is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                status.Text = $"Could not load the providers ({x.Message}). OpenRouter chooses.";
            }
        }
        done.Click += (_, _) => popup.Close();
        Load();
        ShowImagePopup(popup);
    }

    private void ShowImageSize()
    {
        if (_imgApplying || _imgAspect.SelectedIndex < 0)
            return;
        var (_, w, h) = RecommendedImageSize();
        (_imgWidthHint.Text, _imgHeightHint.Text) = (w.ToString(), h.ToString());
    }

    // The chosen preset's (Custom: the last preset's) width and height, and the multiple a typed size rounds to. The primary PC resolves
    // its model from RecipeId; before its list loads, the local recipe stands in for the multiple (and in ReadImageSettings for the negative).
    private (int Multiple, int Width, int Height) RecommendedImageSize()
    {
        var multiple = _imgSharedMode && SharedPick is { } shared ? shared.Multiple : ImageChoice(_imgRecipe.SelectedIndex).Recipe.Multiple;
        var (w, h) = ImageGen.Size(PresetRatio(_imgAspect.SelectedIndex > 0 ? _imgAspect.SelectedIndex : _imgLastRatio), ImageGen.RecommendedLongSide, multiple);
        return (multiple, w, h);
    }

    private string PresetRatio(int index) => _imgFlipped ? ImageGen.Flip(ImageGen.Presets[index].Ratio) : ImageGen.Presets[index].Ratio;
    private string ImageRatio() => _imgAspect.SelectedIndex > 0 ? PresetRatio(_imgAspect.SelectedIndex) : ""; // "" = Custom
    private List<string> AspectLabels() => ImageGen.Presets.Select(p => ImageGen.PresetLabel(p, _imgFlipped)).ToList();

    // digits only (the boxes drop anything else) and far below int.MaxValue in practice; 0 = empty
    private static int TypedSize(ComboBox box) => int.TryParse(box.Text, out var v) ? v : 0;

    // A box is done (focus left, Enter, or a value picked): round it and, with a ratio chosen, make the other follow. A pair that
    // cannot keep the ratio (one side clamped at 4096) turns the preset to Custom. Clearing a linked box clears both: the recommended size.
    private void CommitImageSize(bool width)
    {
        if (_imgApplying || _imgAspect.SelectedIndex < 0)
            return;
        var (box, other) = width ? (_imgWidth, _imgHeight) : (_imgHeight, _imgWidth);
        var multiple = _imgUnlocked ? 1 : RecommendedImageSize().Multiple;
        var typed = TypedSize(box);
        var value = ImageGen.Pick(typed, 0, multiple);
        box.Text = typed > 0 ? value.ToString() : "";
        if (_imgUnlocked || ImageRatio() is not { Length: > 0 } ratio)
            return;
        other.Text = typed > 0 ? ImageGen.Linked(value, ratio, width, multiple).ToString() : "";
        if (typed > 0 && !ImageGen.Matches(TypedSize(_imgWidth), TypedSize(_imgHeight), ratio, multiple))
            _imgAspect.SelectedIndex = 0;
    }

    // The lock beside the size boxes: closed, width and height keep the aspect ratio and round to the model's multiple; open, they
    // are taken as typed (kept to 64-4096). Closing it again fits the typed sizes to the chosen ratio.
    private void ShowSizeLock()
    {
        _imgSizeLock.Content = _imgUnlocked ? "" : ""; // Unlock, Lock
        _imgSizeLock.ToolTip = _imgUnlocked ? "Unlocked: any width and height, as typed. Click to keep the aspect ratio"
            : "Locked: width and height keep the aspect ratio and round to the model's multiple. Click to type any size";
        System.Windows.Automation.AutomationProperties.SetName(_imgSizeLock, _imgUnlocked ? "Aspect ratio unlocked" : "Aspect ratio locked");
        if (!_imgUnlocked && !_imgApplying && _imgWidth is not null)
            ImageAspectChanged();
    }

    private void ImageAspectChanged()
    {
        if (_imgApplying || _imgAspect.SelectedIndex < 0)
            return;
        if (_imgAspect.SelectedIndex > 0) // a preset: typed sizes take its ratio (from the width first); Custom keeps them as they are
        {
            _imgLastRatio = _imgAspect.SelectedIndex;
            if (TypedSize(_imgWidth) > 0)
                CommitImageSize(true);
            else if (TypedSize(_imgHeight) > 0)
                CommitImageSize(false);
        }
        ShowImageSize();
    }

    // The ↑↓ button: width and height trade places, and the presets show their flipped ratios ("Photo (2:3)").
    private void SwapImageSize()
    {
        var index = _imgAspect.SelectedIndex;
        _imgFlipped = !_imgFlipped;
        _imgApplying = true;
        _imgAspect.ItemsSource = AspectLabels();
        _imgAspect.SelectedIndex = index;
        (_imgWidth.Text, _imgHeight.Text) = (_imgHeight.Text, _imgWidth.Text);
        _imgApplying = false;
        ShowImageSize();
    }

    private static ControlTemplate ParseTemplate(string xaml) => (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        xaml.Replace("<ControlTemplate ", "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" ", StringComparison.Ordinal));

    // MainWindow.xaml's ComboBox template has no text box, so IsEditable needs its own: the same look, with PART_EditableTextBox at the
    // left padding (the chevron's 28 px stay the drop-down button). Keep it in step with that template.
    private const string EditableComboXaml = """
        <ControlTemplate TargetType="ComboBox">
            <Grid>
                <Border x:Name="Field" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="{DynamicResource ControlRadius}" />
                <TextBlock Text="&#xE70D;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="10" Foreground="{DynamicResource Muted}"
                           HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,10,0" IsHitTestVisible="False" />
                <ToggleButton Focusable="False" ClickMode="Press" HorizontalAlignment="Right" Width="28"
                              IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                    <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent" /></ControlTemplate></ToggleButton.Template>
                </ToggleButton>
                <TextBox x:Name="PART_EditableTextBox" Margin="{TemplateBinding Padding}" VerticalAlignment="Center" MaxLength="5">
                    <TextBox.Template><ControlTemplate TargetType="TextBox"><ScrollViewer x:Name="PART_ContentHost" Focusable="False" VerticalScrollBarVisibility="Hidden" HorizontalScrollBarVisibility="Hidden" /></ControlTemplate></TextBox.Template>
                </TextBox>
                <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False">
                    <Border Background="{DynamicResource Card}" BorderBrush="{DynamicResource Border}" BorderThickness="1" CornerRadius="{DynamicResource SmallRadius}"
                            Padding="5" Margin="0,2,0,0" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
                        <ScrollViewer MaxHeight="{TemplateBinding MaxDropDownHeight}" VerticalScrollBarVisibility="Auto">
                            <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained" />
                        </ScrollViewer>
                    </Border>
                </Popup>
            </Grid>
            <ControlTemplate.Triggers>
                <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Field" Property="BorderBrush" Value="{DynamicResource Accent}" /></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5" /></Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>
        """;

    private const string RoundButtonXaml = """
        <ControlTemplate TargetType="Button">
            <Border x:Name="Round" Width="30" Height="30" CornerRadius="15" Background="{DynamicResource Card}" BorderBrush="{DynamicResource ControlBorder}" BorderThickness="1">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
            </Border>
            <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Round" Property="BorderBrush" Value="{DynamicResource Accent}" /></Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>
        """;

    private void RefreshImageRuntime()
    {
        string[] version;
        try { version = File.ReadAllLines(Path.Combine(ImageGen.RuntimeFolder, "version.txt")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { version = []; }
        var installed = File.Exists(ImageGen.ServerPath);
        _imgRuntimeText.Text = !installed ? "Image runtime (stable-diffusion.cpp) not installed; Generate downloads it."
            : version.Length >= 2 ? $"stable-diffusion.cpp {version[0]} ({version[1]})" : "stable-diffusion.cpp installed";
        _imgRuntimeButton.Content = installed ? "Update" : "Download image runtime";
        using var running = LauncherLogic.RecordedLlamaServer(_llamaStateFile);
        _imgVramHint.Visibility = running is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshImageRecipes()
    {
        var index = _imgRecipe.SelectedIndex;
        _imgApplying = true;
        string Gb(long b) => $"{b / (double)(1L << 30):0.0} GB";
        long Missing(ImageRecipe r, string local) => MissingImageFiles(r, local).Sum(f => f.Size);
        _imgRecipe.ItemsSource = ImageGen.Recipes.Select(r =>
            {
                var missing = Missing(r, "");
                return $"{r.Name}{(r.Note.Length > 0 ? " · " + r.Note : "")} — {(missing == 0 ? "Downloaded" : $"Download ({Gb(missing)})")}";
            })
            .Concat(_imgLocal.Select(m =>
            {
                var missing = Missing(m.Recipe, m.Path);
                return $"{Path.GetFileName(m.Path)} · {m.Recipe.Name} — {(missing == 0 ? "on this PC" : $"on this PC, needs {Gb(missing)} of parts")}";
            })).ToList();
        _imgRecipe.SelectedIndex = Math.Clamp(index, 0, _imgRecipe.Items.Count - 1);
        _imgFolderText.Text = $"{(_imgLocal.Count == 0 ? "No image models" : $"{_imgLocal.Count} image model{(_imgLocal.Count == 1 ? "" : "s")}")} in {ImageModelsFolder}";
        _imgFolderText.ToolTip = ImageModelsFolder;
        _imgApplying = false;
    }

    // ponytail: the newest 200 pictures only; page the gallery if people keep thousands.
    private void RefreshGallery()
    {
        _imgGallery.Children.Clear();
        var pictures = Directory.Exists(ImageGen.ImagesFolder)
            ? new DirectoryInfo(ImageGen.ImagesFolder).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Take(200).Select(f => f.FullName).ToList()
            : [];
        _imgGalleryHead.Text = pictures.Count == 0 ? "Nothing yet" : $"{pictures.Count}{(pictures.Count == 200 ? "+" : "")}";
        if (_imgResultPath is not null && !File.Exists(_imgResultPath))
            ShowOnStage(null);
        if (_imgResultPath is null && pictures.Count > 0)
            ShowOnStage(pictures[0]); // the newest, until one is picked
        foreach (var path in pictures)
        {
            if (LoadPicture(path, 208) is not { } bitmap)
                continue;
            var thumb = new Border
            {
                Width = 104, Height = 104, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(10), Tag = path, Cursor = Cursors.Hand,
                Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill }, ToolTip = Path.GetFileName(path),
            };
            MarkThumb(thumb);
            thumb.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                    ShowImage(path);
                else
                    ShowOnStage(path);
                e.Handled = true;
            };
            var menu = new ContextMenu();
            void Item(string header, Action click)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => click();
                menu.Items.Add(item);
            }
            var sidecar = Path.ChangeExtension(path, ".json");
            if (File.Exists(sidecar))
                Item("Use these settings", () =>
                {
                    try { ApplyImageSettings(JsonSerializer.Deserialize<ImageSettings>(File.ReadAllText(sidecar)) ?? new()); }
                    catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Toast("Could not read the settings", "#F7C873"); }
                });
            Item("Show in folder", () => Process.Start("explorer.exe", $"/select,\"{path}\""));
            Item("Copy image", () => { if (LoadPicture(path) is { } picture) Clipboard.SetImage(picture); });
            Item("Delete", () =>
            {
                if (SidebarAsk("Delete picture", $"Delete {Path.GetFileName(path)} for good? It is overwritten, not moved to the Recycle Bin, so it can't be brought back.", "Delete") is null)
                    return;
                ShredPictures([path]);
            });
            thumb.ContextMenu = menu;
            _imgGallery.Children.Add(thumb);
        }
    }

    // Overwrites and deletes pictures and their settings files (ImageGen.Shred); the stage lets go of a deleted one first.
    private void ShredPictures(IEnumerable<string> pictures)
    {
        var failed = 0;
        foreach (var picture in pictures)
        {
            if (string.Equals(picture, _imgResultPath, StringComparison.OrdinalIgnoreCase))
                ShowOnStage(null);
            foreach (var file in new[] { picture, Path.ChangeExtension(picture, ".json") }.Where(File.Exists))
                try { ImageGen.Shred(file); }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException) { failed++; Log($"Images: couldn't delete {file}: {x.Message}"); }
        }
        if (failed > 0)
            Toast($"{failed} file{(failed == 1 ? "" : "s")} couldn't be deleted (in use?)", "#FF667A");
        RefreshGallery();
    }

    // The stage's picture is outlined in the gallery.
    private void MarkThumb(Border thumb)
    {
        var shown = string.Equals(thumb.Tag as string, _imgResultPath, StringComparison.OrdinalIgnoreCase);
        thumb.BorderThickness = new Thickness(shown ? 2 : 1);
        thumb.SetResourceReference(Border.BorderBrushProperty, shown ? "Accent" : "Border");
    }

    private void ShowOnStage(string? path)
    {
        _imgResultPath = path;
        _imgResult.Source = path is null ? null : LoadPicture(path);
        _imgPlaceholder.Visibility = _imgResult.Source is null ? Visibility.Visible : Visibility.Collapsed;
        FitStage();
        foreach (var thumb in _imgGallery.Children.OfType<Border>())
            MarkThumb(thumb);
    }

    private void FitStage()
    {
        _imgScale.ScaleX = _imgScale.ScaleY = 1;
        _imgZoomText.Text = "100%";
        _imgStage.ScrollToHorizontalOffset(0);
        _imgStage.ScrollToVerticalOffset(0);
    }

    // Zoom by factor (fit .. 8x), keeping the picture's point under the cursor where it is (as the Image Parser does).
    private void ZoomStage(double factor, Point onImage, Point onStage)
    {
        var next = Math.Clamp(_imgScale.ScaleX * factor, 1, 8);
        if (Math.Abs(next - _imgScale.ScaleX) < 0.001)
            return;
        _imgScale.ScaleX = _imgScale.ScaleY = next;
        _imgZoomText.Text = $"{next * 100:0}%";
        _imgStage.UpdateLayout();
        var moved = _imgResult.TranslatePoint(onImage, _imgStage);
        _imgStage.ScrollToHorizontalOffset(_imgStage.HorizontalOffset + moved.X - onStage.X);
        _imgStage.ScrollToVerticalOffset(_imgStage.VerticalOffset + moved.Y - onStage.Y);
    }

    // Each status the page shows also goes here while a generation for another PC runs (its progress lines).
    private Action<string, double?>? _imgStatusTap;

    private void ImageStatus(string text, double? progress = null, bool indeterminate = false)
    {
        _imgStatusTap?.Invoke(text, indeterminate ? null : progress);
        _imgStatus.Text = text;
        _imgProgress.IsIndeterminate = indeterminate;
        if (progress is { } p)
            _imgProgress.Value = p;
    }

    // One download or generation at a time: Generate and the runtime button are off meanwhile, Cancel is on.
    private async Task RunImageJob(Func<CancellationToken, Task> job)
    {
        if (ImageBusy)
            return;
        using var cts = _imgCts = new CancellationTokenSource();
        _imgGenerate.IsEnabled = _imgRuntimeButton.IsEnabled = false;
        ShowImageSource(); // the CONNECTION pill off (ImageBusy)
        _imgCancel.Visibility = _imgProgress.Visibility = Visibility.Visible;
        try { await job(cts.Token); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { ImageStatus("Cancelled"); }
        catch (Exception e)
        {
            Log($"Image generation failed: {e.Message}");
            Toast(e.Message.Length > 120 ? e.Message[..120] + "…" : e.Message, "#FF667A");
            ImageStatus("Failed: " + e.Message.Split('\n')[0]);
        }
        finally
        {
            _imgCts = null;
            _imgGenerate.IsEnabled = _imgRuntimeButton.IsEnabled = true;
            _imgCancel.Visibility = _imgProgress.Visibility = Visibility.Collapsed;
            ShowImageSource(); // the pill back on
            RefreshImageRuntime();
            RefreshImageRecipes();
        }
    }

    // The latest stable-diffusion.cpp release, installed like llama-server (a failed install keeps the previous one).
    private async Task InstallImageRuntimeAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
        StopImageServer(); // its files are replaced
        ImageStatus("Looking up the latest stable-diffusion.cpp release…", indeterminate: true);
        var nvidia = await Task.Run(LauncherLogic.HasNvidiaGpu, ct);
        var assets = ImageGen.PickAssets(await http.GetStringAsync(ImageGen.ReleaseUrl, ct), nvidia);
        var zips = new List<string>();
        try
        {
            _imgProgress.IsIndeterminate = false;
            foreach (var url in assets.Urls)
            {
                var zip = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-sd-{Guid.NewGuid():N}.zip");
                zips.Add(zip);
                await DownloadAsync(http, url, zip, Path.GetFileName(url), ct, _imgStatus);
            }
            ImageStatus("Installing the image runtime…", indeterminate: true);
            await Task.Run(() => LauncherLogic.InstallLlamaServer(zips, ImageGen.RuntimeFolder, $"{assets.Tag}\n{assets.Flavour}", exe: "sd-server.exe"), ct);
            Log($"stable-diffusion.cpp {assets.Tag} ({assets.Flavour}) installed in {ImageGen.RuntimeFolder}");
            ImageStatus($"Image runtime {assets.Tag} installed", 1);
        }
        finally
        {
            foreach (var zip in zips)
                try { File.Delete(zip); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        }
    }

    // The recipe's files that are neither in the models folder nor downloaded yet.
    private List<ImageFile> MissingImageFiles(ImageRecipe recipe, string local)
    {
        var paths = ImageGen.Paths(recipe, local, _imgFolderFiles, ImageGen.ModelsRoot);
        return [.. recipe.Files.Where((f, i) => paths[i] == ImageGen.LocalPath(ImageGen.ModelsRoot, f) && !HubDownloader.IsDownloaded(ImageGen.ModelsRoot, HubLayout.Flat, f.Repo, f.Path))];
    }

    // The recipe's missing files from Hugging Face, sizes from the hub's tree listing.
    private async Task DownloadImageModelsAsync(ImageRecipe recipe, string local, CancellationToken ct)
    {
        if (local.Length > 0 && !File.Exists(local))
            throw new FileNotFoundException($"The model is gone: {local}");
        var missing = MissingImageFiles(recipe, local);
        if (missing.Count == 0)
            return;
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
        var sized = new List<(string Repo, HubDownloadFile File)>();
        foreach (var file in missing)
        {
            var dir = file.Path.Contains('/') ? "/" + file.Path[..file.Path.LastIndexOf('/')] : "";
            using var tree = JsonDocument.Parse(await http.GetStringAsync($"https://huggingface.co/api/models/{file.Repo}/tree/main{dir}", ct));
            var size = tree.RootElement.EnumerateArray().Where(e => e.GetProperty("path").GetString() == file.Path).Select(e => e.GetProperty("size").GetInt64()).FirstOrDefault();
            if (size <= 0)
                throw new InvalidOperationException($"{file.Repo} has no {file.Path}");
            sized.Add((file.Repo, new HubDownloadFile(file.Path, size)));
        }
        long total = sized.Sum(s => s.File.Size), before = 0;
        static string Gb(long b) => $"{b / (double)(1L << 30):0.0}";
        var downloader = new HubDownloader(http, ImageGen.ModelsRoot, HubLayout.Flat, HubDownloader.EnvToken());
        _imgProgress.IsIndeterminate = false;
        foreach (var group in sized.GroupBy(s => s.Repo))
        {
            var offset = before;
            var progress = new Progress<HubProgress>(p => ImageStatus($"Downloading model… {Gb(offset + p.DoneBytes)}/{Gb(total)} GB", (offset + p.DoneBytes) / (double)total));
            await downloader.DownloadAsync(group.Key, "main", group.Select(g => g.File).ToList(), progress, ct);
            before += group.Sum(g => g.File.Size);
        }
    }

    private async Task GenerateImagesAsync(CancellationToken ct)
    {
        var s = ReadImageSettings();
        if (s.Prompt.Length == 0)
        {
            Toast("Write a prompt first", "#F7C873");
            return;
        }
        if ((_imgRemoteMode && s.RemoteModel.Length == 0) || (_imgSharedMode && s.SharedModel.Length == 0))
        {
            Toast("Choose an image model first", "#F7C873");
            return;
        }
        if (s.RemoteModel.Length == 0 && s.SharedModel.Length == 0 && s.InitImage.Length > 0 && !File.Exists(s.InitImage))
            throw new FileNotFoundException($"The start image is gone: {s.InitImage}");
        _settings.ImageGen = s;
        _settings.Save();
        RefreshImageRuntime(); // the VRAM hint
        var run = JsonSerializer.Deserialize<ImageSettings>(JsonSerializer.Serialize(s))!;
        if (run.Seed < 0)
            run.Seed = Random.Shared.Next(); // chosen here so the sidecar records it
        var files = await PaintAsync(run, () =>
        {
            Toast("Not enough GPU memory: trying again with Low VRAM");
            _imgLowVram.IsChecked = s.LowVram = true; // Low VRAM stays ticked for next time
            _settings.Save();
        }, ct);
        ImageStatus(run.RemoteModel.Length > 0 ? "Done" : $"Done · seed {files[0].Seed}", 1);
    }

    // The generation itself, the page's and the chat's (AnswerImageAsync): run (its seed chosen) goes to the primary PC, OpenRouter or
    // sd-server here by its model, and the pictures land in the gallery (SaveImageFiles). Inside RunImageJob.
    private async Task<List<(string Written, string Final, long Seed)>> PaintAsync(ImageSettings run, Action onLowVram, CancellationToken ct)
    {
        List<(string Written, string Final, long Seed)> files;
        if (run.RemoteModel.Length > 0 || run.SharedModel.Length > 0) // OpenRouter and the primary PC need neither the runtime nor the model files here
        {
            Directory.CreateDirectory(ImageGen.ImagesFolder);
            files = ImageGen.OutputNames(ImageGen.ImagesFolder, DateTime.Now, run.Seed, run.Batch).Files;
            if (run.SharedModel.Length > 0)
            {
                run.InitImage = ""; // the primary PC takes no start image
                await GenerateSharedAsync(run, files, ct);
            }
            else
            {
                // what OpenRouter never read is not recorded beside the pictures (the saved settings above keep it for the way back)
                (run.Negative, run.Sampler, run.Scheduler, run.InitImage, run.Steps, run.Cfg, run.Strength) = ("", "", "", "", 0, 0, 0);
                await GenerateRemoteImageAsync(run, files, ct);
            }
        }
        else
            files = await GenerateLocalAsync(run, ImageGen.ImagesFolder, onLowVram, ct);
        SaveImageFiles(run, files);
        return files;
    }

    // The chat model's generate_image (AppSettings.UseImageGen; the request's image_gen): painted with the chat's model and size
    // (AppSettings.ChatImage) by the page's own generation, one job at a time. The picture joins the reply's from the gallery; the
    // reply is one line for the model's memory and a copy at most 1024 px long. Every outcome answers (busy, cancelled, failed, the
    // turn gone: "ERROR: …"), as the engine waits without a timeout. The turn's Stop (its process ends) cancels the generation.
    private async Task AnswerImageAsync(JsonElement ev, Process? asker)
    {
        string result;
        string? image = null;
        try { (result, image) = await PaintForChatAsync(ev, asker); }
        catch (Exception x) // whatever broke, the engine must hear back
        {
            Log($"Image Gen failed: {x.Message}");
            result = "ERROR: " + x.Message;
        }
        ToTurn(asker, JsonSerializer.Serialize(new { id = Str(ev, "id"), result, image }));
    }

    private async Task<(string Result, string? Image)> PaintForChatAsync(JsonElement ev, Process? asker)
    {
        ScanImageFolder();
        if (ImagesView.Children.Count == 0)
            BuildImagesPage();
        if (ImageBusy)
            return ("ERROR: the image generator is busy", null);
        if (ImageGen.ChatModel(_settings.ChatImage) is not var (recipe, name))
            return ("ERROR: no image model is chosen for Image Gen", null);
        long? seed = ev.TryGetProperty("seed", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : null;
        var negative = ev.TryGetProperty("negative", out var neg) && neg.ValueKind == JsonValueKind.String ? neg.GetString() : null;
        var run = ImageGen.ChatRun(_settings.ChatImage, recipe, Str(ev, "prompt"), negative, seed, _settings.ImageGen.LowVram);
        if (run.Prompt.Length == 0)
            return ("ERROR: the prompt is empty", null);
        var local = run.SharedModel.Length == 0 && run.RemoteModel.Length == 0;
        string? file = null;
        await RunImageJob(async ct =>
        {
            using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Forget(CancelWhenGone());
            async Task CancelWhenGone() // the turn's Stop ends its process: the picture is not wanted any more
            {
                try
                {
                    if (asker is not null)
                        await asker.WaitForExitAsync(watch.Token);
                }
                catch (OperationCanceledException) { return; } // the job ended first
                catch (Exception x) when (x is InvalidOperationException or ObjectDisposedException) { } // disposed: gone
                if (!watch.IsCancellationRequested)
                    CancelImageGen();
            }
            if (asker == _chatProcess)
                _imgStatusTap = (text, _) => SetChatStatus($"Making a picture… {text}");
            try
            {
                ImageStatus("Generating for the chat…", indeterminate: true);
                var files = await PaintAsync(run, () => Toast("Not enough GPU memory: trying again with Low VRAM"), ct);
                file = files.Select(f => f.Final).FirstOrDefault(File.Exists);
                ImageStatus(run.Seed >= 0 ? $"Done · seed {run.Seed}" : "Done", 1);
            }
            finally
            {
                watch.Cancel();
                _imgStatusTap = null;
            }
        });
        if (local && !_settings.ImageKeepBoth && !ImageBusy)
            StopImageServer(); // the GPU back for the chat model, which the engine loads again
        if (file is null) // RunImageJob showed the failure: "Failed: …" or "Cancelled"
            return ("ERROR: " + (_imgStatus.Text.StartsWith("Failed: ") ? "the picture failed: " + _imgStatus.Text[8..] : "the picture was cancelled"), null);
        if (run.SharedModel.Length > 0 && !_settings.ImageKeepBoth)
            try // before the reply: the engine's next call may load a chat model there again
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                await ImageShare.UnloadAsync(http, ModelShare.ImagesUrl(_settings.ShareHost), Environment.GetEnvironmentVariable(ModelShare.KeyEnv) ?? "", CancellationToken.None);
            }
            catch (Exception x) when (x is InvalidOperationException or HttpRequestException or TaskCanceledException) { Log($"Image Gen: the primary PC did not unload: {x.Message}"); }
        NoteTurnPicture(file);
        var (copy, width, height) = ChatCopy(file);
        return (ImageGen.ChatResult(name, run, width, height), copy);
    }

    // The picture as a data URL for a model that can see (a JPEG at most 1024 px long) and its own size.
    private static (string? DataUrl, int Width, int Height) ChatCopy(string file)
    {
        var frame = BitmapFrame.Create(new MemoryStream(File.ReadAllBytes(file)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var scale = Math.Min(1, 1024.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return ("data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray()), frame.PixelWidth, frame.PixelHeight);
    }

    // The pictures written by a generation, renamed to their final names, each with its settings beside it as <name>.json;
    // the gallery shows them and the first goes on the stage.
    private void SaveImageFiles(ImageSettings run, List<(string Written, string Final, long Seed)> files)
    {
        string? first = null;
        foreach (var (written, final, seed) in files.Where(f => File.Exists(f.Written)))
        {
            if (written != final)
                File.Move(written, final, overwrite: true);
            run.Seed = run.RemoteModel.Length > 0 ? -1 : seed; // OpenRouter was not sent one: -1, random
            File.WriteAllText(Path.ChangeExtension(final, ".json"), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
            first ??= final;
        }
        RefreshGallery();
        if (first is not null)
            ShowOnStage(first);
    }

    // sd-server on this PC (the Images page and the primary PC's share server): installs the runtime and downloads the model's
    // missing files first, writes the pictures into folder (run.Seed chosen), progress in the status line. Out of GPU memory
    // without Low VRAM: once more with it (onLowVram is told). Returns the pictures' names (ImageGen.OutputNames), not renamed.
    private async Task<List<(string Written, string Final, long Seed)>> GenerateLocalAsync(ImageSettings run, string folder, Action? onLowVram, CancellationToken ct)
    {
        var recipe = ImageGen.Recipe(run.RecipeId);
        if (!File.Exists(ImageGen.ServerPath))
            await InstallImageRuntimeAsync(ct);
        await DownloadImageModelsAsync(recipe, run.LocalModel, ct);
        Directory.CreateDirectory(folder);
        var (_, files) = ImageGen.OutputNames(folder, DateTime.Now, run.Seed, run.Batch);
        var (context, request) = ImageGen.ServerArgs(recipe, run, ImageGen.Paths(recipe, run.LocalModel, _imgFolderFiles, ImageGen.ModelsRoot));
        if (run.InitImage.Length > 0)
            request["init_image"] = Convert.ToBase64String(await File.ReadAllBytesAsync(run.InitImage, ct));
        var mark = SdMark(); // only this generation's log lines are read for its error
        JsonNode? job = null;
        string? error = null, why = null;
        Exception? broken = null;
        try
        {
            var fresh = await StartImageServerAsync(context, ct);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_sdPort}"), Timeout = TimeSpan.FromSeconds(30) };
            // not cancelled midway: a job it took would run on unseen; a Cancel meanwhile cancels it by id below
            using (var posted = await http.PostAsync("/sdcpp/v1/img_gen", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"), CancellationToken.None))
                job = JsonNode.Parse(await posted.Content.ReadAsStringAsync(CancellationToken.None));
            var id = (string?)job?["id"] ?? throw new InvalidOperationException("sd-server took no job: " + job?.ToJsonString());
            if (!fresh)
                ImageStatus("Encoding prompt…", indeterminate: true); // not the last picture's "Done"
            try
            {
                ct.ThrowIfCancellationRequested();
                while ((string?)job?["status"] is "queued" or "generating")
                {
                    await Task.Delay(500, ct);
                    job = JsonNode.Parse(await http.GetStringAsync($"/sdcpp/v1/jobs/{id}", ct));
                    if (ImageGen.JobProgress(job, fresh) is not { } p)
                        continue;
                    if (p.Loading)
                        ImageStatus("Loading…", indeterminate: true);
                    else
                        ImageStatus($"Step {p.Step}/{p.Steps}{(p.Speed.Length > 0 ? " · " + p.Speed : "")}", p.Step / (double)Math.Max(1, p.Steps));
                    fresh &= p.Loading;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { (await http.PostAsync($"/sdcpp/v1/jobs/{id}/cancel", null, CancellationToken.None)).Dispose(); } // the server and its models stay
                catch (Exception x) when (x is HttpRequestException or TaskCanceledException) { }
                throw;
            }
            if (job?["result"]?["images"] is JsonArray images && (string?)job["status"] == "completed")
            {
                foreach (var image in images)
                    if ((int?)image?["index"] is { } i && i < files.Count && (string?)image["b64_json"] is { } png)
                        await File.WriteAllBytesAsync(files[i].Written, Convert.FromBase64String(png), CancellationToken.None);
                if (File.Exists(files[0].Written))
                    return files;
            }
            error = (string?)job?["error"]?["message"];
        }
        // it crashed, is dying, stopped answering or answered nonsense (not a Cancel): it goes, once its last lines are in
        catch (Exception x) when (x is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            (broken, why) = (x, x is OperationCanceledException ? "it stopped answering" : x.Message);
            for (var i = 0; i < 10 && _sdServer is { HasExited: false }; i++)
                await Task.Delay(200, CancellationToken.None);
            await Task.Delay(200, CancellationToken.None);
        }
        var tail = SdTail(mark);
        Log($"sd-server failed: {error ?? why}\n{tail}");
        var all = $"{error}\n{tail}";
        var outOfMemory = all.Contains("enough memory", StringComparison.OrdinalIgnoreCase) || all.Contains("out of memory", StringComparison.OrdinalIgnoreCase);
        if (broken is not null || _sdServer is not { HasExited: false })
            StopImageServer();
        if (outOfMemory && !run.LowVram) // once more with the weights in RAM (another context: a new server)
        {
            run.LowVram = true;
            onLowVram?.Invoke();
            return await GenerateLocalAsync(run, folder, onLowVram, ct);
        }
        throw new InvalidOperationException(outOfMemory
            ? "Not enough GPU memory for this model even with Low VRAM: unload the chat models, pick a smaller size, or a smaller model"
            : $"sd-server failed: {error ?? FirstError(tail) ?? why ?? "no picture"} (the log has its output)");
    }

    private static string? FirstError(string log) => log.Split('\n').FirstOrDefault(t => t.StartsWith("[E]"))?[3..].Split(" --- ")[0].Trim();

    // sd-server, kept running with its models loaded between generations (Unload models, a runtime update and closing the app
    // stop it): the one running is reused while its context (ImageGen.ServerArgs) is the same, else replaced. Its last log lines
    // are kept for the error text (_sdLines counts every line: a generation reads those after its SdMark). True when it was just
    // started (its first generation reads the weights in: "Loading…"). Exiting while it loads: HttpRequestException, as a crash later.
    // ponytail: a crash of the app leaves it running (no Job object); add one if orphans show up.
    private Process? _sdServer;
    private List<string> _sdContext = [];
    private int _sdPort;
    private readonly Queue<string> _sdTail = new();
    private long _sdLines;

    private long SdMark()
    {
        lock (_sdTail)
            return _sdLines;
    }

    private string SdTail(long since)
    {
        lock (_sdTail)
            return ImageGen.TailSince(_sdTail, _sdLines, since);
    }

    private async Task<bool> StartImageServerAsync(List<string> context, CancellationToken ct)
    {
        if (_sdServer is { HasExited: false } && _sdContext.SequenceEqual(context))
            return false;
        StopImageServer();
        ImageStatus("Loading…", indeterminate: true);
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            _sdPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        var info = new ProcessStartInfo(ImageGen.ServerPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = ImageGen.RuntimeFolder,
        };
        foreach (var arg in (string[])["-l", "127.0.0.1", "--listen-port", _sdPort.ToString(), .. context])
            info.ArgumentList.Add(arg);
        lock (_sdTail)
            _sdTail.Clear();
        void Keep(object _, DataReceivedEventArgs e)
        {
            if (e.Data is not { Length: > 0 } line)
                return;
            lock (_sdTail)
            {
                _sdTail.Enqueue(line);
                _sdLines++;
                if (_sdTail.Count > 40)
                    _sdTail.Dequeue();
            }
        }
        var process = _sdServer = Process.Start(info) ?? throw new InvalidOperationException("sd-server did not start");
        _sdContext = context;
        process.OutputDataReceived += Keep;
        process.ErrorDataReceived += Keep;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Log($"sd-server started (pid {process.Id}, port {_sdPort})");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var loading = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                if (process.HasExited)
                {
                    await Task.Delay(200, CancellationToken.None); // its last lines
                    var code = process.ExitCode;
                    StopImageServer();
                    throw new HttpRequestException($"it stopped while loading the model (exit code {code})");
                }
                if (loading.Elapsed > TimeSpan.FromMinutes(10)) // ponytail: fixed limit; a huge model on a very slow disk may need more
                {
                    StopImageServer();
                    Log($"sd-server still loading after 10 minutes:\n{SdTail(0)}");
                    throw new InvalidOperationException("sd-server was still loading the model after 10 minutes (the log has its output)");
                }
                try
                {
                    using var up = await http.GetAsync($"http://127.0.0.1:{_sdPort}/sdcpp/v1/capabilities", ct);
                    if (up.IsSuccessStatusCode)
                        return true;
                    StopImageServer(); // it listens once its model is loaded, so this is an answer: an older sd-server without /sdcpp/v1
                    Log($"sd-server answered {(int)up.StatusCode} to /sdcpp/v1/capabilities");
                    throw new InvalidOperationException("The image runtime is too old for this version of the app: press Update on the Images page");
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
                await Task.Delay(300, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StopImageServer(); // cancelled while loading: nothing loaded to keep
            throw;
        }
    }

    // Idempotent; stops only the sd-server this app started (its own process).
    private void StopImageServer()
    {
        if (_sdServer is not { } process)
            return;
        _sdServer = null;
        _sdContext = [];
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10000); // its files stay locked until it exits
        }
        catch (Exception x) when (x is InvalidOperationException or System.ComponentModel.Win32Exception) { } // already gone
        process.Dispose();
    }

    // POST /images/generate on the primary PC (no progress: an indeterminate bar); each returned PNG goes to the next of files.
    private async Task GenerateSharedAsync(ImageSettings run, List<(string Written, string Final, long Seed)> files, CancellationToken ct)
    {
        if (_settings.ShareHost.Length == 0)
            throw new InvalidOperationException("Not connected to a primary PC: paste its connection code under Settings > Models");
        ImageStatus($"Generating on {_settings.ShareHost}…", indeterminate: true);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var shown = new Progress<ImageShare.ProgressLine>(p => ImageStatus($"Primary PC: {p.Status} · {p.Progress * 100:0}%", p.Progress)); // posts to the UI thread
        var pictures = await ImageShare.GenerateAsync(http, ModelShare.ImagesUrl(_settings.ShareHost), Environment.GetEnvironmentVariable(ModelShare.KeyEnv) ?? "", run, ct, shown);
        if (pictures.Count == 0)
            throw new InvalidOperationException("The primary PC returned no pictures");
        for (var i = 0; i < Math.Min(pictures.Count, files.Count); i++)
            await File.WriteAllBytesAsync(files[i].Written, Convert.FromBase64String(pictures[i].Png), ct);
    }

    // POST /images on OpenRouter with the chosen model (and the providers pinned in order, no fallbacks, as the chat side does);
    // each returned picture is written to the next of files, so fewer pictures than asked for is fine.
    private async Task GenerateRemoteImageAsync(ImageSettings s, List<(string Written, string Final, long Seed)> files, CancellationToken ct)
    {
        var keyEnv = ImageKeyEnv;
        var key = Environment.GetEnvironmentVariable(keyEnv);
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException($"Missing API key for OpenRouter: set {keyEnv}");
        ImageStatus("Generating…", indeterminate: true);
        var body = new JsonObject { ["model"] = s.RemoteModel, ["prompt"] = s.Prompt, ["n"] = s.Batch, ["aspect_ratio"] = ImageGen.RemoteAspect(s.Width, s.Height), ["response_format"] = "b64_json" };
        var order = s.RemoteProvider.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (order.Length > 0)
            body["provider"] = new JsonObject { ["order"] = new JsonArray(order.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()), ["allow_fallbacks"] = false };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/images") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", key);
        using var response = await _remoteHttp.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try { message = JsonNode.Parse(text)?["error"]?["message"]?.ToString(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { } // not {"error":{"message":…}}: the status code says it
            throw new InvalidOperationException(string.IsNullOrEmpty(message) ? $"OpenRouter returned {(int)response.StatusCode}" : message);
        }
        // Decode and check every picture before writing any, so one bad item fails the whole call with nothing left on disk.
        var pictures = new List<byte[]>();
        try
        {
            using var reply = JsonDocument.Parse(text);
            if (!reply.RootElement.TryGetProperty("data", out var list))
                throw new InvalidOperationException("no data");
            foreach (var item in list.EnumerateArray().Take(files.Count))
            {
                var bytes = item.TryGetProperty("b64_json", out var b64) && b64.GetString() is { Length: > 0 } data ? Convert.FromBase64String(data)
                    : item.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } link ? await _remoteHttp.GetByteArrayAsync(link, ct)
                    : null;
                if (bytes is null)
                    continue;
                using (var stream = new MemoryStream(bytes))
                    System.Windows.Media.Imaging.BitmapFrame.Create(stream); // throws when the bytes are not a picture
                pictures.Add(bytes);
            }
        }
        catch (Exception e) when (e is not (OperationCanceledException or HttpRequestException))
        {
            throw new InvalidOperationException("OpenRouter returned an unreadable reply", e);
        }
        for (var i = 0; i < pictures.Count; i++)
            await File.WriteAllBytesAsync(files[i].Written, pictures[i], ct);
        if (pictures.Count == 0)
            throw new InvalidOperationException("OpenRouter returned no pictures");
    }
}

// Collapsed while the bound value is null (the stage's tools have no picture to act on).
internal sealed class NullToCollapsed : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
