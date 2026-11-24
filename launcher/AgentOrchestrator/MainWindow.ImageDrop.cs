using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AgentOrchestrator;

// A picture dragged onto the Images page fills the page's controls with the settings it was made with (it does not generate): the
// app's own <name>.json beside it when there is one (as the gallery's "Use these settings"), else its embedded parameters (ImageMetadata.cs).
public partial class MainWindow
{
    private static readonly string[] DropImageTypes = [".png", ".jpg", ".jpeg", ".webp"];

    private static string? DroppedImage(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths.FirstOrDefault(p => DropImageTypes.Contains(Path.GetExtension(p).ToLowerInvariant())) : null;

    // Preview events, so the prompt boxes do not take the drop; other files go on to whatever is under the cursor.
    private void HookImageDrop()
    {
        var highlight = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10), Margin = new Thickness(8), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        highlight.SetResourceReference(Border.BorderBrushProperty, "Accent");
        ImagesView.Children.Add(highlight);
        ImagesView.Background ??= System.Windows.Media.Brushes.Transparent; // the page's gaps take the drop too
        ImagesView.AllowDrop = true;
        void Over(object _, DragEventArgs e)
        {
            if (DroppedImage(e) is null)
                return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            highlight.Visibility = Visibility.Visible;
        }
        ImagesView.PreviewDragEnter += Over;
        ImagesView.PreviewDragOver += Over;
        ImagesView.PreviewDragLeave += (_, _) => highlight.Visibility = Visibility.Collapsed;
        ImagesView.PreviewDrop += (_, e) =>
        {
            highlight.Visibility = Visibility.Collapsed;
            if (DroppedImage(e) is not { } path)
                return;
            e.Handled = true;
            ApplyDroppedImage(path);
        };
    }

