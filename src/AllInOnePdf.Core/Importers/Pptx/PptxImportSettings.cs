using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.Core.Importers.Pptx;

/// <summary>What goes between two slides in the document.</summary>
internal enum PptxSlideSeparator
{
    /// <summary>A thematic break (<c>---</c> in Markdown).</summary>
    Rule,

    /// <summary>A page break: every slide starts on a new page.</summary>
    PageBreak,

    /// <summary>Nothing: the slides run together.</summary>
    None,
}

/// <summary>Typed view of the <see cref="PptxImporter"/> options.</summary>
internal sealed record PptxImportSettings(
    PptxSlideSeparator Separator,
    bool IncludeHidden,
    bool ReadingOrderByPosition,
    int HeadingLevel,
    bool NumberUntitledSlides,
    bool DetectCode,
    bool SpeakerNotes,
    bool ExtractImages,
    int MaxImageMb)
{
    public long MaxImageBytes => (long)MaxImageMb * 1024 * 1024;

    public static PptxImportSettings From(OptionValues values) => new(
        (values.GetString(PptxImporter.OptPageBreaks, "rule") ?? "rule").ToLowerInvariant() switch
        {
            "none" => PptxSlideSeparator.None,
            "marker" => PptxSlideSeparator.PageBreak,
            _ => PptxSlideSeparator.Rule,
        },
        values.GetBool(PptxImporter.OptIncludeHidden, false),
        !string.Equals(values.GetString(PptxImporter.OptReadingOrder, "position"), "xml", StringComparison.OrdinalIgnoreCase),
        Math.Clamp(values.GetInt(PptxImporter.OptHeadingLevel, 1), 1, 6),
        !string.Equals(values.GetString(PptxImporter.OptUntitledSlides, "number"), "none", StringComparison.OrdinalIgnoreCase),
        values.GetBool(PptxImporter.OptDetectCode, true),
        values.GetBool(PptxImporter.OptSpeakerNotes, false),
        values.GetBool(PptxImporter.OptExtractImages, true),
        Math.Clamp(values.GetInt(PptxImporter.OptMaxImageMb, 25), 1, 1024));
}
