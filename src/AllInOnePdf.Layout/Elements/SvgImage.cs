using AllInOnePdf.Layout.Infrastructure;
using AllInOnePdf.Layout.Svg;
using SkiaSharp;

namespace AllInOnePdf.Layout.Elements;

/// <summary>
/// A vector picture. Sized like <see cref="Image"/> from the SVG's intrinsic aspect ratio (width/height, else the
/// viewBox); drawn with <see cref="SvgRenderer"/> on <see cref="IDrawingCanvas.Native"/>, so nothing is drawn on the
/// measuring pass. An SVG that does not parse measures as Empty and draws nothing.
/// </summary>
internal sealed class SvgImage : Element
{
    public SvgImage(string svg)
    {
        Document = SvgParser.Parse(svg);
    }

    /// <summary>The parsed picture; null when the markup did not parse.</summary>
    public SvgDocument? Document { get; }

    public ImageScaling Scaling { get; set; } = ImageScaling.FitWidth;

    public override SpacePlan Measure(Size availableSpace) =>
        Document is null ? SpacePlan.Empty() : ImageFit.Measure(Scaling, Document.AspectRatio, availableSpace);

    public override void Draw(Size availableSpace)
    {
        if (Document is null || Canvas.Native is not { } native)
        {
            return;
        }

        var target = ImageFit.TargetSize(Scaling, Document.AspectRatio, availableSpace);
        SvgRenderer.Draw(native, Document, new SKSize(target.Width, target.Height));
    }
}
