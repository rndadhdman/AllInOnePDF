using System.Text.Json;
using System.Text.Json.Serialization;
using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.Core.Themes;

/// <summary>
/// A complete visual theme for PDF output: page geometry, typography, colours, element styles, header/footer,
/// cover page and table of contents. Themes are plain JSON so they can be shared like WordPress themes.
/// Every property has a sensible default; the theme builder edits these live.
/// </summary>
public sealed class PdfTheme
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled theme";
    public string? Description { get; set; }
    public string? Author { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Built-in themes are read-only; "Duplicate" creates an editable copy.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Id of the theme this one was duplicated from, if any.</summary>
    public string? BasedOn { get; set; }

    /// <summary>Optional tags for the gallery ("serif", "dark", "technical").</summary>
    public List<string> Tags { get; set; } = [];

    public PageSettings Page { get; set; } = new();
    public TypographySettings Typography { get; set; } = new();
    public HeadingSettings Headings { get; set; } = new();
    public ColorSettings Colors { get; set; } = new();
    public CodeSettings Code { get; set; } = new();
    public TableSettings Tables { get; set; } = new();
    public QuoteSettings Quotes { get; set; } = new();
    public ListSettings Lists { get; set; } = new();
    public LinkSettings Links { get; set; } = new();
    public ImageSettings Images { get; set; } = new();
    public HeaderFooterSettings Header { get; set; } = new() { Enabled = false, Center = "{title}" };
    public HeaderFooterSettings Footer { get; set; } = new() { Enabled = true, Center = "{page} / {pages}" };
    public CoverSettings Cover { get; set; } = new();
    public TocSettings Toc { get; set; } = new();
    /// <summary>Note / tip / important / warning / caution boxes.</summary>
    public CalloutSettings Callouts { get; set; } = new();

    public PdfTheme Clone()
    {
        var json = JsonSerializer.Serialize(this, CoreJson.Options);
        return JsonSerializer.Deserialize<PdfTheme>(json, CoreJson.Options)!;
    }

    /// <summary>Creates an editable copy with a new id and name.</summary>
    public PdfTheme Duplicate(string? newName = null)
    {
        var copy = Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = newName ?? Name + " copy";
        copy.IsBuiltIn = false;
        copy.BasedOn = Id;
        copy.Modified = DateTimeOffset.UtcNow;
        return copy;
    }

    public string ToJson() => JsonSerializer.Serialize(this, CoreJson.Options);

    public static PdfTheme FromJson(string json)
    {
        var theme = JsonSerializer.Deserialize<PdfTheme>(json, CoreJson.Options)
            ?? throw new JsonException("Theme JSON was empty.");
        theme.IsBuiltIn = false;
        return ThemeValidator.Normalize(theme);
    }
}

public enum PageSizeName { A4, Letter, Legal, A5, A3, Tabloid, Executive, B5, Custom }

public enum PageOrientation { Portrait, Landscape }

public sealed class PageSettings
{
    public PageSizeName Size { get; set; } = PageSizeName.A4;
    public PageOrientation Orientation { get; set; } = PageOrientation.Portrait;

    /// <summary>Custom page width in millimetres (used when <see cref="Size"/> is Custom).</summary>
    public double CustomWidthMm { get; set; } = 210;
    public double CustomHeightMm { get; set; } = 297;

    public double MarginTopMm { get; set; } = 22;
    public double MarginBottomMm { get; set; } = 22;
    public double MarginLeftMm { get; set; } = 20;
    public double MarginRightMm { get; set; } = 20;

    /// <summary>Page background colour. Dark themes set this to a dark grey for comfortable on-screen reading.</summary>
    public string Background { get; set; } = "#FFFFFF";

    /// <summary>Returns (width, height) in millimetres honouring size and orientation.</summary>
    public (double WidthMm, double HeightMm) GetSizeMm()
    {
        var (w, h) = Size switch
        {
            PageSizeName.A4 => (210.0, 297.0),
            PageSizeName.Letter => (215.9, 279.4),
            PageSizeName.Legal => (215.9, 355.6),
            PageSizeName.A5 => (148.0, 210.0),
            PageSizeName.A3 => (297.0, 420.0),
            PageSizeName.Tabloid => (279.4, 431.8),
            PageSizeName.Executive => (184.1, 266.7),
            PageSizeName.B5 => (176.0, 250.0),
            _ => (CustomWidthMm, CustomHeightMm),
        };
        return Orientation == PageOrientation.Landscape ? (Math.Max(w, h), Math.Min(w, h)) : (Math.Min(w, h), Math.Max(w, h));
    }
}

