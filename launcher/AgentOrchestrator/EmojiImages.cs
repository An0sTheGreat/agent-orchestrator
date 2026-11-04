using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;

namespace AgentOrchestrator;

// Colour emoji, as browsers show them. WPF draws emoji as gray outlines, and Windows 11's Segoe UI Emoji keeps its colour
// in COLR v1, which only Direct2D renders: each emoji is drawn by Direct2D once per size into a bitmap (cached) and set in
// the text as an image.
internal static class EmojiImages
{
    // A sequence: a pictograph (or a flag pair, a keycap) with its variation selector, skin tone or tag characters, joined
    // by ZWJ. ©, ™, arrows and the like read as text, as chat apps show them: they are not matched.
    private const string Base = @"(?:\uD83C[\uDDE6-\uDDFF]\uD83C[\uDDE6-\uDDFF]|[\uD83C-\uD83E][\uDC00-\uDFFF]|[☀-➿⬀-⯿⌚⌛⏩-⏳⏸-⏺]|[#*0-9]️?⃣)";
    private const string Mods = @"(?:️|\uD83C[\uDFFB-\uDFFF]|\uDB40[\uDC20-\uDC7F])*";
    public static readonly Regex Match = new($"{Base}{Mods}(?:‍{Base}{Mods})*");

    private static readonly Dictionary<(string, int), BitmapSource> Cache = new();
    private static ID2D1Factory1? _d2d;
    private static IDWriteFactory? _dwrite;
    private static IWICImagingFactory? _wic;

    // The emoji drawn in a square of `pixels` (null when Direct2D is unavailable: the text stays as it is).
    public static BitmapSource? Draw(string emoji, int pixels)
    {
        if (Cache.TryGetValue((emoji, pixels), out var cached))
            return cached;
        try
        {
            _d2d ??= D2D1.D2D1CreateFactory<ID2D1Factory1>();
            _dwrite ??= DWrite.DWriteCreateFactory<IDWriteFactory>();
            _wic ??= new IWICImagingFactory();
            using var bitmap = _wic.CreateBitmap((uint)pixels, (uint)pixels, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
            var format = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            using var target = _d2d.CreateWicBitmapRenderTarget(bitmap, new RenderTargetProperties(format));
            using var text = _dwrite.CreateTextFormat("Segoe UI Emoji", pixels / 1.3f);
            text.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
            text.ParagraphAlignment = ParagraphAlignment.Center;
            using var brush = target.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
            target.BeginDraw();
            target.Clear(new Color4(0, 0, 0, 0));
            target.DrawText(emoji, text, new Vortice.Mathematics.Rect(0, 0, pixels, pixels), brush, DrawTextOptions.EnableColorFont);
            target.EndDraw();
            var pixelsOut = new byte[pixels * pixels * 4];
            bitmap.CopyPixels((uint)pixels * 4, pixelsOut);
            var image = BitmapSource.Create(pixels, pixels, 96, 96, PixelFormats.Pbgra32, null, pixelsOut, pixels * 4);
            image.Freeze();
            return Cache[(emoji, pixels)] = image;
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    // An emoji in running text at `fontSize`: an image a little larger than the letters, sitting on their line.
    public static Inline? Inline(string emoji, double fontSize)
    {
        var box = Math.Round(fontSize * 1.25);
        if (Draw(emoji, (int)(box * 2)) is not { } source) // twice the size: sharp at 200% scaling
            return null;
        var image = new Image { Source = source, Width = box, Height = box, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, -fontSize * 0.2) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Baseline, Tag = emoji };
    }
}
