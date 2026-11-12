using AllInOnePdf.Layout.Drawing;
using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;
using AllInOnePdf.Layout.Text;
using SkiaSharp;

namespace AllInOnePdf.Layout.Tests;

/// <summary>Registers the app's bundled fonts (read from src/AllInOnePdf.Core/Fonts) once per test run.</summary>
internal static class TestFonts
{
    private static readonly Lazy<string> FontDirectory = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllInOnePdf.slnx")))
        {
            dir = dir.Parent;
        }

        return dir is null ? throw new DirectoryNotFoundException("Repository root not found.") : Path.Combine(dir.FullName, "src", "AllInOnePdf.Core", "Fonts");
    });

    private static readonly Lazy<bool> Registration = new(() =>
    {
        foreach (var file in Directory.EnumerateFiles(FontDirectory.Value, "*.ttf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(file);
            FontManager.RegisterFontFromStream(stream);
        }

        return true;
    });

    public static void EnsureRegistered() => _ = Registration.Value;

    public static byte[] Read(string relative) => File.ReadAllBytes(Path.Combine(FontDirectory.Value, relative));
}

/// <summary>A bare container to hang text under, as the core's containers would.</summary>
internal sealed class Holder : ContainerElement;

/// <summary>A fixed-size leaf (stands in for an inline image).</summary>
internal sealed class FixedBox(float width, float height) : Element
{
    public int Draws { get; private set; }

    public override SpacePlan Measure(Size availableSpace) => SpacePlan.FullRender(width, height);

    public override void Draw(Size availableSpace) => Draws++;
}

/// <summary>Records what the text engine draws, in absolute coordinates (translations applied).</summary>
internal sealed class TextRecordingCanvas : IDrawingCanvas
{
    private float _x;
    private float _y;

    public List<(GlyphRun Run, Position Origin)> GlyphRuns { get; } = [];

    public List<(Position Position, Size Size, Color Color)> Rectangles { get; } = [];

    public List<(Position From, Position To, float Thickness)> Lines { get; } = [];

    public List<(Position Position, Size Size, string Target, bool External)> Links { get; } = [];

    public List<Position> Translations { get; } = [];

    public SKCanvas? Native => null;

    public void Translate(Position vector)
    {
        _x += vector.X;
        _y += vector.Y;
        Translations.Add(new Position(_x, _y));
    }

    public void DrawRectangle(Position position, Size size, Color color, float cornerRadius = 0) =>
        Rectangles.Add((Abs(position), size, color));

    public void DrawRectangleOutline(Position position, Size size, Color color, float thickness, float cornerRadius = 0)
    {
    }

    public void DrawLine(Position from, Position to, float thickness, Color color, float[]? dashPattern = null) =>
        Lines.Add((Abs(from), Abs(to), thickness));

    public void DrawGlyphRun(GlyphRun run, Position baselineOrigin) => GlyphRuns.Add((run, Abs(baselineOrigin)));

    public void DrawImage(SKImage image, Position position, Size size)
    {
    }

    public void ClipRectangle(Position position, Size size, float cornerRadius = 0)
    {
    }

    public void Save()
    {
    }

    public void Restore()
    {
    }

    public void DrawHyperlink(Position position, Size size, string url) => Links.Add((Abs(position), size, url, true));

    public void DrawSectionLink(Position position, Size size, string sectionName) => Links.Add((Abs(position), size, sectionName, false));

    public void DrawSectionDestination(string sectionName, Position position)
    {
    }

    private Position Abs(Position p) => new(p.X + _x, p.Y + _y);
}

/// <summary>A minimal Skia raster canvas (the core's SkiaCanvas draws to PDF the same way).</summary>
internal sealed class BitmapCanvas(SKCanvas canvas) : IDrawingCanvas
{
    public SKCanvas? Native => canvas;

    public void Translate(Position vector) => canvas.Translate(vector.X, vector.Y);

    public void DrawRectangle(Position position, Size size, Color color, float cornerRadius = 0)
    {
        using var paint = new SKPaint { Color = color.ToSKColor(), IsAntialias = true };
        canvas.DrawRect(position.X, position.Y, size.Width, size.Height, paint);
    }

    public void DrawRectangleOutline(Position position, Size size, Color color, float thickness, float cornerRadius = 0)
    {
    }

    public void DrawLine(Position from, Position to, float thickness, Color color, float[]? dashPattern = null)
    {
        using var paint = new SKPaint { Color = color.ToSKColor(), StrokeWidth = thickness, IsAntialias = true, Style = SKPaintStyle.Stroke };
        canvas.DrawLine(from.X, from.Y, to.X, to.Y, paint);
    }

    public void DrawGlyphRun(GlyphRun run, Position baselineOrigin)
    {
        using var builder = new SKTextBlobBuilder();
        var buffer = builder.AllocatePositionedRun(run.Font, run.GlyphIds.Length);
        buffer.SetGlyphs(run.GlyphIds);
        buffer.SetPositions(run.Positions);
        using var blob = builder.Build();
        using var paint = new SKPaint { Color = run.Color.ToSKColor(), IsAntialias = true };
        if (blob is not null)
        {
            canvas.DrawText(blob, baselineOrigin.X, baselineOrigin.Y, paint);
        }
    }

    public void DrawImage(SKImage image, Position position, Size size)
    {
    }

    public void ClipRectangle(Position position, Size size, float cornerRadius = 0) =>
        canvas.ClipRect(new SKRect(position.X, position.Y, position.X + size.Width, position.Y + size.Height));

    public void Save() => canvas.Save();

    public void Restore() => canvas.Restore();

    public void DrawHyperlink(Position position, Size size, string url)
    {
    }

    public void DrawSectionLink(Position position, Size size, string sectionName)
    {
    }

    public void DrawSectionDestination(string sectionName, Position position)
    {
    }
}

/// <summary>Builds text blocks through the public fluent API and runs them through the layout protocol.</summary>
internal static class TextHarness
{
    public static TextBlock Block(Action<TextDescriptor> content, PageContext? context = null, IDrawingCanvas? canvas = null)
    {
        TestFonts.EnsureRegistered();
        var holder = new Holder();
        holder.Text(content);
        var block = (TextBlock)holder.Child!;
        block.Initialize(context ?? NewContext(), canvas ?? FreeCanvas.Instance);
        return block;
    }

    public static PageContext NewContext(int page = 1)
    {
        var context = new PageContext();
        context.BeginFirstPass();
        for (var i = 0; i < page; i++)
        {
            context.BeginPage();
        }

        return context;
    }
}

public class TextSupportTests
{
    [Fact]
    public void Text_string_shortcut_creates_a_single_span_block()
    {
        TestFonts.EnsureRegistered();
        var holder = new Holder();
        var span = holder.Text("Hello").Bold().FontSize(20).Italic().Underline(false);
        Assert.NotNull(span);
        var block = Assert.IsType<TextBlock>(holder.Child);
        var item = Assert.Single(block.Items);
        Assert.Equal(FontWeight.Bold, item.Style.FontWeight);
        Assert.Equal(20, item.Style.Size);
        Assert.True(item.Style.IsItalic);
        Assert.False(item.Style.HasUnderline);
    }
}
