using System.Globalization;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters.Docx;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AllInOnePdf.Core.Importers.Docx;

internal enum ParaRole
{
    Normal,
    Heading,
    Title,
    Subtitle,
    CoverMeta,
    Quote,
    QuoteAttribution,
    Code,
    CodeLabel,
    Callout,
    Caption,
    MathBlock,
    HtmlSource,
    HorizontalRule,
    TocEntry,
    TocHeading,
    ListParagraph,
    ListContinue,
}

internal readonly record struct StyleRole(ParaRole Role, int Level = 0, CalloutKind Kind = CalloutKind.Note);

/// <summary>
/// Resolves paragraph, character and table styles through their <c>basedOn</c> chains: semantic roles (headings by
/// id, name or outline level so localised styles work), inherited numbering, run flags and fonts.
/// </summary>
internal sealed class DocxStyleResolver
{
    private static readonly string[] MonoFonts =
    [
        "consolas", "courier", "cascadia", "jetbrains mono", "source code", "menlo", "monaco", "lucida console", "lucida sans typewriter",
        "fira code", "fira mono", "dejavu sans mono", "liberation mono", "roboto mono", "ubuntu mono", "sf mono", "inconsolata",
        "noto mono", "pt mono", "ibm plex mono", "andale mono", "hack", "iosevka", "space mono", "anonymous pro", "letter gothic",
    ];

    private readonly Dictionary<string, Style> _styles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StyleRole> _roles = new(StringComparer.Ordinal);
    private readonly string? _defaultParagraphStyle;

    public DocxStyleResolver(MainDocumentPart main)
    {
        var styles = main.StyleDefinitionsPart?.Styles;
        if (styles is null)
        {
            DefaultFontSizePt = 11;
            return;
        }

        foreach (var style in styles.Elements<Style>())
        {
            if (style.StyleId?.Value is { } id)
            {
                _styles.TryAdd(id, style);
                if (style.Type?.Value == StyleValues.Paragraph && style.Default?.Value == true)
                {
                    _defaultParagraphStyle = id;
                }
            }
        }

        var defaults = styles.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle;
        DefaultFont = defaults?.RunFonts?.Ascii?.Value ?? defaults?.RunFonts?.HighAnsi?.Value;
        var size = defaults?.FontSize?.Val?.Value;
        var normal = _defaultParagraphStyle is null ? null : _styles.GetValueOrDefault(_defaultParagraphStyle);
        size = normal?.StyleRunProperties?.FontSize?.Val?.Value ?? size;
        DefaultFontSizePt = double.TryParse(size, NumberStyles.Float, CultureInfo.InvariantCulture, out var half) ? half / 2 : 11;
        DefaultFont = normal?.StyleRunProperties?.RunFonts?.Ascii?.Value ?? DefaultFont;
    }

    public double DefaultFontSizePt { get; }

    public string? DefaultFont { get; }

    public string? EffectiveParagraphStyle(string? styleId) => styleId ?? _defaultParagraphStyle;

    /// <summary>The semantic role of a paragraph style (first match walking up the basedOn chain).</summary>
    public StyleRole Role(string? styleId)
    {
        styleId = EffectiveParagraphStyle(styleId);
        if (styleId is null)
        {
            return new StyleRole(ParaRole.Normal);
        }

        if (_roles.TryGetValue(styleId, out var cached))
        {
            return cached;
        }

        var role = new StyleRole(ParaRole.Normal);
        foreach (var style in Chain(styleId))
        {
            var id = style.StyleId?.Value ?? string.Empty;
            var name = style.StyleName?.Val?.Value ?? string.Empty;
            if (Classify(id, name) is { } r)
            {
                role = r;
                break;
            }

            if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is { } outline && outline is >= 0 and <= 5)
            {
                role = new StyleRole(ParaRole.Heading, outline + 1);
                break;
            }
        }

