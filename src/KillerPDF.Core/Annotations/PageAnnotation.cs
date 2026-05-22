namespace KillerPDF;

public enum EditTool { Select, Text, Highlight, Draw, Signature, Image, Crop }

public abstract class PageAnnotation
{
    public int PageIndex { get; set; }
}

/// <summary>
/// Base class for placed/resizable annotations (signature, image).
/// Carries the shared position, scale, and source-dimension properties used by the resize handle.
/// </summary>
public abstract class PlacedAnnotation : PageAnnotation
{
    public PointD Position { get; set; }
    public double Scale { get; set; } = 0.5;
    public double SourceWidth { get; set; } = 400;
    public double SourceHeight { get; set; } = 150;
}

public class TextAnnotation : PageAnnotation
{
    public PointD Position { get; set; }
    public string Content { get; set; } = "";
    public double FontSize { get; set; } = 14;
    public byte ColorR { get; set; } = 0;
    public byte ColorG { get; set; } = 0;
    public byte ColorB { get; set; } = 0;
    public byte ColorA { get; set; } = 255;
}

public class InkAnnotation : PageAnnotation
{
    public List<PointD> Points { get; set; } = new();
    public double StrokeWidth { get; set; } = 2;
    public byte ColorR { get; set; } = 255;
    public byte ColorG { get; set; } = 0;
    public byte ColorB { get; set; } = 0;
    public byte ColorA { get; set; } = 255;
}

public class HighlightAnnotation : PageAnnotation
{
    public RectD Bounds { get; set; }
    public byte ColorR { get; set; } = 255;
    public byte ColorG { get; set; } = 255;
    public byte ColorB { get; set; } = 0;
    public byte ColorA { get; set; } = 80;
}

/// <summary>
/// Represents an edit to existing PDF text: whites out original bounds, draws replacement.
/// </summary>
public class TextEditAnnotation : PageAnnotation
{
    public RectD OriginalBounds { get; set; }
    public PointD Position { get; set; }
    public string NewContent { get; set; } = "";
    public string OriginalContent { get; set; } = "";
    public double FontSize { get; set; } = 14;
    public string FontName { get; set; } = "Segoe UI";
}

/// <summary>
/// A signature placed on a PDF page: either ink strokes or an imported image.
/// </summary>
public class SignatureAnnotation : PlacedAnnotation
{
    public List<List<PointD>> Strokes { get; set; } = new();
    /// <summary>Base-64 encoded PNG. Non-null = image sig; null = drawn strokes.</summary>
    public string? ImageData { get; set; }
}

/// <summary>
/// An image placed on a PDF page as a resizable annotation.
/// </summary>
public class ImageAnnotation : PlacedAnnotation
{
    /// <summary>Base-64 encoded image bytes (PNG, JPG, BMP, etc.).</summary>
    public string ImageData { get; set; } = "";
}

/// <summary>
/// A point that can be serialized to JSON. Kept distinct from PointD for backward
/// compatibility with already-saved signature files on disk.
/// </summary>
public class SerializablePoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>
/// A saved signature stored in the user's AppData for reuse.
/// </summary>
public class SavedSignature
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Signature";
    public List<List<SerializablePoint>> Strokes { get; set; } = new();
    public double CanvasWidth { get; set; } = 400;
    public double CanvasHeight { get; set; } = 150;
    /// <summary>Base-64 encoded PNG for imported image signatures. Null = drawn strokes.</summary>
    public string? ImageData { get; set; }
}
