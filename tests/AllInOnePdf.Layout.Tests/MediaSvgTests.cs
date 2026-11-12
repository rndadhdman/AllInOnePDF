using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;
using AllInOnePdf.Layout.Svg;
using SkiaSharp;

namespace AllInOnePdf.Layout.Tests;

public class MediaSvgTests
{
    private const string Ns = "xmlns='http://www.w3.org/2000/svg'";

    private static MediaBitmapCanvas Render(string svg, int size = 100, Func<SvgImageDescriptor, SvgImageDescriptor>? fit = null)
    {
        var canvas = new MediaBitmapCanvas(size, size);
        var root = new MediaRoot();
        var descriptor = root.Svg(svg);
        fit?.Invoke(descriptor);
        MediaHarness.Start(root, canvas);
        var available = new Size(size, size);
        if (root.Measure(available).HasContent)
        {
            root.Draw(available);
        }

        return canvas;
    }

    [Fact]
    public void Parses_every_supported_element_and_attribute()
    {
        var doc = SvgParser.Parse($"""
            <svg {Ns} viewBox='0 0 40 20' width='80' height='40' preserveAspectRatio='xMinYMax slice'>
              <g transform='translate(1 2)' opacity='0.5' fill='red' stroke='#00f' stroke-width='2'
                 stroke-linecap='round' stroke-linejoin='bevel' stroke-miterlimit='8' fill-opacity='0.25'
                 stroke-opacity='50%' fill-rule='evenodd'>
                <path d='M0 0 L10 10 Z'/>
                <rect x='1' y='2' width='3' height='4' rx='0.5'/>
                <circle cx='5' cy='6' r='7'/>
                <ellipse cx='1' cy='2' rx='3' ry='4'/>
                <line x1='1' y1='2' x2='3' y2='4'/>
                <polyline points='0,0 1,1 2,0'/>
                <polygon points='0 0 4 0 2 3' style='fill: rgb(0, 128, 0); stroke: none'/>
              </g>
            </svg>
            """);

        Assert.NotNull(doc);
        Assert.Equal(SKRect.Create(0, 0, 40, 20), doc.ViewBox);
        Assert.Equal((80f, 40f), (doc.Width!.Value, doc.Height!.Value));
        Assert.Equal(new SvgAspectRatio(0, 1, true, false), doc.PreserveAspectRatio);
        Assert.Equal(2f, doc.AspectRatio);

        var g = Assert.IsType<SvgGroup>(Assert.Single(doc.Root.Children));
        Assert.Equal(SKMatrix.CreateTranslation(1, 2), g.Transform);
        Assert.Equal(0.5f, g.Style.Opacity);
        Assert.Equal(SvgPaint.FromColor(SKColors.Red), g.Style.Fill);
        Assert.Equal(SvgPaint.FromColor(new SKColor(0, 0, 255)), g.Style.Stroke);
        Assert.Equal(2f, g.Style.StrokeWidth);
        Assert.Equal(SKStrokeCap.Round, g.Style.StrokeLineCap);
        Assert.Equal(SKStrokeJoin.Bevel, g.Style.StrokeLineJoin);
        Assert.Equal(8f, g.Style.StrokeMiterLimit);
        Assert.Equal(0.25f, g.Style.FillOpacity);
        Assert.Equal(0.5f, g.Style.StrokeOpacity);
        Assert.Equal(SKPathFillType.EvenOdd, g.Style.FillRule);

        var shapes = g.Children.Cast<SvgShape>().ToList();
        Assert.Equal(
            [SvgShapeKind.Path, SvgShapeKind.Rect, SvgShapeKind.Circle, SvgShapeKind.Ellipse, SvgShapeKind.Line, SvgShapeKind.Polyline, SvgShapeKind.Polygon],
            shapes.Select(s => s.Kind));
        Assert.Equal("M0 0 L10 10 Z", shapes[0].PathData);
        Assert.Equal([1f, 2, 3, 4, 0.5f, 0.5f], shapes[1].Values); // ry defaults to rx
        Assert.Equal([5f, 6, 7], shapes[2].Values);
        Assert.Equal([1f, 2, 3, 4], shapes[3].Values);
        Assert.Equal([1f, 2, 3, 4], shapes[4].Values);
        Assert.Equal([0f, 0, 1, 1, 2, 0], shapes[5].Values);
        Assert.Equal(SvgPaint.FromColor(new SKColor(0, 128, 0)), shapes[6].Style.Fill);
        Assert.Equal(SvgPaint.None, shapes[6].Style.Stroke);

        foreach (var shape in shapes)
        {
            using var path = shape.CreatePath();
            Assert.NotNull(path);
        }
    }

