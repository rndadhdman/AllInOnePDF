using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Elements;

/// <summary>The root of content built by <see cref="DynamicContext.CreateElement"/>.</summary>
internal sealed class DynamicElement : ContainerElement, IDynamicElement
{
    public Size Size { get; set; }
}

/// <summary>
/// Hosts an <see cref="IDynamicComponent"/>: composes it for the current page and available size, then measures or
/// draws the result. The composition is reused while the page, pass and size stay the same and nothing was drawn.
/// </summary>
internal sealed class DynamicHostElement(IDynamicComponent component) : Element
{
    private TextStyle _inherited = TextStyle.Default;
    private Composition? _cached;

    public IDynamicComponent Component { get; } = component;

    public override void Initialize(PageContext pageContext, IDrawingCanvas canvas)
    {
        base.Initialize(pageContext, canvas);
        _inherited = TextStyleScope.GetInherited(pageContext);
        _cached = null;
    }

    public override void Reset()
    {
        base.Reset();
        _cached = null;
    }

    public override SpacePlan Measure(Size availableSpace)
    {
        var composition = Compose(availableSpace);
        if (composition.Content is null)
        {
            return composition.HasMore ? SpacePlan.PartialRender(0, 0) : SpacePlan.Empty();
        }

        var plan = composition.Content.Measure(availableSpace);
        return composition.HasMore && plan.IsFull ? SpacePlan.PartialRender(plan.Size) : plan;
    }

    public override void Draw(Size availableSpace)
    {
        var composition = Compose(availableSpace);
        _cached = null;
        composition.Content?.Draw(availableSpace);
    }

    private Composition Compose(Size availableSpace)
    {
        if (_cached is { } cached && cached.Page == PageContext.CurrentPage && cached.FirstPass == PageContext.IsFirstPass && cached.Available == availableSpace)
        {
            return cached;
        }

        DynamicComponentComposeResult result;
        try
        {
            result = Component.Compose(new DynamicContext(PageContext, Canvas, _inherited, availableSpace));
        }
        catch (Exception ex) when (ex is not DocumentLayoutException and not DocumentComposeException)
        {
            throw new DocumentComposeException($"A dynamic component ({Component.GetType().Name}) failed on page {PageContext.CurrentPage}: {ex.Message}", ex);
        }

        if (result.Content is not null and not DynamicElement)
        {
            throw new DocumentComposeException("A dynamic component returned content that was not built with DynamicContext.CreateElement.");
        }

        _cached = new Composition(PageContext.CurrentPage, PageContext.IsFirstPass, availableSpace, result.Content as DynamicElement, result.HasMoreContent);
        return _cached;
    }

    private sealed record Composition(int Page, bool FirstPass, Size Available, DynamicElement? Content, bool HasMore);
}
