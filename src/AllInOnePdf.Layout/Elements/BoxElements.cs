using AllInOnePdf.Layout.Infrastructure;
using SkiaSharp;

namespace AllInOnePdf.Layout.Elements;

/// <summary>Insets the child. Negative values are allowed: the child is offered more space and drawn shifted.</summary>
internal sealed class PaddingElement : ContainerElement
{
    public float Top { get; set; }

    public float Right { get; set; }

    public float Bottom { get; set; }

    public float Left { get; set; }

    public override SpacePlan Measure(Size availableSpace)
    {
        if (Child is null)
        {
            return SpacePlan.Empty();
        }

        var inner = Inner(availableSpace);
        if (inner.Width < -LayoutMath.Epsilon || inner.Height < -LayoutMath.Epsilon)
        {
            return SpacePlan.Wrap();
        }

        var plan = Child.Measure(new Size(Math.Max(0, inner.Width), Math.Max(0, inner.Height)));
        if (!plan.HasContent)
        {
            return plan;
        }

        var size = new Size(Math.Max(0, plan.Width + Left + Right), Math.Max(0, plan.Height + Top + Bottom));
        return plan.IsPartial ? SpacePlan.PartialRender(size) : SpacePlan.FullRender(size);
    }

    public override void Draw(Size availableSpace)
    {
        if (Child is null)
        {
            return;
        }

        var inner = Inner(availableSpace);
        var offset = new Position(Left, Top);
        Canvas.Translate(offset);
        Child.Draw(new Size(Math.Max(0, inner.Width), Math.Max(0, inner.Height)));
        Canvas.Translate(offset.Reverse());
    }

    private Size Inner(Size available) => new(available.Width - Left - Right, available.Height - Top - Bottom);
}

/// <summary>
/// Background, borders and corner radius of one box (QuestPDF's StyledBox): the background fills the drawn size under
/// the child, borders are stroked over it centred on the edges, and a corner radius rounds the background and border
/// and clips the child.
/// </summary>
internal sealed class StyledBoxElement : ContainerElement
{
    public Color? BackgroundColor { get; set; }

    public float BorderTop { get; set; }

    public float BorderRight { get; set; }

    public float BorderBottom { get; set; }

    public float BorderLeft { get; set; }

    public Color BorderColor { get; set; } = Color.Black;

    public float CornerRadius { get; set; }

    public override void Draw(Size availableSpace)
    {
        var radius = Math.Max(0, Math.Min(CornerRadius, Math.Min(availableSpace.Width, availableSpace.Height) / 2));
        if (BackgroundColor is { } background && background.Alpha > 0)
        {
            Canvas.DrawRectangle(Position.Zero, availableSpace, background, radius);
        }

        if (Child is not null)
        {
            if (radius > 0)
            {
                Canvas.Save();
                Canvas.ClipRectangle(Position.Zero, availableSpace, radius);
                Child.Draw(availableSpace);
                Canvas.Restore();
            }
            else
            {
                Child.Draw(availableSpace);
            }
        }

        DrawBorders(availableSpace, radius);
    }

    private void DrawBorders(Size size, float radius)
    {
        if (BorderColor.Alpha == 0 || (BorderTop <= 0 && BorderRight <= 0 && BorderBottom <= 0 && BorderLeft <= 0))
        {
            return;
        }

        // Rounded corners: QuestPDF fills the band between the rounded box and the box inset by each side's width
        // (inner radius reduced by the width), so the whole border lies inside the box. That needs a path, drawn on
        // the native canvas; canvases without one get the centred outline.
        if (radius > 0 && Canvas.Native is { } native)
        {
            DrawRoundedBand(native, size, radius);
            return;
        }

        // A uniform square border is a stroked rectangle (QuestPDF writes `re S`).
        if (BorderTop == BorderRight && BorderTop == BorderBottom && BorderTop == BorderLeft)
        {
            Canvas.DrawRectangleOutline(Position.Zero, size, BorderColor, BorderTop, radius);
            return;
        }

        // Individual sides: QuestPDF fills the band between the outer rectangle (the edges pushed out by half of each
        // side's width) and the inner one (pulled in by half), which PDF viewers rasterise as crisp filled rectangles
        // rather than anti-aliased strokes. Top and bottom bands span the full outer width; left and right fill
        // the height between them.
        var left = BorderLeft / 2;
        var right = BorderRight / 2;
        var top = BorderTop / 2;
        var bottom = BorderBottom / 2;
        var outerWidth = size.Width + left + right;
        if (BorderTop > 0)
        {
            Canvas.DrawRectangle(new Position(-left, -top), new Size(outerWidth, BorderTop), BorderColor);
        }

        if (BorderBottom > 0)
        {
            Canvas.DrawRectangle(new Position(-left, size.Height - bottom), new Size(outerWidth, BorderBottom), BorderColor);
        }

        var sideHeight = size.Height - top - bottom;
        if (BorderLeft > 0 && sideHeight > 0)
        {
            Canvas.DrawRectangle(new Position(-left, top), new Size(BorderLeft, sideHeight), BorderColor);
        }

        if (BorderRight > 0 && sideHeight > 0)
        {
            Canvas.DrawRectangle(new Position(size.Width - right, top), new Size(BorderRight, sideHeight), BorderColor);
        }
    }

    /// <summary>Fills the rounded box minus the box inset by each side's width (even-odd), as QuestPDF does.</summary>
    private void DrawRoundedBand(SKCanvas native, Size size, float radius)
    {
        using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = BorderColor.ToSKColor() };
        using var outer = new SKRoundRect(SKRect.Create(0, 0, size.Width, size.Height), radius);
        var innerRect = new SKRect(BorderLeft, BorderTop, size.Width - BorderRight, size.Height - BorderBottom);
        if (innerRect.Width <= 0 || innerRect.Height <= 0)
        {
            native.DrawRoundRect(outer, paint);
            return;
        }

