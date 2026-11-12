using AllInOnePdf.Layout.Drawing;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Elements;

/// <summary>Stacked layers drawn in declaration order; the primary layer decides the size and the pagination.</summary>
internal sealed class LayersElement : Element
{
    public List<(SlotElement Content, bool IsPrimary)> Layers { get; } = [];

    public override IEnumerable<Element> GetChildren() => Layers.Select(static l => (Element)l.Content);

    private Element? Primary => Layers.Find(static l => l.IsPrimary).Content;

    public override SpacePlan Measure(Size availableSpace) => Primary?.Measure(availableSpace) ?? SpacePlan.Empty();

    public override void Draw(Size availableSpace)
    {
        foreach (var (content, isPrimary) in Layers)
        {
            if (isPrimary || !content.Measure(availableSpace).IsWrap)
            {
                content.Draw(availableSpace);
            }
        }
    }
}

/// <summary>Ends the page: a zero-height partial render the first time, nothing afterwards.</summary>
internal sealed class PageBreakElement : Element
{
    private bool _done;

    public override void Reset() => _done = false;

    public override SpacePlan Measure(Size availableSpace) => _done ? SpacePlan.Empty() : SpacePlan.PartialRender(0, 0);

    public override void Draw(Size availableSpace) => _done = true;
}

/// <summary>
/// Moves the child to the next page when it would not fit whole and less than <see cref="MinHeight"/> of it would be
/// drawn here (QuestPDF compares the partial render's height, not only the space offered: a heading group offered
/// enough space whose paragraph still cannot start moves as a whole). A child that fits whole passes through. On a
/// page after the one where it last moved, the child is accepted as it is, so content that never reaches
/// <see cref="MinHeight"/> cannot move from page to page forever.
/// </summary>
internal sealed class EnsureSpaceElement(float minHeight) : ContainerElement
{
    private int? _wrappedOnPage;

    public float MinHeight { get; } = minHeight;

    public override void Reset()
    {
        base.Reset();
        _wrappedOnPage = null;
    }

    public override SpacePlan Measure(Size availableSpace)
    {
        var plan = base.Measure(availableSpace);
        if (!plan.IsPartial)
        {
            return plan;
        }

        var tooLittle = availableSpace.Height < MinHeight - LayoutMath.Epsilon || plan.Height < MinHeight - LayoutMath.Epsilon;
        if (!tooLittle)
        {
            return plan;
        }

        var page = PageContext?.CurrentPage ?? 0;
        if (_wrappedOnPage is { } moved && page > moved && availableSpace.Height >= MinHeight - LayoutMath.Epsilon)
        {
            // Already moved once and now starting on a fresh page: draw what fits.
            return plan;
        }

        _wrappedOnPage ??= page;
        return SpacePlan.Wrap();
    }

    public override void Draw(Size availableSpace)
    {
        _wrappedOnPage = null;
        base.Draw(availableSpace);
    }
}

/// <summary>Draws the child only where it fits whole; otherwise asks for the next page.</summary>
internal sealed class PreventPageBreakElement : ContainerElement
{
    public override SpacePlan Measure(Size availableSpace)
    {
        var plan = base.Measure(availableSpace);
        return plan.IsPartial ? SpacePlan.Wrap() : plan;
    }
}

/// <summary>Hides the child the first time it would be drawn (headers and footers not shown on the first page).</summary>
internal sealed class SkipOnceElement : ContainerElement
{
    private bool _skipped;

    public override void Reset()
    {
        base.Reset();
        if (PageContext is null || !DocumentState.Of(PageContext).IsSoftReset)
        {
            _skipped = false;
        }
    }

    public override SpacePlan Measure(Size availableSpace) => _skipped ? base.Measure(availableSpace) : SpacePlan.Empty();

    public override void Draw(Size availableSpace)
    {
        if (!_skipped)
        {
            _skipped = true;
            return;
        }

        base.Draw(availableSpace);
    }
}

/// <summary>Records the page the subtree starts on and declares a named destination at its top-left.</summary>
internal sealed class SectionElement(string name) : ContainerElement
{
    private bool _started;

    public string Name { get; } = name;

    public override void Reset()
    {
        base.Reset();
        _started = false;
    }

    public override void Draw(Size availableSpace)
    {
        if (!_started)
        {
            _started = true;
            PageContext.SetSectionPage(Name, PageContext.CurrentPage);
            Canvas.DrawSectionDestination(Name, Position.Zero);
        }

        base.Draw(availableSpace);
    }
}

/// <summary>
/// Records every page the subtree is drawn on under a key (<see cref="PageContext.TrackPage"/>) and, in the drawing
/// pass, the rectangle it is drawn into in page coordinates (<see cref="PageContext.TrackBox"/>); unlike
/// <see cref="SectionElement"/> it declares no destination.
/// </summary>
internal sealed class PageTrackerElement(string key) : ContainerElement
{
    public string Key { get; } = key;

    public override void Draw(Size availableSpace)
    {
        PageContext.TrackPage(Key, PageContext.CurrentPage);
        if (!PageContext.IsFirstPass && Canvas is IPositionTracking tracking)
        {
            var offset = tracking.Offset;
            PageContext.TrackBox(Key, new TrackedBox(PageContext.CurrentPage, offset.X, offset.Y, availableSpace.Width, availableSpace.Height));
        }

        base.Draw(availableSpace);
    }
}

/// <summary>Records the page and position of the subtree's top-left on every page it is drawn on (first pass only, so positions are not doubled).</summary>
internal sealed class CaptureContentPositionElement(string id) : ContainerElement
{
    public string Id { get; } = id;

    public override void Draw(Size availableSpace)
    {
        if (PageContext.IsFirstPass)
        {
            var offset = Canvas is IPositionTracking tracking ? tracking.Offset : Position.Zero;
            PageContext.RegisterCapture(Id, new CapturedPosition(PageContext.CurrentPage, offset.X, offset.Y));
        }

        base.Draw(availableSpace);
    }
}

/// <summary>Makes the drawn rectangle a link to an external URL.</summary>
internal sealed class HyperlinkElement(string url) : ContainerElement
{
    public string Url { get; } = url;

    public override void Draw(Size availableSpace)
    {
        base.Draw(availableSpace);
        if (availableSpace.Width > 0 && availableSpace.Height > 0)
        {
            Canvas.DrawHyperlink(Position.Zero, availableSpace, Url);
        }
    }
}
