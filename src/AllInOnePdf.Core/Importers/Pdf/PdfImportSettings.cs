using System.Globalization;
using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.Core.Importers.Pdf;

internal enum HeadingSensitivity { Low, Normal, High }

/// <summary>Typed view of the PDF importer's option bag.</summary>
internal sealed class PdfImportSettings
{
    public string? Pages { get; init; }
    public bool UseBookmarks { get; init; } = true;
    public bool DetectHeadings { get; init; } = true;
    public HeadingSensitivity HeadingSensitivity { get; init; } = HeadingSensitivity.Normal;
    public bool DetectLists { get; init; } = true;
    public bool DetectCode { get; init; } = true;
    public bool DetectTables { get; init; } = true;
    public bool ExtractImages { get; init; } = true;
    public int MinImageSizePx { get; init; } = 48;
    public bool RemoveHeadersFooters { get; init; } = true;
    public bool SkipTableOfContents { get; init; } = true;
    public bool MergeHyphenation { get; init; } = true;
    public bool StripHeadingNumbers { get; init; }
    public bool PageBreakMarkers { get; init; }
    public bool SimpleLayout { get; init; }
    public string? Password { get; init; }

    /// <summary>Lists, code, tables and callouts need block segmentation; the simple layout turns them off.</summary>
    public bool StructureEnabled => !SimpleLayout;

    public static PdfImportSettings From(OptionValues options) => new()
    {
        Pages = options.GetString(PdfImporter.OptPages),
        UseBookmarks = options.GetBool(PdfImporter.OptUseBookmarks, true),
        DetectHeadings = options.GetBool(PdfImporter.OptDetectHeadings, true),
        HeadingSensitivity = (options.GetString(PdfImporter.OptHeadingSensitivity) ?? "normal").ToLowerInvariant() switch
        {
            "low" => HeadingSensitivity.Low,
            "high" => HeadingSensitivity.High,
            _ => HeadingSensitivity.Normal,
        },
        DetectLists = options.GetBool(PdfImporter.OptDetectLists, true),
        DetectCode = options.GetBool(PdfImporter.OptDetectCode, true),
        DetectTables = options.GetBool(PdfImporter.OptDetectTables, true),
        ExtractImages = options.GetBool(PdfImporter.OptExtractImages, true),
        MinImageSizePx = Math.Clamp(options.GetInt(PdfImporter.OptMinImageSize, 48), 1, 10_000),
        RemoveHeadersFooters = options.GetBool(PdfImporter.OptRemoveHeadersFooters, true),
        SkipTableOfContents = options.GetBool(PdfImporter.OptSkipToc, true),
        MergeHyphenation = options.GetBool(PdfImporter.OptMergeHyphenation, true),
        StripHeadingNumbers = options.GetBool(PdfImporter.OptStripHeadingNumbers, false),
        PageBreakMarkers = string.Equals(options.GetString(PdfImporter.OptPageBreaks), "marker", StringComparison.OrdinalIgnoreCase),
        SimpleLayout = string.Equals(options.GetString(PdfImporter.OptLayout), "simple", StringComparison.OrdinalIgnoreCase),
        Password = options.GetString(PdfImporter.OptPassword),
    };
}

/// <summary>Parses page ranges such as "1-3,7", "5-" or "-2" (1-based, inclusive).</summary>
internal static class PageRangeParser
{
    /// <summary>Returns the selected page numbers in ascending order; all pages when the text is empty.</summary>
    /// <exception cref="ArgumentException">The text is malformed or selects no existing page.</exception>
    public static IReadOnlyList<int> Parse(string? text, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Enumerable.Range(1, pageCount).ToList();
        }

        var selected = new SortedSet<int>();
        foreach (var raw in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = raw.IndexOfAny(['-', '–', '—']);
            int from, to;
            if (dash < 0)
            {
                from = to = ParseNumber(raw, text);
            }
            else
            {
                var left = raw[..dash].Trim();
                var right = raw[(dash + 1)..].Trim();
                from = left.Length == 0 ? 1 : ParseNumber(left, text);
                to = right.Length == 0 ? pageCount : ParseNumber(right, text);
            }

            if (from > to)
            {
                (from, to) = (to, from);
            }

            for (var p = Math.Max(1, from); p <= Math.Min(pageCount, to); p++)
            {
                selected.Add(p);
            }
        }

        if (selected.Count == 0)
        {
            throw new ArgumentException($"The page range \"{text}\" selects no pages; the PDF has {pageCount} page{(pageCount == 1 ? "" : "s")}.");
        }

        return selected.ToList();
    }

    private static int ParseNumber(string value, string whole)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1)
        {
            return n;
        }

        throw new ArgumentException($"The page range \"{whole}\" is not valid. Use page numbers and ranges such as 1-3,7.");
    }
}