public enum TextAlign { Left, Justify, Center, Right }

public sealed class TypographySettings
{
    /// <summary>Body font family. Bundled: "Inter", "Literata", "JetBrains Mono". System fonts are allowed but not portable.</summary>
    public string BodyFont { get; set; } = "Inter";
    public double BodySizePt { get; set; } = 11;
    /// <summary>Line height as a multiple of the font size.</summary>
    public double LineHeight { get; set; } = 1.5;
    /// <summary>Space after each paragraph in points.</summary>
    public double ParagraphSpacingPt { get; set; } = 8;
    /// <summary>First-line indent in points (0 = none). Book-style themes use ~14.</summary>
    public double FirstLineIndentPt { get; set; }
    public TextAlign Alignment { get; set; } = TextAlign.Left;
    /// <summary>Letter spacing in em (0 = normal).</summary>
    public double LetterSpacingEm { get; set; }
    /// <summary>
    /// Inserts soft hyphens into long words of body paragraphs so justified text has fewer rivers.
    /// The layout engine has no dictionary hyphenation; the renderer uses a language-agnostic vowel/consonant rule.
    /// </summary>
    public bool Hyphenate { get; set; }
    /// <summary>Moves a paragraph to the next page rather than leaving a single line at the bottom of a page.</summary>
    public bool OrphanControl { get; set; } = true;
}

public enum FontWeight { Regular, Medium, SemiBold, Bold }

public sealed class HeadingLevelStyle
{
    public double SizePt { get; set; } = 16;
    public double SpaceBeforePt { get; set; } = 18;
    public double SpaceAfterPt { get; set; } = 6;
    public FontWeight Weight { get; set; } = FontWeight.SemiBold;
    public bool Italic { get; set; }
    public bool Uppercase { get; set; }
    /// <summary>Horizontal alignment of the heading text (Justify behaves like Left).</summary>
    public TextAlign Alignment { get; set; } = TextAlign.Left;
    /// <summary>Overrides <see cref="HeadingSettings.Color"/> for this level.</summary>
    public string? Color { get; set; }
    /// <summary>Draws a rule under the heading (common for H1/H2 in technical docs).</summary>
    public bool Underline { get; set; }
    /// <summary>Colour of the <see cref="Underline"/> rule; null uses the accent colour.</summary>
    public string? RuleColor { get; set; }
    /// <summary>Thickness of the <see cref="Underline"/> rule in points.</summary>
    public double RuleWidthPt { get; set; } = 0.75;
    public double LetterSpacingEm { get; set; }
}

public sealed class HeadingSettings
{
    /// <summary>Heading font family; null means "same as body".</summary>
    public string? Font { get; set; }
    public string Color { get; set; } = "#111827";
    public bool NumberHeadings { get; set; }
    /// <summary>Start every level-1 heading on a new page (chapters).</summary>
    public bool PageBreakBeforeH1 { get; set; }
    /// <summary>Line height multiple for headings.</summary>
    public double LineHeight { get; set; } = 1.2;
    /// <summary>Never leave a heading alone at the bottom of a page: it moves to the next page with the start of the following block.</summary>
    public bool KeepWithNext { get; set; } = true;

    public HeadingLevelStyle H1 { get; set; } = new() { SizePt = 28, SpaceBeforePt = 24, SpaceAfterPt = 12, Weight = FontWeight.Bold };
    public HeadingLevelStyle H2 { get; set; } = new() { SizePt = 20, SpaceBeforePt = 20, SpaceAfterPt = 8, Weight = FontWeight.SemiBold };
    public HeadingLevelStyle H3 { get; set; } = new() { SizePt = 16, SpaceBeforePt = 16, SpaceAfterPt = 6, Weight = FontWeight.SemiBold };
    public HeadingLevelStyle H4 { get; set; } = new() { SizePt = 13, SpaceBeforePt = 14, SpaceAfterPt = 4, Weight = FontWeight.SemiBold };
    public HeadingLevelStyle H5 { get; set; } = new() { SizePt = 11.5, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.SemiBold };
    public HeadingLevelStyle H6 { get; set; } = new() { SizePt = 11, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.Medium, Uppercase = true, LetterSpacingEm = 0.04 };

