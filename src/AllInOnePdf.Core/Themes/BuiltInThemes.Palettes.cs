namespace AllInOnePdf.Core.Themes;

/// <summary>
/// Colour schemes people already know from their editors and terminals, applied to the page: Solarized, Nord, Dracula,
/// Catppuccin, Gruvbox, Tokyo Night, Everforest, Rosé Pine and GitHub's dark mode. Hex values follow each project's
/// published palette; the dark ones use the slightly larger type and leading of Night for on-screen reading.
/// </summary>
public static partial class BuiltInThemes
{
    /// <summary>Shared defaults for the dark screen themes: larger type, open leading, a dark syntax palette.</summary>
    private static void UseScreenReadingDefaults(PdfTheme t)
    {
        t.Typography.BodySizePt = 11.5;
        t.Typography.LineHeight = 1.6;
        t.Code.Palette = SyntaxPalette.Dark;
        t.Callouts.ShowIcon = true;
        t.Images.CornerRadiusPt = 4;
    }

    private static void SetCallouts(PdfTheme t, string note, string tip, string important, string warning, string caution, string? background = null)
    {
        t.Callouts.Note.Accent = note;
        t.Callouts.Tip.Accent = tip;
        t.Callouts.Important.Accent = important;
        t.Callouts.Warning.Accent = warning;
        t.Callouts.Caution.Accent = caution;
        t.Callouts.Background = background;
    }

    // ---- Light papers -------------------------------------------------------------------------------------------

    /// <summary>Ethan Schoonover's Solarized on its cream base3 paper.</summary>
    public static PdfTheme SolarizedLight()
    {
        var t = Create("solarized-light", "Solarized Light",
            "Ethan Schoonover's Solarized colours on a cream page: grey-blue text, blue links and orange bullets.",
            "sans", "warm", "light");
        t.Page.Background = "#FDF6E3";
        t.Headings.Color = "#073642";
        t.Colors.Accent = "#268BD2";
        t.Colors.Text = "#586E75";
        t.Colors.Muted = "#93A1A1";
        t.Colors.Surface = "#EEE8D5";
        t.Colors.Border = "#D3CBB7";
        t.Code.Background = "#EEE8D5";
        t.Code.TextColor = "#586E75";
        t.Code.Palette = SyntaxPalette.Light;
        t.Code.CornerRadiusPt = 3;
        t.Code.InlineBackground = "#EEE8D5";
        t.Tables.Style = TableStyle.Lines;
        t.Tables.HeaderBackground = "#EEE8D5";
        t.Tables.BorderColor = "#D3CBB7";
        t.Quotes.BorderColor = "#B58900";
        t.Quotes.Background = "#F6EFDC";
        t.Quotes.TextColor = "#657B83";
        t.Links.Color = "#268BD2";
        t.Lists.MarkerColor = "#CB4B16";
        t.Footer.Color = "#93A1A1";
        t.Callouts.ShowIcon = true;
        SetCallouts(t, "#268BD2", "#859900", "#6C71C4", "#CB4B16", "#DC322F", "#EEE8D5");
        return t;
    }

    /// <summary>Catppuccin Latte: soft pastel accents on a cool off-white base.</summary>
    public static PdfTheme CatppuccinLatte()
    {
        var t = Create("catppuccin-latte", "Catppuccin Latte",
            "Catppuccin Latte colours: pale grey-blue page, purple accents, blue links and rounded code and callout panels.",
            "sans", "pastel", "light");
        t.Page.Background = "#EFF1F5";
        t.Headings.Color = "#4C4F69";
        t.Colors.Accent = "#8839EF";
        t.Colors.Text = "#4C4F69";
        t.Colors.Muted = "#6C6F85";
        t.Colors.Surface = "#E6E9EF";
        t.Colors.Border = "#CCD0DA";
        t.Code.Background = "#E6E9EF";
        t.Code.Palette = SyntaxPalette.Light;
        t.Code.CornerRadiusPt = 6;
        t.Code.InlineBackground = "#DCE0E8";
        t.Tables.Style = TableStyle.Striped;
        t.Tables.HeaderBackground = "#DCE0E8";
        t.Tables.StripeColor = "#E6E9EF";
        t.Tables.BorderColor = "#CCD0DA";
        t.Quotes.BorderColor = "#7287FD";
        t.Quotes.Background = "#E6E9EF";
        t.Links.Color = "#1E66F5";
        t.Lists.MarkerColor = "#8839EF";
        t.Footer.Color = "#6C6F85";
        t.Images.CornerRadiusPt = 6;
        t.Callouts.ShowIcon = true;
        t.Callouts.CornerRadiusPt = 6;
        SetCallouts(t, "#1E66F5", "#40A02B", "#8839EF", "#DF8E1D", "#D20F39", "#E6E9EF");
        return t;
    }

