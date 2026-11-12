using SkiaSharp;

namespace AllInOnePdf.Layout.Svg;

/// <summary>A parsed SVG: the root viewport and the drawable tree. Immutable, so one instance can be drawn from any thread.</summary>
public sealed class SvgDocument
{
    /// <summary>The default size of an SVG without width, height or viewBox (CSS's replaced-element default).</summary>
    public static readonly SKSize DefaultSize = new(300, 150);

    internal SvgDocument(SKRect? viewBox, float? width, float? height, SvgAspectRatio preserveAspectRatio, SvgGroup root)
    {
        ViewBox = viewBox;
        Width = width;
        Height = height;
        PreserveAspectRatio = preserveAspectRatio;
        Root = root;
    }

    /// <summary>The <c>viewBox</c> (min-x, min-y, width, height), when present and valid.</summary>
    public SKRect? ViewBox { get; }

    /// <summary>The root's absolute <c>width</c> in CSS pixels; null when missing or relative.</summary>
    public float? Width { get; }

    /// <summary>The root's absolute <c>height</c> in CSS pixels; null when missing or relative.</summary>
    public float? Height { get; }

    public SvgAspectRatio PreserveAspectRatio { get; }

    /// <summary>The root element's content (the root itself, with its own style, transform and opacity).</summary>
    public SvgGroup Root { get; }

    /// <summary>
    /// The natural size: width and height when both are given; one of them with the viewBox ratio for the other; the
    /// viewBox size; else <see cref="DefaultSize"/>.
    /// </summary>
    public SKSize IntrinsicSize
    {
        get
        {
            var vb = ViewBox;
            return (Width, Height) switch
            {
                ({ } w, { } h) => new SKSize(w, h),
                ({ } w, null) when vb is { } box => new SKSize(w, w * box.Height / box.Width),
                (null, { } h) when vb is { } box => new SKSize(h * box.Width / box.Height, h),
                ({ } w, null) => new SKSize(w, DefaultSize.Height),
                (null, { } h) => new SKSize(DefaultSize.Width, h),
                _ => vb is { } box ? new SKSize(box.Width, box.Height) : DefaultSize,
            };
        }
    }

    /// <summary>Width / height of <see cref="IntrinsicSize"/>.</summary>
    public float AspectRatio
    {
        get
        {
            var size = IntrinsicSize;
            return size.Width > 0 && size.Height > 0 ? size.Width / size.Height : 1;
        }
    }
}

/// <summary>The <c>preserveAspectRatio</c> attribute of the root.</summary>
/// <param name="AlignX">0 = min, 0.5 = mid, 1 = max; ignored when <paramref name="None"/>.</param>
/// <param name="AlignY">0 = min, 0.5 = mid, 1 = max.</param>
/// <param name="Slice">True for <c>slice</c> (cover), false for <c>meet</c> (contain).</param>
/// <param name="None">True for <c>none</c>: stretch non-uniformly.</param>
public readonly record struct SvgAspectRatio(float AlignX, float AlignY, bool Slice, bool None)
{
    /// <summary>The SVG default, <c>xMidYMid meet</c>.</summary>
    public static readonly SvgAspectRatio Default = new(0.5f, 0.5f, false, false);
}

/// <summary>A paint value: a colour, or <c>none</c>.</summary>
public readonly record struct SvgPaint(SKColor Color, bool IsNone)
{
    public static readonly SvgPaint None = new(SKColors.Transparent, true);

    public static SvgPaint FromColor(SKColor color) => new(color, false);
}

/// <summary>Presentation properties as declared on one element (attributes overridden by the <c>style</c> attribute); null means "inherit".</summary>
public sealed record SvgStyle
{
    public static readonly SvgStyle Empty = new();

    public SvgPaint? Fill { get; init; }

    public SvgPaint? Stroke { get; init; }

    public float? StrokeWidth { get; init; }

    public SKStrokeCap? StrokeLineCap { get; init; }

    public SKStrokeJoin? StrokeLineJoin { get; init; }

