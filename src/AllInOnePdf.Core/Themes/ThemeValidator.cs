using System.Globalization;
using System.Text.Json;
using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.Core.Themes;

/// <summary>What a JSON file looks like to the theme importer.</summary>
public enum ThemeJsonKind
{
    /// <summary>A JSON object with theme properties.</summary>
    Theme,

    /// <summary>Not valid JSON at all.</summary>
    Malformed,

    /// <summary>Valid JSON, but not a theme (an array, a number, or an object without any theme property).</summary>
    NotATheme,
}

/// <summary>
/// Repairs themes that come from outside the app (imported JSON, hand-edited files): fills missing objects,
/// assigns ids/names, and clamps numbers to ranges the renderer can lay out. Never throws.
/// </summary>
public static class ThemeValidator
{
    /// <summary>Top-level property names of <see cref="PdfTheme"/> (camelCase, as exported).</summary>
    private static readonly HashSet<string> ThemeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "description", "author", "schemaVersion", "tags", "basedOn", "page", "typography", "headings", "colors",
        "code", "tables", "quotes", "lists", "links", "images", "header", "footer", "cover", "toc", "callouts",
    };

    public static PdfTheme Normalize(PdfTheme theme) => Normalize(theme, changes: null);

    /// <summary>
    /// Normalises <paramref name="theme"/> and, when <paramref name="changes"/> is given, adds one plain sentence for
    /// every value that was out of range or not a colour and had to be changed (missing values are filled silently).
    /// </summary>
    public static PdfTheme Normalize(PdfTheme theme, ICollection<string>? changes)
    {
        var r = new Repairs(changes);
        theme.Id = string.IsNullOrWhiteSpace(theme.Id) ? Guid.NewGuid().ToString("N") : theme.Id.Trim();
        theme.Name = string.IsNullOrWhiteSpace(theme.Name) ? "Imported theme" : theme.Name.Trim();
        theme.Tags ??= [];
        theme.Tags.RemoveAll(string.IsNullOrWhiteSpace);
        theme.SchemaVersion = Math.Max(1, theme.SchemaVersion);

        theme.Page ??= new PageSettings();
        theme.Typography ??= new TypographySettings();
        theme.Headings ??= new HeadingSettings();
        theme.Colors ??= new ColorSettings();
        theme.Code ??= new CodeSettings();
        theme.Tables ??= new TableSettings();
        theme.Quotes ??= new QuoteSettings();
        theme.Lists ??= new ListSettings();
        theme.Links ??= new LinkSettings();
        theme.Images ??= new ImageSettings();
        theme.Header ??= new HeaderFooterSettings();
        theme.Footer ??= new HeaderFooterSettings();
        theme.Cover ??= new CoverSettings();
        theme.Toc ??= new TocSettings();
        theme.Callouts ??= new CalloutSettings();
        var defaults = new CalloutSettings();
        theme.Callouts.Note ??= defaults.Note;
        theme.Callouts.Tip ??= defaults.Tip;
        theme.Callouts.Important ??= defaults.Important;
        theme.Callouts.Warning ??= defaults.Warning;
        theme.Callouts.Caution ??= defaults.Caution;
        theme.Callouts.Background = r.OptionalColor(theme.Callouts.Background, "Callout background");
        theme.Callouts.BorderWidthPt = r.Clamp(theme.Callouts.BorderWidthPt, 0, 20, 3, "Callout border width", "pt");
        theme.Callouts.PaddingPt = r.Clamp(theme.Callouts.PaddingPt, 0, 60, 10, "Callout padding", "pt");
        theme.Callouts.CornerRadiusPt = r.Clamp(theme.Callouts.CornerRadiusPt, 0, 40, 0, "Callout corner radius", "pt");
        foreach (var (kind, name) in new[]
                 {
                     (theme.Callouts.Note, "Note"), (theme.Callouts.Tip, "Tip"), (theme.Callouts.Important, "Important"),
                     (theme.Callouts.Warning, "Warning"), (theme.Callouts.Caution, "Caution"),
                 })
        {
            kind.Accent = r.OptionalColor(kind.Accent, $"{name} callout accent");
            kind.Background = r.OptionalColor(kind.Background, $"{name} callout background");
            kind.Label = r.Trim(kind.Label, 60, $"{name} callout label");
        }

        var p = theme.Page;
        p.CustomWidthMm = r.Clamp(p.CustomWidthMm, 50, 2000, 210, "Custom page width", "mm");
        p.CustomHeightMm = r.Clamp(p.CustomHeightMm, 50, 2000, 297, "Custom page height", "mm");
        var (w, h) = p.GetSizeMm();
        var maxMarginW = Math.Max(0, w / 2 - 10);
        var maxMarginH = Math.Max(0, h / 2 - 10);
        p.MarginLeftMm = r.Clamp(p.MarginLeftMm, 0, maxMarginW, 20, "Left margin", "mm");
        p.MarginRightMm = r.Clamp(p.MarginRightMm, 0, maxMarginW, 20, "Right margin", "mm");
        p.MarginTopMm = r.Clamp(p.MarginTopMm, 0, maxMarginH, 22, "Top margin", "mm");
        p.MarginBottomMm = r.Clamp(p.MarginBottomMm, 0, maxMarginH, 22, "Bottom margin", "mm");
        p.Background = r.Color(p.Background, "#FFFFFF", "Page background");

        var t = theme.Typography;
        t.BodyFont = string.IsNullOrWhiteSpace(t.BodyFont) ? "Inter" : t.BodyFont.Trim();
        t.BodySizePt = r.Clamp(t.BodySizePt, 6, 48, 11, "Body text size", "pt");
        t.LineHeight = r.Clamp(t.LineHeight, 0.8, 3, 1.5, "Body line height", null);
        t.ParagraphSpacingPt = r.Clamp(t.ParagraphSpacingPt, 0, 72, 8, "Paragraph spacing", "pt");
        t.FirstLineIndentPt = r.Clamp(t.FirstLineIndentPt, 0, 72, 0, "First-line indent", "pt");
        t.LetterSpacingEm = r.Clamp(t.LetterSpacingEm, -0.1, 0.5, 0, "Body letter spacing", "em");

        var hd = theme.Headings;
        hd.Color = r.Color(hd.Color, "#111827", "Heading colour");
        hd.LineHeight = r.Clamp(hd.LineHeight, 0.8, 3, 1.2, "Heading line height", null);
        var levels = new[] { hd.H1 ??= new(), hd.H2 ??= new(), hd.H3 ??= new(), hd.H4 ??= new(), hd.H5 ??= new(), hd.H6 ??= new() };
        for (var i = 0; i < levels.Length; i++)
        {
            var level = levels[i];
            var tag = "H" + (i + 1).ToString(CultureInfo.InvariantCulture);
            level.SizePt = r.Clamp(level.SizePt, 6, 120, 16, $"{tag} size", "pt");
            level.SpaceBeforePt = r.Clamp(level.SpaceBeforePt, 0, 200, 16, $"{tag} space before", "pt");
            level.SpaceAfterPt = r.Clamp(level.SpaceAfterPt, 0, 200, 6, $"{tag} space after", "pt");
            level.LetterSpacingEm = r.Clamp(level.LetterSpacingEm, -0.1, 0.5, 0, $"{tag} letter spacing", "em");
            level.Color = r.OptionalColor(level.Color, $"{tag} colour");
        }

        var c = theme.Colors;
        c.Accent = r.Color(c.Accent, "#0F6CBD", "Accent colour");
        c.Text = r.Color(c.Text, "#1F2937", "Text colour");
        c.Muted = r.Color(c.Muted, "#6B7280", "Muted colour");
        c.Surface = r.Color(c.Surface, "#F3F4F6", "Surface colour");
        c.Border = r.Color(c.Border, "#D1D5DB", "Border colour");

        var code = theme.Code;
        code.Font = string.IsNullOrWhiteSpace(code.Font) ? "JetBrains Mono" : code.Font.Trim();
        code.SizePt = r.Clamp(code.SizePt, 5, 36, 9.5, "Code size", "pt");
        code.LineHeight = r.Clamp(code.LineHeight, 0.8, 3, 1.45, "Code line height", null);
        code.BorderWidthPt = r.Clamp(code.BorderWidthPt, 0, 10, 0.75, "Code border width", "pt");
        code.CornerRadiusPt = r.Clamp(code.CornerRadiusPt, 0, 40, 4, "Code corner radius", "pt");
        code.PaddingPt = r.Clamp(code.PaddingPt, 0, 60, 10, "Code padding", "pt");
        code.InlineScale = r.Clamp(code.InlineScale, 0.5, 1.5, 0.9, "Inline code scale", null);
        code.Background = r.OptionalColor(code.Background, "Code background");
        code.TextColor = r.OptionalColor(code.TextColor, "Code text colour");
        code.BorderColor = r.OptionalColor(code.BorderColor, "Code border colour");
        code.InlineBackground = r.OptionalColor(code.InlineBackground, "Inline code background");
        code.InlineTextColor = r.OptionalColor(code.InlineTextColor, "Inline code text colour");

        var tb = theme.Tables;
        tb.BorderWidthPt = r.Clamp(tb.BorderWidthPt, 0, 10, 0.75, "Table border width", "pt");
        tb.CellPaddingPt = r.Clamp(tb.CellPaddingPt, 0, 40, 6, "Table cell padding", "pt");
        tb.FontScale = r.Clamp(tb.FontScale, 0.5, 1.5, 0.92, "Table text scale", null);
        tb.HeaderBackground = r.OptionalColor(tb.HeaderBackground, "Table header background");
        tb.HeaderTextColor = r.OptionalColor(tb.HeaderTextColor, "Table header text colour");
        tb.BorderColor = r.OptionalColor(tb.BorderColor, "Table border colour");
        tb.StripeColor = r.OptionalColor(tb.StripeColor, "Table stripe colour");

        var q = theme.Quotes;
        q.BorderWidthPt = r.Clamp(q.BorderWidthPt, 0, 20, 3, "Quote border width", "pt");
        q.PaddingPt = r.Clamp(q.PaddingPt, 0, 60, 10, "Quote padding", "pt");
        q.FontScale = r.Clamp(q.FontScale, 0.5, 1.5, 1, "Quote text scale", null);
        q.BorderColor = r.OptionalColor(q.BorderColor, "Quote border colour");
        q.Background = r.OptionalColor(q.Background, "Quote background");
        q.TextColor = r.OptionalColor(q.TextColor, "Quote text colour");

        var l = theme.Lists;
        l.IndentPt = r.Clamp(l.IndentPt, 4, 80, 18, "List indent", "pt");
        l.ItemSpacingPt = r.Clamp(l.ItemSpacingPt, 0, 40, 3, "List item spacing", "pt");
        l.MarkerColor = r.OptionalColor(l.MarkerColor, "List marker colour");

        theme.Links.Color = r.OptionalColor(theme.Links.Color, "Link colour");

        var im = theme.Images;
        im.MaxWidthPercent = r.Clamp(im.MaxWidthPercent, 5, 100, 100, "Image maximum width", "%");
        im.CaptionSizePt = r.Clamp(im.CaptionSizePt, 5, 30, 9, "Caption size", "pt");
        im.CornerRadiusPt = r.Clamp(im.CornerRadiusPt, 0, 60, 0, "Image corner radius", "pt");
        im.SpacingPt = r.Clamp(im.SpacingPt, 0, 60, 10, "Image spacing", "pt");

        foreach (var (hf, name) in new[] { (theme.Header, "Header"), (theme.Footer, "Footer") })
        {
            hf.SizePt = r.Clamp(hf.SizePt, 5, 24, 8.5, $"{name} text size", "pt");
            hf.SpacingPt = r.Clamp(hf.SpacingPt, 0, 60, 10, $"{name} distance from text", "pt");
            hf.Color = r.OptionalColor(hf.Color, $"{name} colour");
            hf.Left = r.Trim(hf.Left, 200, $"{name} left text");
            hf.Center = r.Trim(hf.Center, 200, $"{name} centre text");
            hf.Right = r.Trim(hf.Right, 200, $"{name} right text");
        }

        var cv = theme.Cover;
        cv.TitleSizePt = r.Clamp(cv.TitleSizePt, 10, 160, 40, "Cover title size", "pt");
        cv.Background = r.OptionalColor(cv.Background, "Cover background");
        cv.TitleColor = r.OptionalColor(cv.TitleColor, "Cover title colour");
        cv.Note = r.Trim(cv.Note, 500, "Cover note");
        cv.TitleOverride = r.Trim(cv.TitleOverride, 200, "Cover title");

        var toc = theme.Toc;
        toc.Title = string.IsNullOrWhiteSpace(toc.Title) ? "Contents" : r.Trim(toc.Title, 100, "Contents title")!;
        var depth = Math.Clamp(toc.Depth, 1, 6);
        if (depth != toc.Depth)
        {
            r.Add($"Contents depth {toc.Depth.ToString(CultureInfo.InvariantCulture)} was changed to {depth.ToString(CultureInfo.InvariantCulture)} (1 to 6 levels).");
        }

        toc.Depth = depth;
        return theme;
    }

    /// <summary>Tells a theme file from malformed JSON and from JSON that is not a theme (an array, or an object without theme properties).</summary>
    public static ThemeJsonKind Classify(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ThemeJsonKind.Malformed;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ThemeJsonKind.NotATheme;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (ThemeProperties.Contains(property.Name))
                {
                    return ThemeJsonKind.Theme;
                }
            }

            return ThemeJsonKind.NotATheme;
        }
        catch (JsonException)
        {
            return ThemeJsonKind.Malformed;
        }
    }

    /// <summary>
    /// The values an import of <paramref name="json"/> would change (see <see cref="Normalize(PdfTheme, ICollection{string})"/>);
    /// empty when nothing needs changing or the JSON cannot be read as a theme.
    /// </summary>
    public static IReadOnlyList<string> DescribeRepairs(string json)
    {
        try
        {
            var theme = JsonSerializer.Deserialize<PdfTheme>(json, CoreJson.Options);
            if (theme is null)
            {
                return [];
            }

            var changes = new List<string>();
            Normalize(theme, changes);
            return changes;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return [];
        }
    }

    public static bool IsHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var s = value.Trim();
        if (s.Length is not (4 or 7 or 9) || s[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < s.Length; i++)
        {
            if (!Uri.IsHexDigit(s[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The repair helpers, recording a sentence per changed value when a list is given.</summary>
    private readonly struct Repairs(ICollection<string>? changes)
    {
        public void Add(string change) => changes?.Add(change);

        public double Clamp(double value, double min, double max, double fallback, string label, string? unit)
        {
            if (!double.IsFinite(value))
            {
                Add($"{label} was not a number and was set to {Number(fallback, unit)}.");
                return fallback;
            }

            var clamped = Math.Clamp(value, min, max);
            if (clamped != value)
            {
                Add($"{label} {Number(value, unit)} was changed to {Number(clamped, unit)} ({(value > max ? "the largest" : "the smallest")} value allowed).");
            }

            return clamped;
        }

        public string Color(string? value, string fallback, string label)
        {
            if (IsHexColor(value))
            {
                return value!.Trim();
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                Add($"{label} \"{Short(value)}\" is not a hex colour and was set to {fallback}.");
            }

            return fallback;
        }

        public string? OptionalColor(string? value, string label)
        {
            if (IsHexColor(value))
            {
                return value!.Trim();
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                Add($"{label} \"{Short(value)}\" is not a hex colour and was cleared.");
            }

            return null;
        }

        public string? Trim(string? value, int maxLength, string label)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.Length <= maxLength)
            {
                return trimmed;
            }

            Add($"{label} was cut to {maxLength.ToString(CultureInfo.InvariantCulture)} characters.");
            return trimmed[..maxLength];
        }

        private static string Number(double value, string? unit)
        {
            var text = value.ToString("0.##", CultureInfo.InvariantCulture);
            return unit switch
            {
                null => text,
                "%" => text + "%",
                _ => text + " " + unit,
            };
        }

        private static string Short(string value) => value.Length <= 24 ? value : value[..24] + "…";
    }
}
