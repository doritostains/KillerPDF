using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace KillerPDF;

/// <summary>
/// Bakes in-memory annotations into the PDF document by drawing them onto each page
/// via PdfSharp's XGraphics. Used by Save Flattened and the temp-save reload path.
///
/// Entirely platform-neutral: depends only on PdfSharpCore types and Core annotation
/// primitives. Both WPF and Avalonia UIs call this directly.
/// </summary>
public static class PdfAnnotationFlattener
{
    /// <summary>
    /// Draws every annotation in <paramref name="annotations"/> onto the matching page of
    /// <paramref name="doc"/>. The <paramref name="renderDims"/> dictionary supplies the
    /// canvas pixel dimensions used during interactive editing, which are used to scale
    /// annotation coordinates back to PDF page points.
    /// </summary>
    public static void FlattenInto(
        PdfDocument doc,
        IReadOnlyDictionary<int, List<PageAnnotation>> annotations,
        IReadOnlyDictionary<int, (int w, int h)> renderDims)
    {
        foreach (var kvp in annotations)
        {
            int pageIdx = kvp.Key;
            var annots = kvp.Value;
            if (annots.Count == 0 || pageIdx >= doc.PageCount) continue;
            if (!renderDims.ContainsKey(pageIdx)) continue;

            var page = doc.Pages[pageIdx];
            var (renderW, renderH) = renderDims[pageIdx];
            double sx = page.Width.Point / renderW;
            double sy = page.Height.Point / renderH;

            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            foreach (var annot in annots)
            {
                switch (annot)
                {
                    case TextAnnotation ta:
                        var font = new XFont("Segoe UI", ta.FontSize * sy);
                        var lines = ta.Content.Split('\n');
                        double lineH = ta.FontSize * sy * 1.2;
                        double ty = ta.Position.Y * sy + ta.FontSize * sy;
                        var taBrush = new XSolidBrush(XColor.FromArgb(ta.ColorA, ta.ColorR, ta.ColorG, ta.ColorB));
                        foreach (var line in lines)
                        {
                            if (!string.IsNullOrEmpty(line))
                                gfx.DrawString(line, font, taBrush, ta.Position.X * sx, ty);
                            ty += lineH;
                        }
                        break;

                    case HighlightAnnotation ha:
                        var hBrush = new XSolidBrush(XColor.FromArgb(ha.ColorA, ha.ColorR, ha.ColorG, ha.ColorB));
                        gfx.DrawRectangle(hBrush,
                            ha.Bounds.X * sx, ha.Bounds.Y * sy,
                            ha.Bounds.Width * sx, ha.Bounds.Height * sy);
                        break;

                    case InkAnnotation ia:
                        if (ia.Points.Count < 2) break;
                        var pen = new XPen(XColor.FromArgb(ia.ColorA, ia.ColorR, ia.ColorG, ia.ColorB), ia.StrokeWidth * sx)
                        {
                            LineJoin = XLineJoin.Round,
                            LineCap = XLineCap.Round
                        };
                        for (int i = 0; i < ia.Points.Count - 1; i++)
                        {
                            gfx.DrawLine(pen,
                                ia.Points[i].X * sx, ia.Points[i].Y * sy,
                                ia.Points[i + 1].X * sx, ia.Points[i + 1].Y * sy);
                        }
                        break;

                    case TextEditAnnotation tea:
                        var whiteRect = new XSolidBrush(XColors.White);
                        gfx.DrawRectangle(whiteRect,
                            (tea.OriginalBounds.X - 2) * sx, (tea.OriginalBounds.Y - 2) * sy,
                            (tea.OriginalBounds.Width + 4) * sx, (tea.OriginalBounds.Height + 4) * sy);
                        var editFont = new XFont(tea.FontName, tea.FontSize * sy);
                        double ety = tea.Position.Y * sy + tea.FontSize * sy;
                        gfx.DrawString(tea.NewContent, editFont, XBrushes.Black, tea.Position.X * sx, ety);
                        break;

                    case SignatureAnnotation sa:
                        if (sa.ImageData is not null)
                        {
                            try
                            {
                                var imgBytes = Convert.FromBase64String(sa.ImageData);
                                var xImg = XImage.FromStream(() => new System.IO.MemoryStream(imgBytes));
                                double imgX = sa.Position.X * sx;
                                double imgY = sa.Position.Y * sy;
                                double imgW = sa.SourceWidth * sa.Scale * sx;
                                double imgH = sa.SourceHeight * sa.Scale * sy;
                                gfx.DrawImage(xImg, imgX, imgY, imgW, imgH);
                            }
                            catch { /* skip broken image */ }
                        }
                        else
                        {
                            var sigPen = new XPen(XColors.Black, 2 * sa.Scale * sx)
                            {
                                LineJoin = XLineJoin.Round,
                                LineCap = XLineCap.Round
                            };
                            foreach (var stroke in sa.Strokes)
                            {
                                for (int i = 0; i < stroke.Count - 1; i++)
                                {
                                    double x1 = (sa.Position.X + stroke[i].X * sa.Scale) * sx;
                                    double y1 = (sa.Position.Y + stroke[i].Y * sa.Scale) * sy;
                                    double x2 = (sa.Position.X + stroke[i + 1].X * sa.Scale) * sx;
                                    double y2 = (sa.Position.Y + stroke[i + 1].Y * sa.Scale) * sy;
                                    gfx.DrawLine(sigPen, x1, y1, x2, y2);
                                }
                            }
                        }
                        break;

                    case ImageAnnotation ia:
                        try
                        {
                            var iaBytes = Convert.FromBase64String(ia.ImageData);
                            var xia = XImage.FromStream(() => new System.IO.MemoryStream(iaBytes));
                            double iaX = ia.Position.X * sx;
                            double iaY = ia.Position.Y * sy;
                            double iaW = ia.SourceWidth * ia.Scale * sx;
                            double iaH = ia.SourceHeight * ia.Scale * sy;
                            gfx.DrawImage(xia, iaX, iaY, iaW, iaH);
                        }
                        catch { /* skip broken image */ }
                        break;
                }
            }
        }
    }
}
