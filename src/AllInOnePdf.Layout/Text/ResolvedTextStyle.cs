using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Text;

/// <summary>
/// A fully resolved span style (no unset values) plus the derived numbers the layout needs. The super/subscript
/// rules follow QuestPDF's text style conversion: the font is drawn at 0.6 × size, superscript is raised by
/// 0.35 × size and subscript lowered by 0.25 × size (both of the unreduced size).
/// </summary>
internal sealed class ResolvedTextStyle
{
    /// <summary>Font size factor for super/subscript.</summary>
    public const float ScriptSizeFactor = 0.6f;

    /// <summary>Superscript baseline rise, as a fraction of the (unreduced) size.</summary>
    public const float SuperscriptRise = 0.35f;

    /// <summary>Subscript baseline drop, as a fraction of the (unreduced) size.</summary>
    public const float SubscriptDrop = 0.25f;

    private ShapingOptions? _shaping;
    private float? _hyphenWidth;

    public ResolvedTextStyle(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var s = style.Apply(TextStyle.Default);
        Families = s.FontFamilies is { Count: > 0 } f ? f : ["Inter"];
        Size = Math.Max(0, s.Size ?? 12);
        LineHeight = s.LineHeightFactor is > 0 ? s.LineHeightFactor.Value : 1.2f;
        LetterSpacingEm = s.LetterSpacingEm ?? 0;
        Weight = (int)(s.FontWeight ?? FontWeight.Normal);
        IsItalic = s.IsItalic ?? false;
        Color = s.Color ?? Infrastructure.Color.Black;
        Background = s.Background is { Alpha: > 0 } ? s.Background : default;
        Underline = s.HasUnderline ?? false;
        Strikethrough = s.HasStrikethrough ?? false;
        Position = s.Position ?? FontPosition.Normal;
        DisabledFeatures = s.DisabledFontFeatures ?? [];
        WrapAnywhere = s.WrapAnywhere ?? false;
    }

    public IReadOnlyList<string> Families { get; }

    /// <summary>The style's size (before the super/subscript reduction).</summary>
    public float Size { get; }

    public float LineHeight { get; }

    public float LetterSpacingEm { get; }

    public int Weight { get; }

    public bool IsItalic { get; }

    public Color Color { get; }

    public Color? Background { get; }

    public bool Underline { get; }

    public bool Strikethrough { get; }

    public FontPosition Position { get; }

    public IReadOnlyList<string> DisabledFeatures { get; }

    public bool WrapAnywhere { get; }

    /// <summary>The size glyphs are drawn at.</summary>
    public float FontSize => Position == FontPosition.Normal ? Size : Size * ScriptSizeFactor;

    /// <summary>Vertical baseline offset, y-down (negative raises).</summary>
    public float BaselineShift => Position switch
    {
        FontPosition.Superscript => -Size * SuperscriptRise,
        FontPosition.Subscript => Size * SubscriptDrop,
        _ => 0,
    };

    /// <summary>Letter spacing in points (em × the unreduced size, as QuestPDF does).</summary>
    public float LetterSpacing => LetterSpacingEm * Size;

    /// <summary>Faces and parameters to shape with (font chain resolved through the registry, cached).</summary>
    public ShapingOptions Shaping => _shaping ??= new ShapingOptions(
        FontRegistry.ResolveChain(Families, Weight, IsItalic), FontSize, LetterSpacing, DisabledFeatures, Weight, IsItalic);

    /// <summary>Advance of the "-" shown at a soft-hyphen break.</summary>
    public float HyphenWidth => _hyphenWidth ??= TextShaper.Shape("-", Shaping).Sum(r => r.Width);
}
