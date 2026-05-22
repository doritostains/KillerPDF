using UglyToad.PdfPig;

namespace KillerPDF;

/// <summary>
/// Case-insensitive text search across all pages of a PDF using PdfPig. Returns
/// per-page bounding boxes in PDF user-space coordinates (origin bottom-left, y up).
/// UI layers scale these to the canvas coordinate space at draw time.
/// </summary>
public static class PdfSearchService
{
    /// <summary>
    /// A single match on a page, with the bounding box in PDF user-space
    /// (Left/Bottom/Right/Top in points, y axis pointing up).
    /// </summary>
    public readonly record struct SearchHit(double Left, double Bottom, double Right, double Top);

    public readonly record struct PageHits(int PageIndex, IReadOnlyList<SearchHit> Hits);

    /// <summary>
    /// Searches every page of the document and returns one entry per page that has any
    /// matches. Pages without matches are not included.
    /// </summary>
    public static IReadOnlyList<PageHits> SearchDocument(string filePath, string query)
    {
        var results = new List<PageHits>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        string lowerQuery = query.ToLowerInvariant();
        using var pigDoc = PdfDocument.Open(filePath);
        for (int pi = 0; pi < pigDoc.NumberOfPages; pi++)
        {
            var page = pigDoc.GetPage(pi + 1);
            var hits = FindMatchesOnPage(page, lowerQuery);
            if (hits.Count > 0)
                results.Add(new PageHits(pi, hits));
        }
        return results;
    }

    private static List<SearchHit> FindMatchesOnPage(UglyToad.PdfPig.Content.Page page, string lowerQuery)
    {
        var result = new List<SearchHit>();
        var words = page.GetWords().ToList();

        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].Text.ToLowerInvariant().Contains(lowerQuery))
            {
                var bb = words[i].BoundingBox;
                result.Add(new SearchHit(bb.Left, bb.Bottom, bb.Right, bb.Top));
                continue;
            }

            // Multi-word match
            string combined = words[i].Text;
            for (int j = i + 1; j < words.Count && combined.Length < lowerQuery.Length + 20; j++)
            {
                combined += " " + words[j].Text;
                if (combined.ToLowerInvariant().Contains(lowerQuery))
                {
                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue;
                    for (int k = i; k <= j; k++)
                    {
                        var wbb = words[k].BoundingBox;
                        minX = Math.Min(minX, wbb.Left);
                        minY = Math.Min(minY, wbb.Bottom);
                        maxX = Math.Max(maxX, wbb.Right);
                        maxY = Math.Max(maxY, wbb.Top);
                    }
                    result.Add(new SearchHit(minX, minY, maxX, maxY));
                    break;
                }
            }
        }
        return result;
    }
}
