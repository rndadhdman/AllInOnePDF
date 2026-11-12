namespace AllInOnePdf.Core.Themes;

/// <summary>
/// Themes that ship with the app. They are read-only; users duplicate them to customise.
/// This file holds the original six; <c>BuiltInThemes.Documents.cs</c> adds document styles (GitHub, Swiss, Academic …)
/// and <c>BuiltInThemes.Palettes.cs</c> the editor colour schemes (Solarized, Nord, Dracula, Catppuccin …).
/// </summary>
public static partial class BuiltInThemes
{
    public const string DefaultId = "clean";

    /// <summary>Built-ins carry a fixed timestamp so caches keyed on id + Modified stay stable.</summary>
    private static readonly DateTimeOffset Stamp = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    private static PdfTheme Create(string id, string name, string description, params string[] tags) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Author = "AllInOne PDF",
        IsBuiltIn = true,
        Modified = Stamp,
        Tags = [.. tags],
    };

    public static PdfTheme Clean() => new()
    {
        Id = "clean",
        Name = "Clean",
        Description = "Inter at 11 pt on A4, 1.5 line spacing and space between paragraphs. For notes, documents and reports.",
        Author = "AllInOne PDF",
        IsBuiltIn = true,
        Modified = Stamp,
        Tags = ["sans", "light"],
        Callouts = new CalloutSettings { ShowIcon = true },
        Images = new ImageSettings { CornerRadiusPt = 4 },
    };

    public static PdfTheme Manuscript()
    {
        var t = new PdfTheme
        {
            Id = "manuscript",
            Name = "Manuscript",
            Description = "A5 pages, justified Literata with first-line indents and hyphenation, a cover, a contents page and each level-1 heading on a new page.",
            Author = "AllInOne PDF",
            IsBuiltIn = true,
            Modified = Stamp,
            Tags = ["serif", "book", "light"],
        };
        t.Page.Size = PageSizeName.A5;
        t.Page.MarginTopMm = 20;
        t.Page.MarginBottomMm = 22;
        t.Page.MarginLeftMm = 18;
        t.Page.MarginRightMm = 18;
        t.Typography.BodyFont = "Literata";
        t.Typography.BodySizePt = 10.5;
        t.Typography.LineHeight = 1.55;
        t.Typography.ParagraphSpacingPt = 0;
        t.Typography.FirstLineIndentPt = 14;
        t.Typography.Alignment = TextAlign.Justify;
        t.Typography.Hyphenate = true;
        t.Headings.Font = "Literata";
        t.Headings.Color = "#1C1917";
        t.Headings.PageBreakBeforeH1 = true;
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 26, SpaceBeforePt = 60, SpaceAfterPt = 28, Weight = FontWeight.Medium };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 16, SpaceBeforePt = 22, SpaceAfterPt = 10, Weight = FontWeight.SemiBold };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 12.5, SpaceBeforePt = 16, SpaceAfterPt = 6, Weight = FontWeight.SemiBold, Italic = true };
        t.Colors.Accent = "#7C2D12";
        t.Colors.Text = "#1C1917";
        t.Colors.Muted = "#78716C";
        t.Colors.Surface = "#F5F5F4";
        t.Colors.Border = "#D6D3D1";
        t.Lists.Bullet = BulletStyle.Dash;
        t.Quotes.Italic = true;
        t.Quotes.BorderWidthPt = 0;
        t.Footer.Center = "{page}";
        t.Footer.SizePt = 9;
        t.Cover.Enabled = true;
        t.Cover.Style = CoverStyle.Centered;
        t.Cover.TitleSizePt = 34;
        t.Cover.AccentBar = false;
        t.Toc.Enabled = true;
        t.Toc.Depth = 2;
        t.Callouts.BorderWidthPt = 2;
        t.Callouts.Tip.Accent = "#3F6212";
        t.Callouts.Important.Accent = "#6B21A8";
        t.Callouts.Warning.Accent = "#A16207";
        t.Callouts.Caution.Accent = "#991B1B";
        return t;
    }

    public static PdfTheme Technical()
    {
        var t = new PdfTheme
        {
            Id = "technical",
            Name = "Technical",
            Description = "Letter pages, numbered headings, a contents page, line-numbered code, grid tables and the section name in the header.",
            Author = "AllInOne PDF",
            IsBuiltIn = true,
            Modified = Stamp,
            Tags = ["sans", "code", "numbered", "letter", "light"],
        };
        t.Page.Size = PageSizeName.Letter;
        t.Typography.BodySizePt = 10.5;
        t.Typography.LineHeight = 1.45;
        t.Headings.NumberHeadings = true;
        t.Headings.Color = "#0B3D6B";
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 24, SpaceBeforePt = 26, SpaceAfterPt = 12, Weight = FontWeight.Bold, Underline = true };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 17, SpaceBeforePt = 20, SpaceAfterPt = 8, Weight = FontWeight.SemiBold };
        t.Colors.Accent = "#0B5CAD";
        t.Code.ShowLineNumbers = true;
        t.Code.ShowLanguageLabel = true;
        t.Code.SizePt = 9;
        t.Tables.Style = TableStyle.Grid;
        t.Tables.HeaderBackground = "#E8EFF7";
        t.Header.Enabled = true;
        t.Header.Left = "{title}";
        t.Header.Right = "{section}";
        t.Header.Center = null;
        t.Header.Rule = true;
        t.Footer.Left = "{date}";
        t.Footer.Center = null;
        t.Footer.Right = "Page {page} of {pages}";
        t.Footer.Rule = true;
        t.Cover.Enabled = true;
        t.Cover.Style = CoverStyle.Banner;
        t.Toc.Enabled = true;
        t.Toc.Depth = 3;
        t.Headings.H1.RuleColor = "#0B5CAD";
        t.Headings.H1.RuleWidthPt = 1.25;
        t.Callouts.ShowIcon = true;
        t.Images.Border = true;
        return t;
    }

    public static PdfTheme Night()
    {
        var t = new PdfTheme
        {
            Id = "night",
            Name = "Night",
            Description = "Dark grey page, light grey text at 11.5 pt and blue links, for reading on screen.",
            Author = "AllInOne PDF",
            IsBuiltIn = true,
            Modified = Stamp,
            Tags = ["dark", "screen", "sans"],
        };
        t.Page.Background = "#1B1E24";
        t.Typography.BodySizePt = 11.5;
        t.Typography.LineHeight = 1.6;
        t.Headings.Color = "#E6E8EC";
        t.Colors.Accent = "#7FB4FF";
        t.Colors.Text = "#C9CDD4";
        t.Colors.Muted = "#8B919C";
        t.Colors.Surface = "#242830";
        t.Colors.Border = "#343943";
        t.Code.Background = "#14161B";
        t.Code.TextColor = "#D7DBE2";
        t.Code.Palette = SyntaxPalette.Dark;
        t.Code.InlineBackground = "#2A2F38";
        t.Tables.HeaderBackground = "#242830";
        t.Tables.Style = TableStyle.Striped;
        t.Tables.StripeColor = "#1F2329";
        t.Quotes.Background = "#20242B";
        t.Quotes.BorderColor = "#7FB4FF";
        t.Links.Color = "#9CC4FF";
        t.Footer.Color = "#6B7280";
        t.Callouts.ShowIcon = true;
        t.Callouts.Tip.Accent = "#6CCB8A";
        t.Callouts.Important.Accent = "#B794F6";
        t.Callouts.Warning.Accent = "#E3A857";
        t.Callouts.Caution.Accent = "#F28B82";
        t.Images.CornerRadiusPt = 4;
        return t;
    }

    public static PdfTheme Compact()
    {
        var t = new PdfTheme
        {
            Id = "compact",
            Name = "Compact",
            Description = "14 mm margins, 9.5 pt text and tight spacing between lines, paragraphs and list items.",
            Author = "AllInOne PDF",
            IsBuiltIn = true,
            Modified = Stamp,
            Tags = ["sans", "dense", "light"],
        };
        t.Page.MarginTopMm = 14;
        t.Page.MarginBottomMm = 14;
        t.Page.MarginLeftMm = 14;
        t.Page.MarginRightMm = 14;
        t.Typography.BodySizePt = 9.5;
        t.Typography.LineHeight = 1.35;
        t.Typography.ParagraphSpacingPt = 5;
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 20, SpaceBeforePt = 14, SpaceAfterPt = 6, Weight = FontWeight.Bold };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 15, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.SemiBold };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 12, SpaceBeforePt = 10, SpaceAfterPt = 3, Weight = FontWeight.SemiBold };
        t.Code.SizePt = 8.5;
        t.Code.PaddingPt = 6;
        t.Tables.CellPaddingPt = 3.5;
        t.Lists.ItemSpacingPt = 1.5;
        t.Images.MaxWidthPercent = 80;
        t.Footer.SizePt = 8;
        t.Callouts.PaddingPt = 7;
        t.Callouts.BorderWidthPt = 2.5;
        t.Toc.PageBreakAfter = false;
        return t;
    }

    public static PdfTheme Sepia()
    {
        var t = new PdfTheme
        {
            Id = "sepia",
            Name = "Sepia",
            Description = "Cream page, brown text and Literata at 11 pt with 1.6 line spacing.",
            Author = "AllInOne PDF",
            IsBuiltIn = true,
            Modified = Stamp,
            Tags = ["serif", "warm", "screen", "light"],
        };
        t.Page.Background = "#F7F1E3";
        t.Typography.BodyFont = "Literata";
        t.Typography.BodySizePt = 11;
        t.Typography.LineHeight = 1.6;
        t.Headings.Font = "Literata";
        t.Headings.Color = "#3B2F1E";
        t.Colors.Accent = "#9A6B2F";
        t.Colors.Text = "#3F3426";
        t.Colors.Muted = "#8A7A62";
        t.Colors.Surface = "#EFE6D2";
        t.Colors.Border = "#D9CDB4";
        t.Code.Background = "#EFE6D2";
        t.Quotes.Italic = true;
        t.Lists.Bullet = BulletStyle.Circle;
        t.Callouts.ShowIcon = true;
        t.Callouts.Tip.Accent = "#4D7C0F";
        t.Callouts.Important.Accent = "#7E3F8F";
        t.Callouts.Warning.Accent = "#B45309";
        t.Callouts.Caution.Accent = "#A1281F";
        t.Images.CornerRadiusPt = 3;
        return t;
    }

    /// <summary>Every built-in, in gallery order: everyday document styles, then warm and pastel papers, then dark screens.</summary>
    public static IReadOnlyList<PdfTheme> All() =>
    [
        // Documents (white paper)
        Clean(), Manuscript(), Technical(), Compact(), GitHub(), Swiss(), Academic(), Tufte(), Newsprint(), Contract(),
        Handout(), Typewriter(), Print(), LargePrint(),
        // Tinted papers
        Sepia(), SolarizedLight(), CatppuccinLatte(), GruvboxLight(), RosePineDawn(),
        // Dark screens
        Night(), GitHubDark(), SolarizedDark(), Nord(), Dracula(), CatppuccinMocha(), GruvboxDark(), TokyoNight(), Everforest(),
    ];

    public static PdfTheme? Find(string id) => All().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}