        using var inner = new SKRoundRect();
        inner.SetRectRadii(innerRect,
        [
            new SKPoint(Math.Max(0, radius - BorderLeft), Math.Max(0, radius - BorderTop)),
            new SKPoint(Math.Max(0, radius - BorderRight), Math.Max(0, radius - BorderTop)),
            new SKPoint(Math.Max(0, radius - BorderRight), Math.Max(0, radius - BorderBottom)),
            new SKPoint(Math.Max(0, radius - BorderLeft), Math.Max(0, radius - BorderBottom)),
        ]);
        native.DrawRoundRectDifference(outer, inner, paint);
    }
}

/// <summary>Horizontal placement of an aligned child.</summary>
internal enum HorizontalAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>Vertical placement of an aligned child.</summary>
internal enum VerticalAlignment
{
    Top,
    Middle,
    Bottom,
}

/// <summary>
/// Positions the child at its natural size. An unset direction stretches: the child gets the full available extent in
/// that direction.
/// </summary>
internal sealed class AlignmentElement : ContainerElement
{
    public HorizontalAlignment? Horizontal { get; set; }

    public VerticalAlignment? Vertical { get; set; }

    public override void Draw(Size availableSpace)
    {
        if (Child is null)
        {
            return;
        }

        var plan = Child.Measure(availableSpace);
        if (plan.IsWrap)
        {
            return;
        }

        var size = new Size(
            Horizontal is null ? availableSpace.Width : Math.Min(plan.Width, availableSpace.Width),
            Vertical is null ? availableSpace.Height : Math.Min(plan.Height, availableSpace.Height));

        var left = Horizontal switch
        {
            HorizontalAlignment.Center => (availableSpace.Width - size.Width) / 2,
            HorizontalAlignment.Right => availableSpace.Width - size.Width,
            _ => 0,
        };

        var top = Vertical switch
        {
            VerticalAlignment.Middle => (availableSpace.Height - size.Height) / 2,
            VerticalAlignment.Bottom => availableSpace.Height - size.Height,
            _ => 0,
        };

        var offset = new Position(left, top);
        Canvas.Translate(offset);
        Child.Draw(size);
        Canvas.Translate(offset.Reverse());
    }
}

/// <summary>Width and height limits (Width, MinWidth, MaxWidth, Height, MinHeight, MaxHeight).</summary>
internal sealed class ConstrainedElement : ContainerElement
{
    public float MinWidth { get; set; }

    public float MaxWidth { get; set; } = Size.Infinity;

    public float MinHeight { get; set; }

    public float MaxHeight { get; set; } = Size.Infinity;

    public override SpacePlan Measure(Size availableSpace)
    {
        if (MinWidth > availableSpace.Width + LayoutMath.Epsilon || MinHeight > availableSpace.Height + LayoutMath.Epsilon)
        {
            return SpacePlan.Wrap();
        }

        var inner = Inner(availableSpace);
        var plan = Child?.Measure(inner) ?? SpacePlan.Empty();
        if (plan.IsWrap)
        {
            return plan;
        }

        if (plan.IsEmpty && MinWidth <= 0 && MinHeight <= 0)
        {
            return plan;
        }

        var size = new Size(
            Math.Min(Math.Max(MinWidth, plan.Width), inner.Width),
            Math.Min(Math.Max(MinHeight, plan.Height), inner.Height));
        return plan.IsPartial ? SpacePlan.PartialRender(size) : SpacePlan.FullRender(size);
    }

    public override void Draw(Size availableSpace) => Child?.Draw(Inner(availableSpace));

    private Size Inner(Size available) => new(
        Math.Max(0, Math.Min(MaxWidth, available.Width)),
        Math.Max(0, Math.Min(MaxHeight, available.Height)));
}

/// <summary>Sizes the child by a width-to-height ratio.</summary>
internal sealed class AspectRatioElement(float ratio, Fluent.AspectRatioOption option) : ContainerElement
{
    public float Ratio { get; } = ratio;

    public Fluent.AspectRatioOption Option { get; } = option;

    public override SpacePlan Measure(Size availableSpace)
    {
        if (Ratio <= 0)
        {
            return Child?.Measure(availableSpace) ?? SpacePlan.Empty();
        }

        var target = Target(availableSpace);
        if (target.Width > availableSpace.Width + LayoutMath.Epsilon || target.Height > availableSpace.Height + LayoutMath.Epsilon)
        {
            return SpacePlan.Wrap();
        }

        var plan = Child?.Measure(target) ?? SpacePlan.Empty();
        return plan.Type switch
        {
            SpacePlanType.Wrap => plan,
            SpacePlanType.PartialRender => SpacePlan.PartialRender(target),
            _ => SpacePlan.FullRender(target),
        };
    }

    public override void Draw(Size availableSpace) => Child?.Draw(Ratio <= 0 ? availableSpace : Target(availableSpace));

    private Size Target(Size available)
    {
        var option = Option;
        if (option == Fluent.AspectRatioOption.FitArea)
        {
            option = available.Width / Math.Max(LayoutMath.Epsilon, available.Height) > Ratio
                ? Fluent.AspectRatioOption.FitHeight
                : Fluent.AspectRatioOption.FitWidth;
        }

        return option == Fluent.AspectRatioOption.FitHeight
            ? new Size(available.Height * Ratio, available.Height)
            : new Size(available.Width, available.Width / Ratio);
    }
}
