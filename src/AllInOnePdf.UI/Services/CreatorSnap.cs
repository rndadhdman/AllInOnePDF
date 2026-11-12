using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.UI.Services;

/// <summary>A box on a free-flow canvas, in points from the canvas's top-left.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct CanvasBox(double X, double Y, double Width, double Height)
{
    /// <summary>The box of a placed module.</summary>
    public static CanvasBox Of(PlacedModule item) => new(item.XPt, item.YPt, item.WidthPt, item.HeightPt);
}

/// <summary>The result of snapping a box: the box itself and the guide lines to draw (canvas coordinates), if any.</summary>
/// <param name="Box">The snapped box.</param>
/// <param name="GuideX">A vertical guide at this x while a horizontal snap is active.</param>
/// <param name="GuideY">A horizontal guide at this y while a vertical snap is active.</param>
public readonly record struct SnapResult(CanvasBox Box, double? GuideX, double? GuideY);

/// <summary>
/// The free-flow canvas's snapping and resizing maths (the reference the canvas script mirrors): a dragged box snaps
/// when one of its edges or its centre comes within the tolerance of the canvas's edges or centre lines, or of another
/// item's edges or centre; the closest candidate wins per axis. Resizing moves only the dragged edges, keeps at least
/// <see cref="PlacedModule.MinSizePt"/>, and corner handles keep the aspect ratio when asked.
/// </summary>
public static class CreatorSnap
{
    /// <summary>Snap distance on screen in CSS pixels; the canvas converts it to points with its zoom.</summary>
    public const double TolerancePx = 4;

    /// <summary>The x lines a box can snap to: the canvas's left, centre and right, and every other item's left, centre and right.</summary>
    public static List<double> TargetsX(double canvasWidth, IEnumerable<CanvasBox> others)
    {
        var list = new List<double> { 0, canvasWidth / 2, canvasWidth };
        foreach (var o in others)
        {
            list.Add(o.X);
            list.Add(o.X + (o.Width / 2));
            list.Add(o.X + o.Width);
        }

        return list;
    }

    /// <summary>The y lines a box can snap to: the canvas's top, centre and bottom, and every other item's top, centre and bottom.</summary>
    public static List<double> TargetsY(double canvasHeight, IEnumerable<CanvasBox> others)
    {
        var list = new List<double> { 0, canvasHeight / 2, canvasHeight };
        foreach (var o in others)
        {
            list.Add(o.Y);
            list.Add(o.Y + (o.Height / 2));
            list.Add(o.Y + o.Height);
        }

        return list;
    }

    /// <summary>
    /// The smallest shift that puts one of <paramref name="edges"/> on one of <paramref name="targets"/>, when it is
    /// within <paramref name="tolerance"/>; the shift and the target line, or (0, null).
    /// </summary>
    public static (double Offset, double? Guide) Axis(IReadOnlyList<double> edges, IReadOnlyList<double> targets, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(targets);
        var best = double.PositiveInfinity;
        var offset = 0.0;
        double? guide = null;
        foreach (var e in edges)
        {
            foreach (var t in targets)
            {
                var d = Math.Abs(t - e);
                if (d <= tolerance && d < best)
                {
                    best = d;
                    offset = t - e;
                    guide = t;
                }
            }
        }

        return (offset, guide);
    }

    /// <summary>Snaps a moved box by its left, centre and right (and top, middle, bottom).</summary>
    public static SnapResult Move(CanvasBox box, double canvasWidth, double canvasHeight, IEnumerable<CanvasBox> others, double tolerance)
    {
        var list = others as IReadOnlyCollection<CanvasBox> ?? others.ToList();
        var (dx, gx) = Axis([box.X, box.X + (box.Width / 2), box.X + box.Width], TargetsX(canvasWidth, list), tolerance);
        var (dy, gy) = Axis([box.Y, box.Y + (box.Height / 2), box.Y + box.Height], TargetsY(canvasHeight, list), tolerance);
        return new SnapResult(box with { X = box.X + dx, Y = box.Y + dy }, gx, gy);
    }

    /// <summary>
    /// Resizes <paramref name="start"/> by dragging <paramref name="handle"/> (<c>n</c>, <c>ne</c>, <c>e</c>, <c>se</c>,
    /// <c>s</c>, <c>sw</c>, <c>w</c>, <c>nw</c>) by (<paramref name="dx"/>, <paramref name="dy"/>) points. A corner keeps
    /// the width-to-height ratio when <paramref name="keepAspect"/> (the larger relative change wins); no side gets
    /// smaller than <paramref name="minSize"/>, and the opposite edge stays where it was.
    /// </summary>
    public static CanvasBox Resize(CanvasBox start, string handle, double dx, double dy, bool keepAspect, double minSize = PlacedModule.MinSizePt)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var west = handle.Contains('w', StringComparison.Ordinal);
        var east = handle.Contains('e', StringComparison.Ordinal);
        var north = handle.Contains('n', StringComparison.Ordinal);
        var south = handle.Contains('s', StringComparison.Ordinal);

        var w = start.Width + (east ? dx : west ? -dx : 0);
        var h = start.Height + (south ? dy : north ? -dy : 0);
        w = Math.Max(minSize, w);
        h = Math.Max(minSize, h);

        var corner = (west || east) && (north || south);
        if (keepAspect && corner && start.Width > 0 && start.Height > 0)
        {
            var ratio = start.Width / start.Height;
            if (Math.Abs(w / start.Width - 1) >= Math.Abs(h / start.Height - 1))
            {
                h = w / ratio;
            }
            else
            {
                w = h * ratio;
            }

            if (w < minSize)
            {
                w = minSize;
                h = w / ratio;
            }

            if (h < minSize)
            {
                h = minSize;
                w = h * ratio;
            }
        }

        var x = west ? start.X + start.Width - w : start.X;
        var y = north ? start.Y + start.Height - h : start.Y;
        return new CanvasBox(x, y, w, h);
    }

    /// <summary>
    /// Snaps the edges a resize moves (<paramref name="handle"/>) to the targets: the moved edge shifts onto the closest
    /// line within the tolerance and the opposite edge stays. Aspect-locked corner drags are not snapped.
    /// </summary>
    public static SnapResult SnapResize(CanvasBox box, string handle, double canvasWidth, double canvasHeight, IEnumerable<CanvasBox> others, double tolerance, double minSize = PlacedModule.MinSizePt)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var list = others as IReadOnlyCollection<CanvasBox> ?? others.ToList();
        double? gx = null, gy = null;
        var (x, y, w, h) = (box.X, box.Y, box.Width, box.Height);
        if (handle.Contains('e', StringComparison.Ordinal) || handle.Contains('w', StringComparison.Ordinal))
        {
            var west = handle.Contains('w', StringComparison.Ordinal);
            var (d, g) = Axis([west ? x : x + w], TargetsX(canvasWidth, list), tolerance);
            if (g is not null && w + (west ? -d : d) >= minSize)
            {
                (x, w, gx) = west ? (x + d, w - d, g) : (x, w + d, g);
            }
        }

        if (handle.Contains('n', StringComparison.Ordinal) || handle.Contains('s', StringComparison.Ordinal))
        {
            var north = handle.Contains('n', StringComparison.Ordinal);
            var (d, g) = Axis([north ? y : y + h], TargetsY(canvasHeight, list), tolerance);
            if (g is not null && h + (north ? -d : d) >= minSize)
            {
                (y, h, gy) = north ? (y + d, h - d, g) : (y, h + d, g);
            }
        }

        return new SnapResult(new CanvasBox(x, y, w, h), gx, gy);
    }
}