    /// <summary>Gruvbox light: retro warm paper, square bullets, square corners, faded accents.</summary>
    public static PdfTheme GruvboxLight()
    {
        var t = Create("gruvbox-light", "Gruvbox Light",
            "Gruvbox colours on yellow-cream paper: orange accents, teal links, square bullets and square corners.",
            "sans", "warm", "light");
        t.Page.Background = "#FBF1C7";
        t.Headings.Color = "#282828";
        t.Headings.H1.Underline = true;
        t.Headings.H1.RuleColor = "#D5C4A1";
        t.Headings.H1.RuleWidthPt = 1;
        t.Colors.Accent = "#AF3A03";
        t.Colors.Text = "#3C3836";
        t.Colors.Muted = "#7C6F64";
        t.Colors.Surface = "#EBDBB2";
        t.Colors.Border = "#D5C4A1";
        t.Code.Background = "#F2E5BC";
        t.Code.BorderColor = "#D5C4A1";
        t.Code.CornerRadiusPt = 0;
        t.Code.Palette = SyntaxPalette.Light;
        t.Code.InlineBackground = "#EBDBB2";
        t.Tables.Style = TableStyle.Grid;
        t.Tables.BorderColor = "#D5C4A1";
        t.Tables.HeaderBackground = "#EBDBB2";
        t.Quotes.BorderColor = "#79740E";
        t.Quotes.Background = "#F2E5BC";
        t.Links.Color = "#076678";
        t.Lists.Bullet = BulletStyle.Square;
        t.Lists.MarkerColor = "#AF3A03";
        t.Footer.Color = "#7C6F64";
        SetCallouts(t, "#076678", "#79740E", "#8F3F71", "#B57614", "#9D0006", "#EBDBB2");
        return t;
    }

    /// <summary>Rosé Pine Dawn: a serif body on warm pastel paper, with the palette's rose, pine, foam, iris and gold.</summary>
    public static PdfTheme RosePineDawn()
    {
        var t = Create("rose-pine-dawn", "Rosé Pine Dawn",
            "Rosé Pine Dawn colours on a cream page: Literata body text, Inter headings, rose bullets and teal links.",
            "serif", "warm", "pastel", "light");
        t.Page.Background = "#FAF4ED";
        t.Typography.BodyFont = "Literata";
        t.Typography.BodySizePt = 10.5;
        t.Typography.LineHeight = 1.55;
        t.Headings.Font = "Inter";
        t.Headings.Color = "#575279";
        t.Colors.Accent = "#907AA9";
        t.Colors.Text = "#575279";
        t.Colors.Muted = "#797593";
        t.Colors.Surface = "#F2E9E1";
        t.Colors.Border = "#DFDAD9";
        t.Code.Background = "#FFFAF3";
        t.Code.BorderColor = "#DFDAD9";
        t.Code.CornerRadiusPt = 6;
        t.Code.Palette = SyntaxPalette.Light;
        t.Code.InlineBackground = "#F2E9E1";
        t.Tables.Style = TableStyle.Striped;
        t.Tables.HeaderBackground = "#F2E9E1";
        t.Tables.StripeColor = "#F4EDE8";
        t.Tables.BorderColor = "#DFDAD9";
        t.Quotes.BorderColor = "#D7827E";
        t.Quotes.Background = "#FFFAF3";
        t.Links.Color = "#286983";
        t.Lists.Bullet = BulletStyle.Circle;
        t.Lists.MarkerColor = "#B4637A";
        t.Footer.Color = "#9893A5";
        t.Images.CornerRadiusPt = 6;
        t.Callouts.ShowIcon = true;
        t.Callouts.CornerRadiusPt = 6;
        SetCallouts(t, "#286983", "#56949F", "#907AA9", "#EA9D34", "#B4637A", "#F2E9E1");
        return t;
    }

    // ---- Dark screens -------------------------------------------------------------------------------------------

