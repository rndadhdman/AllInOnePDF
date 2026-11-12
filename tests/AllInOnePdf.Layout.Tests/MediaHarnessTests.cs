using AllInOnePdf.Layout.Infrastructure;
using SkiaSharp;

namespace AllInOnePdf.Layout.Tests;

/// <summary>A root to hang fluent content under (the core agent's page containers stand in for it later).</summary>
internal sealed class MediaRoot : ContainerElement
{
    /// <summary>The element the fluent call put under the root.</summary>
    public Element Content => Child ?? throw new InvalidOperationException("Nothing was added.");
}

/// <summary>A fixed-size test element; it fills whatever space it is drawn with (like a background) and remembers it.</summary>
internal sealed class MediaBox(float width, float height, string name = "") : Element
{
    public string Name { get; } = name;

    public List<Size> Drawn { get; } = [];

    public int Measured { get; private set; }

    public override SpacePlan Measure(Size availableSpace)
    {
        Measured++;
        return height > availableSpace.Height + 0.001f
            ? SpacePlan.Wrap()
            : SpacePlan.FullRender(Math.Min(width, availableSpace.Width), height);
    }

    public override void Draw(Size availableSpace)
    {
        Drawn.Add(availableSpace);
        Canvas.DrawRectangle(Position.Zero, availableSpace, Color.Black);
        if (Canvas is MediaRecordingCanvas recording)
        {
            recording.Mark(Name, availableSpace);
        }
    }
}

/// <summary>One call on <see cref="MediaRecordingCanvas"/>, with the absolute origin at the time.</summary>
internal sealed record MediaOp(string Kind, Position At, Size Size, string Name = "", float Thickness = 0, Color Color = default, float[]? Dash = null, Position To = default);

/// <summary>Records drawing calls in absolute coordinates; no native canvas.</summary>
internal sealed class MediaRecordingCanvas : IDrawingCanvas
{
    private Position _origin = Position.Zero;

    public List<MediaOp> Ops { get; } = [];

    public SKCanvas? Native => null;

    public IEnumerable<MediaOp> Marks => Ops.Where(o => o.Kind == "mark");

    public void Mark(string name, Size size) => Ops.Add(new MediaOp("mark", _origin, size, name));

    public void Translate(Position vector) => _origin = new Position(_origin.X + vector.X, _origin.Y + vector.Y);

    public void DrawRectangle(Position position, Size size, Color color, float cornerRadius = 0) =>
        Ops.Add(new MediaOp("rect", Abs(position), size, Color: color));

    public void DrawRectangleOutline(Position position, Size size, Color color, float thickness, float cornerRadius = 0) =>
        Ops.Add(new MediaOp("outline", Abs(position), size, Thickness: thickness, Color: color));

    public void DrawLine(Position from, Position to, float thickness, Color color, float[]? dashPattern = null) =>
        Ops.Add(new MediaOp("line", Abs(from), Size.Zero, Thickness: thickness, Color: color, Dash: dashPattern, To: Abs(to)));

    public void DrawGlyphRun(GlyphRun run, Position baselineOrigin) => Ops.Add(new MediaOp("glyphs", Abs(baselineOrigin), Size.Zero));

    public void DrawImage(SKImage image, Position position, Size size) => Ops.Add(new MediaOp("image", Abs(position), size));

    public void ClipRectangle(Position position, Size size, float cornerRadius = 0) => Ops.Add(new MediaOp("clip", Abs(position), size));

    public void Save() => Ops.Add(new MediaOp("save", _origin, Size.Zero));

    public void Restore() => Ops.Add(new MediaOp("restore", _origin, Size.Zero));

    public void DrawHyperlink(Position position, Size size, string url)
    {
    }

    public void DrawSectionLink(Position position, Size size, string sectionName)
    {
    }

    public void DrawSectionDestination(string sectionName, Position position)
    {
    }

    private Position Abs(Position p) => new(_origin.X + p.X, _origin.Y + p.Y);
}