    public float? StrokeMiterLimit { get; init; }

    public float? FillOpacity { get; init; }

    public float? StrokeOpacity { get; init; }

    public SKPathFillType? FillRule { get; init; }

    /// <summary>Not inherited: multiplies the element's (group's) rendering.</summary>
    public float Opacity { get; init; } = 1;
}

/// <summary>A node of the drawable tree.</summary>
public abstract class SvgNode
{
    private protected SvgNode(SvgStyle style, SKMatrix transform)
    {
        Style = style;
        Transform = transform;
    }

    public SvgStyle Style { get; }

    /// <summary>The <c>transform</c> attribute, identity when absent.</summary>
    public SKMatrix Transform { get; }
}

/// <summary>An <c>svg</c> root or a <c>g</c> group.</summary>
public sealed class SvgGroup : SvgNode
{
    internal SvgGroup(SvgStyle style, SKMatrix transform, IReadOnlyList<SvgNode> children)
        : base(style, transform)
    {
        Children = children;
    }

    public IReadOnlyList<SvgNode> Children { get; }
}

/// <summary>The kinds of shape the renderer draws.</summary>
public enum SvgShapeKind
{
    Path,
    Rect,
    Circle,
    Ellipse,
    Line,
    Polyline,
    Polygon,
}

/// <summary>
/// A basic shape. <see cref="Values"/> holds the geometry in element order: rect x, y, width, height, rx, ry;
/// circle cx, cy, r; ellipse cx, cy, rx, ry; line x1, y1, x2, y2; polyline/polygon the point coordinates. Paths keep
/// their <see cref="PathData"/>.
/// </summary>
public sealed class SvgShape : SvgNode
{
    internal SvgShape(SvgShapeKind kind, SvgStyle style, SKMatrix transform, IReadOnlyList<float> values, string? pathData)
        : base(style, transform)
    {
        Kind = kind;
        Values = values;
        PathData = pathData;
    }

    public SvgShapeKind Kind { get; }

    public IReadOnlyList<float> Values { get; }

    public string? PathData { get; }

    /// <summary>Builds the Skia path for this shape (the caller disposes it); null when the geometry draws nothing.</summary>
    public SKPath? CreatePath()
    {
        var v = Values;
        if (Kind == SvgShapeKind.Path)
        {
            return string.IsNullOrWhiteSpace(PathData) ? null : SKPath.ParseSvgPathData(PathData);
        }

        using var builder = new SKPathBuilder();
        switch (Kind)
        {
            case SvgShapeKind.Rect when v[2] > 0 && v[3] > 0:
                var rect = SKRect.Create(v[0], v[1], v[2], v[3]);
                if (v[4] > 0 || v[5] > 0)
                {
                    builder.AddRoundRect(rect, Math.Min(v[4], v[2] / 2), Math.Min(v[5], v[3] / 2), SKPathDirection.Clockwise);
                }
                else
                {
                    builder.AddRect(rect, SKPathDirection.Clockwise);
                }

                break;

            case SvgShapeKind.Circle when v[2] > 0:
                builder.AddCircle(v[0], v[1], v[2], SKPathDirection.Clockwise);
                break;

            case SvgShapeKind.Ellipse when v[2] > 0 && v[3] > 0:
                builder.AddOval(new SKRect(v[0] - v[2], v[1] - v[3], v[0] + v[2], v[1] + v[3]), SKPathDirection.Clockwise);
                break;

            case SvgShapeKind.Line:
                builder.MoveTo(v[0], v[1]);
                builder.LineTo(v[2], v[3]);
                break;

            case SvgShapeKind.Polyline or SvgShapeKind.Polygon when v.Count >= 4:
                builder.MoveTo(v[0], v[1]);
                for (var i = 2; i + 1 < v.Count; i += 2)
                {
                    builder.LineTo(v[i], v[i + 1]);
                }

                if (Kind == SvgShapeKind.Polygon)
                {
                    builder.Close();
                }

                break;

            default:
                return null;
        }

        return builder.Detach();
    }
}