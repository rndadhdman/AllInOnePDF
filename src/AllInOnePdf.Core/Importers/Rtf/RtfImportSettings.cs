using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.Core.Importers.Rtf;

/// <summary>Typed view of the <see cref="RtfImporter"/> options.</summary>
internal sealed record RtfImportSettings(
    bool DetectHeadings,
    bool DetectLists,
    bool DetectCode,
    bool PageBreakMarkers,
    bool Footnotes,
    bool ExtractImages,
    int MaxImageMb)
{
    public long MaxImageBytes => (long)MaxImageMb * 1024 * 1024;

    public static RtfImportSettings From(OptionValues values) => new(
        values.GetBool(RtfImporter.OptDetectHeadings, true),
        values.GetBool(RtfImporter.OptDetectLists, true),
        values.GetBool(RtfImporter.OptDetectCode, true),
        !string.Equals(values.GetString(RtfImporter.OptPageBreaks, "marker"), "none", StringComparison.OrdinalIgnoreCase),
        values.GetBool(RtfImporter.OptFootnotes, true),
        values.GetBool(RtfImporter.OptExtractImages, true),
        Math.Clamp(values.GetInt(RtfImporter.OptMaxImageMb, 25), 1, 1024));
}