    [Theory]
    [InlineData("red", 255, 0, 0, 255)]
    [InlineData("RebeccaPurple", 0x66, 0x33, 0x99, 255)]
    [InlineData("#0f0", 0, 255, 0, 255)]
    [InlineData("#00ff0080", 0, 255, 0, 128)]
    [InlineData("#0f08", 0, 255, 0, 0x88)]
    [InlineData("rgb(1, 2, 3)", 1, 2, 3, 255)]
    [InlineData("rgb(100%, 0%, 50%)", 255, 0, 128, 255)]
    [InlineData("rgba(0,0,255,0.5)", 0, 0, 255, 128)]
    [InlineData("transparent", 0, 0, 0, 0)]
    public void Parses_colours(string value, int r, int g, int b, int a)
    {
        Assert.True(SvgColors.TryParseColor(value, out var color));
        Assert.Equal(new SKColor((byte)r, (byte)g, (byte)b, (byte)a), color);
    }

    [Fact]
    public void Knows_the_css_named_colours_and_none()
    {
        Assert.Equal(148, SvgColors.Names.Count);
        Assert.True(SvgColors.TryParsePaint("none", out var none));
        Assert.True(none.IsNone);
        Assert.False(SvgColors.TryParsePaint("currentColor", out _));
        Assert.False(SvgColors.TryParseColor("#12", out _));
    }

    [Fact]
    public void Parses_transform_lists()
    {
        var m = SvgParser.ParseTransform("translate(10 20) scale(2)");
        Assert.Equal(new SKPoint(12, 22), m.MapPoint(1, 1));

        var r = SvgParser.ParseTransform("rotate(90)").MapPoint(1, 0);
        Assert.Equal(0, r.X, 4);
        Assert.Equal(1, r.Y, 4);

        var about = SvgParser.ParseTransform("rotate(180, 5, 5)").MapPoint(0, 0);
        Assert.Equal(10, about.X, 4);
        Assert.Equal(10, about.Y, 4);

        Assert.Equal(new SKPoint(1 * 1 + 2 * 3 + 5, 1 * 2 + 2 * 4 + 6), SvgParser.ParseTransform("matrix(1,2,3,4,5,6)").MapPoint(1, 2));
        Assert.Equal(SKMatrix.Identity, SvgParser.ParseTransform("bogus(1)"));
    }

    [Fact]
    public void A_red_circle_paints_red_at_its_centre_and_leaves_the_outside_white()
    {
        using var canvas = Render($"<svg {Ns} viewBox='0 0 10 10'><circle cx='5' cy='5' r='4' fill='red'/></svg>");
        Assert.True(MediaHarness.IsNear(canvas.Pixel(50, 50), SKColors.Red), canvas.Pixel(50, 50).ToString());
        Assert.True(MediaHarness.IsNear(canvas.Pixel(3, 3), SKColors.White), canvas.Pixel(3, 3).ToString());
        Assert.True(MediaHarness.IsNear(canvas.Pixel(97, 50), SKColors.White), canvas.Pixel(97, 50).ToString());
    }

    [Fact]
    public void The_view_box_is_scaled_into_the_fitted_box()
    {
        // 2:1 picture, FitWidth into 100 x 100: drawn 100 x 50 at the top.
        using var canvas = Render($"<svg {Ns} viewBox='0 0 20 10'><rect width='20' height='10' fill='blue'/></svg>");
        Assert.True(MediaHarness.IsNear(canvas.Pixel(50, 25), SKColors.Blue));
        Assert.True(MediaHarness.IsNear(canvas.Pixel(50, 75), SKColors.White));

        // FitHeight in a 100 x 100 box would be 200 wide: Wrap, so nothing is drawn.
        using var tooWide = Render($"<svg {Ns} viewBox='0 0 20 10'><rect width='20' height='10' fill='blue'/></svg>", fit: d => d.FitHeight());
        Assert.True(MediaHarness.IsNear(tooWide.Pixel(50, 25), SKColors.White));

        // FitArea of a 1:2 picture: 50 x 100 at the left.
        using var tall = Render($"<svg {Ns} viewBox='0 0 10 20'><rect width='10' height='20' fill='blue'/></svg>", fit: d => d.FitArea());
        Assert.True(MediaHarness.IsNear(tall.Pixel(25, 50), SKColors.Blue));
        Assert.True(MediaHarness.IsNear(tall.Pixel(75, 50), SKColors.White));
    }