    /// <summary>GitHub's dark mode.</summary>
    public static PdfTheme GitHubDark()
    {
        var t = Create("github-dark", "GitHub Dark",
            "GitHub's dark mode: near-black page, grey rules under level-1 and level-2 headings, blue links and coloured alert boxes.",
            "sans", "markdown", "code", "dark", "screen");
        UseScreenReadingDefaults(t);
        t.Page.Background = "#0D1117";
        t.Typography.BodySizePt = 11;
        t.Typography.LineHeight = 1.55;
        t.Headings.Color = "#F0F6FC";
        t.Headings.H1 = new HeadingLevelStyle { SizePt = 22, SpaceBeforePt = 22, SpaceAfterPt = 12, Weight = FontWeight.SemiBold, Underline = true, RuleColor = "#3D444D", RuleWidthPt = 1 };
        t.Headings.H2 = new HeadingLevelStyle { SizePt = 17, SpaceBeforePt = 20, SpaceAfterPt = 10, Weight = FontWeight.SemiBold, Underline = true, RuleColor = "#3D444D", RuleWidthPt = 1 };
        t.Headings.H3 = new HeadingLevelStyle { SizePt = 13.5, SpaceBeforePt = 16, SpaceAfterPt = 6, Weight = FontWeight.SemiBold };
        t.Headings.H4 = new HeadingLevelStyle { SizePt = 11, SpaceBeforePt = 14, SpaceAfterPt = 4, Weight = FontWeight.SemiBold };
        t.Headings.H5 = new HeadingLevelStyle { SizePt = 10, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.SemiBold };
        t.Headings.H6 = new HeadingLevelStyle { SizePt = 9.5, SpaceBeforePt = 12, SpaceAfterPt = 4, Weight = FontWeight.SemiBold, Color = "#9198A1" };
        t.Colors.Accent = "#4493F8";
        t.Colors.Text = "#E6EDF3";
        t.Colors.Muted = "#9198A1";
        t.Colors.Surface = "#161B22";
        t.Colors.Border = "#3D444D";
        t.Code.Background = "#161B22";
        t.Code.TextColor = "#E6EDF3";
        t.Code.BorderWidthPt = 0;
        t.Code.CornerRadiusPt = 6;
        t.Code.InlineBackground = "#343941";
        t.Code.InlineScale = 0.85;
        t.Tables.Style = TableStyle.Grid;
        t.Tables.BorderColor = "#3D444D";
        t.Tables.StripeColor = "#161B22";
        t.Quotes.BorderColor = "#3D444D";
        t.Quotes.TextColor = "#9198A1";
        t.Quotes.Italic = false;
        t.Links.Color = "#4493F8";
        t.Footer.Color = "#9198A1";
        t.Images.CornerRadiusPt = 0;
        SetCallouts(t, "#4493F8", "#3FB950", "#AB7DF8", "#D29922", "#F85149", "#0D1117");
        return t;
    }

    /// <summary>Solarized on its deep teal base03.</summary>
    public static PdfTheme SolarizedDark()
    {
        var t = Create("solarized-dark", "Solarized Dark",
            "Solarized colours on a dark teal page: grey-blue text, teal links and orange bullets.",
            "sans", "dark", "screen");
        UseScreenReadingDefaults(t);
        t.Page.Background = "#002B36";
        t.Headings.Color = "#EEE8D5";
        t.Colors.Accent = "#268BD2";
        t.Colors.Text = "#93A1A1";
        t.Colors.Muted = "#657B83";
        t.Colors.Surface = "#073642";
        t.Colors.Border = "#1B4F5C";
        t.Code.Background = "#00212B";
        t.Code.TextColor = "#93A1A1";
        t.Code.CornerRadiusPt = 3;
        t.Code.InlineBackground = "#073642";
        t.Tables.Style = TableStyle.Striped;
        t.Tables.HeaderBackground = "#073642";
        t.Tables.StripeColor = "#01313D";
        t.Tables.BorderColor = "#1B4F5C";
        t.Quotes.BorderColor = "#B58900";
        t.Quotes.Background = "#03303B";
        t.Links.Color = "#2AA198";
        t.Lists.MarkerColor = "#CB4B16";
        t.Footer.Color = "#586E75";
        SetCallouts(t, "#268BD2", "#859900", "#6C71C4", "#CB4B16", "#DC322F", "#073642");
        return t;
    }