    public HeadingLevelStyle Level(int level) => level switch
    {
        <= 1 => H1,
        2 => H2,
        3 => H3,
        4 => H4,
        5 => H5,
        _ => H6,
    };
}

public sealed class ColorSettings
{
    /// <summary>Brand / accent colour used for rules, bullets, links by default and the cover.</summary>
    public string Accent { get; set; } = "#0F6CBD";
    public string Text { get; set; } = "#1F2937";
    /// <summary>Secondary text: captions, footers, attributions.</summary>
    public string Muted { get; set; } = "#6B7280";
    /// <summary>Panels such as code blocks and table headers.</summary>
    public string Surface { get; set; } = "#F3F4F6";
    public string Border { get; set; } = "#D1D5DB";
}

public enum SyntaxPalette { Auto, Light, Dark, Mono }

public sealed class CodeSettings
{
    public string Font { get; set; } = "JetBrains Mono";
    public double SizePt { get; set; } = 9.5;
    public double LineHeight { get; set; } = 1.45;
    public string? Background { get; set; }
    public string? TextColor { get; set; }
    public string? BorderColor { get; set; }
    public double BorderWidthPt { get; set; } = 0.75;
    public double CornerRadiusPt { get; set; } = 4;
    public double PaddingPt { get; set; } = 10;
    public bool ShowLineNumbers { get; set; }
    public bool SyntaxHighlighting { get; set; } = true;
    public SyntaxPalette Palette { get; set; } = SyntaxPalette.Auto;
    /// <summary>
    /// True: long lines wrap at any character (soft wrap). False: no wrapping; the block's font shrinks (to at most
    /// 75 %) so the longest line fits, and lines still too long are cut with an ellipsis.
    /// </summary>
    public bool WrapLongLines { get; set; } = true;
    /// <summary>Show the language or caption as a small label above the block.</summary>
    public bool ShowLanguageLabel { get; set; }
    public string? InlineBackground { get; set; }
    public string? InlineTextColor { get; set; }
    /// <summary>Inline code size relative to body (1.0 = same).</summary>
    public double InlineScale { get; set; } = 0.9;
}

public enum TableStyle { Grid, Lines, Minimal, Striped }

public sealed class TableSettings
{
    public TableStyle Style { get; set; } = TableStyle.Lines;
    public string? HeaderBackground { get; set; }
    public string? HeaderTextColor { get; set; }
    public FontWeight HeaderWeight { get; set; } = FontWeight.SemiBold;
    public string? BorderColor { get; set; }
    public double BorderWidthPt { get; set; } = 0.75;
    public string? StripeColor { get; set; }
    public double CellPaddingPt { get; set; } = 6;
    /// <summary>Table text size relative to body.</summary>
    public double FontScale { get; set; } = 0.92;
    public bool ShowCaptions { get; set; } = true;
    public bool RepeatHeaderOnNewPage { get; set; } = true;
}

public sealed class QuoteSettings
{
    public string? BorderColor { get; set; }
    public double BorderWidthPt { get; set; } = 3;
    public string? Background { get; set; }
    public string? TextColor { get; set; }
    public bool Italic { get; set; } = true;
    public double PaddingPt { get; set; } = 10;
    /// <summary>Quote text size relative to body.</summary>
    public double FontScale { get; set; } = 1.0;
}

public enum BulletStyle { Disc, Circle, Square, Dash, Arrow }

public enum NumberStyle { Decimal, DecimalParen, LowerAlpha, UpperAlpha, LowerRoman, UpperRoman }

public sealed class ListSettings
{
    public BulletStyle Bullet { get; set; } = BulletStyle.Disc;
    public NumberStyle Numbering { get; set; } = NumberStyle.Decimal;
    public double IndentPt { get; set; } = 18;
    public double ItemSpacingPt { get; set; } = 3;
    public string? MarkerColor { get; set; }
    public bool TaskCheckboxes { get; set; } = true;
}

public sealed class LinkSettings
{
    public string? Color { get; set; }
    public bool Underline { get; set; }
    /// <summary>Print the URL after the link text, for paper readers.</summary>
    public bool ShowUrl { get; set; }
    /// <summary>Bold / weight for links.</summary>
    public FontWeight Weight { get; set; } = FontWeight.Regular;
}

public enum ImageAlign { Left, Center, Right }

