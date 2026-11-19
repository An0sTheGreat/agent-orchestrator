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
    private WrapPanel _imgGallery = null!;
    private bool _imgApplying; // Apply sets the controls: no recipe reset meanwhile

    private void OpenImages()
    {
        CloseRemoteApi();
        CloseModelHub();
        CloseModelSettings();
        CloseSkills();
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
            var recipe = ImageGen.Recipes[_imgRecipe.SelectedIndex];
            ApplyImageSettings(new ImageSettings
            {
                RecipeId = recipe.Id, Prompt = _imgPrompt.Text, Negative = _imgNegative.Text, Steps = recipe.Steps, Cfg = recipe.Cfg, Sampler = recipe.Sampler,
                Seed = ReadSeed(), Batch = _imgBatch.SelectedIndex + 1, LowVram = _imgLowVram.IsChecked == true, Strength = _imgStrength.Value, InitImage = _imgInit,
            });
        };
        left.Children.Add(_imgRecipe);

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
        _imgLowVram = new CheckBox { Content = "Low VRAM (weights in RAM, tiled VAE)", Margin = new Thickness(0, 12, 0, 0) };
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

        // right: the current picture with its progress, the gallery below
        _imgResult = new Image { Stretch = Stretch.Uniform, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 8) };
        _imgResult.MouseLeftButtonUp += (_, _) => { if (_imgResultPath is { } p) ShowImage(p); };
        _imgProgress = new ProgressBar { Height = 4, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
        _imgStatus = Muted("", 12);
        _imgStatus.Tag = "ImagesStatus";
        _imgCancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 3, 12, 3), Visibility = Visibility.Collapsed, Margin = new Thickness(10, 0, 0, 0) };
        _imgCancel.Click += (_, _) => CancelImageGen();
        DockPanel.SetDock(_imgCancel, Dock.Right);
        var statusRow = new DockPanel { Children = { _imgCancel, _imgStatus } };
        _imgGallery = new WrapPanel();
        var galleryHead = Muted("GALLERY", 11);
        galleryHead.FontWeight = FontWeights.SemiBold;
        galleryHead.Margin = new Thickness(0, 14, 0, 6);
        var right = new Grid { Margin = new Thickness(16, 24, 24, 24) };
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        var galleryScroll = new ScrollViewer { Content = _imgGallery, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var (element, row) in new (UIElement, int)[] { (_imgResult, 0), (_imgProgress, 1), (statusRow, 2), (galleryHead, 3), (galleryScroll, 4) })
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
        var recipe = ImageGen.Recipe(s.RecipeId);
        _imgRecipe.SelectedIndex = Array.IndexOf(ImageGen.Recipes, recipe);
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
        var recipe = ImageGen.Recipes[Math.Max(0, _imgRecipe.SelectedIndex)];
        var (aspect, longSide) = ((string)_imgAspect.SelectedItem, (int)_imgLongSide.SelectedItem);
        var (w, h) = ImageGen.Size(aspect, longSide, recipe.Multiple);
        return new()
        {
            RecipeId = recipe.Id, Prompt = _imgPrompt.Text.Trim(), Negative = recipe.Negative ? _imgNegative.Text.Trim() : "", Aspect = aspect, LongSide = longSide,
            Width = w, Height = h, Steps = (int)_imgSteps.Value, Cfg = _imgCfg.Value, Seed = ReadSeed(), Batch = _imgBatch.SelectedIndex + 1,
            Sampler = _imgSampler.SelectedIndex > 0 ? ImageGen.Samplers[_imgSampler.SelectedIndex - 1] : "", LowVram = _imgLowVram.IsChecked == true,
            Strength = Math.Round(_imgStrength.Value, 2), InitImage = _imgInit,
        };
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
        _imgRecipe.ItemsSource = ImageGen.Recipes.Select(r =>
        {
            var have = r.Files.All(f => HubDownloader.IsDownloaded(ImageGen.ModelsRoot, HubLayout.Flat, f.Repo, f.Path));
            var state = have ? "Downloaded" : $"Download ({r.TotalSize / (double)(1L << 30):0.0} GB)";
            return $"{r.Name}{(r.Note.Length > 0 ? " · " + r.Note : "")} — {state}";
        }).ToList();
        _imgRecipe.SelectedIndex = Math.Max(0, index);
        _imgApplying = false;
    }

    // ponytail: the newest 200 pictures only; page the gallery if people keep thousands.
    private void RefreshGallery()
    {
        _imgGallery.Children.Clear();
        if (!Directory.Exists(ImageGen.ImagesFolder))
            return;
        foreach (var path in new DirectoryInfo(ImageGen.ImagesFolder).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Take(200).Select(f => f.FullName))
        {
            if (Thumb(path, 112) is not { } thumb)
                continue;
            thumb.Margin = new Thickness(0, 0, 8, 8);
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
            Item("Copy image", () => { if (LoadPicture(path) is { } bitmap) Clipboard.SetImage(bitmap); });
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

    // The recipe's missing files from Hugging Face, sizes from the hub's tree listing.
    private async Task DownloadImageModelsAsync(ImageRecipe recipe, CancellationToken ct)
    {
        var missing = recipe.Files.Where(f => !HubDownloader.IsDownloaded(ImageGen.ModelsRoot, HubLayout.Flat, f.Repo, f.Path)).ToList();
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
        await DownloadImageModelsAsync(recipe, ct);

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
        foreach (var arg in ImageGen.Args(recipe, run, ImageGen.ModelsRoot, output))
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
            throw new InvalidOperationException($"sd-cli failed (exit code {process.ExitCode}); the log has its output");
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
        _imgResultPath = first;
        _imgResult.Source = first is null ? null : LoadPicture(first);
        ImageStatus($"Done · seed {files[0].Seed}", 1);
        RefreshGallery();
    }
}
