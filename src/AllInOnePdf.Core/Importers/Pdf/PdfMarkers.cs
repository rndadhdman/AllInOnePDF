using System.Globalization;
using AllInOnePdf.Core.Rendering;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>A named destination the app's PDF renderer writes as an invisible marker (page and top-left point).</summary>
internal readonly record struct PdfMarker(string Name, int Page, double X, double Y);

/// <summary>
/// The invisible markers the app's own PDF renderer leaves in its output as named destinations (they draw nothing):
/// <c>aio-cover</c> on the cover page and <c>aio-code-&lt;n&gt;-&lt;language&gt;</c> at the top of each code block
/// with a language. Read from the catalog's <c>/Dests</c> dictionary and the <c>/Names /Dests</c> name tree; PDFs
/// from other writers simply have none.
/// </summary>
internal sealed class PdfMarkers
{
    /// <summary>Prefix of every marker name.</summary>
    public const string Prefix = "aio-";

    /// <summary>Marker on the cover page.</summary>
    public const string CoverName = ThemedComposer.CoverMarker;

    /// <summary>Prefix of a code block marker; the block number and the language follow.</summary>
    public const string CodePrefix = ThemedComposer.CodeMarkerPrefix;

    private const int MaxNodes = 10_000;

    private readonly List<PdfMarker> _markers;
    private readonly HashSet<PdfMarker> _used = [];

    public PdfMarkers(IEnumerable<PdfMarker> markers)
    {
        _markers = markers.ToList();
    }

    /// <summary>No markers (a PDF from another writer).</summary>
    public static PdfMarkers Empty { get; } = new([]);

    public IReadOnlyList<PdfMarker> All => _markers;

    /// <summary>True when the page carries the cover marker.</summary>
    public bool IsCover(int page) => _markers.Any(m => m.Page == page && m.Name == CoverName);

    /// <summary>
    /// The language of the code block whose first line's top is <paramref name="top"/> on <paramref name="page"/>:
    /// the nearest unused code marker at or just above it (the marker sits at the block's top-left, above the
    /// padding and any label). Each marker is used once.
    /// </summary>
    public string? TakeCodeLanguage(int page, double top, double size)
    {
        PdfMarker? best = null;
        foreach (var m in _markers)
        {
            if (m.Page != page || !m.Name.StartsWith(CodePrefix, StringComparison.Ordinal) || _used.Contains(m))
            {
                continue;
            }

            var above = m.Y - top;
            if (above < -2 || above > Math.Max(40, size * 5))
            {
                continue;
            }

            if (best is null || above < best.Value.Y - top)
            {
                best = m;
            }
        }

        if (best is not { } found)
        {
            return null;
        }

        _used.Add(found);
        return LanguageOf(found.Name);
    }

    /// <summary>The language part of <c>aio-code-&lt;n&gt;-&lt;language&gt;</c>, or null.</summary>
    internal static string? LanguageOf(string name)
    {
        if (!name.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = name[CodePrefix.Length..];
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || dash == rest.Length - 1 || !int.TryParse(rest[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        var language = rest[(dash + 1)..];
        return language.Length <= 32 && language.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '#' or '.' or '_' or '-') ? language : null;
    }

    /// <summary>Reads the app's markers; any structural problem yields no markers rather than an error.</summary>
    public static PdfMarkers Read(PdfDocument pdf)
    {
        try
        {
            var catalog = pdf.Structure.Catalog.CatalogDictionary;
            var pages = PageReferences(pdf, catalog);
            if (pages.Count == 0)
            {
                return Empty;
            }

            var result = new List<PdfMarker>();
            if (Resolve(pdf, catalog, "Dests") is DictionaryToken dests)
            {
                foreach (var (name, value) in dests.Data)
                {
                    Add(pdf, pages, name, value, result);
                }
            }

            if (Resolve(pdf, catalog, "Names") is DictionaryToken names && Resolve(pdf, names, "Dests") is DictionaryToken tree)
            {
                var budget = MaxNodes;
                WalkNameTree(pdf, pages, tree, result, ref budget);
            }

            return new PdfMarkers(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return Empty;
        }
    }

    private static void Add(PdfDocument pdf, Dictionary<IndirectReference, int> pages, string name, IToken value, List<PdfMarker> result)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return;
        }

        var dest = Resolve(pdf, value);
        if (dest is DictionaryToken dict)
        {
            dest = Resolve(pdf, dict, "D");
        }

        if (dest is not ArrayToken { Length: >= 4 } array || array[0] is not IndirectReferenceToken pageRef || !pages.TryGetValue(pageRef.Data, out var page))
        {
            return;
        }

        var x = Resolve(pdf, array[2]) is NumericToken nx ? nx.Double : 0;
        var y = Resolve(pdf, array[3]) is NumericToken ny ? ny.Double : 0;
        result.Add(new PdfMarker(name, page, x, y));
    }

    private static void WalkNameTree(PdfDocument pdf, Dictionary<IndirectReference, int> pages, DictionaryToken node, List<PdfMarker> result, ref int budget)
    {
        if (--budget < 0)
        {
            return;
        }

        if (Resolve(pdf, node, "Names") is ArrayToken names)
        {
            for (var i = 0; i + 1 < names.Length; i += 2)
            {
                if (Resolve(pdf, names[i]) is StringToken key)
                {
                    Add(pdf, pages, key.Data, names[i + 1], result);
                }
            }
        }

        if (Resolve(pdf, node, "Kids") is ArrayToken kids)
        {
            foreach (var kid in kids.Data)
            {
                if (Resolve(pdf, kid) is DictionaryToken child)
                {
                    WalkNameTree(pdf, pages, child, result, ref budget);
                }
            }
        }
    }

    /// <summary>Page object references in document order (page tree walk) mapped to 1-based page numbers.</summary>
    private static Dictionary<IndirectReference, int> PageReferences(PdfDocument pdf, DictionaryToken catalog)
    {
        var result = new Dictionary<IndirectReference, int>();
        if (!catalog.Data.TryGetValue("Pages", out var root))
        {
            return result;
        }

        var budget = MaxNodes;
        var visited = new HashSet<IndirectReference>();

        void Visit(IToken token)
        {
            if (--budget < 0 || token is not IndirectReferenceToken reference || !visited.Add(reference.Data))
            {
                return;
            }

            if (Resolve(pdf, token) is not DictionaryToken dict)
            {
                return;
            }

            if (Resolve(pdf, dict, "Kids") is ArrayToken kids)
            {
                foreach (var kid in kids.Data)
                {
                    Visit(kid);
                }
            }
            else
            {
                result[reference.Data] = result.Count + 1;
            }
        }

        Visit(root);
        return result;
    }

    private static IToken? Resolve(PdfDocument pdf, DictionaryToken dict, string key) =>
        dict.Data.TryGetValue(key, out var token) ? Resolve(pdf, token) : null;

    private static IToken? Resolve(PdfDocument pdf, IToken? token)
    {
        for (var depth = 0; depth < 8 && token is IndirectReferenceToken reference; depth++)
        {
            token = pdf.Structure.GetObject(reference.Data)?.Data;
        }

        return token;
    }
}
