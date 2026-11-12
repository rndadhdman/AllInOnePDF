namespace AllInOnePdf.Core.Themes;

/// <summary>
/// Document styles modelled on well-known layouts: GitHub's Markdown rendering, Swiss typography, APA papers,
/// Tufte handouts, newsprint, contracts, landscape handouts, typewriter drafts, ink-saving print and large print.
/// </summary>
public static partial class BuiltInThemes
{
    /// <summary>The look of a README on github.com: system sans, ruled H1/H2, grey code panels, blue links.</summary>
    public static PdfTheme GitHub()
    {
        var t = Create("github", "GitHub",
            "Like a README on GitHub: grey rules under level-1 and level-2 headings, grey code panels, blue links and coloured alert boxes.",
            "sans", "light", "markdown", "code");
        t.Typography.BodySizePt = 10.5;
        t.Typography.LineHeight = 1.5;
        t.Typography.ParagraphSpacingPt = 10;
        t.Headings.Color = "#1F2328";
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 22, SpaceBeforePt = 22, SpaceAfterPt = 12, Weight = FontWeight.SemiBold, Underline = true, RuleColor = "#D1D9E0", RuleWidthPt = 1 };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 17, SpaceBeforePt = 20, SpaceAfterPt = 10, Weight = FontWeight.SemiBold, Underline = true, RuleColor = "#D1D9E0", RuleWidthPt = 1 };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 13.5, SpaceBeforePt = 16, SpaceAfterPt = 6, Weight = FontWeight.SemiBold };
        t.Headings.H4 = new HeadingLevelStyle { SizePt = 11, SpaceBeforePt = 14, SpaceAfterPt = 4, Weight = FontWeight.SemiBold };
        t.Headings.H5 = new HeadingLevelStyle { SizePt = 10, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.SemiBold };
        t.Headings.H6 = new HeadingLevelStyle { SizePt = 9.5, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.SemiBold, Color = "#59636E" };
        t.Colors.Accent = "#0969DA";
        t.Colors.Text = "#1F2328";
        t.Colors.Muted = "#59636E";
        t.Colors.Surface = "#F6F8FA";
        t.Colors.Border = "#D1D9E0";
        t.Code.Background = "#F6F8FA";
        t.Code.BorderWidthPt = 0;
        t.Code.CornerRadiusPt = 6;
        t.Code.InlineBackground = "#EFF1F3";
        t.Code.InlineScale = 0.85;
        t.Code.Palette = SyntaxPalette.Light;
        t.Tables.Style = TableStyle.Grid;
        t.Tables.BorderColor = "#D1D9E0";
        t.Tables.StripeColor = "#F6F8FA";
        t.Quotes.BorderColor = "#D1D9E0";
        t.Quotes.BorderWidthPt = 3;
        t.Quotes.TextColor = "#59636E";
        t.Quotes.Italic = false;
        t.Links.Color = "#0969DA";
        t.Footer.Color = "#59636E";
        t.Callouts.ShowIcon = true;
        t.Callouts.Background = "#FFFFFF";
        t.Callouts.Note.Accent = "#0969DA";
        t.Callouts.Tip.Accent = "#1A7F37";
        t.Callouts.Important.Accent = "#8250DF";
        t.Callouts.Warning.Accent = "#9A6700";
        t.Callouts.Caution.Accent = "#CF222E";
        return t;
    }

    /// <summary>International Typographic Style: big bold sans headings, a red rule, asymmetric margins, square bullets.</summary>
    public static PdfTheme Swiss()
    {
        var t = Create("swiss", "Swiss",
            "Large bold sans headings, a thick red rule under level-1 headings, red square bullets and a wider left margin.",
            "sans", "light", "bold");
        t.Page.MarginTopMm = 24;
        t.Page.MarginBottomMm = 24;
        t.Page.MarginLeftMm = 30;
        t.Page.MarginRightMm = 18;
        t.Typography.BodySizePt = 10.5;
        t.Typography.LineHeight = 1.45;
        t.Typography.ParagraphSpacingPt = 9;
        t.Headings.Color = "#111111";
        t.Headings.LineHeight = 1.1;
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 34, SpaceBeforePt = 36, SpaceAfterPt = 18, Weight = FontWeight.Bold, LetterSpacingEm = -0.02, Underline = true, RuleColor = "#D62828", RuleWidthPt = 3 };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 20, SpaceBeforePt = 24, SpaceAfterPt = 10, Weight = FontWeight.Bold, LetterSpacingEm = -0.01 };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 14, SpaceBeforePt = 18, SpaceAfterPt = 6, Weight = FontWeight.SemiBold };
        t.Headings.H4 = new HeadingLevelStyle { SizePt = 11, SpaceBeforePt = 14, SpaceAfterPt = 4, Weight = FontWeight.SemiBold, Uppercase = true, LetterSpacingEm = 0.06 };
        t.Headings.H5 = new HeadingLevelStyle { SizePt = 10.5, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.SemiBold };
        t.Headings.H6 = new HeadingLevelStyle { SizePt = 9.5, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.Medium, Uppercase = true, LetterSpacingEm = 0.08 };
        t.Colors.Accent = "#D62828";
        t.Colors.Text = "#111111";
        t.Colors.Muted = "#6B6B6B";
        t.Colors.Surface = "#F2F2F2";
        t.Colors.Border = "#CCCCCC";
        t.Lists.Bullet = BulletStyle.Square;
        t.Lists.MarkerColor = "#D62828";
        t.Lists.IndentPt = 16;
        t.Tables.Style = TableStyle.Lines;
        t.Tables.BorderColor = "#111111";
        t.Tables.BorderWidthPt = 1;
        t.Tables.HeaderWeight = FontWeight.Bold;
        t.Tables.FontScale = 0.9;
        t.Quotes.BorderColor = "#111111";
        t.Quotes.BorderWidthPt = 2;
        t.Quotes.Italic = false;
        t.Quotes.FontScale = 1.15;
        t.Links.Color = "#D62828";
        t.Links.Weight = FontWeight.Medium;
        t.Code.Background = "#F2F2F2";
        t.Code.BorderWidthPt = 0;
        t.Code.CornerRadiusPt = 0;
        t.Code.Palette = SyntaxPalette.Light;
        t.Header.Enabled = true;
        t.Header.Left = "{title}";
        t.Header.Center = null;
        t.Header.Right = "{section}";
        t.Header.Rule = true;
        t.Header.SizePt = 8;
        t.Footer.Left = "{date}";
        t.Footer.Center = null;
        t.Footer.Right = "{page}";
        t.Footer.SizePt = 8;
        t.Cover.Enabled = true;
        t.Cover.Style = CoverStyle.Bold;
        t.Cover.TitleSizePt = 56;
        t.Images.Align = ImageAlign.Left;
        t.Callouts.BorderWidthPt = 4;
        t.Callouts.Background = "#F2F2F2";
        t.Callouts.Note.Accent = "#111111";
        t.Callouts.Tip.Accent = "#2E7D32";
        t.Callouts.Important.Accent = "#5E35B1";
        t.Callouts.Warning.Accent = "#EF6C00";
        t.Callouts.Caution.Accent = "#D62828";
        return t;
    }

    /// <summary>APA-style student paper: Letter, one-inch margins, 12 pt serif, double spacing, page number top right.</summary>
    public static PdfTheme Academic()
    {
        var t = Create("academic", "Academic",
            "APA-style paper: Letter, one-inch margins, 12 pt serif, double spacing, half-inch first-line indents and the page number top right.",
            "serif", "light", "academic", "letter", "double-spaced");
        t.Page.Size = PageSizeName.Letter;
        t.Page.MarginTopMm = 25.4;
        t.Page.MarginBottomMm = 25.4;
        t.Page.MarginLeftMm = 25.4;
        t.Page.MarginRightMm = 25.4;
        t.Typography.BodyFont = "Literata";
        t.Typography.BodySizePt = 12;
        t.Typography.LineHeight = 2;
        t.Typography.ParagraphSpacingPt = 0;
        t.Typography.FirstLineIndentPt = 36;
        t.Headings.Font = "Literata";
        t.Headings.Color = "#000000";
        t.Headings.LineHeight = 1.3;
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 14, SpaceBeforePt = 24, SpaceAfterPt = 12, Weight = FontWeight.Bold };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 12, SpaceBeforePt = 12, SpaceAfterPt = 0, Weight = FontWeight.Bold };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 12, SpaceBeforePt = 12, SpaceAfterPt = 0, Weight = FontWeight.Bold, Italic = true };
        t.Headings.H4 = new HeadingLevelStyle { SizePt = 12, SpaceBeforePt = 12, SpaceAfterPt = 0, Weight = FontWeight.Bold };
        t.Headings.H5 = new HeadingLevelStyle { SizePt = 12, SpaceBeforePt = 12, SpaceAfterPt = 0, Weight = FontWeight.Bold, Italic = true };
        t.Headings.H6 = new HeadingLevelStyle { SizePt = 12, SpaceBeforePt = 12, SpaceAfterPt = 0, Weight = FontWeight.Regular, Italic = true };
        t.Colors.Accent = "#1F1F1F";
        t.Colors.Text = "#000000";
        t.Colors.Muted = "#444444";
        t.Colors.Surface = "#F2F2F2";
        t.Colors.Border = "#000000";
        t.Links.Color = "#000000";
        t.Links.Underline = true;
        t.Lists.MarkerColor = "#000000";
        t.Lists.IndentPt = 36;
        t.Lists.ItemSpacingPt = 0;
        t.Tables.Style = TableStyle.Lines;
        t.Tables.BorderColor = "#000000";
        t.Tables.HeaderWeight = FontWeight.Bold;
        t.Tables.FontScale = 1;
        t.Tables.CellPaddingPt = 5;
        t.Quotes.BorderWidthPt = 0;
        t.Quotes.Italic = false;
        t.Quotes.PaddingPt = 36;
        t.Code.SizePt = 10;
        t.Code.Background = "#F7F7F7";
        t.Code.CornerRadiusPt = 0;
        t.Code.Palette = SyntaxPalette.Mono;
        t.Header.Enabled = true;
        t.Header.Left = null;
        t.Header.Center = null;
        t.Header.Right = "{page}";
        t.Header.SizePt = 12;
        t.Header.Color = "#000000";
        t.Footer.Enabled = false;
        t.Cover.Enabled = true;
        t.Cover.Style = CoverStyle.Centered;
        t.Cover.TitleSizePt = 24;
        t.Cover.AccentBar = false;
        t.Images.CaptionSizePt = 10;
        t.Callouts.BorderWidthPt = 1;
        t.Callouts.Background = "#FFFFFF";
        t.Callouts.Note.Accent = "#333333";
        t.Callouts.Tip.Accent = "#333333";
        t.Callouts.Important.Accent = "#333333";
        t.Callouts.Warning.Accent = "#333333";
        t.Callouts.Caution.Accent = "#333333";
        return t;
    }

    /// <summary>Edward Tufte's handouts: off-white paper, a narrow text column with a wide outer margin, light serif headings, no colour.</summary>
    public static PdfTheme Tufte()
    {
        var t = Create("tufte", "Tufte",
            "Edward Tufte's handout style: off-white Letter pages, a 62 mm right margin, Literata throughout, regular-weight headings (italic from level 2) and no colour.",
            "serif", "light", "essay", "wide-margin", "letter");
        t.Page.Size = PageSizeName.Letter;
        t.Page.Background = "#FFFFF8";
        t.Page.MarginTopMm = 25;
        t.Page.MarginBottomMm = 25;
        t.Page.MarginLeftMm = 22;
        t.Page.MarginRightMm = 62;
        t.Typography.BodyFont = "Literata";
        t.Typography.BodySizePt = 11;
        t.Typography.LineHeight = 1.45;
        t.Typography.ParagraphSpacingPt = 10;
        t.Headings.Font = "Literata";
        t.Headings.Color = "#111111";
        t.Headings.LineHeight = 1.15;
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 26, SpaceBeforePt = 36, SpaceAfterPt = 14, Weight = FontWeight.Regular };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 18, SpaceBeforePt = 26, SpaceAfterPt = 8, Weight = FontWeight.Regular, Italic = true };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 14, SpaceBeforePt = 18, SpaceAfterPt = 6, Weight = FontWeight.Regular, Italic = true };
        t.Headings.H4 = new HeadingLevelStyle { SizePt = 11.5, SpaceBeforePt = 14, SpaceAfterPt = 4, Weight = FontWeight.Medium, Italic = true };
        t.Headings.H5 = new HeadingLevelStyle { SizePt = 11, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.Medium, Italic = true };
        t.Headings.H6 = new HeadingLevelStyle { SizePt = 11, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.Regular, Italic = true };
        t.Colors.Accent = "#111111";
        t.Colors.Text = "#111111";
        t.Colors.Muted = "#555555";
        t.Colors.Surface = "#F5F5EC";
        t.Colors.Border = "#CCCCCC";
        t.Links.Color = "#111111";
        t.Links.Underline = true;
        t.Lists.Bullet = BulletStyle.Dash;
        t.Lists.MarkerColor = "#555555";
        t.Quotes.BorderWidthPt = 0;
        t.Quotes.Italic = true;
        t.Quotes.PaddingPt = 12;
        t.Code.Background = "#FFFFF8";
        t.Code.BorderWidthPt = 0;
        t.Code.CornerRadiusPt = 0;
        t.Code.PaddingPt = 4;
        t.Code.Palette = SyntaxPalette.Mono;
        t.Code.InlineBackground = "#FFFFF8";
        t.Tables.Style = TableStyle.Minimal;
        t.Tables.FontScale = 0.95;
        t.Tables.BorderColor = "#111111";
        t.Footer.Center = null;
        t.Footer.Right = "{page}";
        t.Footer.SizePt = 9;
        t.Footer.Color = "#555555";
        t.Images.Align = ImageAlign.Left;
        t.Callouts.BorderWidthPt = 1;
        t.Callouts.Background = "#FFFFF8";
        t.Callouts.Note.Accent = "#555555";
        t.Callouts.Tip.Accent = "#555555";
        t.Callouts.Important.Accent = "#555555";
        t.Callouts.Warning.Accent = "#555555";
        t.Callouts.Caution.Accent = "#555555";
        return t;
    }

    /// <summary>Typora's Newsprint: warm paper, dark ink, justified serif text, thin black rules, no colour.</summary>
    public static PdfTheme Newsprint()
    {
        var t = Create("newsprint", "Newsprint",
            "Grey-cream paper, near-black text, justified Literata, thin black rules and the title and date in the header.",
            "serif", "warm", "light");
        t.Page.Background = "#F3F2EE";
        t.Typography.BodyFont = "Literata";
        t.Typography.BodySizePt = 10.5;
        t.Typography.LineHeight = 1.5;
        t.Typography.ParagraphSpacingPt = 7;
        t.Typography.Alignment = TextAlign.Justify;
        t.Typography.Hyphenate = true;
        t.Headings.Font = "Literata";
        t.Headings.Color = "#1F0909";
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 26, SpaceBeforePt = 26, SpaceAfterPt = 12, Weight = FontWeight.Bold, Underline = true, RuleColor = "#1F0909", RuleWidthPt = 0.5 };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 18, SpaceBeforePt = 20, SpaceAfterPt = 8, Weight = FontWeight.Bold };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 13.5, SpaceBeforePt = 16, SpaceAfterPt = 6, Weight = FontWeight.Bold };
        t.Headings.H4 = new HeadingLevelStyle { SizePt = 11.5, SpaceBeforePt = 14, SpaceAfterPt = 4, Weight = FontWeight.Bold, Italic = true };
        t.Headings.H5 = new HeadingLevelStyle { SizePt = 10.5, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.Bold };
        t.Headings.H6 = new HeadingLevelStyle { SizePt = 10.5, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.Medium, Italic = true };
        t.Colors.Accent = "#1F0909";
        t.Colors.Text = "#1F0909";
        t.Colors.Muted = "#6F6A62";
        t.Colors.Surface = "#E9E7E0";
        t.Colors.Border = "#C9C5BB";
        t.Links.Color = "#1F0909";
        t.Links.Underline = true;
        t.Quotes.BorderColor = "#1F0909";
        t.Quotes.BorderWidthPt = 1;
        t.Quotes.Italic = true;
        t.Code.Background = "#E9E7E0";
        t.Code.CornerRadiusPt = 0;
        t.Code.BorderWidthPt = 0;
        t.Code.Palette = SyntaxPalette.Mono;
        t.Tables.Style = TableStyle.Lines;
        t.Tables.BorderColor = "#1F0909";
        t.Tables.HeaderWeight = FontWeight.Bold;
        t.Tables.FontScale = 0.9;
        t.Header.Enabled = true;
        t.Header.Left = "{title}";
        t.Header.Center = null;
        t.Header.Right = "{date}";
        t.Header.Rule = true;
        t.Header.SizePt = 8;
        t.Footer.Center = "{page}";
        t.Footer.SizePt = 8.5;
        t.Images.Border = true;
        t.Callouts.BorderWidthPt = 1;
        t.Callouts.Background = "#E9E7E0";
        t.Callouts.Note.Accent = "#1F0909";
        t.Callouts.Tip.Accent = "#3F4F2E";
        t.Callouts.Important.Accent = "#4A2F4F";
        t.Callouts.Warning.Accent = "#6B4A1C";
        t.Callouts.Caution.Accent = "#6B1F1F";
        return t;
    }

    /// <summary>Agreements and policies: numbered clauses, uppercase section titles, justified 12 pt serif, "Page x of y".</summary>
    public static PdfTheme Contract()
    {
        var t = Create("contract", "Contract",
            "For agreements and policies: Letter pages, numbered clauses (1, 1.1, 1.1.1), uppercase level-1 headings, justified Literata and \"Page x of y\" in the footer.",
            "serif", "legal", "numbered", "letter", "light");
        t.Page.Size = PageSizeName.Letter;
        t.Page.MarginTopMm = 25.4;
        t.Page.MarginBottomMm = 25.4;
        t.Page.MarginLeftMm = 25.4;
        t.Page.MarginRightMm = 25.4;
        t.Typography.BodyFont = "Literata";
        t.Typography.BodySizePt = 11.5;
        t.Typography.LineHeight = 1.3;
        t.Typography.ParagraphSpacingPt = 9;
        t.Typography.Alignment = TextAlign.Justify;
        t.Typography.Hyphenate = true;
        t.Headings.Font = "Literata";
        t.Headings.Color = "#000000";
        t.Headings.NumberHeadings = true;
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 13, SpaceBeforePt = 22, SpaceAfterPt = 8, Weight = FontWeight.Bold, Uppercase = true, LetterSpacingEm = 0.05 };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 11.5, SpaceBeforePt = 14, SpaceAfterPt = 6, Weight = FontWeight.Bold };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 11.5, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.SemiBold, Italic = true };
        t.Headings.H4 = new HeadingLevelStyle { SizePt = 11.5, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.Regular, Italic = true };
        t.Headings.H5 = new HeadingLevelStyle { SizePt = 11.5, SpaceBeforePt = 10, SpaceAfterPt = 4, Weight = FontWeight.Regular, Italic = true };
        t.Headings.H6 = new HeadingLevelStyle { SizePt = 11.5, SpaceBeforePt = 10, SpaceAfterPt = 4, Weight = FontWeight.Regular, Italic = true };
        t.Colors.Accent = "#1F1F1F";
        t.Colors.Text = "#000000";
        t.Colors.Muted = "#555555";
        t.Colors.Surface = "#F4F4F4";
        t.Colors.Border = "#999999";
        t.Links.Color = "#000000";
        t.Links.Underline = true;
        t.Lists.Bullet = BulletStyle.Dash;
        t.Lists.Numbering = NumberStyle.LowerAlpha;
        t.Lists.IndentPt = 24;
        t.Lists.MarkerColor = "#000000";
        t.Tables.Style = TableStyle.Grid;
        t.Tables.BorderColor = "#000000";
        t.Tables.HeaderWeight = FontWeight.Bold;
        t.Tables.FontScale = 0.95;
        t.Quotes.BorderWidthPt = 0;
        t.Quotes.Italic = false;
        t.Quotes.PaddingPt = 24;
        t.Quotes.FontScale = 0.95;
        t.Code.Background = "#F4F4F4";
        t.Code.CornerRadiusPt = 0;
        t.Code.Palette = SyntaxPalette.Mono;
        t.Header.Enabled = true;
        t.Header.Left = "{title}";
        t.Header.Center = null;
        t.Header.Right = "{date}";
        t.Header.Rule = true;
        t.Header.SizePt = 8.5;
        t.Header.ShowOnFirstPage = false;
        t.Footer.Center = "Page {page} of {pages}";
        t.Footer.SizePt = 8.5;
        t.Footer.Color = "#000000";
        t.Images.Border = true;
        t.Callouts.BorderWidthPt = 1.5;
        t.Callouts.Background = "#F4F4F4";
        t.Callouts.Note.Accent = "#1F1F1F";
        t.Callouts.Tip.Accent = "#1F1F1F";
        t.Callouts.Important.Accent = "#1F1F1F";
        t.Callouts.Warning.Accent = "#1F1F1F";
        t.Callouts.Caution.Accent = "#1F1F1F";
        return t;
    }

    /// <summary>Landscape pages, large type and one section per page: print a Markdown file as a talk handout.</summary>
    public static PdfTheme Handout()
    {
        var t = Create("handout", "Handout",
            "Landscape A4, 14 pt Inter, a cover, a contents page and each level-1 heading on a new page.",
            "sans", "landscape", "large", "light");
        t.Page.Orientation = PageOrientation.Landscape;
        t.Page.MarginTopMm = 18;
        t.Page.MarginBottomMm = 16;
        t.Page.MarginLeftMm = 24;
        t.Page.MarginRightMm = 24;
        t.Typography.BodySizePt = 14;
        t.Typography.LineHeight = 1.45;
        t.Typography.ParagraphSpacingPt = 12;
        t.Headings.Color = "#0F172A";
        t.Headings.PageBreakBeforeH1 = true;
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 36, SpaceBeforePt = 0, SpaceAfterPt = 18, Weight = FontWeight.Bold, Underline = true, RuleColor = "#2563EB", RuleWidthPt = 2 };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 24, SpaceBeforePt = 22, SpaceAfterPt = 10, Weight = FontWeight.SemiBold };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 18, SpaceBeforePt = 16, SpaceAfterPt = 8, Weight = FontWeight.SemiBold };
        t.Headings.H4 = new HeadingLevelStyle { SizePt = 15, SpaceBeforePt = 14, SpaceAfterPt = 6, Weight = FontWeight.SemiBold };
        t.Headings.H5 = new HeadingLevelStyle { SizePt = 14, SpaceBeforePt = 12, SpaceAfterPt = 6, Weight = FontWeight.SemiBold };
        t.Headings.H6 = new HeadingLevelStyle { SizePt = 14, SpaceBeforePt = 12, SpaceAfterPt = 6, Weight = FontWeight.Medium, Uppercase = true, LetterSpacingEm = 0.04 };
        t.Colors.Accent = "#2563EB";
        t.Colors.Text = "#1E293B";
        t.Colors.Muted = "#64748B";
        t.Colors.Surface = "#F1F5F9";
        t.Colors.Border = "#CBD5E1";
        t.Lists.IndentPt = 24;
        t.Lists.ItemSpacingPt = 6;
        t.Lists.MarkerColor = "#2563EB";
        t.Code.SizePt = 12;
        t.Code.PaddingPt = 12;
        t.Code.CornerRadiusPt = 6;
        t.Tables.Style = TableStyle.Striped;
        t.Tables.StripeColor = "#F8FAFC";
        t.Tables.HeaderBackground = "#E2E8F0";
        t.Tables.FontScale = 0.95;
        t.Tables.CellPaddingPt = 8;
        t.Quotes.BorderColor = "#2563EB";
        t.Quotes.BorderWidthPt = 4;
        t.Quotes.Background = "#F1F5F9";
        t.Quotes.FontScale = 1.1;
        t.Footer.Left = "{title}";
        t.Footer.Center = null;
        t.Footer.Right = "{page} / {pages}";
        t.Footer.SizePt = 10;
        t.Footer.Color = "#64748B";
        t.Cover.Enabled = true;
        t.Cover.Style = CoverStyle.Bold;
        t.Cover.TitleSizePt = 60;
        t.Toc.Enabled = true;
        t.Toc.Depth = 2;
        t.Images.MaxWidthPercent = 70;
        t.Images.CornerRadiusPt = 8;
        t.Callouts.ShowIcon = true;
        t.Callouts.CornerRadiusPt = 6;
        t.Callouts.PaddingPt = 12;
        return t;
    }

    /// <summary>Monospace draft: everything in JetBrains Mono, uppercase underlined titles, dashes, URLs printed after links.</summary>
    public static PdfTheme Typewriter()
    {
        var t = Create("typewriter", "Typewriter",
            "JetBrains Mono throughout, uppercase level-1 and level-2 headings, dash bullets and web addresses printed after links.",
            "mono", "draft", "letter", "light");
        t.Page.Size = PageSizeName.Letter;
        t.Page.MarginTopMm = 25.4;
        t.Page.MarginBottomMm = 25.4;
        t.Page.MarginLeftMm = 25.4;
        t.Page.MarginRightMm = 25.4;
        t.Typography.BodyFont = "JetBrains Mono";
        t.Typography.BodySizePt = 10;
        t.Typography.LineHeight = 1.5;
        t.Typography.ParagraphSpacingPt = 10;
        t.Headings.Font = "JetBrains Mono";
        t.Headings.Color = "#222222";
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 16, SpaceBeforePt = 28, SpaceAfterPt = 12, Weight = FontWeight.Bold, Uppercase = true, LetterSpacingEm = 0.08, Underline = true, RuleColor = "#222222", RuleWidthPt = 0.5 };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 13, SpaceBeforePt = 20, SpaceAfterPt = 8, Weight = FontWeight.Bold, Uppercase = true, LetterSpacingEm = 0.06 };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 11, SpaceBeforePt = 16, SpaceAfterPt = 6, Weight = FontWeight.Bold };
        t.Headings.H4 = new HeadingLevelStyle { SizePt = 10, SpaceBeforePt = 14, SpaceAfterPt = 4, Weight = FontWeight.Bold, Italic = true };
        t.Headings.H5 = new HeadingLevelStyle { SizePt = 10, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.Regular, Uppercase = true, LetterSpacingEm = 0.06 };
        t.Headings.H6 = new HeadingLevelStyle { SizePt = 10, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.Regular, Italic = true };
        t.Colors.Accent = "#222222";
        t.Colors.Text = "#222222";
        t.Colors.Muted = "#777777";
        t.Colors.Surface = "#F4F4F4";
        t.Colors.Border = "#BBBBBB";
        t.Links.Color = "#222222";
        t.Links.Underline = true;
        t.Links.ShowUrl = true;
        t.Lists.Bullet = BulletStyle.Dash;
        t.Lists.Numbering = NumberStyle.DecimalParen;
        t.Lists.IndentPt = 22;
        t.Lists.MarkerColor = "#222222";
        t.Code.SizePt = 9.5;
        t.Code.Background = "#F4F4F4";
        t.Code.BorderColor = "#BBBBBB";
        t.Code.BorderWidthPt = 0.5;
        t.Code.CornerRadiusPt = 0;
        t.Code.Palette = SyntaxPalette.Mono;
        t.Code.InlineBackground = "#F4F4F4";
        t.Tables.Style = TableStyle.Grid;
        t.Tables.BorderColor = "#222222";
        t.Tables.FontScale = 0.9;
        t.Quotes.BorderColor = "#222222";
        t.Quotes.BorderWidthPt = 1;
        t.Quotes.Italic = false;
        t.Header.Enabled = true;
        t.Header.Left = "{title}";
        t.Header.Center = null;
        t.Header.Right = "{date}";
        t.Header.SizePt = 8.5;
        t.Footer.Center = "- {page} -";
        t.Footer.SizePt = 9;
        t.Images.Border = true;
        t.Images.MaxWidthPercent = 80;
        t.Callouts.BorderWidthPt = 1;
        t.Callouts.Background = "#FFFFFF";
        t.Callouts.Note.Accent = "#222222";
        t.Callouts.Tip.Accent = "#222222";
        t.Callouts.Important.Accent = "#222222";
        t.Callouts.Warning.Accent = "#222222";
        t.Callouts.Caution.Accent = "#222222";
        return t;
    }

    /// <summary>Pure black on white for office printers: no fills, outlined code and tables, URLs printed after links.</summary>
    public static PdfTheme Print()
    {
        var t = Create("print", "Print",
            "Black on white with no grey fills: outlined code blocks, grid tables and web addresses printed after links.",
            "sans", "print", "black-and-white", "light");
        t.Typography.BodySizePt = 11;
        t.Typography.LineHeight = 1.45;
        t.Headings.Color = "#000000";
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 26, SpaceBeforePt = 24, SpaceAfterPt = 12, Weight = FontWeight.Bold, Underline = true, RuleColor = "#000000", RuleWidthPt = 1 };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 19, SpaceBeforePt = 20, SpaceAfterPt = 8, Weight = FontWeight.Bold };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 15, SpaceBeforePt = 16, SpaceAfterPt = 6, Weight = FontWeight.SemiBold };
        t.Headings.H4 = new HeadingLevelStyle { SizePt = 12.5, SpaceBeforePt = 14, SpaceAfterPt = 4, Weight = FontWeight.SemiBold };
        t.Headings.H5 = new HeadingLevelStyle { SizePt = 11.5, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.SemiBold };
        t.Headings.H6 = new HeadingLevelStyle { SizePt = 11, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.SemiBold, Italic = true };
        t.Colors.Accent = "#000000";
        t.Colors.Text = "#000000";
        t.Colors.Muted = "#444444";
        t.Colors.Surface = "#FFFFFF";
        t.Colors.Border = "#000000";
        t.Links.Color = "#000000";
        t.Links.Underline = true;
        t.Links.ShowUrl = true;
        t.Lists.MarkerColor = "#000000";
        t.Code.Background = "#FFFFFF";
        t.Code.BorderColor = "#000000";
        t.Code.BorderWidthPt = 0.75;
        t.Code.CornerRadiusPt = 0;
        t.Code.Palette = SyntaxPalette.Mono;
        t.Code.InlineBackground = "#FFFFFF";
        t.Tables.Style = TableStyle.Grid;
        t.Tables.BorderColor = "#000000";
        t.Tables.HeaderWeight = FontWeight.Bold;
        t.Quotes.BorderColor = "#000000";
        t.Quotes.BorderWidthPt = 1.5;
        t.Header.Enabled = true;
        t.Header.Left = "{title}";
        t.Header.Center = null;
        t.Header.Right = "{date}";
        t.Header.Rule = true;
        t.Header.SizePt = 8.5;
        t.Header.Color = "#000000";
        t.Footer.Center = "{page} / {pages}";
        t.Footer.SizePt = 9;
        t.Footer.Color = "#000000";
        t.Images.Border = true;
        t.Callouts.BorderWidthPt = 1.5;
        t.Callouts.Background = "#FFFFFF";
        t.Callouts.Note.Accent = "#000000";
        t.Callouts.Tip.Accent = "#000000";
        t.Callouts.Important.Accent = "#000000";
        t.Callouts.Warning.Accent = "#000000";
        t.Callouts.Caution.Accent = "#000000";
        return t;
    }

    /// <summary>Large print for low vision: 15 pt sans, open leading, high contrast, no italics or uppercase, underlined links.</summary>
    public static PdfTheme LargePrint()
    {
        var t = Create("large-print", "Large Print",
            "15 pt Inter, 1.6 line spacing, black text on white, no italics, and underlined blue links.",
            "sans", "large", "accessible", "light");
        t.Page.MarginTopMm = 20;
        t.Page.MarginBottomMm = 20;
        t.Page.MarginLeftMm = 20;
        t.Page.MarginRightMm = 20;
        t.Typography.BodySizePt = 15;
        t.Typography.LineHeight = 1.6;
        t.Typography.ParagraphSpacingPt = 14;
        t.Typography.LetterSpacingEm = 0.01;
        t.Headings.Color = "#000000";
        t.Headings.LineHeight = 1.25;
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 30, SpaceBeforePt = 28, SpaceAfterPt = 14, Weight = FontWeight.Bold };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 24, SpaceBeforePt = 24, SpaceAfterPt = 10, Weight = FontWeight.Bold };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 19, SpaceBeforePt = 20, SpaceAfterPt = 8, Weight = FontWeight.SemiBold };
        t.Headings.H4 = new HeadingLevelStyle { SizePt = 16.5, SpaceBeforePt = 16, SpaceAfterPt = 6, Weight = FontWeight.SemiBold };
        t.Headings.H5 = new HeadingLevelStyle { SizePt = 15, SpaceBeforePt = 14, SpaceAfterPt = 6, Weight = FontWeight.SemiBold };
        t.Headings.H6 = new HeadingLevelStyle { SizePt = 15, SpaceBeforePt = 14, SpaceAfterPt = 6, Weight = FontWeight.Medium };
        t.Colors.Accent = "#0054A6";
        t.Colors.Text = "#000000";
        t.Colors.Muted = "#3D3D3D";
        t.Colors.Surface = "#F2F2F2";
        t.Colors.Border = "#8A8A8A";
        t.Lists.IndentPt = 26;
        t.Lists.ItemSpacingPt = 6;
        t.Lists.MarkerColor = "#000000";
        t.Links.Color = "#0054A6";
        t.Links.Underline = true;
        t.Links.Weight = FontWeight.Medium;
        t.Code.SizePt = 13;
        t.Code.LineHeight = 1.5;
        t.Code.PaddingPt = 12;
        t.Code.Background = "#F2F2F2";
        t.Code.Palette = SyntaxPalette.Mono;
        t.Tables.Style = TableStyle.Grid;
        t.Tables.BorderColor = "#8A8A8A";
        t.Tables.HeaderBackground = "#E6E6E6";
        t.Tables.FontScale = 1;
        t.Tables.CellPaddingPt = 8;
        t.Quotes.BorderColor = "#0054A6";
        t.Quotes.BorderWidthPt = 4;
        t.Quotes.Italic = false;
        t.Quotes.PaddingPt = 12;
        t.Footer.Center = "Page {page} of {pages}";
        t.Footer.SizePt = 12;
        t.Footer.Color = "#000000";
        t.Images.CaptionSizePt = 12;
        t.Callouts.ShowIcon = true;
        t.Callouts.PaddingPt = 12;
        t.Callouts.BorderWidthPt = 4;
        t.Callouts.Background = "#F2F2F2";
        t.Callouts.Note.Accent = "#0054A6";
        t.Callouts.Tip.Accent = "#0B6E2F";
        t.Callouts.Important.Accent = "#5B2C9E";
        t.Callouts.Warning.Accent = "#8A5A00";
        t.Callouts.Caution.Accent = "#B00020";
        return t;
    }
}
