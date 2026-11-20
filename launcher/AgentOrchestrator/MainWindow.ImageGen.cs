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
using Microsoft.Win32;

namespace AgentOrchestrator;

// The Images page (the sidebar's Images): local image generation with stable-diffusion.cpp's sd-cli (ImageGen.cs).
// Settings on the left, the current picture and the gallery (<data>\images\*.png, each with its settings as <name>.json)
// on the right. Generate downloads the runtime and the recipe's model files first when they are missing.
public partial class MainWindow
{
    private CancellationTokenSource? _imgCts; // the download or generation in progress; Cancel and OnClosing cancel it
    private Process? _imgProcess;
    private ComboBox _imgRecipe = null!, _imgAspect = null!, _imgWidth = null!, _imgHeight = null!,_imgBatch = null!, _imgSampler = null!, _imgScheduler = null!;
    private TextBox _imgPrompt = null!, _imgNegative = null!, _imgSeed = null!;
    private bool _imgFlipped; // the swap button: the presets show and give their ratios flipped (portrait)
    private int _imgLastRatio = 1; // the preset whose recommended size Custom keeps showing
    private FrameworkElement _imgNegativeRow = null!;
    private Slider _imgSteps = null!, _imgCfg = null!, _imgStrength = null!;
    private CheckBox _imgLowVram = null!;
    private string _imgInit = "";
    private TextBlock _imgWidthHint = null!, _imgHeightHint = null!,_imgRuntimeText = null!, _imgStatus = null!, _imgVramHint = null!, _imgInitText = null!;
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
    private Button _imgUseShared = null!;
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
        _imgCts?.Cancel();
        try { if (_imgProcess is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private bool ImageBusy => _imgCts is not null;

    private void BuildImagesPage()
    {
        TextBlock Muted(string text, double size = 12)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return t;
        }
        var left = new StackPanel { Margin = new Thickness(24, 24, 16, 24) };
        void Label(string text) => left.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 14, 0, 4) });
        Slider NewSlider(double min, double max, double step, string format, StackPanel into)
        {
            var value = new TextBlock { Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, _) => value.Text = slider.Value.ToString(format);
            DockPanel.SetDock(value, Dock.Right);
            into.Children.Add(new DockPanel { Children = { value, slider } });
            return slider;
        }

        left.Children.Add(new TextBlock { Text = "Images", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 22, FontWeight = FontWeights.SemiBold });
        _imgRuntimeText = Muted("", 11.5);
        _imgRuntimeText.Margin = new Thickness(0, 6, 0, 4);
        _imgRuntimeButton = new Button { Tag = "ImagesRuntime", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left };
        _imgRuntimeButton.Click += async (_, _) => await RunImageJob(InstallImageRuntimeAsync);
        _imgVramHint = Muted("Chat models are loaded and using VRAM; image generation may be slower or run out of memory.", 11.5);
        _imgVramHint.Margin = new Thickness(0, 6, 0, 0);
        var runtime = new StackPanel { Children = { _imgRuntimeText, _imgRuntimeButton, _imgVramHint } }; // one panel: hidden as a whole in OpenRouter mode
        left.Children.Add(runtime);
        _imgLocalOnly.Add(runtime);

        Label("Model");
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
        // The local model (the picker and its folder) or an OpenRouter one: the two panels swap; the local pick is never touched on the way.
        Button SourceButton(string text, string tag, Action click)
        {
            var b = new Button { Content = text, Tag = tag, Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            b.Click += (_, _) => click();
            return b;
        }
        _imgLocalPanel = new StackPanel { Children = { _imgRecipe } };
        _imgRemoteModelButton = SourceButton("", "ImagesRemoteModelButton", ChooseImageModel);
        _imgRemoteProvidersButton = SourceButton("", "ImagesRemoteProvidersButton", ChooseImageProviders);
        _imgRemotePanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Children =
            {
                Muted($"Runs on OpenRouter with the key in {ImageKeyEnv}."), _imgRemoteModelButton, _imgRemoteProvidersButton,
                SourceButton("Switch to Local", "ImagesSwitchToLocal", () => { _imgRemoteMode = false; ShowImageSource(); }),
            },
        };
        _imgSharedModel = new ComboBox { Tag = "ImagesSharedModel" };
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
        _imgSharedNote = Muted("", 11.5);
        _imgSharedNote.Margin = new Thickness(0, 4, 0, 0);
        _imgSharedPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Children =
            {
                Muted("Runs on the primary PC this PC is connected to (Settings > Models)."), _imgSharedModel, _imgSharedNote,
                SourceButton("Switch to Local", "ImagesSharedToLocal", () => { _imgSharedMode = false; ShowImageSource(); }),
            },
        };
        left.Children.Add(_imgLocalPanel);
        left.Children.Add(_imgRemotePanel);
        left.Children.Add(_imgSharedPanel);
        _imgFolderText = Muted("", 11.5);
        _imgFolderText.Tag = "ImagesFolder";
        _imgFolderText.VerticalAlignment = VerticalAlignment.Center;
        var chooseFolder = new Button { Content = "Choose folder…", Tag = "ImagesChooseFolder", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "A folder with image models you already have (searched with its subfolders); only image models are listed" };
        chooseFolder.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Folder with image models", InitialDirectory = Directory.Exists(ImageModelsFolder) ? ImageModelsFolder : "" };
            if (dialog.ShowDialog(this) != true)
                return;
            _settings.ImageModelsFolder = dialog.FolderName;
            _settings.Save();
            ScanImageFolder();
            RefreshImageRecipes();
            Toast(_imgLocal.Count == 0 ? "No image models in that folder" : $"{_imgLocal.Count} image model{(_imgLocal.Count == 1 ? "" : "s")} found");
        };
        var folderRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0), Children = { chooseFolder, _imgFolderText } };
        DockPanel.SetDock(chooseFolder, Dock.Left);
        _imgLocalPanel.Children.Add(folderRow);
        _imgLocalPanel.Children.Add(SourceButton("Use OpenRouter…", "ImagesUseRemoteApi", () => { _imgRemoteMode = true; ShowImageSource(); }));
        _imgUseShared = SourceButton("Use the primary PC…", "ImagesUseShared", () => { _imgSharedMode = true; ShowImageSource(); LoadSharedModels(); });
        _imgLocalPanel.Children.Add(_imgUseShared);

        Label("Prompt");
        _imgPrompt = new TextBox { Tag = "ImagesPrompt", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        left.Children.Add(AttachPromptFade(_imgPrompt, 150, "ImagesPromptToggle"));
        _imgNegative = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _imgNegativeRow = new StackPanel
        {
            Tag = "ImagesNegative",
            Children = { new TextBlock { Text = "Negative prompt", FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 14, 0, 4) }, AttachPromptFade(_imgNegative, 90, "ImagesNegativeToggle") },
        };
        left.Children.Add(_imgNegativeRow);

        Label("Aspect ratio");
        _imgAspect = new ComboBox { Tag = "ImagesAspect", Width = 200, Margin = new Thickness(0, 0, 8, 0) };
        _imgAspect.SelectionChanged += (_, _) => ImageAspectChanged();
        var swap = new Button { Content = "↑↓", Tag = "ImagesSwapSize", ToolTip = "Swap width and height (portrait or landscape)", Template = ParseTemplate(RoundButtonXaml), Cursor = Cursors.Hand };
        swap.Click += (_, _) => SwapImageSize();
        left.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _imgAspect, swap } });
        Label("Resolution");
        var resolutionLabel = left.Children[left.Children.Count - 1];
        // Width and height: editable lists. Empty shows the recommended value greyed where typed text starts (the caret sits at its left
        // edge); a typed or picked value is rounded to the model's multiple when the box loses focus, and with a ratio chosen the other follows.
        var editable = ParseTemplate(EditableComboXaml);
        StackPanel SizeBox(string tag, bool width, out ComboBox box, out TextBlock hint)
        {
            var b = box = new ComboBox { Tag = tag, Width = 100, IsEditable = true, IsTextSearchEnabled = false, Template = editable, ItemsSource = ImageGen.SizeChoices,
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
            return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, width ? 12 : 0, 0), Children = { icon, new Grid { Children = { b, h } } } };
        }
        var sizeBoxes = new StackPanel { Orientation = Orientation.Horizontal,
            Children = { SizeBox("ImagesWidth", true, out _imgWidth, out _imgWidthHint), SizeBox("ImagesHeight", false, out _imgHeight, out _imgHeightHint) } };
        left.Children.Add(sizeBoxes);
        _imgLocalOnly.Add(resolutionLabel);
        _imgLocalOnly.Add(sizeBoxes); // OpenRouter is sent the nearest aspect ratio only, not this size

        var localFrom = left.Children.Count;
        Label("Steps");
        _imgSteps = NewSlider(1, 100, 1, "0", left);
        Label("Guidance");
        _imgCfg = NewSlider(0, 20, 0.5, "0.0", left);
        Label("Seed");
        _imgSeed = new TextBox { ToolTip = "Empty: a random seed (recorded with the picture)" };
        left.Children.Add(_imgSeed);
        _imgLocalOnly.AddRange(left.Children.Cast<UIElement>().Skip(localFrom)); // Steps, Guidance, Seed with their labels
        Label("Images");
        _imgBatch = new ComboBox { ItemsSource = new[] { 1, 2, 3, 4 }, Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        left.Children.Add(_imgBatch);

        var advancedHost = new StackPanel(); // the toggle and Advanced (Sampler, Low VRAM, start image): hidden as one in OpenRouter mode
        var advanced = new StackPanel { Visibility = Visibility.Collapsed };
        var toggle = new Button { Content = "▸ Advanced", Style = (Style)FindResource("BareButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        toggle.SetResourceReference(ForegroundProperty, "TextPrimary"); // BareButton's own is dark on the dark theme
        toggle.Click += (_, _) =>
        {
            var show = advanced.Visibility != Visibility.Visible;
            advanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = (show ? "▾" : "▸") + " Advanced";
        };
        advancedHost.Children.Add(toggle);
        advancedHost.Children.Add(advanced);
        left.Children.Add(advancedHost);
        _imgLocalOnly.Add(advancedHost);
        advanced.Children.Add(new TextBlock { Text = "Sampler", FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
        _imgSampler = new ComboBox { ItemsSource = ImageGen.Samplers.Prepend("Default").ToList() };
        advanced.Children.Add(_imgSampler);
        advanced.Children.Add(new TextBlock { Text = "Scheduler", FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
        _imgScheduler = new ComboBox { ItemsSource = ImageGen.Schedulers.Prepend("Default").ToList(), Tag = "ImagesScheduler",
            ToolTip = "How the noise level steps down over the steps; Default is the model's own" };
        advanced.Children.Add(_imgScheduler);
        _imgLowVram = new CheckBox { Content = "Low VRAM: one model on the GPU at a time", ToolTip = "Runs the text encoder, then swaps in the image model, then the decoder (weights wait in RAM). Slower to load; needed when the models together are bigger than the GPU", Margin = new Thickness(0, 12, 0, 0) };
        _imgLowVram.SetResourceReference(ForegroundProperty, "TextPrimary");
        advanced.Children.Add(_imgLowVram);
        advanced.Children.Add(new TextBlock { Text = "Start from image", FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 14, 0, 4) });
        _imgInitText = Muted("None", 11.5);
        var pick = new Button { Content = "Choose…", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var clear = new Button { Content = "Clear", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        pick.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
            if (dialog.ShowDialog(this) == true)
                SetInitImage(dialog.FileName);
        };
        clear.Click += (_, _) => SetInitImage("");
        _imgInitText.VerticalAlignment = VerticalAlignment.Center;
        advanced.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { pick, clear, _imgInitText } });
        advanced.Children.Add(Muted("Strength (how far from the image)", 11.5));
        _imgStrength = NewSlider(0, 1, 0.05, "0.00", advanced);

        _imgGenerate = new Button { Content = "Generate", Tag = "ImagesGenerate", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 20, 0, 0) };
        _imgGenerate.Click += async (_, _) => await RunImageJob(GenerateImagesAsync);
        left.Children.Add(_imgGenerate);

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

        _imgProgress = new ProgressBar { Height = 4, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        _imgStatus = Muted("", 12);
        _imgStatus.Tag = "ImagesStatus";
        _imgStatus.VerticalAlignment = VerticalAlignment.Center;
        _imgCancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 3, 12, 3), Visibility = Visibility.Collapsed, Margin = new Thickness(10, 0, 0, 0) };
        _imgCancel.Click += (_, _) => CancelImageGen();
        DockPanel.SetDock(_imgCancel, Dock.Right);
        var statusRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0), Children = { _imgCancel, _imgStatus } };
        _imgGallery = new StackPanel { Orientation = Orientation.Horizontal, Tag = "ImagesGallery" };
        _imgGalleryHead = Muted("GALLERY", 11);
        _imgGalleryHead.FontWeight = FontWeights.SemiBold;
        _imgGalleryHead.Margin = new Thickness(0, 14, 0, 6);
        // one row of thumbnails, always in view; the wheel scrolls it sideways
        var galleryScroll = new ScrollViewer
        {
            Content = _imgGallery, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Height = 128,
        };
        galleryScroll.PreviewMouseWheel += (_, e) =>
        {
            galleryScroll.ScrollToHorizontalOffset(galleryScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        var right = new Grid { Margin = new Thickness(16, 24, 24, 20) };
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var (element, row) in new (UIElement, int)[] { (stage, 0), (_imgProgress, 1), (statusRow, 2), (_imgGalleryHead, 3), (galleryScroll, 4) })
        {
            Grid.SetRow(element, row);
            right.Children.Add(element);
        }

        var page = new Grid { Tag = "ImagesPage" };
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        page.ColumnDefinitions.Add(new ColumnDefinition());
        var leftScroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(right, 1);
        page.Children.Add(leftScroll);
        page.Children.Add(right);
        ImagesView.Children.Add(page);
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
        if (ImageRatio() is { Length: > 0 } ratio && TypedSize(_imgWidth) is > 0 and var w && TypedSize(_imgHeight) is > 0 and var h &&
            !ImageGen.Matches(w, h, ratio, RecommendedImageSize().Multiple))
            _imgAspect.SelectedIndex = 0; // a saved pair that is not its ratio: Custom
    }

    private ImageSettings ReadImageSettings()
    {
        var (recipe, local) = ImageChoice(_imgRecipe.SelectedIndex);
        var negative = _imgSharedMode && SharedPick is { } shared ? shared.Negative : recipe.Negative;
        var (multiple, rw, rh) = RecommendedImageSize();
        var (cw, ch) = (TypedSize(_imgWidth), TypedSize(_imgHeight));
        int w = ImageGen.Pick(cw, rw, multiple), h = ImageGen.Pick(ch, rh, multiple);
        var custom = _imgAspect.SelectedIndex == 0; // saves both sizes: Custom's recommended one is not restored
        return new()
        {
            RecipeId = _imgSharedMode ? _imgSharedId : recipe.Id, LocalModel = _imgSharedMode ? "" : local, SharedModel = _imgSharedMode ? _imgSharedId : "",
            Prompt = _imgPrompt.Text.Trim(), Negative = negative ? _imgNegative.Text.Trim() : "", Aspect = custom ? "Custom" : ImageRatio(),
            Width = w, Height = h, CustomWidth = custom || cw > 0 ? w : 0, CustomHeight = custom || ch > 0 ? h : 0, Steps = (int)_imgSteps.Value, Cfg = _imgCfg.Value, Seed = ReadSeed(), Batch = _imgBatch.SelectedIndex + 1,
            Sampler = _imgSampler.SelectedIndex > 0 ? ImageGen.Samplers[_imgSampler.SelectedIndex - 1] : "", LowVram = _imgLowVram.IsChecked == true,
            Scheduler = _imgScheduler.SelectedIndex > 0 ? ImageGen.Schedulers[_imgScheduler.SelectedIndex - 1] : "",
            Strength = Math.Round(_imgStrength.Value, 2), InitImage = _imgInit,
            RemoteModel = _imgRemoteMode ? _imgRemoteModel.Id.Trim() : "", RemoteProvider = _imgRemoteMode ? string.Join(", ", _imgRemoteProviders) : "",
        };
    }

    // A prompt box that stops at collapsedMax: when its text is taller, the bottom edge fades out (an opacity mask, so any theme's
    // background shows through) and a ghost toggle below expands it to ExpandedMax (the box's own scrollbar takes over beyond that).
    private const double ExpandedMax = 420, FadeHeight = 20;

    private FrameworkElement AttachPromptFade(TextBox box, double collapsedMax, string toggleTag)
    {
        var toggle = new Button { Tag = toggleTag, Style = (Style)FindResource("GhostButton"), Padding = new Thickness(8, 1, 8, 1), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, Focusable = false, Visibility = Visibility.Collapsed };
        var (expanded, tall) = (false, false);
        var fade = Math.Min(FadeHeight, collapsedMax / 2) / collapsedMax;
        var fadeMask = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 1 - fade), new GradientStop(Color.FromArgb(0x10, 0, 0, 0), 1),
        }, new Point(0, 0), new Point(0, 1));
        void Update()
        {
            if (!box.IsVisible || box.ViewportHeight <= 0)
                return; // hidden (the negative row in OpenRouter mode) or not laid out yet: keep the state
            var chrome = box.ActualHeight - box.ViewportHeight;
            var nowTall = box.ExtentHeight > collapsedMax - chrome + 1;
            if (!nowTall)
                expanded = false;
            tall = nowTall;
            box.MaxHeight = expanded ? ExpandedMax : collapsedMax;
            toggle.Visibility = tall ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = expanded ? "Show less ▴" : "Show more ▾";
            // nothing below to hint at once the end of the text is in view: the line being typed must not sit in the fade
            var atEnd = box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 1;
            var mask = !tall || expanded || atEnd ? null : fadeMask;
            if (!ReferenceEquals(box.OpacityMask, mask))
                box.OpacityMask = mask;
        }
        box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Update()));
        box.MaxHeight = collapsedMax;
        box.TextChanged += (_, _) => box.Dispatcher.BeginInvoke(Update, System.Windows.Threading.DispatcherPriority.Loaded);
        box.SizeChanged += (_, _) => Update();
        box.IsVisibleChanged += (_, _) => box.Dispatcher.BeginInvoke(Update, System.Windows.Threading.DispatcherPriority.Loaded);
        toggle.Click += (_, _) => { expanded = !expanded; Update(); };
        return new StackPanel { Children = { box, toggle } };
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
        _imgUseShared.Visibility = Shown(_settings.ShareHost.Length > 0);
        foreach (var control in _imgLocalOnly) // the primary PC takes the settings, but not this PC's runtime
            control.Visibility = Shown(!_imgRemoteMode && !(_imgSharedMode && control == _imgRuntimeText.Parent));
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
        var multiple = RecommendedImageSize().Multiple;
        var typed = TypedSize(box);
        var value = ImageGen.Pick(typed, 0, multiple);
        box.Text = typed > 0 ? value.ToString() : "";
        if (ImageRatio() is not { Length: > 0 } ratio)
            return;
        other.Text = typed > 0 ? ImageGen.Linked(value, ratio, width, multiple).ToString() : "";
        if (typed > 0 && !ImageGen.Matches(TypedSize(_imgWidth), TypedSize(_imgHeight), ratio, multiple))
            _imgAspect.SelectedIndex = 0;
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
        var installed = File.Exists(ImageGen.CliPath);
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
        _imgGalleryHead.Text = pictures.Count == 0 ? "GALLERY · nothing yet" : $"GALLERY · {pictures.Count}{(pictures.Count == 200 ? "+" : "")}";
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
                if (SidebarAsk("Delete picture", $"Move {Path.GetFileName(path)} to the Recycle Bin?", "Delete") is null)
                    return;
                foreach (var file in new[] { path, sidecar }.Where(File.Exists))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                RefreshGallery();
            });
            thumb.ContextMenu = menu;
            _imgGallery.Children.Add(thumb);
        }
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
            _imgProcess = null;
            _imgGenerate.IsEnabled = _imgRuntimeButton.IsEnabled = true;
            _imgCancel.Visibility = _imgProgress.Visibility = Visibility.Collapsed;
            RefreshImageRuntime();
            RefreshImageRecipes();
        }
    }

    // The latest stable-diffusion.cpp release, installed like llama-server (a failed install keeps the previous one).
    private async Task InstallImageRuntimeAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
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
            await Task.Run(() => LauncherLogic.InstallLlamaServer(zips, ImageGen.RuntimeFolder, $"{assets.Tag}\n{assets.Flavour}", exe: "sd-cli.exe"), ct);
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
        List<(string Written, string Final, long Seed)> files;
        if (s.RemoteModel.Length > 0 || s.SharedModel.Length > 0) // OpenRouter and the primary PC need neither the runtime nor the model files here
        {
            Directory.CreateDirectory(ImageGen.ImagesFolder);
            files = ImageGen.OutputNames(ImageGen.ImagesFolder, DateTime.Now, run.Seed, run.Batch).Files;
            if (s.SharedModel.Length > 0)
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
            files = await GenerateLocalAsync(run, ImageGen.ImagesFolder, () =>
            {
                Toast("Not enough GPU memory: trying again with Low VRAM");
                _imgLowVram.IsChecked = s.LowVram = true; // Low VRAM stays ticked for next time
                _settings.Save();
            }, ct);
        SaveImageFiles(run, files);
        ImageStatus(run.RemoteModel.Length > 0 ? "Done" : $"Done · seed {files[0].Seed}", 1);
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

    // sd-cli on this PC (the Images page and the primary PC's share server): installs the runtime and downloads the model's
    // missing files first, writes the pictures into folder (run.Seed chosen), progress in the status line. Out of GPU memory
    // without Low VRAM: once more with it (onLowVram is told). Returns the pictures' names (ImageGen.OutputNames), not renamed.
    private async Task<List<(string Written, string Final, long Seed)>> GenerateLocalAsync(ImageSettings run, string folder, Action? onLowVram, CancellationToken ct)
    {
        var recipe = ImageGen.Recipe(run.RecipeId);
        if (!File.Exists(ImageGen.CliPath))
            await InstallImageRuntimeAsync(ct);
        await DownloadImageModelsAsync(recipe, run.LocalModel, ct);
        Directory.CreateDirectory(folder);
        var (output, files) = ImageGen.OutputNames(folder, DateTime.Now, run.Seed, run.Batch);
        var info = new ProcessStartInfo(ImageGen.CliPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = ImageGen.RuntimeFolder,
        };
        foreach (var arg in ImageGen.Args(recipe, run, ImageGen.Paths(recipe, run.LocalModel, _imgFolderFiles, ImageGen.ModelsRoot), output))
            info.ArgumentList.Add(arg);
        ImageStatus("Loading…", indeterminate: true);
        var tail = new Queue<string>();
        void Keep(string line)
        {
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 40)
                    tail.Dequeue();
            }
        }
        using var process = _imgProcess = Process.Start(info) ?? throw new InvalidOperationException("sd-cli did not start");
        using var kill = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        // stdout: log lines and the \r-separated progress line
        var stdout = Task.Run(async () =>
        {
            var buffer = new char[4096];
            var line = new StringBuilder();
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer)) > 0)
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] is not ('\r' or '\n'))
                    {
                        line.Append(buffer[i]);
                        continue;
                    }
                    var text = line.ToString();
                    line.Clear();
                    if (text.Trim().Length == 0)
                        continue;
                    if (ImageGen.ParseProgress(text) is { } p)
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            if (p.Loading)
                                ImageStatus($"Loading… {p.Step * 100 / Math.Max(1, p.Steps)}%", p.Step / (double)Math.Max(1, p.Steps));
                            else
                                ImageStatus($"Step {p.Step}/{p.Steps} · {p.Speed}", p.Step / (double)Math.Max(1, p.Steps));
                        });
                    else
                        Keep(text);
                }
        });
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } text)
                Keep(text);
        });
        await process.WaitForExitAsync(CancellationToken.None); // a cancel kills it, and this returns
        await Task.WhenAll(stdout, stderr);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode == 0 && File.Exists(files[0].Written))
            return files;
        Log($"sd-cli exited with {process.ExitCode}:\n" + string.Join("\n", tail));
        var outOfMemory = tail.Any(t => t.Contains("enough memory", StringComparison.OrdinalIgnoreCase) || t.Contains("out of memory", StringComparison.OrdinalIgnoreCase));
        if (outOfMemory && !run.LowVram) // once more with the weights in RAM
        {
            run.LowVram = true;
            onLowVram?.Invoke();
            return await GenerateLocalAsync(run, folder, onLowVram, ct);
        }
        throw new InvalidOperationException(outOfMemory
            ? "Not enough GPU memory for this model even with Low VRAM: unload the chat models, pick a smaller size, or a smaller model"
            : $"sd-cli failed: {tail.FirstOrDefault(t => t.StartsWith("[E]"))?[3..].Split(" --- ")[0].Trim() ?? $"exit code {process.ExitCode}"} (the log has its output)");
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