        _roles[styleId] = role;
        return role;
    }

    private static StyleRole? Classify(string id, string name)
    {
        static string Norm(string s) => s.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var nid = Norm(id);
        var nname = Norm(name);
        bool Is(params string[] keys) => keys.Any(k => nid == k || nname == k);

        for (var level = 1; level <= 9; level++)
        {
            var key = "heading" + level.ToString(CultureInfo.InvariantCulture);
            if (Is(key))
            {
                return new StyleRole(ParaRole.Heading, Math.Min(level, 6));
            }

            var toc = "toc" + level.ToString(CultureInfo.InvariantCulture);
            if (Is(toc))
            {
                return new StyleRole(ParaRole.TocEntry, level);
            }
        }

        foreach (var kind in Enum.GetValues<CalloutKind>())
        {
            if (Is(Norm(DocxStyleIds.Callout(kind))))
            {
                return new StyleRole(ParaRole.Callout, 0, kind);
            }
        }

        if (Is("title"))
        {
            return new StyleRole(ParaRole.Title);
        }

        if (Is("subtitle"))
        {
            return new StyleRole(ParaRole.Subtitle);
        }

        if (Is("covermeta", "coverdetails"))
        {
            return new StyleRole(ParaRole.CoverMeta);
        }

        if (Is("quoteattribution"))
        {
            return new StyleRole(ParaRole.QuoteAttribution);
        }

        if (Is("quote", "intensequote", "blocktext"))
        {
            return new StyleRole(ParaRole.Quote);
        }

        if (Is("codelabel"))
        {
            return new StyleRole(ParaRole.CodeLabel);
        }

        if (Is("code", "htmlpreformatted", "sourcecode", "codeblock", "preformatted"))
        {
            return new StyleRole(ParaRole.Code);
        }

        if (Is("caption"))
        {
            return new StyleRole(ParaRole.Caption);
        }

        if (Is("mathblock"))
        {
            return new StyleRole(ParaRole.MathBlock);
        }

        if (Is("htmlsource"))
        {
            return new StyleRole(ParaRole.HtmlSource);
        }

        if (Is("horizontalrule"))
        {
            return new StyleRole(ParaRole.HorizontalRule);
        }

        if (Is("tocheading"))
        {
            return new StyleRole(ParaRole.TocHeading);
        }

        if (Is("listcontinue"))
        {
            return new StyleRole(ParaRole.ListContinue);
        }

        if (Is("listparagraph") || nid.StartsWith("listbullet", StringComparison.Ordinal) || nid.StartsWith("listnumber", StringComparison.Ordinal))
        {
            return new StyleRole(ParaRole.ListParagraph);
        }

        return null;
    }

    /// <summary>Numbering inherited from the paragraph style chain (list styles such as "List Bullet").</summary>
    public (int NumId, int Level)? StyleNumbering(string? styleId)
    {
        styleId = EffectiveParagraphStyle(styleId);
        if (styleId is null)
        {
            return null;
        }

        int? numId = null;
        int? level = null;
        foreach (var style in Chain(styleId))
        {
            var numPr = style.StyleParagraphProperties?.NumberingProperties;
            numId ??= numPr?.NumberingId?.Val?.Value;
            level ??= numPr?.NumberingLevelReference?.Val?.Value;
        }

        return numId is { } id ? (id, level ?? 0) : null;
    }

    /// <summary>Font declared by a paragraph style chain.</summary>
    public string? ParagraphFont(string? styleId)
    {
        styleId = EffectiveParagraphStyle(styleId);
        return styleId is null ? DefaultFont : Chain(styleId).Select(s => s.StyleRunProperties?.RunFonts?.Ascii?.Value).FirstOrDefault(f => f is not null) ?? DefaultFont;
    }

    /// <summary>Font declared by a character style chain.</summary>
    public string? CharacterFont(string? styleId) =>
        styleId is null ? null : Chain(styleId).Select(s => s.StyleRunProperties?.RunFonts?.Ascii?.Value).FirstOrDefault(f => f is not null);

    /// <summary>Bold/italic/... flags carried by a character style (e.g. "Strong", "Emphasis").</summary>
    public InlineStyle CharacterFlags(string? styleId)
    {
        if (styleId is null || IsHyperlinkStyle(styleId))
        {
            return InlineStyle.None;
        }

        var flags = InlineStyle.None;
        foreach (var style in Chain(styleId).Reverse())
        {
            var r = style.StyleRunProperties;
            if (r is null)
            {
                continue;
            }

            flags = Apply(flags, InlineStyle.Bold, r.Bold);
            flags = Apply(flags, InlineStyle.Italic, r.Italic);
            flags = Apply(flags, InlineStyle.Strikethrough, r.Strike);
            flags = Apply(flags, InlineStyle.SmallCaps, r.SmallCaps);
            if (r.Underline?.Val is { } u)
            {
                flags = u.Value == UnderlineValues.None ? flags & ~InlineStyle.Underline : flags | InlineStyle.Underline;
            }

            if (r.VerticalTextAlignment?.Val is { } v)
            {
                flags &= ~(InlineStyle.Superscript | InlineStyle.Subscript);
                if (v.Value == VerticalPositionValues.Superscript)
                {
                    flags |= InlineStyle.Superscript;
                }
                else if (v.Value == VerticalPositionValues.Subscript)
                {
                    flags |= InlineStyle.Subscript;
                }
            }
        }

        return flags;
    }

    public static InlineStyle Apply(InlineStyle flags, InlineStyle flag, OnOffType? element)
    {
        if (element is null)
        {
            return flags;
        }

        var on = element.Val is null || element.Val.Value;
        return on ? flags | flag : flags & ~flag;
    }

    public bool IsHyperlinkStyle(string styleId) =>
        Chain(styleId).Any(s => s.StyleId?.Value is "Hyperlink" or "FollowedHyperlink" || string.Equals(s.StyleName?.Val?.Value, "Hyperlink", StringComparison.OrdinalIgnoreCase));

    public bool IsCodeCharacterStyle(string styleId) => Chain(styleId).Any(s =>
    {
        var id = (s.StyleId?.Value ?? string.Empty).ToLowerInvariant();
        var name = (s.StyleName?.Val?.Value ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return id is "codechar" or "htmlcode" or "verbatimchar" or "inlinecode" || name is "codechar" or "htmlcode" or "verbatimchar" or "inlinecode" or "code";
    }) || IsMonospace(CharacterFont(styleId));

    public bool IsStyle(string? styleId, string expected) => styleId is not null && Chain(styleId).Any(s => s.StyleId?.Value == expected);

    /// <summary>True when the table style (or one it is based on) formats the first row as a header.</summary>
    public bool TableStyleHasHeaderRow(string? styleId) =>
        styleId is not null && Chain(styleId).Any(s => s.Elements<TableStyleProperties>().Any(p => p.Type?.Value == TableStyleOverrideValues.FirstRow));

    public static bool IsMonospace(string? font)
    {
        if (string.IsNullOrWhiteSpace(font))
        {
            return false;
        }

        var f = font.ToLowerInvariant();
        return MonoFonts.Any(m => f.Contains(m, StringComparison.Ordinal)) || f.EndsWith(" mono", StringComparison.Ordinal) || f.Contains(" mono ", StringComparison.Ordinal);
    }

    private IEnumerable<Style> Chain(string styleId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = styleId;
        while (current is not null && seen.Add(current) && _styles.TryGetValue(current, out var style))
        {
            yield return style;
            current = style.BasedOn?.Val?.Value;
        }
    }
}
