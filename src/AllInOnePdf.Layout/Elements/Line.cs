using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Elements;

/// <summary>
/// A horizontal or vertical rule. A horizontal line plans <c>(available width, thickness)</c> (width 0 when the
/// width is unconstrained), a vertical one <c>(thickness, available height)</c>; Wrap when the thickness does not
/// fit. The line is drawn across the whole width (height) it is drawn with, centred in its thickness.
/// </summary>
internal sealed class Line : Element, ILineDescriptor
{
    private const float Epsilon = 0.001f;

    public Line(bool vertical, float thickness)
    {
        if (!(thickness >= 0) || !float.IsFinite(thickness))
        {
            throw new ArgumentOutOfRangeException(nameof(thickness), thickness, "A line's thickness must be a finite, non-negative number.");
        }

        IsVertical = vertical;
        Thickness = thickness;
    }

    public bool IsVertical { get; }

    public float Thickness { get; }

    public Color Color { get; private set; } = Color.Black;

    /// <summary>Alternating on/off lengths in points; null for a solid line.</summary>
    public float[]? DashPattern { get; private set; }

    /// <inheritdoc />
    public ILineDescriptor LineColor(Color color)
    {
        Color = color;
        return this;
    }

    /// <inheritdoc />
    public ILineDescriptor LineDashPattern(float[] dashPattern, Unit unit = Unit.Point)
    {
        ArgumentNullException.ThrowIfNull(dashPattern);
        if (dashPattern.Length == 0 || dashPattern.Any(d => !(d >= 0) || !float.IsFinite(d)) || dashPattern.All(d => d == 0))
        {
            throw new ArgumentException("A dash pattern needs at least one positive length and no negative ones.", nameof(dashPattern));
        }

        var pattern = dashPattern.Select(d => d.ToPoints(unit));

        // An odd list repeats itself (SVG's rule) so on and off keep alternating.
        DashPattern = (dashPattern.Length % 2 == 1 ? pattern.Concat(pattern) : pattern).ToArray();
        return this;
    }

    public override SpacePlan Measure(Size availableSpace)
    {
        if (availableSpace.Width < -Epsilon || availableSpace.Height < -Epsilon)
        {
            return SpacePlan.Wrap();
        }

        return IsVertical
            ? availableSpace.Width + Epsilon >= Thickness ? SpacePlan.FullRender(Thickness, Length(availableSpace.Height)) : SpacePlan.Wrap()
            : availableSpace.Height + Epsilon >= Thickness ? SpacePlan.FullRender(Length(availableSpace.Width), Thickness) : SpacePlan.Wrap();
    }

    /// <summary>The line's length in a plan: the offered length, or nothing when the offer is unconstrained.</summary>
    private static float Length(float available) => available >= Size.Infinity ? 0 : Math.Max(0, available);

    public override void Draw(Size availableSpace)
    {
        if (Thickness <= 0)
        {
            return;
        }

        var half = Thickness / 2;
        if (IsVertical)
        {
            Canvas.DrawLine(new Position(half, 0), new Position(half, availableSpace.Height), Thickness, Color, DashPattern);
        }
        else
        {
            Canvas.DrawLine(new Position(0, half), new Position(availableSpace.Width, half), Thickness, Color, DashPattern);
        }
    }
}
