using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Rendering;

/// <summary>
/// The space a <see cref="BoxDesign"/> takes around its content, exactly as the renderer draws it: margins and
/// padding capped at 400 pt (non-numbers and negatives count as 0) and a border on both sides when it has a width.
/// </summary>
internal static class BoxMetrics
{
    /// <summary>A spacing value as the renderer uses it.</summary>
    public static float Pt(double? value) => value is { } v && double.IsFinite(v) && v > 0 ? (float)Math.Min(v, 400) : 0f;

    /// <summary>The box draws a border.</summary>
    public static bool HasBorder(BoxDesign d) => d.BorderWidthPt > 0 && double.IsFinite(d.BorderWidthPt);

    /// <summary>Border width as drawn (0 without a border).</summary>
    public static double Border(BoxDesign d) => HasBorder(d) ? d.BorderWidthPt : 0;

    /// <summary>Width a design box takes from its container: margins, padding and both borders.</summary>
    public static double Horizontal(BoxDesign d) =>
        Pt(d.Margin?.LeftPt) + Pt(d.Margin?.RightPt) + Pt(d.Padding?.LeftPt) + Pt(d.Padding?.RightPt) + (2 * Border(d));

    /// <summary>Height a design box adds around its content: margins, padding and both borders.</summary>
    public static double Vertical(BoxDesign d) =>
        Pt(d.Margin?.TopPt) + Pt(d.Margin?.BottomPt) + Pt(d.Padding?.TopPt) + Pt(d.Padding?.BottomPt) + (2 * Border(d));

    /// <summary>Distance from the box's left edge (outside the margin) to its content's left edge.</summary>
    public static double Left(BoxDesign d) => Pt(d.Margin?.LeftPt) + Border(d) + Pt(d.Padding?.LeftPt);

    /// <summary>Distance from the box's top edge (outside the margin) to its content's top edge.</summary>
    public static double Top(BoxDesign d) => Pt(d.Margin?.TopPt) + Border(d) + Pt(d.Padding?.TopPt);
}
