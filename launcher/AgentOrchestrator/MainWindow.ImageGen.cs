using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
    private ComboBox _imgRecipe = null!, _imgAspect = null!, _imgLongSide = null!, _imgBatch = null!, _imgSampler = null!;
    private TextBox _imgPrompt = null!, _imgNegative = null!, _imgSeed = null!;
    private FrameworkElement _imgNegativeRow = null!;
    private Slider _imgSteps = null!, _imgCfg = null!, _imgStrength = null!;
    private CheckBox _imgLowVram = null!;
    private string _imgInit = "";
    private TextBlock _imgSizeText = null!, _imgRuntimeText = null!, _imgStatus = null!, _imgVramHint = null!, _imgInitText = null!;
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
        left.Children.Add(_imgRuntimeText);
        _imgRuntimeButton = new Button { Tag = "ImagesRuntime", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left };
        _imgRuntimeButton.Click += async (_, _) => await RunImageJob(InstallImageRuntimeAsync);
        left.Children.Add(_imgRuntimeButton);
        _imgVramHint = Muted("Chat models are loaded and using VRAM; image generation may be slower or run out of memory.", 11.5);
        _imgVramHint.Margin = new Thickness(0, 6, 0, 0);
        left.Children.Add(_imgVramHint);

        Label("Model");
        _imgRecipe = new ComboBox { Tag = "ImagesRecipe" };
        _imgRecipe.SelectionChanged += (_, _) =>
        {
            if (_imgApplying || _imgRecipe.SelectedIndex < 0)
                return;
            var (recipe, local) = ImageChoice(_imgRecipe.SelectedIndex);
            ApplyImageSettings(new ImageSettings
            {
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
                Muted($"Runs on OpenRouter with the key in {RemoteCatalog.Services[0].KeyEnv}."), _imgRemoteModelButton, _imgRemoteProvidersButton,
                SourceButton("Switch to Local", "ImagesSwitchToLocal", () => { _imgRemoteMode = false; ShowImageSource(); }),
            },
        };
        left.Children.Add(_imgLocalPanel);
        left.Children.Add(_imgRemotePanel);
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

        Label("Prompt");
        _imgPrompt = new TextBox { Tag = "ImagesPrompt", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        left.Children.Add(_imgPrompt);
        _imgNegative = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44 };
        _imgNegativeRow = new StackPanel
        {
            Tag = "ImagesNegative",
            Children = { new TextBlock { Text = "Negative prompt", FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 14, 0, 4) }, _imgNegative },
        };
        left.Children.Add(_imgNegativeRow);

        Label("Aspect ratio and size");
        _imgAspect = new ComboBox { ItemsSource = ImageGen.Aspects, Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        _imgLongSide = new ComboBox { ItemsSource = ImageGen.LongSides, Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        _imgSizeText = Muted("");
        _imgSizeText.VerticalAlignment = VerticalAlignment.Center;
        _imgSizeText.Tag = "ImagesSize";
        _imgAspect.SelectionChanged += (_, _) => ShowImageSize();
        _imgLongSide.SelectionChanged += (_, _) => ShowImageSize();
        left.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _imgAspect, _imgLongSide, _imgSizeText } });

        Label("Steps");
        _imgSteps = NewSlider(1, 60, 1, "0", left);
        Label("Guidance");
        _imgCfg = NewSlider(0, 20, 0.5, "0.0", left);
        Label("Seed");
        _imgSeed = new TextBox { ToolTip = "Empty: a random seed (recorded with the picture)" };
        left.Children.Add(_imgSeed);
        Label("Images");
        _imgBatch = new ComboBox { ItemsSource = new[] { 1, 2, 3, 4 }, Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        left.Children.Add(_imgBatch);

        var advanced = new StackPanel { Visibility = Visibility.Collapsed };
        var toggle = new Button { Content = "▸ Advanced", Style = (Style)FindResource("BareButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        toggle.Click += (_, _) =>
        {
            var show = advanced.Visibility != Visibility.Visible;
            advanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = (show ? "▾" : "▸") + " Advanced";
        };
        left.Children.Add(toggle);
        left.Children.Add(advanced);
        advanced.Children.Add(new TextBlock { Text = "Sampler", FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
        _imgSampler = new ComboBox { ItemsSource = ImageGen.Samplers.Prepend("Default").ToList() };
        advanced.Children.Add(_imgSampler);
        _imgLowVram = new CheckBox { Content = "Low VRAM: one model on the GPU at a time", ToolTip = "Runs the text encoder, then swaps in the image model, then the decoder (weights wait in RAM). Slower to load; needed when the models together are bigger than the GPU", Margin = new Thickness(0, 12, 0, 0) };
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
        _imgRemoteMode = s.RemoteModel.Length > 0;
        if (_imgRemoteMode) // the local pick stays for the way back
        {
            recipe = ImageChoice(_imgRecipe.SelectedIndex).Recipe;
            (_imgRemoteModel, _imgRemoteProviders) = ((s.RemoteModel, ""), s.RemoteProvider.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList());
        }
        else
            _imgRecipe.SelectedIndex = local >= 0 ? ImageGen.Recipes.Length + local : Array.IndexOf(ImageGen.Recipes, recipe);
        ShowImageSource();
        _imgPrompt.Text = s.Prompt;
        _imgNegative.Text = s.Negative;
        _imgAspect.SelectedItem = ImageGen.Aspects.Contains(s.Aspect) ? s.Aspect : "1:1";
        _imgLongSide.SelectedItem = ImageGen.LongSides.Contains(s.LongSide) ? s.LongSide : 1024;
        _imgSteps.Value = s.Steps;
        _imgCfg.Value = s.Cfg;
        _imgSeed.Text = s.Seed >= 0 ? s.Seed.ToString() : "";
        _imgBatch.SelectedIndex = Math.Clamp(s.Batch, 1, 4) - 1;
        _imgSampler.SelectedIndex = Array.IndexOf(ImageGen.Samplers, s.Sampler) + 1; // not found: Default
        _imgLowVram.IsChecked = s.LowVram;
        _imgStrength.Value = s.Strength;
        SetInitImage(s.InitImage);
        _imgNegativeRow.Visibility = recipe.Negative ? Visibility.Visible : Visibility.Collapsed;
        _imgApplying = false;
        ShowImageSize();
    }

    private ImageSettings ReadImageSettings()
    {
        var (recipe, local) = ImageChoice(_imgRecipe.SelectedIndex);
        var (aspect, longSide) = ((string)_imgAspect.SelectedItem, (int)_imgLongSide.SelectedItem);
        var (w, h) = ImageGen.Size(aspect, longSide, recipe.Multiple);
        return new()
        {
            RecipeId = recipe.Id, LocalModel = local, Prompt = _imgPrompt.Text.Trim(), Negative = recipe.Negative ? _imgNegative.Text.Trim() : "", Aspect = aspect, LongSide = longSide,
            Width = w, Height = h, Steps = (int)_imgSteps.Value, Cfg = _imgCfg.Value, Seed = ReadSeed(), Batch = _imgBatch.SelectedIndex + 1,
            Sampler = _imgSampler.SelectedIndex > 0 ? ImageGen.Samplers[_imgSampler.SelectedIndex - 1] : "", LowVram = _imgLowVram.IsChecked == true,
            Strength = Math.Round(_imgStrength.Value, 2), InitImage = _imgInit,
            RemoteModel = _imgRemoteMode ? _imgRemoteModel.Id.Trim() : "", RemoteProvider = _imgRemoteMode ? string.Join(", ", _imgRemoteProviders) : "",
        };
    }

    // The panel for the source in use; the model button shows the name (the id until the list has been fetched), the providers button the ticks.
    private void ShowImageSource()
    {
        _imgLocalPanel.Visibility = _imgRemoteMode ? Visibility.Collapsed : Visibility.Visible;
        _imgRemotePanel.Visibility = _imgRemoteMode ? Visibility.Visible : Visibility.Collapsed;
        _imgRemoteModelButton.Content = _imgRemoteModel.Id.Length == 0 ? "Choose a model…" : _imgRemoteModel.Name.Length > 0 ? _imgRemoteModel.Name : _imgRemoteModel.Id;
        _imgRemoteProvidersButton.Content = "Providers: " + (_imgRemoteProviders.Count == 0 ? "any" : string.Join(", ", _imgRemoteProviders));
    }

    private void ShowImagePopup(DialogShell popup)
    {
        if (_imgDialogTest is { } test)
            test(popup);
        else
            popup.ShowDialog();
    }

    // A filterable list of OpenRouter's image models; a pick keeps its id and name and closes it. A model with no id is skipped.
    private void ChooseImageModel()
    {
        var filter = new TextBox { Tag = "ImagesModelFilter", Margin = new Thickness(0, 0, 0, 8) };
        var status = RemoteMuted("Loading models…");
        var list = new StackPanel();
        var popup = new DialogShell(this, "OpenRouter image models", new StackPanel { MinWidth = 420, Children = { filter, status, new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } } });
        var models = new List<(string Id, string Name)>();
        void Show()
        {
            list.Children.Clear();
            foreach (var (id, name) in models.Where(m => $"{m.Name} {m.Id}".Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                var row = new Button
                {
                    Content = new StackPanel { Children = { new TextBlock { Text = name }, RemoteMuted(id) } }, Tag = "imgmodel:" + id, Style = (Style)FindResource("GhostButton"),
                    HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 0, 2),
                };
                row.Click += (_, _) =>
                {
                    if (_imgRemoteModel.Id != id)
                        _imgRemoteProviders = []; // providers belong to a model
                    _imgRemoteModel = (id, name);
                    ShowImageSource();
                    popup.Close();
                };
                list.Children.Add(row);
            }
        }
        async void Load()
        {
            try
            {
                var got = await RemoteCatalog.FetchImageModelsAsync(_remoteHttp, Environment.GetEnvironmentVariable(RemoteCatalog.Services[0].KeyEnv), CancellationToken.None);
                models = [.. got.Where(m => m.Id.Trim().Length > 0).Select(m => (m.Id.Trim(), m.Name.Trim().Length > 0 ? m.Name.Trim() : m.Id.Trim()))];
                status.Text = $"{models.Count} image models";
                Show();
            }
            catch (Exception x) when (x is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                status.Text = $"Could not load the models ({x.Message}).";
            }
        }
        filter.TextChanged += (_, _) => Show();
        Load();
        ShowImagePopup(popup);
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
                var rows = (await RemoteCatalog.FetchImageProvidersAsync(_remoteHttp, model, CancellationToken.None, Environment.GetEnvironmentVariable(RemoteCatalog.Services[0].KeyEnv))).Where(r => r.ProviderTag.Length > 0).ToList();
                _imgRemoteProviders.RemoveAll(t => rows.All(r => r.ProviderTag != t)); // a provider gone since it was ticked
                status.Text = rows.Count == 0 ? $"No providers listed for {model}: OpenRouter chooses." : $"{rows.Count} providers · USD per image";
                foreach (var r in rows)
                {
                    var tag = r.ProviderTag;
                    var mark = new TextBlock { FontWeight = FontWeights.SemiBold, Width = 28, TextAlignment = TextAlignment.Right };
                    mark.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
                    orderMarks[tag] = mark;
                    var who = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = r.ProviderName, FontWeight = FontWeights.SemiBold }, RemoteMuted(tag) } };
                    who.Children[1].SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
                    var cost = RemoteMuted(r.CostPerImage is { } c ? RemotePrice(c) : "—");
                    cost.Width = 70;
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
        if (_imgApplying || _imgAspect.SelectedItem is null || _imgLongSide.SelectedItem is null)
            return;
        var s = ReadImageSettings();
        _imgSizeText.Text = $"{s.Width} × {s.Height}";
    }

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

    private void ImageStatus(string text, double? progress = null, bool indeterminate = false)
    {
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
        if (s.InitImage.Length > 0 && !File.Exists(s.InitImage))
            throw new FileNotFoundException($"The start image is gone: {s.InitImage}");
        _settings.ImageGen = s;
        _settings.Save();
        RefreshImageRuntime(); // the VRAM hint
        if (!File.Exists(ImageGen.CliPath))
            await InstallImageRuntimeAsync(ct);
        var recipe = ImageGen.Recipe(s.RecipeId);
        await DownloadImageModelsAsync(recipe, s.LocalModel, ct);

        var run = JsonSerializer.Deserialize<ImageSettings>(JsonSerializer.Serialize(s))!;
        if (run.Seed < 0)
            run.Seed = Random.Shared.Next(); // chosen here so the sidecar records it
        Directory.CreateDirectory(ImageGen.ImagesFolder);
        var (output, files) = ImageGen.OutputNames(ImageGen.ImagesFolder, DateTime.Now, run.Seed, run.Batch);
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
        if (process.ExitCode != 0 || !File.Exists(files[0].Written))
        {
            Log($"sd-cli exited with {process.ExitCode}:\n" + string.Join("\n", tail));
            var outOfMemory = tail.Any(t => t.Contains("enough memory", StringComparison.OrdinalIgnoreCase) || t.Contains("out of memory", StringComparison.OrdinalIgnoreCase));
            if (outOfMemory && !s.LowVram) // once more with the weights in RAM; Low VRAM stays ticked for next time
            {
                Toast("Not enough GPU memory: trying again with Low VRAM");
                _imgLowVram.IsChecked = true;
                await GenerateImagesAsync(ct);
                return;
            }
            throw new InvalidOperationException(outOfMemory
                ? "Not enough GPU memory for this model even with Low VRAM: unload the chat models, pick a smaller size, or a smaller model"
                : $"sd-cli failed: {tail.FirstOrDefault(t => t.StartsWith("[E]"))?[3..].Split(" --- ")[0].Trim() ?? $"exit code {process.ExitCode}"} (the log has its output)");
        }
        string? first = null;
        foreach (var (written, final, seed) in files.Where(f => File.Exists(f.Written)))
        {
            if (written != final)
                File.Move(written, final, overwrite: true);
            run.Seed = seed;
            File.WriteAllText(Path.ChangeExtension(final, ".json"), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
            first ??= final;
        }
        ImageStatus($"Done · seed {files[0].Seed}", 1);
        RefreshGallery();
        if (first is not null)
            ShowOnStage(first);
    }
}

// Collapsed while the bound value is null (the stage's tools have no picture to act on).
internal sealed class NullToCollapsed : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
