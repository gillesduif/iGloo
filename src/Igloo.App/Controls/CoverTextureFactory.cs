using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Igloo.App.Controls;

/// <summary>
/// Builds the square cover textures that <see cref="CoverFlow3DControl"/> maps onto its
/// 3D planes. Every cover is a rounded dark tile; if the distro ships a logo PNG it is
/// composed centered onto the tile, otherwise a fallback cover is generated from the
/// distro's initial on a color derived from a stable hash of its id.
///
/// Results are <see cref="RenderTargetBitmap"/>s, frozen and cached per (key, pixel size)
/// so the carousel can cheaply ask for low-res variants of far-away covers.
/// </summary>
public sealed class CoverTextureFactory
{
    private readonly Dictionary<(string Key, int Pixels), BitmapSource> _cache = [];

    /// <summary>
    /// Returns a square <paramref name="pixels"/>×<paramref name="pixels"/> cover texture.
    /// </summary>
    /// <param name="cacheKey">Stable identity of the cover (the distro id).</param>
    /// <param name="logoPath">Absolute path to a PNG logo, or null for the generated fallback.</param>
    /// <param name="displayName">Used for the fallback cover's initial glyph.</param>
    /// <param name="pixels">Texture edge length, e.g. 512 near center, 144 far out.</param>
    public BitmapSource GetCover(string cacheKey, string? logoPath, string displayName, int pixels)
    {
        if (_cache.TryGetValue((cacheKey, pixels), out var cached))
            return cached;

        var texture = Render(logoPath, displayName, cacheKey, pixels);
        _cache[(cacheKey, pixels)] = texture;
        return texture;
    }

    private static BitmapSource Render(string? logoPath, string displayName, string hashKey, int pixels)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var logo = TryLoadLogo(logoPath, pixels);
            if (logo is not null)
            {
                DrawTile(dc, pixels, Color.FromRgb(0x23, 0x2A, 0x3E), Color.FromRgb(0x12, 0x16, 0x23));
                DrawLogo(dc, logo, pixels);
            }
            else
            {
                var hue = StableHash(hashKey) % 360;
                DrawTile(dc, pixels, FromHsv(hue, 0.50, 0.46), FromHsv(hue, 0.62, 0.20));
                DrawInitial(dc, displayName, pixels);
            }
            DrawSheenAndBorder(dc, pixels);
        }

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // ── Drawing helpers ──────────────────────────────────────────────────────

    private static void DrawTile(DrawingContext dc, int pixels, Color top, Color bottom)
    {
        var radius = pixels * 0.07;
        var fill = new LinearGradientBrush(top, bottom, 90);
        fill.Freeze();
        dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, pixels, pixels), radius, radius);
    }

    private static void DrawLogo(DrawingContext dc, BitmapSource logo, int pixels)
    {
        // Center the logo in a 68% inset box, preserving aspect ratio.
        var box = pixels * 0.68;
        var scale = Math.Min(box / logo.PixelWidth, box / logo.PixelHeight);
        var w = logo.PixelWidth * scale;
        var h = logo.PixelHeight * scale;
        dc.DrawImage(logo, new Rect((pixels - w) / 2, (pixels - h) / 2, w, h));
    }

    private static void DrawInitial(DrawingContext dc, string displayName, int pixels)
    {
        var initial = string.IsNullOrWhiteSpace(displayName)
            ? "?"
            : char.ToUpperInvariant(displayName.TrimStart()[0]).ToString();

        var text = new FormattedText(
            initial,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            pixels * 0.52,
            new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
            pixelsPerDip: 1.0);

        dc.DrawText(text, new Point((pixels - text.Width) / 2, (pixels - text.Height) / 2));
    }

    private static void DrawSheenAndBorder(DrawingContext dc, int pixels)
    {
        var radius = pixels * 0.07;

        // Subtle top sheen so the tile reads as a glossy slab once lit in 3D.
        var sheen = new LinearGradientBrush(
            Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90);
        sheen.Freeze();
        dc.DrawRoundedRectangle(sheen, null, new Rect(0, 0, pixels, pixels * 0.45), radius, radius);

        var border = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), Math.Max(1.0, pixels / 400.0));
        border.Freeze();
        dc.DrawRoundedRectangle(null, border, new Rect(0.5, 0.5, pixels - 1, pixels - 1), radius, radius);
    }

    // ── Asset loading / color derivation ────────────────────────────────────

    private static BitmapSource? TryLoadLogo(string? path, int decodePixels)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            // Decode at the size we will actually draw - keeps low-res covers cheap.
            image.DecodePixelWidth = decodePixels;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
        {
            return null; // Corrupt/unreadable logo → generated fallback cover.
        }
    }

    /// <summary>FNV-1a - stable across processes, unlike string.GetHashCode().</summary>
    private static uint StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in value)
                hash = (hash ^ c) * 16777619;
            return hash;
        }
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