    /// <summary>Nord: arctic, bluish greys with frost accents and thin rules.</summary>
    public static PdfTheme Nord()
    {
        var t = Create("nord", "Nord",
            "Nord colours: blue-grey page, light grey text, pale blue accents and a thin rule under level-1 headings.",
            "sans", "dark", "screen");
        UseScreenReadingDefaults(t);
        t.Page.Background = "#2E3440";
        t.Headings.Color = "#ECEFF4";
        t.Headings.H1.Underline = true;
        t.Headings.H1.RuleColor = "#434C5E";
        t.Headings.H1.RuleWidthPt = 1;
        t.Colors.Accent = "#88C0D0";
        t.Colors.Text = "#D8DEE9";
        t.Colors.Muted = "#7B88A1";
        t.Colors.Surface = "#3B4252";
        t.Colors.Border = "#434C5E";
        t.Code.Background = "#292E39";
        t.Code.TextColor = "#D8DEE9";
        t.Code.InlineBackground = "#3B4252";
        t.Tables.Style = TableStyle.Lines;
        t.Tables.HeaderBackground = "#3B4252";
        t.Tables.BorderColor = "#434C5E";
        t.Quotes.BorderColor = "#88C0D0";
        t.Quotes.Background = "#353B49";
        t.Links.Color = "#81A1C1";
        t.Lists.MarkerColor = "#88C0D0";
        t.Footer.Color = "#7B88A1";
        SetCallouts(t, "#88C0D0", "#A3BE8C", "#B48EAD", "#EBCB8B", "#BF616A", "#3B4252");
        return t;
    }

    /// <summary>Dracula: purple headings, pink bullets, green inline code, cyan links.</summary>
    public static PdfTheme Dracula()
    {
        var t = Create("dracula", "Dracula",
            "Dracula colours: purple headings, pink bullets, green inline code and cyan links on a dark slate page.",
            "sans", "dark", "screen");
        UseScreenReadingDefaults(t);
        t.Page.Background = "#282A36";
        t.Headings.Color = "#BD93F9";
        t.Colors.Accent = "#BD93F9";
        t.Colors.Text = "#F8F8F2";
        t.Colors.Muted = "#6272A4";
        t.Colors.Surface = "#343746";
        t.Colors.Border = "#44475A";
        t.Code.Background = "#21222C";
        t.Code.TextColor = "#F8F8F2";
        t.Code.CornerRadiusPt = 6;
        t.Code.InlineBackground = "#44475A";
        t.Code.InlineTextColor = "#50FA7B";
        t.Tables.Style = TableStyle.Striped;
        t.Tables.HeaderBackground = "#44475A";
        t.Tables.StripeColor = "#2C2E3B";
        t.Tables.BorderColor = "#44475A";
        t.Quotes.BorderColor = "#F1FA8C";
        t.Quotes.Background = "#2F3140";
        t.Links.Color = "#8BE9FD";
        t.Lists.MarkerColor = "#FF79C6";
        t.Footer.Color = "#6272A4";
        t.Images.CornerRadiusPt = 6;
        t.Callouts.CornerRadiusPt = 4;
        SetCallouts(t, "#8BE9FD", "#50FA7B", "#BD93F9", "#FFB86C", "#FF5555", "#343746");
        return t;
    }

    /// <summary>Catppuccin Mocha: soothing pastels on a dark lavender-grey base.</summary>
    public static PdfTheme CatppuccinMocha()
    {
        var t = Create("catppuccin-mocha", "Catppuccin Mocha",
            "Catppuccin Mocha colours: dark blue-grey page, lavender headings, pastel accents and rounded panels.",
            "sans", "pastel", "dark", "screen");
        UseScreenReadingDefaults(t);
        t.Page.Background = "#1E1E2E";
        t.Headings.Color = "#B4BEFE";
        t.Colors.Accent = "#CBA6F7";
        t.Colors.Text = "#CDD6F4";
        t.Colors.Muted = "#9399B2";
        t.Colors.Surface = "#313244";
        t.Colors.Border = "#45475A";
        t.Code.Background = "#181825";
        t.Code.TextColor = "#CDD6F4";
        t.Code.CornerRadiusPt = 6;
        t.Code.InlineBackground = "#313244";
        t.Tables.Style = TableStyle.Striped;
        t.Tables.HeaderBackground = "#313244";
        t.Tables.StripeColor = "#252537";
        t.Tables.BorderColor = "#45475A";
        t.Quotes.BorderColor = "#B4BEFE";
        t.Quotes.Background = "#26263A";
        t.Links.Color = "#89B4FA";
        t.Lists.MarkerColor = "#CBA6F7";
        t.Footer.Color = "#9399B2";
        t.Images.CornerRadiusPt = 6;
        t.Callouts.CornerRadiusPt = 6;
        SetCallouts(t, "#89B4FA", "#A6E3A1", "#CBA6F7", "#F9E2AF", "#F38BA8", "#313244");
        return t;
    }

