using SkiaSharp;

namespace AllInOnePdf.Layout.Svg;

/// <summary>
/// Draws a parsed <see cref="SvgDocument"/> onto an <see cref="SKCanvas"/>: the viewBox is mapped into the box with
/// the root's <c>preserveAspectRatio</c> (default <c>xMidYMid meet</c>), the drawing is clipped to the box, and every
/// shape is filled, then stroked, with inherited presentation properties. Stateless and thread-safe.
/// </summary>
public static class SvgRenderer
{
    /// <summary>Parses and draws <paramref name="svg"/>; false (and nothing drawn) when it does not parse.</summary>
    public static bool TryDraw(SKCanvas canvas, string svg, SKSize box)
    {
        var document = SvgParser.Parse(svg);
        if (document is null)
        {
            return false;
        }

        Draw(canvas, document, box);
        return true;
    }

    /// <summary>Draws <paramref name="document"/> scaled into a box of <paramref name="box"/> at the canvas origin.</summary>
    public static void Draw(SKCanvas canvas, SvgDocument document, SKSize box)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(document);
        if (box.Width <= 0 || box.Height <= 0)
        {
            return;
        }

        var count = canvas.Save();
        try
        {
            canvas.ClipRect(SKRect.Create(0, 0, box.Width, box.Height));
            canvas.Concat(ViewBoxTransform(document, box));
            DrawNode(canvas, document.Root, ComputedStyle.Initial);
        }
        finally
        {
            canvas.RestoreToCount(count);
        }
    }

    /// <summary>The matrix that maps user units (the viewBox, or the intrinsic size without one) into <paramref name="box"/>.</summary>
    public static SKMatrix ViewBoxTransform(SvgDocument document, SKSize box)
    {
        ArgumentNullException.ThrowIfNull(document);
        var size = document.IntrinsicSize;
        var vb = document.ViewBox ?? SKRect.Create(0, 0, size.Width, size.Height);
        if (vb.Width <= 0 || vb.Height <= 0)
        {
            return SKMatrix.Identity;
        }

        var sx = box.Width / vb.Width;
        var sy = box.Height / vb.Height;
        var aspect = document.PreserveAspectRatio;
        if (!aspect.None)
        {
            sx = sy = aspect.Slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
        }

        var tx = (box.Width - vb.Width * sx) * (aspect.None ? 0 : aspect.AlignX) - vb.Left * sx;
        var ty = (box.Height - vb.Height * sy) * (aspect.None ? 0 : aspect.AlignY) - vb.Top * sy;
        return new SKMatrix(sx, 0, tx, 0, sy, ty, 0, 0, 1);
    }

    private static void DrawNode(SKCanvas canvas, SvgNode node, ComputedStyle inherited)
    {
        var style = inherited.Apply(node.Style);
        var opacity = node.Style.Opacity;
        if (opacity <= 0)
        {
            return;
        }

        var count = canvas.Save();
        try
        {
            if (!node.Transform.IsIdentity)
            {
                canvas.Concat(node.Transform);
            }

            switch (node)
            {
                case SvgGroup group:
                    // Group opacity composites the whole group once (a transparency group in the PDF).
                    if (opacity < 1)
                    {
                        using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255)) };
                        canvas.SaveLayer(layer);
                    }

                    foreach (var child in group.Children)
                    {
                        DrawNode(canvas, child, style);
                    }

                    break;

                case SvgShape shape:
                    DrawShape(canvas, shape, style, opacity);
                    break;
            }
        }
        finally
        {
            canvas.RestoreToCount(count);
        }
    }

    private static void DrawShape(SKCanvas canvas, SvgShape shape, ComputedStyle style, float opacity)
    {
        using var path = shape.CreatePath();
        if (path is null)
        {
            return;
        }

        var fill = shape.Kind != SvgShapeKind.Line && !style.Fill.IsNone && style.FillOpacity > 0;
        var stroke = !style.Stroke.IsNone && style.StrokeWidth > 0 && style.StrokeOpacity > 0;

        // Element opacity over a filled and stroked shape needs a layer (else the overlap shows through); alone, fold it into the paint.
        var layered = opacity < 1 && fill && stroke;
        if (layered)
        {
            using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255)) };
            canvas.SaveLayer(layer);
            opacity = 1;
        }

        if (fill)
        {
            path.FillType = style.FillRule;
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = WithOpacity(style.Fill.Color, style.FillOpacity * opacity),
            };
            canvas.DrawPath(path, paint);
        }

        if (stroke)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                Color = WithOpacity(style.Stroke.Color, style.StrokeOpacity * opacity),
                StrokeWidth = style.StrokeWidth,
                StrokeCap = style.LineCap,
                StrokeJoin = style.LineJoin,
                StrokeMiter = style.MiterLimit,
            };
            canvas.DrawPath(path, paint);
        }

        if (layered)
        {
            canvas.Restore();
        }
    }

    private static SKColor WithOpacity(SKColor color, float opacity) =>
        color.WithAlpha((byte)Math.Round(color.Alpha * Math.Clamp(opacity, 0, 1)));

    /// <summary>Resolved (inherited) presentation properties at one node.</summary>
    private readonly record struct ComputedStyle(
        SvgPaint Fill,
        SvgPaint Stroke,
        float StrokeWidth,
        SKStrokeCap LineCap,
        SKStrokeJoin LineJoin,
        float MiterLimit,
        float FillOpacity,
        float StrokeOpacity,
        SKPathFillType FillRule)
    {
        /// <summary>SVG's initial values: black fill, no stroke, width 1, butt caps, miter joins (limit 4), nonzero.</summary>
        public static readonly ComputedStyle Initial = new(
            SvgPaint.FromColor(SKColors.Black), SvgPaint.None, 1, SKStrokeCap.Butt, SKStrokeJoin.Miter, 4, 1, 1, SKPathFillType.Winding);

        public ComputedStyle Apply(SvgStyle s) => new(
            s.Fill ?? Fill,
            s.Stroke ?? Stroke,
            s.StrokeWidth ?? StrokeWidth,
            s.StrokeLineCap ?? LineCap,
            s.StrokeLineJoin ?? LineJoin,
            s.StrokeMiterLimit ?? MiterLimit,
            s.FillOpacity ?? FillOpacity,
            s.StrokeOpacity ?? StrokeOpacity,
            s.FillRule ?? FillRule);
    }
}