/// <summary>A minimal <see cref="IDrawingCanvas"/> over a white Skia bitmap, for pixel checks.</summary>
internal sealed class MediaBitmapCanvas : IDrawingCanvas, IDisposable
{
    private readonly SKCanvas _canvas;

    public MediaBitmapCanvas(int width, int height)
    {
        Bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        _canvas = new SKCanvas(Bitmap);
        _canvas.Clear(SKColors.White);
    }

    public SKBitmap Bitmap { get; }

    public List<SKImage> Images { get; } = [];

    public SKCanvas? Native => _canvas;

    public SKColor Pixel(int x, int y)
    {
        _canvas.Flush();
        return Bitmap.GetPixel(x, y);
    }

    public void Translate(Position vector) => _canvas.Translate(vector.X, vector.Y);

    public void DrawRectangle(Position position, Size size, Color color, float cornerRadius = 0)
    {
        using var paint = new SKPaint { Color = color.ToSKColor() };
        _canvas.DrawRect(SKRect.Create(position.X, position.Y, size.Width, size.Height), paint);
    }

    public void DrawRectangleOutline(Position position, Size size, Color color, float thickness, float cornerRadius = 0)
    {
        using var paint = new SKPaint { Color = color.ToSKColor(), Style = SKPaintStyle.Stroke, StrokeWidth = thickness };
        _canvas.DrawRect(SKRect.Create(position.X, position.Y, size.Width, size.Height), paint);
    }

    public void DrawLine(Position from, Position to, float thickness, Color color, float[]? dashPattern = null)
    {
        using var paint = new SKPaint { Color = color.ToSKColor(), StrokeWidth = thickness, Style = SKPaintStyle.Stroke };
        _canvas.DrawLine(from.X, from.Y, to.X, to.Y, paint);
    }

    public void DrawGlyphRun(GlyphRun run, Position baselineOrigin)
    {
    }

    public void DrawImage(SKImage image, Position position, Size size)
    {
        Images.Add(image);
        _canvas.DrawImage(image, SKRect.Create(position.X, position.Y, size.Width, size.Height), new SKSamplingOptions(SKFilterMode.Linear));
    }

    public void ClipRectangle(Position position, Size size, float cornerRadius = 0) =>
        _canvas.ClipRect(SKRect.Create(position.X, position.Y, size.Width, size.Height));

    public void Save() => _canvas.Save();

    public void Restore() => _canvas.Restore();

    public void DrawHyperlink(Position position, Size size, string url)
    {
    }

    public void DrawSectionLink(Position position, Size size, string sectionName)
    {
    }

    public void DrawSectionDestination(string sectionName, Position position)
    {
    }

    public void Dispose()
    {
        _canvas.Dispose();
        Bitmap.Dispose();
    }
}

internal static class MediaHarness
{
    /// <summary>Initialises <paramref name="element"/> with a fresh context on page 1.</summary>
    public static PageContext Start(Element element, IDrawingCanvas canvas)
    {
        var context = new PageContext();
        context.BeginFirstPass();
        element.Initialize(context, canvas);
        context.BeginPage();
        return context;
    }

    /// <summary>Encodes a solid bitmap (optionally with a transparent right half) as PNG or JPEG.</summary>
    public static byte[] Picture(int width, int height, SKColor color, bool transparentHalf = false, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, transparentHalf && x >= width / 2 ? SKColors.Transparent : color);
            }
        }

        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    public static bool IsNear(SKColor actual, SKColor expected, int tolerance = 40) =>
        Math.Abs(actual.Red - expected.Red) <= tolerance
        && Math.Abs(actual.Green - expected.Green) <= tolerance
        && Math.Abs(actual.Blue - expected.Blue) <= tolerance;
}

public class MediaHarnessTests
{
    [Fact]
    public void Recording_canvas_tracks_translations()
    {
        var canvas = new MediaRecordingCanvas();
        canvas.Translate(new Position(10, 5));
        canvas.DrawRectangle(new Position(1, 1), new Size(2, 2), Color.Black);
        canvas.Translate(new Position(-10, -5));
        Assert.Equal(new Position(11, 6), canvas.Ops.Single().At);
    }
}
