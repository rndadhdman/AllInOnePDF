using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.Core.Exporters.Pptx;

internal enum PptxSplitMode { H1, H2, PageBreak }

/// <summary>Typed view of the <see cref="PptxExporter"/> options.</summary>
internal sealed record PptxExportSettings(PptxSplitMode Split, bool? TitleSlide, int MaxLines, bool Widescreen, bool Metadata)
{
    public static PptxExportSettings From(OptionValues values) => new(
        values.GetString(PptxExporter.OptSplitOn, "h1")?.ToLowerInvariant() switch
        {
            "h2" => PptxSplitMode.H2,
            "pagebreak" => PptxSplitMode.PageBreak,
            _ => PptxSplitMode.H1,
        },
        values.GetString(PptxExporter.OptTitleSlide, "yes")?.ToLowerInvariant() switch
        {
            "yes" or "true" => true,
            "no" or "false" => false,
            _ => null,
        },
        Math.Clamp(values.GetInt(PptxExporter.OptMaxLines, 12), 6, 30),
        !string.Equals(values.GetString(PptxExporter.OptAspect, "16:9"), "4:3", StringComparison.Ordinal),
        values.GetBool(PptxExporter.OptMetadata, true));
}

/// <summary>Slide geometry in EMU and the derived text sizes.</summary>
internal sealed class PptxGeometry
{
    public const long MarginX = 457200;
    public const long TitleTop = 228600;
    public const long TitleHeight = 1005840;
    public const long ContentTop = 1371600;
    public const long Gap = 91440;
    private const long ReferenceContentWidth = 12192000 - 2 * MarginX;

    public PptxGeometry(bool widescreen, int maxLines, double themeBodyPt)
    {
        SlideWidth = widescreen ? 12192000 : 9144000;
        SlideHeight = 6858000;
        MaxLines = maxLines;
        var linePt = ContentHeight / 12700.0 / maxLines;
        BodyPt = Math.Round(Math.Clamp(Math.Min(Math.Round(themeBodyPt * 1.8), linePt / 1.35), 10, 28) * 2) / 2;
    }

    public long SlideWidth { get; }

    public long SlideHeight { get; }

    public int MaxLines { get; }

    public double BodyPt { get; }

    public double CodePt => Math.Max(8, Math.Round(BodyPt * 0.8 * 2) / 2);

    public double TablePt => Math.Max(8, Math.Round(BodyPt * 0.8 * 2) / 2);

    public double CaptionPt => Math.Max(8, Math.Round(BodyPt * 0.7 * 2) / 2);

    public long ContentBottom => SlideHeight - 320040;

    public long ContentWidth => SlideWidth - 2 * MarginX;

    public long ContentHeight => ContentBottom - ContentTop;

    /// <summary>Height of one estimated body line.</summary>
    public long LineEmu => ContentHeight / MaxLines;

    /// <summary>Approximate characters per body line (95 on a 16:9 slide).</summary>
    public double CharsPerLine => 95.0 * ContentWidth / ReferenceContentWidth;
}