public sealed class ImageSettings
{
    public double MaxWidthPercent { get; set; } = 100;
    public ImageAlign Align { get; set; } = ImageAlign.Center;
    public bool ShowCaptions { get; set; } = true;
    public double CaptionSizePt { get; set; } = 9;
    public double CornerRadiusPt { get; set; }
    public bool Border { get; set; }
    public double SpacingPt { get; set; } = 10;
}

public sealed class HeaderFooterSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Templates support {title} {subtitle} {author} {date} {page} {pages} {section} {filename}.
    /// {section} is the first top-level heading starting on the page, else the last one before it (the document title
    /// before the first heading). "Top-level" is the shallowest heading level that occurs more than once.
    /// </summary>
    public string? Left { get; set; }
    public string? Center { get; set; }
    public string? Right { get; set; }
    public double SizePt { get; set; } = 8.5;
    public string? Color { get; set; }
    public bool Rule { get; set; }
    public bool ShowOnFirstPage { get; set; } = true;
    public bool ShowOnCover { get; set; }
    /// <summary>Distance from the content in points.</summary>
    public double SpacingPt { get; set; } = 10;

    public bool HasContent => Enabled && (!string.IsNullOrWhiteSpace(Left) || !string.IsNullOrWhiteSpace(Center) || !string.IsNullOrWhiteSpace(Right));
}

public enum CoverStyle { Minimal, Centered, Banner, Bold, Split }

public sealed class CoverSettings
{
    public bool Enabled { get; set; }
    public CoverStyle Style { get; set; } = CoverStyle.Centered;
    public bool ShowTitle { get; set; } = true;
    public bool ShowSubtitle { get; set; } = true;
    public bool ShowAuthor { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public double TitleSizePt { get; set; } = 40;
    public string? Background { get; set; }
    public string? TitleColor { get; set; }
    public bool AccentBar { get; set; } = true;
    /// <summary>Free text under the title block (edition, confidentiality note ...).</summary>
    public string? Note { get; set; }
    /// <summary>Overrides the document title.</summary>
    public string? TitleOverride { get; set; }
}

/// <summary>Per-kind styling for a callout. Null values fall back to <see cref="CalloutSettings"/> or the theme colours.</summary>
public sealed class CalloutKindStyle
{
    /// <summary>Left bar, icon and label colour; null uses <see cref="ColorSettings.Accent"/>.</summary>
    public string? Accent { get; set; }
    /// <summary>Box background; null uses <see cref="CalloutSettings.Background"/>, then <see cref="ColorSettings.Surface"/>.</summary>
    public string? Background { get; set; }
    /// <summary>Label shown when the callout has no title of its own (e.g. "Hinweis"); null uses the kind name.</summary>
    public string? Label { get; set; }
}

public sealed class CalloutSettings
{
    /// <summary>Show a small round icon before the label.</summary>
    public bool ShowIcon { get; set; }
    /// <summary>Show the label / title line.</summary>
    public bool ShowLabel { get; set; } = true;
    /// <summary>Default background for every kind; null uses <see cref="ColorSettings.Surface"/>.</summary>
    public string? Background { get; set; }
    public double BorderWidthPt { get; set; } = 3;
    public double PaddingPt { get; set; } = 10;
    public double CornerRadiusPt { get; set; }

    public CalloutKindStyle Note { get; set; } = new();
    public CalloutKindStyle Tip { get; set; } = new() { Accent = "#1E8E3E" };
    public CalloutKindStyle Important { get; set; } = new() { Accent = "#7C3AED" };
    public CalloutKindStyle Warning { get; set; } = new() { Accent = "#B45309" };
    public CalloutKindStyle Caution { get; set; } = new() { Accent = "#B91C1C" };

    public CalloutKindStyle Kind(Documents.CalloutKind kind) => kind switch
    {
        Documents.CalloutKind.Tip => Tip,
        Documents.CalloutKind.Important => Important,
        Documents.CalloutKind.Warning => Warning,
        Documents.CalloutKind.Caution => Caution,
        _ => Note,
    };
}

public sealed class TocSettings
{
    public bool Enabled { get; set; }
    public string Title { get; set; } = "Contents";
    public int Depth { get; set; } = 3;
    public bool ShowPageNumbers { get; set; } = true;
    public bool DotLeaders { get; set; } = true;
    public bool PageBreakAfter { get; set; } = true;
    public bool IndentLevels { get; set; } = true;
}
