using AllInOnePdf.Layout.Elements;
using AllInOnePdf.Layout.Infrastructure;
using SkiaSharp;

namespace AllInOnePdf.Layout.Tests;

/// <summary>A root container for element tests.</summary>
internal sealed class TestRoot : ContainerElement
{
    public static TestRoot Build(Action<IContainer> compose)
    {
        var root = new TestRoot();
        compose(root);
        return root;
    }

    public TestRoot Init(IDrawingCanvas? canvas = null)
    {
        Initialize(new PageContext(), canvas ?? FreeCanvas.Instance);
        return this;
    }
}

/// <summary>A fixed-size box that draws once (then answers Empty) and records where and when it was drawn.</summary>
internal sealed class Block(float width, float height, Color? color = null) : Element
{
    private bool _done;

    public List<int> Pages { get; } = [];

    public List<(int Page, bool FirstPass)> Draws { get; } = [];

    public Size LastDrawSize { get; private set; }

    /// <summary>When false the block never finishes (stays drawable on every page, like a header leaf).</summary>
    public bool Once { get; init; } = true;

    public override void Reset() => _done = false;

    public override SpacePlan Measure(Size availableSpace)
    {
        if (_done)
        {
            return SpacePlan.Empty();
        }

        var w = width <= 0 ? availableSpace.Width : width;
        return w > availableSpace.Width + 0.001f || height > availableSpace.Height + 0.001f
            ? SpacePlan.Wrap()
            : SpacePlan.FullRender(w, height);
    }

    public override void Draw(Size availableSpace)
    {
        LastDrawSize = availableSpace;
        Pages.Add(PageContext.CurrentPage);
        Draws.Add((PageContext.CurrentPage, PageContext.IsFirstPass));
        Canvas.DrawRectangle(Position.Zero, new Size(width <= 0 ? availableSpace.Width : width, height), color ?? Color.Black);
        _done = Once;
    }
}

/// <summary>A paginating leaf: <c>count</c> lines of <c>lineHeight</c>, as many whole lines per page as fit.</summary>
internal sealed class Lines(int count, float lineHeight) : Element
{
    private int _drawn;

    public override void Reset() => _drawn = 0;

    public override SpacePlan Measure(Size availableSpace)
    {
        var left = count - _drawn;
        if (left <= 0)
        {
            return SpacePlan.Empty();
        }

        var fit = (int)Math.Floor((availableSpace.Height + 0.001f) / lineHeight);
        if (fit <= 0)
        {
            return SpacePlan.Wrap();
        }

        return fit >= left
            ? SpacePlan.FullRender(availableSpace.Width, left * lineHeight)
            : SpacePlan.PartialRender(availableSpace.Width, fit * lineHeight);
    }

    public override void Draw(Size availableSpace)
    {
        var fit = (int)Math.Floor((availableSpace.Height + 0.001f) / lineHeight);
        _drawn = Math.Min(count, _drawn + Math.Max(0, fit));
    }
}

/// <summary>Reads the inherited text style during initialisation, like a text block does.</summary>
internal sealed class StyleProbe : Element
{
    public TextStyle? Seen { get; private set; }

    public override void Initialize(PageContext pageContext, IDrawingCanvas canvas)
    {
        base.Initialize(pageContext, canvas);
        Seen = TextStyleScope.GetInherited(pageContext);
    }

    public override SpacePlan Measure(Size availableSpace) => SpacePlan.FullRender(0, 1);

    public override void Draw(Size availableSpace)
    {
    }
}

/// <summary>Calls back on every draw with the element's page context (to inspect sections and captures).</summary>
internal sealed class ContextProbe(Action<PageContext> onDraw) : Element
{
    public override SpacePlan Measure(Size availableSpace) => SpacePlan.FullRender(availableSpace.Width, 1);

    public override void Draw(Size availableSpace) => onDraw(PageContext);
}

/// <summary>A canvas that follows translations and records rectangles, lines and links in absolute coordinates.</summary>
internal sealed class RecordingCanvas : IDrawingCanvas
{
    private readonly Stack<Position> _saved = new();
    private Position _offset;

    public List<(Position Position, Size Size, Color Color, float Radius)> Rectangles { get; } = [];

    public List<(Position Position, Size Size, float Thickness)> Outlines { get; } = [];

    public List<(Position From, Position To, float Thickness)> Lines { get; } = [];

    public List<(Position Position, Size Size)> Clips { get; } = [];

    public List<(Position Position, Size Size, string Url)> Links { get; } = [];

    public SKCanvas? Native => null;

    private Position Abs(Position p) => new(_offset.X + p.X, _offset.Y + p.Y);

    public void Translate(Position vector) => _offset = new Position(_offset.X + vector.X, _offset.Y + vector.Y);

    public void DrawRectangle(Position position, Size size, Color color, float cornerRadius = 0) => Rectangles.Add((Abs(position), size, color, cornerRadius));

    public void DrawRectangleOutline(Position position, Size size, Color color, float thickness, float cornerRadius = 0) => Outlines.Add((Abs(position), size, thickness));

    public void DrawLine(Position from, Position to, float thickness, Color color, float[]? dashPattern = null) => Lines.Add((Abs(from), Abs(to), thickness));

    public void DrawGlyphRun(GlyphRun run, Position baselineOrigin)
    {
    }

    public void DrawImage(SKImage image, Position position, Size size)
    {
    }

    public void ClipRectangle(Position position, Size size, float cornerRadius = 0) => Clips.Add((Abs(position), size));

    public void Save() => _saved.Push(_offset);

    public void Restore() => _offset = _saved.Pop();

    public void DrawHyperlink(Position position, Size size, string url) => Links.Add((Abs(position), size, url));

    public void DrawSectionLink(Position position, Size size, string sectionName)
    {
    }

    public void DrawSectionDestination(string sectionName, Position position)
    {
    }
}

public class LayoutTestElementsTests
{
    [Fact]
    public void Test_leaves_behave()
    {
        var lines = new Lines(5, 10);
        lines.Initialize(new PageContext(), FreeCanvas.Instance);
        Assert.Equal(SpacePlan.PartialRender(100, 30), lines.Measure(new Size(100, 35)));
        lines.Draw(new Size(100, 35));
        Assert.Equal(SpacePlan.FullRender(100, 20), lines.Measure(new Size(100, 35)));
        Assert.Equal(SpacePlanType.Wrap, lines.Measure(new Size(100, 5)).Type);
    }
}