    // Applies and saves the picture's settings (saved as Generate saves them); returns the toast shown.
    private string ApplyDroppedImage(string path)
    {
        var name = Path.GetFileName(path);
        ImageSettings? saved = null;
        var sidecar = Path.ChangeExtension(path, ".json");
        if (File.Exists(sidecar))
            try { saved = JsonSerializer.Deserialize<ImageSettings>(File.ReadAllText(sidecar)); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        string message;
        if (saved is not null)
        {
            ApplyImageSettings(saved);
            message = $"Loaded settings from {name}";
        }
        else if (ImageMetadata.Parse(ImageMetadata.ReadParameters(path)) is { } p)
            message = $"Loaded settings from {name}: {string.Join(", ", ApplyImageParameters(p))}";
        else
        {
            message = $"No generation settings found in {name}";
            Toast(message, "#F7C873");
            return message;
        }
        _settings.ImageGen = ReadImageSettings();
        _settings.Save();
        Toast(message);
        return message;
    }

    // Embedded parameters onto the current settings: the model first (its own steps, guidance and sampler come in, as picking it
    // does), then the rest; Steps and Guidance go through their boxes as if typed. Returns the fields applied, in the toast's words.
    private List<string> ApplyImageParameters(ImageParameters p)
    {
        var s = ReadImageSettings();
        s.Negative = _imgNegative.Text; // Read leaves it out for a model without one
        var applied = new List<string>();
        if (p.Prompt.Length > 0)
        {
            (s.Prompt, s.Negative) = (p.Prompt, p.Negative);
            applied.Add("prompt");
            if (p.Negative.Length > 0)
                applied.Add("negative prompt");
        }
        var missingModel = "";
        if (p.Model.Length > 0)
        {
            static bool Named(string file, string model) =>
                string.Equals(Path.GetFileName(file), model, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileNameWithoutExtension(file), model, StringComparison.OrdinalIgnoreCase);
            var local = _imgLocal.FindIndex(m => Named(m.Path, p.Model));
            var recipe = local >= 0 ? _imgLocal[local].Recipe
                : ImageGen.Recipes.FirstOrDefault(r => Named(r.Files[0].Path, p.Model)) ?? ImageGen.Recipes.FirstOrDefault(r => Regex.IsMatch(p.Model, r.Family, RegexOptions.IgnoreCase));
            if (recipe is null)
                missingModel = p.Model;
            else
            {
                (s.RecipeId, s.LocalModel, s.RemoteModel, s.SharedModel) = (recipe.Id, local >= 0 ? _imgLocal[local].Path : "", "", "");
                (s.Steps, s.Cfg, s.Sampler) = (recipe.Steps, recipe.Cfg, recipe.Sampler);
                applied.Add("model");
            }
        }
        if (p.Steps is not null)
            applied.Add("steps");
        if (p.Cfg is not null)
            applied.Add("guidance");
        if (p.Sampler.Length > 0)
        {
            s.Sampler = p.Sampler;
            applied.Add("sampler");
        }
        if (p.Scheduler.Length > 0)
        {
            s.Scheduler = p.Scheduler;
            applied.Add("scheduler");
        }
        if (p.Seed is >= 0 and var seed)
        {
            s.Seed = seed;
            applied.Add("seed");
        }
        if (p is { Width: > 0 and var w, Height: > 0 and var h })
        {
            // a preset only on its exact ratio, else Custom; the lock opens when the model's multiple would round the size
            var g = (int)System.Numerics.BigInteger.GreatestCommonDivisor(w, h);
            var ratio = $"{w / g}:{h / g}";
            var preset = ImageGen.Presets.Skip(1).Any(x => x.Ratio == ratio || ImageGen.Flip(x.Ratio) == ratio);
            var multiple = s.SharedModel.Length > 0 && SharedPick is { } shared ? shared.Multiple
                : s.LocalModel.Length > 0 ? _imgLocal.First(m => m.Path == s.LocalModel).Recipe.Multiple : ImageGen.Recipe(s.RecipeId).Multiple;
            (s.Aspect, s.CustomWidth, s.CustomHeight, s.LongSide) = (preset ? ratio : "Custom", w, h, ImageGen.RecommendedLongSide);
            s.SizeUnlocked |= w % multiple != 0 || h % multiple != 0;
            applied.Add("size");
        }
        if (p.Strength is { } strength && _imgInit.Length > 0)
        {
            s.Strength = strength;
            applied.Add("strength");
        }
        ApplyImageSettings(s);
        void Type(string tag, double value)
        {
            if (SmokeFind<TextBox>(ImagesView, b => b.Tag as string == tag) is not { } box)
                return;
            box.Text = value.ToString(CultureInfo.InvariantCulture);
            box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, box, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent }); // SliderCell's Commit
        }
        if (p.Steps is { } steps)
            Type("ImagesStepsBox", steps);
        if (p.Cfg is { } cfg)
            Type("ImagesGuidanceBox", cfg);
        if (missingModel.Length > 0)
            applied.Add($"model {missingModel} not found");
        return applied;
    }

    // --ui-smoke: a PNG carrying "parameters" fills the prompts, steps, guidance, seed and size; a picture with a sidecar takes the sidecar;
    // a picture with neither says so. The page's settings are put back after.
    private bool ImageDropSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: image drop: {what}"); return false; }
        var was = ReadImageSettings();
        was.Negative = _imgNegative.Text;
        var folder = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var png = ImageMetadata.TinyPng();
            var embedded = Path.Combine(folder, "fox.png");
            File.WriteAllBytes(embedded, ImageMetadata.WithText(png, "parameters",
                "a red fox in snow\nNegative prompt: blurry,\nlow quality\nSteps: 23, Sampler: euler_a, CFG scale: 6.3, Seed: 12345, Size: 640x512, Model: no-such-model"));
            var message = ApplyDroppedImage(embedded);
            var s = ReadImageSettings();
            if (_imgPrompt.Text != "a red fox in snow" || _imgNegative.Text != "blurry,\nlow quality" || s.Steps != 23 || s.Cfg != 6.5 || s.Seed != 12345 ||
                s.Width != 640 || s.Height != 512 || s.Sampler != "euler_a" || !message.StartsWith("Loaded settings from fox.png: prompt, negative prompt") ||
                !message.EndsWith("model no-such-model not found") || _settings.ImageGen.Seed != 12345)
                return Fail($"embedded parameters gave \"{_imgPrompt.Text}\" / \"{_imgNegative.Text}\", steps {s.Steps}, guidance {s.Cfg}, seed {s.Seed}, {s.Width}x{s.Height}, {s.Sampler}; \"{message}\"");
            var withSidecar = Path.Combine(folder, "owl.png");
            File.WriteAllBytes(withSidecar, png);
            File.WriteAllText(Path.ChangeExtension(withSidecar, ".json"), JsonSerializer.Serialize(new ImageSettings { Prompt = "an owl", Steps = 9, Seed = 7 }));
            message = ApplyDroppedImage(withSidecar);
            if (_imgPrompt.Text != "an owl" || ReadImageSettings() is not { Steps: 9, Seed: 7 } || message != "Loaded settings from owl.png")
                return Fail($"the sidecar gave \"{_imgPrompt.Text}\"; \"{message}\"");
            var bare = Path.Combine(folder, "bare.png");
            File.WriteAllBytes(bare, png);
            if (ApplyDroppedImage(bare) != "No generation settings found in bare.png" || _imgPrompt.Text != "an owl")
                return Fail("a picture without settings changed the page");
            return true;
        }
        finally
        {
            ApplyImageSettings(was);
            _settings.ImageGen = was;
            _settings.Save();
            Directory.Delete(folder, true);
        }
    }
}
