using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentOrchestrator;

// The worker settings' model picker: opens under the Model box instead of its own list. A search box (fuzzy: each typed word's
// letters in order), then the models under group headers (Text only, Vision capable, Image generation), each row its name,
// quantization and file size in aligned columns; a column title sorts every group by it, a second click reverses.
// Up/Down move the highlight, Enter picks, Escape or a click outside closes. Image generation models are listed but cannot
// be picked (a worker chats).
public partial class MainWindow
{
    // A listed model: its choice, name, quantization ("—": unknown), file size (0: unknown) and group (PickerGroups).
    // File: the GGUF found on this computer (its tip, so two entries of one model tell their places apart).
    private sealed record PickerEntry(ModelChoice Choice, string Name, string Quant, long Bytes, string Group, string? File = null);

    private static readonly string[] PickerGroups = ["Text only", "Vision capable", "Image generation"];

    // general.architecture of diffusion GGUFs (stable-diffusion.cpp, ComfyUI-GGUF).
    private static readonly HashSet<string> ImageArchitectures = new(StringComparer.OrdinalIgnoreCase)
        { "flux", "sd1", "sd2", "sdxl", "sd3", "aura", "ltxv", "hyvid", "wan", "hidream", "cosmos", "lumina2", "chroma", "qwen_image" };

