using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.Core.Importers.Docx;

/// <summary>Typed view of the <see cref="DocxImporter"/> options.</summary>
internal sealed record DocxImportSettings(
    bool DetectCode,
    bool PageBreakMarkers,
    bool ExtractImages,
    bool IncludeHeadersFooters,
    bool Footnotes,
    bool SkipToc,
    int MaxImageMb)
{
    public long MaxImageBytes => (long)MaxImageMb * 1024 * 1024;

    public static DocxImportSettings From(OptionValues values) => new(
        values.GetBool(DocxImporter.OptDetectCode, true),
        !string.Equals(values.GetString(DocxImporter.OptPageBreaks, "marker"), "none", StringComparison.OrdinalIgnoreCase),
        values.GetBool(DocxImporter.OptExtractImages, true),
        values.GetBool(DocxImporter.OptIncludeHeadersFooters, false),
        values.GetBool(DocxImporter.OptFootnotes, true),
        values.GetBool(DocxImporter.OptSkipToc, true),
        Math.Clamp(values.GetInt(DocxImporter.OptMaxImageMb, 25), 1, 1024));
}