    /// <summary>Gruvbox dark: warm retro browns with orange, yellow and green.</summary>
    public static PdfTheme GruvboxDark()
    {
        var t = Create("gruvbox-dark", "Gruvbox Dark",
            "Gruvbox colours on a dark brown-grey page: cream text, orange accents, square bullets and square corners.",
            "sans", "warm", "dark", "screen");
        UseScreenReadingDefaults(t);
        t.Page.Background = "#282828";
        t.Headings.Color = "#FBF1C7";
        t.Headings.H1.Underline = true;
        t.Headings.H1.RuleColor = "#504945";
        t.Headings.H1.RuleWidthPt = 1;
        t.Colors.Accent = "#FE8019";
        t.Colors.Text = "#EBDBB2";
        t.Colors.Muted = "#A89984";
        t.Colors.Surface = "#3C3836";
        t.Colors.Border = "#504945";
        t.Code.Background = "#1D2021";
        t.Code.TextColor = "#EBDBB2";
        t.Code.BorderColor = "#504945";
        t.Code.CornerRadiusPt = 0;
        t.Code.InlineBackground = "#3C3836";
        t.Tables.Style = TableStyle.Grid;
        t.Tables.BorderColor = "#504945";
        t.Tables.HeaderBackground = "#3C3836";
        t.Quotes.BorderColor = "#B8BB26";
        t.Quotes.Background = "#32302F";
        t.Links.Color = "#83A598";
        t.Lists.Bullet = BulletStyle.Square;
        t.Lists.MarkerColor = "#FE8019";
        t.Footer.Color = "#928374";
        t.Images.CornerRadiusPt = 0;
        t.Callouts.ShowIcon = false;
        SetCallouts(t, "#83A598", "#B8BB26", "#D3869B", "#FABD2F", "#FB4934", "#3C3836");
        return t;
    }

    /// <summary>Tokyo Night: deep navy with neon blue, purple and cyan.</summary>
    public static PdfTheme TokyoNight()
    {
        var t = Create("tokyo-night", "Tokyo Night",
            "Tokyo Night colours: dark navy page, blue headings, purple quote bars and bullets, and cyan links.",
            "sans", "dark", "screen");
        UseScreenReadingDefaults(t);
        t.Page.Background = "#1A1B26";
        t.Headings.Color = "#7AA2F7";
        t.Colors.Accent = "#7AA2F7";
        t.Colors.Text = "#C0CAF5";
        t.Colors.Muted = "#737AA2";
        t.Colors.Surface = "#24283B";
        t.Colors.Border = "#414868";
        t.Code.Background = "#16161E";
        t.Code.TextColor = "#C0CAF5";
        t.Code.CornerRadiusPt = 6;
        t.Code.InlineBackground = "#292E42";
        t.Tables.Style = TableStyle.Striped;
        t.Tables.HeaderBackground = "#24283B";
        t.Tables.StripeColor = "#1E2030";
        t.Tables.BorderColor = "#414868";
        t.Quotes.BorderColor = "#BB9AF7";
        t.Quotes.Background = "#1F2335";
        t.Quotes.TextColor = "#A9B1D6";
        t.Links.Color = "#7DCFFF";
        t.Lists.MarkerColor = "#BB9AF7";
        t.Footer.Color = "#565F89";
        t.Callouts.CornerRadiusPt = 6;
        SetCallouts(t, "#7AA2F7", "#9ECE6A", "#BB9AF7", "#E0AF68", "#F7768E", "#24283B");
        return t;
    }

    /// <summary>Everforest: a dark green-grey forest floor with soft green and aqua.</summary>
    public static PdfTheme Everforest()
    {
        var t = Create("everforest", "Everforest",
            "Everforest colours: dark green-grey page, beige text, soft green accents and aqua links.",
            "sans", "dark", "screen");
        UseScreenReadingDefaults(t);
        t.Page.Background = "#2D353B";
        t.Headings.Color = "#D3C6AA";
        t.Headings.H1.Underline = true;
        t.Headings.H1.RuleColor = "#475258";
        t.Headings.H1.RuleWidthPt = 1;
        t.Colors.Accent = "#A7C080";
        t.Colors.Text = "#D3C6AA";
        t.Colors.Muted = "#859289";
        t.Colors.Surface = "#343F44";
        t.Colors.Border = "#475258";
        t.Code.Background = "#232A2E";
        t.Code.TextColor = "#D3C6AA";
        t.Code.InlineBackground = "#3D484D";
        t.Tables.Style = TableStyle.Lines;
        t.Tables.HeaderBackground = "#343F44";
        t.Tables.BorderColor = "#475258";
        t.Quotes.BorderColor = "#A7C080";
        t.Quotes.Background = "#303A3F";
        t.Links.Color = "#7FBBB3";
        t.Lists.MarkerColor = "#A7C080";
        t.Footer.Color = "#859289";
        SetCallouts(t, "#7FBBB3", "#A7C080", "#D699B6", "#DBBC7F", "#E67E80", "#343F44");
        return t;
    }
}
