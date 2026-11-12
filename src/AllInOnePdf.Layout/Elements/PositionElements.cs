using AllInOnePdf.Layout.Drawing;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Elements;

/// <summary>
/// Shifts the child's drawing (QuestPDF's Translate): the child measures as before and keeps its slot; only where it
/// paints moves. Nothing is clipped, so the child may paint over its neighbours.
/// </summary>
internal sealed class TranslateElement : ContainerElement
{
    public float X { get; set; }

    public float Y { get; set; }

    public override void Draw(Size availableSpace)
    {
        if (Child is null)
        {
            return;
        }

        if (X == 0 && Y == 0)
        {
            Child.Draw(availableSpace);
            return;
        }

        var offset = new Position(X, Y);
        Canvas.Translate(offset);
        Child.Draw(availableSpace);
        Canvas.Translate(offset.Reverse());
    }
}

/// <summary>
/// Paints the child on a layer above the page's normal content. The child measures and keeps its slot as before;
/// when <see cref="Z"/> is positive its drawing on a page is deferred (<see cref="DeferredLayers"/>) and replayed with
/// the canvas state of the moment it would have drawn, after everything at z 0 on that page.
/// </summary>
internal sealed class ZIndexElement(int z) : ContainerElement
{
    public int Z { get; } = z;

    public override void Draw(Size availableSpace)
    {
        if (Child is null)
        {
            return;
        }

        var layers = DocumentState.Of(PageContext).Layers;
        if (!layers.IsActive || Z <= layers.CurrentLevel || Canvas is not ICanvasStateCapture capture)
        {
            Child.Draw(availableSpace);
            return;
        }

        layers.Defer(Z, capture.CaptureState(), Child, availableSpace);
    }
}

/// <summary>
/// The per-document queue of deferred draws. The page loop activates it, and after each page's header, content and
/// footer it calls <see cref="Flush"/>, which draws the queue in ascending z (ties in the order they were deferred).
/// A z-index element met while a layer is being flushed defers only when its z is above that layer.
/// </summary>
internal sealed class DeferredLayers
{
    private readonly List<Entry> _entries = [];
    private long _sequence;

    /// <summary>True while the page loop runs (outside it, e.g. in element tests, z-index draws in place).</summary>
    public bool IsActive { get; set; }

    /// <summary>The layer being flushed (0 while the page's normal content draws).</summary>
    public int CurrentLevel { get; private set; }

    public int Count => _entries.Count;

    public void Defer(int z, CanvasState state, Element element, Size size) =>
        _entries.Add(new Entry(z, _sequence++, state, element, size));

    /// <summary>Draws every deferred element on <paramref name="canvas"/>; the canvas origin must be at the page origin.</summary>
    public void Flush(ICanvasStateCapture canvas)
    {
        var drawing = (IDrawingCanvas)canvas;
        try
        {
            while (_entries.Count > 0)
            {
                var next = 0;
                for (var i = 1; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    var best = _entries[next];
                    if (e.Z < best.Z || (e.Z == best.Z && e.Sequence < best.Sequence))
                    {
                        next = i;
                    }
                }

                var entry = _entries[next];
                _entries.RemoveAt(next);
                CurrentLevel = entry.Z;
                canvas.ApplyState(entry.State);
                try
                {
                    entry.Element.Draw(entry.Size);
                }
                finally
                {
                    drawing.Restore();
                }
            }
        }
        finally
        {
            CurrentLevel = 0;
        }
    }

    /// <summary>Drops anything pending (between passes).</summary>
    public void Clear()
    {
        _entries.Clear();
        _sequence = 0;
        CurrentLevel = 0;
    }

    private readonly record struct Entry(int Z, long Sequence, CanvasState State, Element Element, Size Size);
}

/// <summary>One absolutely placed child of a <see cref="FreeformElement"/>.</summary>
internal sealed record FreeformItem(float X, float Y, float Width, float Height, int Z, int Order, string? Key, SlotElement Content);

/// <summary>
/// A fixed canvas with absolutely placed children. It takes the whole space it is offered; every item is measured and
/// drawn at its own size at its position, clipped to its box and to the canvas, painted in ascending z (ties in
/// insertion order); an item with no content still draws (its background and borders fill the box). An item whose content does not fit is drawn as far as it fits (nothing when not even the start fits) and, when keyed, reported in
/// <see cref="PageContext.Overflowing"/>.
/// </summary>
internal sealed class FreeformElement : Element
{
    private List<FreeformItem>? _paintOrder;

    public List<FreeformItem> Items { get; } = [];

    public override IEnumerable<Element> GetChildren() => Items.Select(static i => (Element)i.Content);

    public override SpacePlan Measure(Size availableSpace) =>
        availableSpace.Width <= LayoutMath.Epsilon || availableSpace.Height <= LayoutMath.Epsilon
            ? SpacePlan.Wrap()
            : SpacePlan.FullRender(availableSpace);

    public override void Draw(Size availableSpace)
    {
        _paintOrder ??= [.. Items.OrderBy(static i => i.Z).ThenBy(static i => i.Order)];
        Canvas.Save();
        Canvas.ClipRectangle(Position.Zero, availableSpace);
        foreach (var item in _paintOrder)
        {
            var size = new Size(Math.Max(0, item.Width), Math.Max(0, item.Height));
            var plan = item.Content.Measure(size);
            if (item.Key is not null && (plan.IsPartial || plan.IsWrap))
            {
                PageContext.MarkOverflowing(item.Key);
            }

            if (plan.IsWrap)
            {
                // Nothing of the content fits its box (drawing it would break the layout protocol).
                continue;
            }

            Canvas.Save();
            Canvas.Translate(new Position(item.X, item.Y));
            Canvas.ClipRectangle(Position.Zero, size);
            item.Content.Draw(size);
            Canvas.Restore();
        }

        Canvas.Restore();
    }
}
