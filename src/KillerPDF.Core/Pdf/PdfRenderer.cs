using Docnet.Core;
using Docnet.Core.Models;

namespace KillerPDF;

/// <summary>
/// Rasterizes PDF pages via Docnet/PDFium to raw BGRA32 byte arrays. UI layers wrap the
/// bytes into their own bitmap type (WriteableBitmap on WPF, Bitmap on Avalonia).
///
/// Note: Docnet's GetImage() returns BGRA8888 (not RGBA) so consumers should construct
/// bitmaps with a BGRA-compatible pixel format.
/// </summary>
public static class PdfRenderer
{
    public readonly record struct RenderResult(byte[] BgraPixels, int Width, int Height)
    {
        public bool IsValid => Width > 0 && Height > 0 && BgraPixels.Length > 0;
    }

    /// <summary>
    /// Renders a single page from the given PDF file. <paramref name="maxDimension"/>
    /// caps both width and height in pixels (Docnet scales to fit). The caller is
    /// responsible for choosing a sensible value based on display DPI and zoom level.
    /// </summary>
    public static RenderResult RenderPage(string filePath, int pageIndex, int maxDimension)
    {
        using var docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(maxDimension, maxDimension));
        using var pageReader = docReader.GetPageReader(pageIndex);
        int width = pageReader.GetPageWidth();
        int height = pageReader.GetPageHeight();
        byte[] rawBytes = pageReader.GetImage();
        return new RenderResult(rawBytes ?? Array.Empty<byte>(), width, height);
    }
}