    [Fact]
    public void Strokes_transforms_and_opacity_draw()
    {
        using var canvas = Render($"""
            <svg {Ns} viewBox='0 0 100 100'>
              <line x1='0' y1='10' x2='100' y2='10' stroke='black' stroke-width='6'/>
              <g transform='translate(50 50)'><rect width='20' height='20' fill='lime'/></g>
              <rect x='0' y='80' width='20' height='20' fill='red' opacity='0.5'/>
            </svg>
            """);

        Assert.True(MediaHarness.IsNear(canvas.Pixel(50, 10), SKColors.Black));
        Assert.True(MediaHarness.IsNear(canvas.Pixel(60, 60), new SKColor(0, 255, 0)));
        Assert.True(MediaHarness.IsNear(canvas.Pixel(10, 90), new SKColor(255, 128, 128), 20), canvas.Pixel(10, 90).ToString());
        Assert.True(MediaHarness.IsNear(canvas.Pixel(40, 30), SKColors.White));
    }

    [Fact]
    public void Unknown_elements_are_ignored_with_their_content()
    {
        var svg = $"""
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">
            <svg {Ns} viewBox='0 0 10 10'>
              <title>t</title>
              <defs><circle cx='5' cy='5' r='5' fill='red'/></defs>
              <foo><rect width='10' height='10' fill='red'/></foo>
              <rect width='10' height='10' fill='blue' unknown-attribute='x'/>
              <rect width='10' height='10' fill='red' display='none'/>
            </svg>
            """;
        var doc = SvgParser.Parse(svg);
        Assert.NotNull(doc);
        Assert.Single(doc.Root.Children);

        using var canvas = Render(svg);
        Assert.True(MediaHarness.IsNear(canvas.Pixel(50, 50), SKColors.Blue));
    }

    [Theory]
    [InlineData("<svg><circle")]
    [InlineData("not xml at all")]
    [InlineData("<html/>")]
    [InlineData("")]
    public void Invalid_svg_draws_nothing(string svg)
    {
        Assert.Null(SvgParser.Parse(svg));
        var root = new MediaRoot();
        root.Svg(svg);
        using var canvas = new MediaBitmapCanvas(20, 20);
        MediaHarness.Start(root, canvas);
        Assert.Equal(SpacePlanType.Empty, root.Measure(new Size(20, 20)).Type);
        root.Draw(new Size(20, 20));
        Assert.True(MediaHarness.IsNear(canvas.Pixel(10, 10), SKColors.White, 0));
    }

    [Fact]
    public void Nothing_is_drawn_on_the_measuring_pass()
    {
        var root = new MediaRoot();
        root.Svg($"<svg {Ns} viewBox='0 0 10 10'><circle cx='5' cy='5' r='4' fill='red'/></svg>").FitArea();
        var canvas = new MediaRecordingCanvas();
        MediaHarness.Start(root, canvas);
        Assert.Equal(SpacePlan.FullRender(30, 30), root.Measure(new Size(30, 40)));
        root.Draw(new Size(30, 40));
        Assert.Empty(canvas.Ops);
    }

    [Fact]
    public void The_renderers_icons_parse_and_draw()
    {
        const string accent = "#0F6CBD";
        var tick = $"<svg {Ns} viewBox='0 0 16 16' width='16' height='16'><path d='M3.6 8.4 L6.6 11.4 L12.4 4.8' stroke='#FFFFFF' stroke-width='2.2' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>";
        var info = $"<svg {Ns} viewBox='0 0 16 16' width='16' height='16'><circle cx='8' cy='8' r='8' fill='{accent}'/><rect x='7' y='6.8' width='2' height='5.8' rx='1' fill='#FFFFFF'/><circle cx='8' cy='4.4' r='1.2' fill='#FFFFFF'/></svg>";
        var caution = $"<svg {Ns} viewBox='0 0 16 16' width='16' height='16'><polygon points='4.7,0.3 11.3,0.3 15.7,4.7 15.7,11.3 11.3,15.7 4.7,15.7 0.3,11.3 0.3,4.7' fill='{accent}'/><path d='M5.5 5.5 L10.5 10.5 M10.5 5.5 L5.5 10.5' stroke='#FFFFFF' stroke-width='1.8' stroke-linecap='round'/></svg>";

        Assert.NotNull(SvgParser.Parse(tick));
        using var infoCanvas = Render(info, 160, d => d.FitArea());
        Assert.True(MediaHarness.IsNear(infoCanvas.Pixel(30, 80), SKColor.Parse(accent)));
        Assert.True(MediaHarness.IsNear(infoCanvas.Pixel(80, 95), SKColors.White));

        using var cautionCanvas = Render(caution, 160, d => d.FitArea());
        Assert.True(MediaHarness.IsNear(cautionCanvas.Pixel(80, 80), SKColors.White));
        Assert.True(MediaHarness.IsNear(cautionCanvas.Pixel(80, 20), SKColor.Parse(accent)));
    }
}
