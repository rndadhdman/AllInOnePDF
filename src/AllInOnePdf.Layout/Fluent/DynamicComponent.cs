using AllInOnePdf.Layout.Elements;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Content computed per page (running headers that depend on the page being drawn).</summary>
public interface IDynamicComponent
{
    /// <summary>
    /// Builds the content for the page being laid out. Called every time the element is measured or drawn, so it must
    /// be cheap and free of side effects.
    /// </summary>
    /// <param name="context">The page number, the available size and a factory for the content.</param>
    DynamicComponentComposeResult Compose(DynamicContext context);
}

/// <summary>A piece of content built by <see cref="DynamicContext.CreateElement"/>.</summary>
public interface IDynamicElement
{
    /// <summary>The size the content needs in the context's available size.</summary>
    Size Size { get; }
}

/// <summary>The answer of <see cref="IDynamicComponent.Compose"/>.</summary>
public sealed class DynamicComponentComposeResult
{
    /// <summary>The content for this page (from <see cref="DynamicContext.CreateElement"/>); null draws nothing.</summary>
    public IDynamicElement? Content { get; set; }

    /// <summary>True when the component continues on the next page (the element answers a partial render).</summary>
    public bool HasMoreContent { get; set; }
}

/// <summary>What a dynamic component knows about the page it is composed for.</summary>
public sealed class DynamicContext
{
    private readonly PageContext _pageContext;
    private readonly IDrawingCanvas _canvas;
    private readonly TextStyle _inheritedStyle;

    internal DynamicContext(PageContext pageContext, IDrawingCanvas canvas, TextStyle inheritedStyle, Size availableSize)
    {
        _pageContext = pageContext;
        _canvas = canvas;
        _inheritedStyle = inheritedStyle;
        AvailableSize = availableSize;
    }

    /// <summary>1-based number of the page being laid out.</summary>
    public int PageNumber => _pageContext.CurrentPage;

    /// <summary>Total pages of the document (0 during the first pass).</summary>
    public int TotalPages => _pageContext.PageCount;

    /// <summary>The space the component's content may use on this page.</summary>
    public Size AvailableSize { get; }

    /// <summary>The text style the content inherits from its position in the document.</summary>
    public TextStyle TextStyle => _inheritedStyle;

    /// <summary>Builds content with the fluent API; the result is measured in <see cref="AvailableSize"/>.</summary>
    /// <param name="content">Composes the content into the given container.</param>
    public IDynamicElement CreateElement(Action<IContainer> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var element = new DynamicElement();
        content(element);
        TextStyleScope.InitializeWithin(_pageContext, _inheritedStyle, () => element.Initialize(_pageContext, _canvas));
        var plan = element.Measure(AvailableSize);
        element.Size = plan.HasContent ? plan.Size : Size.Zero;
        return element;
    }

    /// <summary>
    /// Every position captured with <c>CaptureContentPosition(id)</c>. During the first pass only the pages laid out so
    /// far are known; the second pass sees the whole document.
    /// </summary>
    /// <param name="id">The capture id.</param>
    public IEnumerable<CapturedPosition> GetContentCapturedPositions(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _pageContext.GetCaptures(id);
    }
}
