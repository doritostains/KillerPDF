using System.Reflection;
using PdfSharpCore.Pdf;

namespace KillerPDF;

/// <summary>
/// PdfSharp-only helpers for resolving named destinations, walking name trees, and
/// rewriting cross-document link annotations during merge. Entirely platform-neutral.
/// </summary>
public static class PdfLinkExtractor
{
    /// <summary>
    /// Dereferences a PdfItem if it is an indirect reference. PdfReference is internal in
    /// PdfSharpCore so we detect it by reflecting for a "Value" property of type PdfObject.
    /// </summary>
    public static PdfItem DerefItem(PdfItem item)
    {
        var valueProp = item.GetType().GetProperty("Value",
            BindingFlags.Public | BindingFlags.Instance);
        if (valueProp?.GetValue(item) is PdfObject resolved)
            return resolved;
        return item;
    }

    /// <summary>
    /// Returns the PDF object number of a PdfItem that is an indirect reference, or -1.
    /// </summary>
    public static int GetObjectNumber(PdfItem? item)
    {
        if (item is null) return -1;
        var prop = item.GetType().GetProperty("ObjectNumber",
            BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(item) is int n ? n : -1;
    }

    /// <summary>
    /// Resolves a named destination (string or name) to a destination array using the
    /// catalog's /Dests dictionary or /Names /Dests name tree.
    /// </summary>
    public static PdfArray? ResolveNamedDest(PdfDocument doc, PdfItem nameItem)
    {
        string name = nameItem switch
        {
            PdfString s => s.Value,
            PdfName n => n.Value.TrimStart('/'),
            _ => ""
        };
        if (string.IsNullOrEmpty(name)) return null;

        var catalog = doc.Internals.Catalog;

        // Legacy /Dests dictionary (direct mapping)
        var dests = catalog.Elements.GetDictionary("/Dests");
        if (dests != null)
        {
            PdfItem? val = DerefItem(dests.Elements[name] ?? dests.Elements["/" + name] ?? new PdfInteger(-1));
            if (val is PdfArray da) return da;
            if (val is PdfDictionary dd) return dd.Elements.GetArray("/D");
        }

        // Modern /Names /Dests name tree
        var names = catalog.Elements.GetDictionary("/Names");
        var destTree = names?.Elements.GetDictionary("/Dests");
        if (destTree != null)
            return ResolveNameTree(destTree, name);

        return null;
    }

    /// <summary>
    /// Walks a PDF name tree to find the destination array for the given name.
    /// </summary>
    public static PdfArray? ResolveNameTree(PdfDictionary node, string name)
    {
        var namesArr = node.Elements.GetArray("/Names");
        if (namesArr != null)
        {
            for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
            {
                var key = namesArr.Elements[i];
                string keyStr = key is PdfString ks ? ks.Value : key?.ToString() ?? "";
                if (keyStr == name)
                {
                    PdfItem? val = DerefItem(namesArr.Elements[i + 1]);
                    if (val is PdfArray va) return va;
                    if (val is PdfDictionary vd) return vd.Elements.GetArray("/D");
                }
            }
        }

        var kids = node.Elements.GetArray("/Kids");
        if (kids != null)
        {
            for (int i = 0; i < kids.Elements.Count; i++)
            {
                PdfItem? kid = DerefItem(kids.Elements[i]);
                if (kid is PdfDictionary kd)
                {
                    var result = ResolveNameTree(kd, name);
                    if (result != null) return result;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Walks the source document's /Names /Dests name tree and builds a map of named-
    /// destination → page index. Used by merge to rewrite links from imported pages.
    /// </summary>
    public static void WalkNameTree(PdfDocument src, PdfDictionary node, Dictionary<string, int> map)
    {
        var namesArr = node.Elements.GetArray("/Names");
        if (namesArr != null)
        {
            for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
            {
                var keyItem = namesArr.Elements[i];
                string key = keyItem is PdfString ks ? ks.Value : keyItem?.ToString()?.TrimStart('/') ?? "";
                if (string.IsNullOrEmpty(key)) continue;
                PdfItem? val = DerefItem(namesArr.Elements[i + 1]);
                int? idx = ResolveDestPageIndexInDoc(src, val);
                if (idx.HasValue) map[key] = idx.Value;
            }
        }

        var kids = node.Elements.GetArray("/Kids");
        if (kids != null)
        {
            for (int i = 0; i < kids.Elements.Count; i++)
            {
                if (DerefItem(kids.Elements[i]) is PdfDictionary kid)
                    WalkNameTree(src, kid, map);
            }
        }
    }

    /// <summary>
    /// Resolves a destination value (PdfArray or PdfDictionary with /D) to a page index
    /// within the given source document by matching the page object number.
    /// </summary>
    public static int? ResolveDestPageIndexInDoc(PdfDocument src, PdfItem? val)
    {
        PdfArray? arr = val as PdfArray;
        if (arr is null && val is PdfDictionary vd)
            arr = vd.Elements.GetArray("/D");
        if (arr is null || arr.Elements.Count == 0) return null;

        var first = arr.Elements[0];
        int objNum = GetObjectNumber(first);
        if (objNum > 0)
        {
            for (int i = 0; i < src.PageCount; i++)
            {
                var pgRef = src.Pages[i].Reference;
                if (pgRef != null && pgRef.ObjectNumber == objNum) return i;
            }
        }
        else if (first is PdfInteger pi && pi.Value >= 0 && pi.Value < src.PageCount)
        {
            return pi.Value;
        }
        return null;
    }

    /// <summary>
    /// Walks all link annotations in pages [pageOffset, doc.PageCount) and rewrites any
    /// named-destination /D values to explicit [pageRef /Fit] arrays using the merged
    /// document's page references.
    /// </summary>
    public static void RewriteNamedDestLinks(PdfDocument doc, int pageOffset,
        Dictionary<string, int> namedDestMap)
    {
        for (int pi = pageOffset; pi < doc.PageCount; pi++)
        {
            try
            {
                var page = doc.Pages[pi];
                var annotsArr = page.Elements.GetArray("/Annots");
                if (annotsArr is null) continue;

                for (int ai = 0; ai < annotsArr.Elements.Count; ai++)
                {
                    PdfItem? elem = annotsArr.Elements[ai];
                    PdfDictionary? ann = elem as PdfDictionary
                        ?? (DerefItem(elem) as PdfDictionary);
                    if (ann is null) continue;

                    var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                    if (!subtype.Contains("Link")) continue;

                    var actionDict = ann.Elements.GetDictionary("/A");
                    if (actionDict != null)
                    {
                        var s = actionDict.Elements["/S"]?.ToString() ?? "";
                        if (s.Contains("GoTo"))
                        {
                            var destItem = actionDict.Elements["/D"];
                            string? name = ExtractDestName(destItem);
                            if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                            {
                                int targetIdx = pageOffset + srcIdx;
                                if (targetIdx < doc.PageCount)
                                    actionDict.Elements["/D"] = MakeExplicitDest(doc, targetIdx);
                            }
                        }
                    }
                    else
                    {
                        var destItem = ann.Elements["/Dest"];
                        string? name = ExtractDestName(destItem);
                        if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                        {
                            int targetIdx = pageOffset + srcIdx;
                            if (targetIdx < doc.PageCount)
                                ann.Elements["/Dest"] = MakeExplicitDest(doc, targetIdx);
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RewriteNamedDestLinks p{pi}: {ex}"); }
        }
    }

    public static string? ExtractDestName(PdfItem? item)
    {
        if (item is null) return null;
        if (item is PdfString ps) return ps.Value;
        if (item is PdfName pn) return pn.Value.TrimStart('/');
        return null;
    }

    public static PdfArray MakeExplicitDest(PdfDocument doc, int pageIndex)
    {
        var arr = new PdfArray(doc);
        arr.Elements.Add(doc.Pages[pageIndex].Reference);
        arr.Elements.Add(new PdfName("/Fit"));
        return arr;
    }

    /// <summary>
    /// Builds a map of named destination string → 0-based page index from a source document's
    /// /Dests dictionary and /Names /Dests name tree. Used by merge to preserve cross-document
    /// links when imported pages are renumbered.
    /// </summary>
    public static Dictionary<string, int> BuildNamedDestMap(PdfDocument src)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var catalog = src.Internals.Catalog;

            // Legacy flat /Dests dictionary
            var destsDict = catalog.Elements.GetDictionary("/Dests");
            if (destsDict != null)
            {
                foreach (var key in destsDict.Elements.Keys)
                {
                    PdfItem? val = DerefItem(destsDict.Elements[key] ?? new PdfInteger(-1));
                    int? idx = ResolveDestPageIndexInDoc(src, val);
                    if (idx.HasValue) map[key.TrimStart('/')] = idx.Value;
                }
            }

            // Modern /Names /Dests name tree
            var namesDict = catalog.Elements.GetDictionary("/Names");
            var destTree = namesDict?.Elements.GetDictionary("/Dests");
            if (destTree != null)
                WalkNameTree(src, destTree, map);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BuildNamedDestMap: {ex}"); }
        return map;
    }
}
