using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Publishing.Model;

/// <summary>Four edge distances in points (margin or padding).</summary>
public sealed class Spacing
{
    /// <summary>Top edge in points.</summary>
    public double TopPt { get; set; }

    /// <summary>Right edge in points.</summary>
    public double RightPt { get; set; }

    /// <summary>Bottom edge in points.</summary>
    public double BottomPt { get; set; }

    /// <summary>Left edge in points.</summary>
    public double LeftPt { get; set; }

    /// <summary>The same distance on every side.</summary>
    public static Spacing All(double pt) => new() { TopPt = pt, RightPt = pt, BottomPt = pt, LeftPt = pt };

    /// <summary>The distance at the top and bottom, nothing on the sides.</summary>
    public static Spacing Vertical(double pt) => new() { TopPt = pt, BottomPt = pt };
}

/// <summary>Box styling shared by sections, rows, columns and modules.</summary>
public class BoxDesign
{
    /// <summary>Space outside the border.</summary>
    public Spacing Margin { get; set; } = new();

    /// <summary>Space inside the border.</summary>
    public Spacing Padding { get; set; } = new();

    /// <summary>Background colour (hex), or null for none.</summary>
    public string? Background { get; set; }

    /// <summary>Border colour (hex), or null for none.</summary>
    public string? BorderColor { get; set; }

    /// <summary>Border width in points (0 = no border).</summary>
    public double BorderWidthPt { get; set; }

    /// <summary>Corner radius in points.</summary>
    public double CornerRadiusPt { get; set; }

    /// <summary>Largest offset either way, in points (the validator clamps to ±this).</summary>
    public const double MaxOffsetPt = 400;

    /// <summary>Highest Z level (the validator clamps to 0..this).</summary>
    public const int MaxZIndex = 100;

    /// <summary>
    /// Horizontal shift of the box's drawing in points (positive = right). Neighbours keep their places, so the box
    /// may overlap them. 0 = where the flow puts it.
    /// </summary>
    public double OffsetXPt { get; set; }

    /// <summary>Vertical shift of the box's drawing in points (positive = down). Neighbours keep their places.</summary>
    public double OffsetYPt { get; set; }

    /// <summary>Paint order among overlapping boxes: a higher level paints over lower ones; 0 is the normal flow.</summary>
    public int ZIndex { get; set; }
}

/// <summary>Box styling for a module plus text overrides.</summary>
public sealed class ModuleDesign : BoxDesign
{
    /// <summary>Text alignment override; null lets the theme decide.</summary>
    public TextAlignment? Alignment { get; set; }

    /// <summary>Text colour override (hex), or null for the theme colour.</summary>
    public string? TextColor { get; set; }

    /// <summary>Multiplier applied to the theme's font sizes (0.5 to 3).</summary>
    public double FontScale { get; set; } = 1;
}
