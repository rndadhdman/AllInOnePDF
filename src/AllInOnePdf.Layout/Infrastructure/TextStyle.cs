namespace AllInOnePdf.Layout.Infrastructure;

/// <summary>CSS-like font weights; the font registry picks the closest face a family has.</summary>
public enum FontWeight
{
    Thin = 100,
    ExtraLight = 200,
    Light = 300,
    Normal = 400,
    Medium = 500,
    SemiBold = 600,
    Bold = 700,
    ExtraBold = 800,
    Black = 900,
}

/// <summary>Baseline position of a span.</summary>
public enum FontPosition
{
    Normal,
    Subscript,
    Superscript,
}

/// <summary>
/// The style of a run of text. Every property is optional: a null means "inherit from the enclosing style", and
/// <see cref="Apply"/> fills the gaps from a parent. <see cref="Default"/> is the root of every chain. Immutable:
/// the fluent setters return a new instance, so a style can be shared freely.
/// </summary>
public sealed record TextStyle
{
    /// <summary>Family names in order of preference; the first family that has the glyph is used per run.</summary>
    public IReadOnlyList<string>? FontFamilies { get; init; }

    public float? Size { get; init; }

    /// <summary>Line height as a multiple of the font size; the line box is <c>Size × LineHeightFactor</c>.</summary>
    public float? LineHeightFactor { get; init; }

    /// <summary>Extra advance after every glyph, in em (0.05 = 5 % of the font size).</summary>
    public float? LetterSpacingEm { get; init; }

    public FontWeight? FontWeight { get; init; }

    public bool? IsItalic { get; init; }

    public Color? Color { get; init; }

    /// <summary>Painted behind the glyph run (highlights, inline code).</summary>
    public Color? Background { get; init; }

    public bool? HasUnderline { get; init; }

    public bool? HasStrikethrough { get; init; }

    public FontPosition? Position { get; init; }

    /// <summary>OpenType features turned off for the run ("calt", "liga").</summary>
    public IReadOnlyList<string>? DisabledFontFeatures { get; init; }

    /// <summary>Allow a line break inside a word (code blocks) instead of only at spaces, hyphens and soft hyphens.</summary>
    public bool? WrapAnywhere { get; init; }

    /// <summary>The root style: Inter 12 pt, line height 1.2, black, normal weight.</summary>
    public static TextStyle Default { get; } = new()
    {
        FontFamilies = ["Inter"],
        Size = 12,
        LineHeightFactor = 1.2f,
        LetterSpacingEm = 0,
        FontWeight = Infrastructure.FontWeight.Normal,
        IsItalic = false,
        Color = Infrastructure.Color.Black,
        Background = null,
        HasUnderline = false,
        HasStrikethrough = false,
        Position = FontPosition.Normal,
        DisabledFontFeatures = [],
        WrapAnywhere = false,
    };

    /// <summary>A style with every property unset (inherits everything).</summary>
    public static TextStyle Inherit { get; } = new();

    /// <summary>This style with every unset property taken from <paramref name="parent"/>. Disabled features accumulate.</summary>
    public TextStyle Apply(TextStyle parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return new TextStyle
        {
            FontFamilies = FontFamilies ?? parent.FontFamilies,
            Size = Size ?? parent.Size,
            LineHeightFactor = LineHeightFactor ?? parent.LineHeightFactor,
            LetterSpacingEm = LetterSpacingEm ?? parent.LetterSpacingEm,
            FontWeight = FontWeight ?? parent.FontWeight,
            IsItalic = IsItalic ?? parent.IsItalic,
            Color = Color ?? parent.Color,
            Background = Background ?? parent.Background,
            HasUnderline = HasUnderline ?? parent.HasUnderline,
            HasStrikethrough = HasStrikethrough ?? parent.HasStrikethrough,
            Position = Position ?? parent.Position,
            DisabledFontFeatures = DisabledFontFeatures is null || DisabledFontFeatures.Count == 0
                ? parent.DisabledFontFeatures
                : parent.DisabledFontFeatures is null ? DisabledFontFeatures : [.. parent.DisabledFontFeatures, .. DisabledFontFeatures],
            WrapAnywhere = WrapAnywhere ?? parent.WrapAnywhere,
        };
    }

    // ---- fluent setters (QuestPDF names; the data properties are named differently so these can be methods)

    public TextStyle FontFamily(params string[] families) => this with { FontFamilies = families };

    public TextStyle FontSize(float size) => this with { Size = size };

    public TextStyle LineHeight(float factor) => this with { LineHeightFactor = factor };

    public TextStyle LetterSpacing(float em) => this with { LetterSpacingEm = em };

    public TextStyle FontColor(Color color) => this with { Color = color };

    public TextStyle BackgroundColor(Color color) => this with { Background = color };

    public TextStyle Weight(FontWeight weight) => this with { FontWeight = weight };

    public TextStyle Bold() => this with { FontWeight = Infrastructure.FontWeight.Bold };

    public TextStyle SemiBold() => this with { FontWeight = Infrastructure.FontWeight.SemiBold };

    public TextStyle Medium() => this with { FontWeight = Infrastructure.FontWeight.Medium };

    public TextStyle NormalWeight() => this with { FontWeight = Infrastructure.FontWeight.Normal };

    public TextStyle Italic(bool value = true) => this with { IsItalic = value };

    public TextStyle Underline(bool value = true) => this with { HasUnderline = value };

    public TextStyle Strikethrough(bool value = true) => this with { HasStrikethrough = value };

    public TextStyle Superscript() => this with { Position = FontPosition.Superscript };

    public TextStyle Subscript() => this with { Position = FontPosition.Subscript };

    public TextStyle DisableFontFeature(string feature) => this with { DisabledFontFeatures = [.. DisabledFontFeatures ?? [], feature] };

    public TextStyle BreakAnywhere() => this with { WrapAnywhere = true };
}