    // Off the UI thread: each choice's GGUF on this computer gives its size, its vision projector and its architecture. A
    // model whose file is not found (a remote Unsloth path) is Text only, size unknown.
    private static List<PickerEntry> DescribeChoices(IEnumerable<ModelChoice> choices, string modelsFolder, string hubCache)
    {
        var ggufs = LauncherLogic.FindLocalGgufs(modelsFolder, hubCache);
        string? FileOf(ModelChoice c) =>
            c.File is { } f && File.Exists(f) ? f
            : c.LoadPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && File.Exists(c.LoadPath) ? c.LoadPath
            : ggufs.Where(g => g.Repo.Equals(c.LoadPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(g => c.Variant is { } v && g.Variant.Equals(v, StringComparison.OrdinalIgnoreCase)).FirstOrDefault()?.File;
        return choices.Select(c =>
        {
            var file = FileOf(c);
            var (model, vision) = file is null ? (0L, 0L) : VramEstimate.FileSizes(file);
            var meta = LauncherLogic.ReadGgufMetadata(file);
            // A language model's GGUF carries its tokenizer; a diffusion model's (Flux, Qwen-Image, MOSS-Image) has none.
            var image = meta is not null && (!meta.ContainsKey("tokenizer.ggml.model") || ImageArchitectures.Contains(meta.GetValueOrDefault("general.architecture") ?? ""));
            // the pick's variant, else the file name's ending, else the file's own general.file_type
            var quant = c.Variant is { Length: > 0 } v ? v : file is null ? "" : LauncherLogic.QuantOf(file) is { Length: > 0 } named ? named
                : LauncherLogic.QuantOfFileType(meta?.GetValueOrDefault("general.file_type"));
            return new PickerEntry(c, c.Label.Split("  ·  ")[0], quant.Length > 0 ? quant : "—", model,
                image ? PickerGroups[2] : vision > 0 ? PickerGroups[1] : PickerGroups[0], file);
        }).ToList();
    }

    // Every space-separated word of the query has its letters, in order, somewhere in the text (any case).
    internal static bool FuzzyMatch(string text, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
        {
            var i = 0;
            foreach (var ch in text)
                if (i < word.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(word[i]))
                    i++;
            return i == word.Length;
        });

    // A quantization's sort key: its bits (Q4_K_M 4, IQ3_XXS 3, BF16 16; unknown last), then its name.
    private static (double Bits, string Name) QuantKey(string quant) =>
        (Regex.Match(quant, @"(?:^|[^A-Za-z])(?:UD-)?(?:I?Q|B?F|MXFP)(\d+)", RegexOptions.IgnoreCase) is { Success: true } m
            ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : double.MaxValue, quant);

    private ModelPicker? _modelPickerTest; // --ui-smoke: the picker of the settings body built last

    private sealed class ModelPicker
    {
        private const double QuantWidth = 96, SizeWidth = 70;
        private readonly ComboBox _anchor;
        private readonly Action<ModelChoice?> _pick; // null: the model the worker had (not in the list)
        private readonly string _currentName;
        private readonly Popup _popup;
        private readonly TextBox _search;
        private readonly TextBlock _status, _searchHint;
        private readonly StackPanel _rows;
        private readonly ScrollViewer _scroll;
        private readonly Dictionary<string, TextBlock> _titles = new();
        private List<PickerEntry>? _entries;
        private ModelChoice? _current, _initial;
        private string _sort = "Name";
        private bool _descending;
        private int _highlight = -1;
        private readonly List<Border> _pickable = new();

        public bool IsOpen => _popup.IsOpen;
        private DateTime _closedAt;
        // Closed by this same click's press (a click on the box while open): its release must not open it again.
        public bool JustClosed => DateTime.UtcNow - _closedAt < TimeSpan.FromMilliseconds(400);
        internal IReadOnlyList<Border> Rows => _pickable; // --ui-smoke
        internal TextBox Search => _search;
        internal FrameworkElement Card => (FrameworkElement)_popup.Child;

        public ModelPicker(ComboBox anchor, string currentName, Action<ModelChoice?> pick)
        {
            (_anchor, _currentName, _pick) = (anchor, currentName, pick);
            _search = new TextBox { Tag = "ModelSearch", Height = 30, Padding = new Thickness(10, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center };
            _searchHint = new TextBlock { Text = "Search models", Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            _searchHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _search.TextChanged += (_, _) =>
            {
                _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                Refresh();
            };
            _search.PreviewKeyDown += (_, e) => e.Handled = Key(e.Key);

            // The column titles: Name left; Quant and Size right-aligned over their columns (no lines between them).
            var titles = new Grid { Margin = new Thickness(10, 10, 10, 4) };
            foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(QuantWidth), new GridLength(SizeWidth) })
                titles.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            foreach (var (column, name) in new[] { (0, "Name"), (1, "Quant"), (2, "Size") })
            {
                var title = _titles[name] = new TextBlock
                {
                    FontSize = 11, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, Tag = name, ToolTip = $"Sort by {name.ToLowerInvariant()}",
                    HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                };
                title.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    SortBy(name);
                };
                Grid.SetColumn(title, column);
                titles.Children.Add(title);
            }
            _status = new TextBlock { FontSize = 12, Margin = new Thickness(10, 8, 10, 8), TextWrapping = TextWrapping.Wrap };
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _rows = new StackPanel();
            _scroll = new ScrollViewer { Content = _rows, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var card = new Border
            {
                BorderThickness = new Thickness(1), Padding = new Thickness(6),
                Child = new StackPanel { Children = { new Grid { Children = { _search, _searchHint } }, titles, _status, _scroll } },
            };
            card.SetResourceReference(Border.BackgroundProperty, "Card");
            card.SetResourceReference(Border.BorderBrushProperty, "Border");
            card.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
            _popup = new Popup
            {
                Child = card, PlacementTarget = anchor, Placement = PlacementMode.Bottom, VerticalOffset = 4, AllowsTransparency = true, StaysOpen = false,
            };
            _popup.Opened += (_, _) => _search.Focus();
            _popup.Closed += (_, _) =>
            {
                _closedAt = DateTime.UtcNow;
                if (_window is not null)
                    _window.PreviewMouseDown -= OnWindowPress;
                _window = null;
            };
            // StaysOpen=false closes only while the popup holds the mouse, which the search box takes: a press anywhere in
            // the window closes it now (one on the box too: its release then does not reopen it, JustClosed). A box that
            // leaves the screen (the worker tab rebuilt) closes it, or the popup would stay open at the window's corner.
            anchor.Unloaded += (_, _) => Close();
            anchor.IsVisibleChanged += (_, _) =>
            {
                if (!anchor.IsVisible)
                    Close();
            };
            SortTitles();
        }

        private Window? _window;

        private void OnWindowPress(object sender, MouseButtonEventArgs e) => Close();

        // The listed models (null: still loading) and the line shown above them.
        public void SetEntries(List<PickerEntry>? entries, string status)
        {
            (_entries, _status.Text) = (entries, status);
            _status.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        }

        // current: the pick shown in the box (or the worker's model); initial: the worker's model as listed (null: not listed).
        public void Open(ModelChoice? current, ModelChoice? initial)
        {
            (_current, _initial) = (current, initial);
            _search.Text = "";
            Refresh();
            ((Border)_popup.Child).Width = Math.Max(_anchor.ActualWidth, 380);
            _popup.IsOpen = true;
            if (_popup.IsOpen && Window.GetWindow(_anchor) is { } window && _window is null)
            {
                _window = window;
                window.PreviewMouseDown += OnWindowPress; // presses in the popup go to its own window, not this one
            }
        }

        public void Close() => _popup.IsOpen = false;

        internal void SortBy(string column)
        {
            (_sort, _descending) = (column, _sort == column && !_descending);
            SortTitles();
            Refresh();
        }

        private void SortTitles()
        {
            foreach (var (name, title) in _titles)
            {
                title.Text = name + (name == _sort ? _descending ? "  ▼" : "  ▲" : "");
                title.SetResourceReference(TextBlock.ForegroundProperty, name == _sort ? "AccentLight" : "Muted");
            }
        }

        // The groups' headers and rows for the search and sort; the highlight goes to the first pickable row.
        private void Refresh()
        {
            _rows.Children.Clear();
            _pickable.Clear();
            var query = _search.Text.Trim();
            var shown = (_entries ?? []).Where(e => query.Length == 0 || FuzzyMatch($"{e.Name} {e.Quant} {e.Group}", query)).ToList();
            // the worker's model when the list does not have it: a way back after a pick
            if (_entries is not null && query.Length == 0 && !_entries.Any(e => e.Choice == _initial))
                _rows.Children.Add(CurrentRow());
            foreach (var group in PickerGroups)
            {
                var members = shown.Where(e => e.Group == group);
                IOrderedEnumerable<PickerEntry> ordered = _sort switch
                {
                    "Quant" => members.OrderBy(e => QuantKey(e.Quant).Bits).ThenBy(e => e.Quant, StringComparer.OrdinalIgnoreCase),
                    "Size" => members.OrderBy(e => e.Bytes == 0 ? long.MaxValue : e.Bytes),
                    _ => members.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => QuantKey(e.Quant).Bits),
                };
                var list = (_descending ? ordered.Reverse() : ordered).ToList();
                if (list.Count == 0)
                    continue;
                var header = new TextBlock { Text = $"{group.ToUpperInvariant()}  ·  {list.Count}", FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(10, _rows.Children.Count == 0 ? 4 : 12, 10, 4), Tag = "Group" };
                header.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                _rows.Children.Add(header);
                foreach (var entry in list)
                    _rows.Children.Add(Row(entry, group != PickerGroups[2]));
            }
            if (_entries is not null && shown.Count == 0)
                _rows.Children.Add(new TextBlock { Text = query.Length > 0 ? $"No model matches \"{query}\"." : "", Margin = new Thickness(10, 6, 10, 6) });
            Highlight(_pickable.FindIndex(r => r.Tag is PickerEntry e && e.Choice == _current) is var at and >= 0 && query.Length == 0 ? at : 0);
        }

        private Border CurrentRow()
        {
            var name = new TextBlock { Text = $"{_currentName}  (current)", TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
            name.SetResourceReference(TextBlock.ForegroundProperty, _current is null ? "AccentLight" : "TextPrimary");
            var row = new Border { Child = name, Padding = new Thickness(10, 6, 10, 6), Tag = null, Cursor = Cursors.Hand, ToolTip = "The model this worker has now" };
            row.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
            var index = _pickable.Count;
            _pickable.Add(row);
            row.MouseEnter += (_, _) => Highlight(index);
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Choose(null);
            };
            return row;
        }

        private Border Row(PickerEntry entry, bool pickable)
        {
            var grid = new Grid();
            foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(QuantWidth), new GridLength(SizeWidth) })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var current = entry.Choice == _current;
            var name = new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
            name.SetResourceReference(TextBlock.ForegroundProperty, current ? "AccentLight" : "TextPrimary");
            var quant = new TextBlock { Text = entry.Quant, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12, Tag = "Quant" };
            var size = new TextBlock { Text = entry.Bytes > 0 ? VramEstimate.Gb(entry.Bytes) : "—", HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12, Tag = "Size" };
            foreach (var muted in new[] { quant, size })
            {
                muted.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                muted.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
            }
            Grid.SetColumn(quant, 1);
            Grid.SetColumn(size, 2);
            grid.Children.Add(name);
            grid.Children.Add(quant);
            grid.Children.Add(size);
            var row = new Border { Child = grid, Padding = new Thickness(10, 6, 10, 6), Tag = entry, ToolTip = entry.File ?? entry.Choice.Label };
            row.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
            if (!pickable)
            {
                (row.Opacity, row.ToolTip) = (0.5, "An image generation model: a worker needs a model that writes text.");
                return row;
            }
            row.Cursor = Cursors.Hand;
            var index = _pickable.Count;
            _pickable.Add(row);
            row.MouseEnter += (_, _) => Highlight(index);
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Choose(entry);
            };
            return row;
        }

        private void Highlight(int index)
        {
            if (_pickable.Count == 0)
            {
                _highlight = -1;
                return;
            }
            _highlight = (index % _pickable.Count + _pickable.Count) % _pickable.Count; // wraps
            for (var i = 0; i < _pickable.Count; i++)
                if (i == _highlight)
                    _pickable[i].SetResourceReference(Border.BackgroundProperty, "Hover");
                else
                    _pickable[i].Background = null;
            _pickable[_highlight].BringIntoView();
        }

        // The search box's keys; true when handled.
        internal bool Key(Key key)
        {
            switch (key)
            {
                case System.Windows.Input.Key.Down:
                    Highlight(_highlight + 1);
                    return true;
                case System.Windows.Input.Key.Up:
                    Highlight(_highlight - 1);
                    return true;
                case System.Windows.Input.Key.Enter when _highlight >= 0:
                    Choose(_pickable[_highlight].Tag as PickerEntry);
                    return true;
                case System.Windows.Input.Key.Escape:
                    Close();
                    _anchor.Focus();
                    return true;
                default:
                    return false;
            }
        }

        internal void Choose(PickerEntry? entry)
        {
            Close();
            _pick(entry?.Choice);
            _anchor.Focus();
        }
    }
}
