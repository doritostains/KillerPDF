using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace KillerPDF;

/// <summary>
/// Pure page-manipulation operations on a PdfSharpCore PdfDocument.
/// All UI concerns (dialogs, status updates, refresh) stay in the UI layer; this service
/// owns only the document mutations themselves.
/// </summary>
public static class PdfDocumentService
{
    /// <summary>
    /// Removes the given page indices from <paramref name="doc"/>. Indices are sorted
    /// descending internally so callers may pass them in any order.
    /// </summary>
    public static void DeletePages(PdfDocument doc, IEnumerable<int> pageIndices)
    {
        foreach (var idx in pageIndices.OrderByDescending(i => i))
            doc.Pages.RemoveAt(idx);
    }

    /// <summary>
    /// Moves a page from one index to another within the same document.
    /// </summary>
    public static void MovePage(PdfDocument doc, int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= doc.PageCount) return;
        if (toIndex < 0 || toIndex >= doc.PageCount) return;
        var page = doc.Pages[fromIndex];
        doc.Pages.RemoveAt(fromIndex);
        doc.Pages.Insert(toIndex, page);
    }

    /// <summary>
    /// Inserts a blank A4 page (595×842 pt) after the given index. Pass -1 to prepend.
    /// </summary>
    public static int InsertBlankPage(PdfDocument doc, int afterIndex)
    {
        var blank = new PdfPage { Width = XUnit.FromPoint(595), Height = XUnit.FromPoint(842) };
        int insertAt = afterIndex + 1;
        doc.Pages.Insert(insertAt, blank);
        return insertAt;
    }

    /// <summary>
    /// Rotates the given pages by <paramref name="delta"/> degrees (typically ±90).
    /// Rotation is normalized to [0, 360).
    /// </summary>
    public static void RotatePages(PdfDocument doc, IEnumerable<int> pageIndices, int delta)
    {
        foreach (var idx in pageIndices)
            doc.Pages[idx].Rotate = ((doc.Pages[idx].Rotate + delta) % 360 + 360) % 360;
    }

    /// <summary>
    /// Creates a new PDF containing only the pages at the given indices from
    /// <paramref name="sourcePath"/>, written to <paramref name="outputPath"/>.
    /// Indices are sorted ascending so output preserves source order.
    /// </summary>
    public static void ExtractPages(string sourcePath, IEnumerable<int> pageIndices, string outputPath)
    {
        using var importDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
        var newDoc = new PdfDocument();
        foreach (var idx in pageIndices.OrderBy(i => i))
            newDoc.AddPage(importDoc.Pages[idx]);
        newDoc.Save(outputPath);
    }

    /// <summary>
    /// Appends every page from <paramref name="sourcePath"/> to <paramref name="target"/>,
    /// rewriting named-destination links so cross-document references remain valid.
    /// Returns the page offset where the appended pages start (i.e. target.PageCount
    /// before this call).
    /// </summary>
    public static int AppendDocument(PdfDocument target, string sourcePath)
    {
        int pageOffset = target.PageCount;
        using var srcRead = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
        var namedDestMap = PdfLinkExtractor.BuildNamedDestMap(srcRead);
        for (int i = 0; i < srcRead.PageCount; i++)
            target.AddPage(srcRead.Pages[i]);
        if (namedDestMap.Count > 0)
            PdfLinkExtractor.RewriteNamedDestLinks(target, pageOffset, namedDestMap);
        return pageOffset;
    }
}
