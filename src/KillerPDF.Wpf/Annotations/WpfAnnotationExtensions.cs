using System.Windows;
using System.Windows.Media;

namespace KillerPDF;

/// <summary>
/// Bridges between platform-neutral Core primitives (PointD/RectD/ColorRgba) and WPF types.
/// All annotation properties are typed against Core primitives; UI code converts at the boundary.
/// </summary>
public static class WpfAnnotationExtensions
{
    // ── PointD ↔ Point ────────────────────────────────────────────
    public static Point ToWpf(this PointD p) => new(p.X, p.Y);
    public static PointD ToCore(this Point p) => new(p.X, p.Y);

    // ── RectD ↔ Rect ──────────────────────────────────────────────
    public static Rect ToWpf(this RectD r) => new(r.X, r.Y, r.Width, r.Height);
    public static RectD ToCore(this Rect r) => new(r.X, r.Y, r.Width, r.Height);

    // ── ColorRgba ↔ Color ─────────────────────────────────────────
    public static Color ToWpf(this ColorRgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    public static ColorRgba ToCore(this Color c) => new(c.R, c.G, c.B, c.A);

    // ── Annotation color helpers (preserve original API used in MainWindow.xaml.cs) ──
    public static Color GetColor(this TextAnnotation a) => Color.FromArgb(a.ColorA, a.ColorR, a.ColorG, a.ColorB);
    public static void SetColor(this TextAnnotation a, Color c) { a.ColorR = c.R; a.ColorG = c.G; a.ColorB = c.B; a.ColorA = c.A; }

    public static Color GetColor(this InkAnnotation a) => Color.FromArgb(a.ColorA, a.ColorR, a.ColorG, a.ColorB);
    public static void SetColor(this InkAnnotation a, Color c) { a.ColorR = c.R; a.ColorG = c.G; a.ColorB = c.B; a.ColorA = c.A; }

    public static Color GetColor(this HighlightAnnotation a) => Color.FromArgb(a.ColorA, a.ColorR, a.ColorG, a.ColorB);
    public static void SetColor(this HighlightAnnotation a, Color c) { a.ColorR = c.R; a.ColorG = c.G; a.ColorB = c.B; a.ColorA = c.A; }
}
