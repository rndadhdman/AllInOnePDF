namespace AllInOnePdf.Layout.Infrastructure;

/// <summary>
/// A node of the layout tree. Layout is a two-step conversation per page: the parent asks
/// <see cref="Measure"/> how much of the offered space the element needs (<see cref="SpacePlan"/>), then calls
/// <see cref="Draw"/> with that space, and the element paints itself on <see cref="Canvas"/> at the canvas origin
/// (parents translate the canvas before calling). An element that answered <see cref="SpacePlanType.PartialRender"/>
/// keeps its own state about what was drawn and continues on the next page; <see cref="Reset"/> clears that state
/// between the measuring pass and the drawing pass.
/// </summary>
public abstract class Element
{
    /// <summary>Page numbers, section positions and captured positions for the document being generated.</summary>
    public PageContext PageContext { get; private set; } = null!;

    /// <summary>Where to draw. A no-op canvas during the first (measuring) pass.</summary>
    public IDrawingCanvas Canvas { get; private set; } = null!;

    /// <summary>Gives every node of the subtree the context and canvas; called once per pass before any measurement.</summary>
    public virtual void Initialize(PageContext pageContext, IDrawingCanvas canvas)
    {
        PageContext = pageContext;
        Canvas = canvas;
        foreach (var child in GetChildren())
        {
            child.Initialize(pageContext, canvas);
        }
    }

    /// <summary>Clears pagination state so the tree can be laid out again from the first page.</summary>
    public virtual void Reset()
    {
        foreach (var child in GetChildren())
        {
            child.Reset();
        }
    }

    /// <summary>Direct children, for initialisation, reset and tree walks.</summary>
    public virtual IEnumerable<Element> GetChildren() => [];

    /// <summary>How much of <paramref name="availableSpace"/> the element needs now. Must not draw.</summary>
    public abstract SpacePlan Measure(Size availableSpace);

    /// <summary>Draws the element into <paramref name="availableSpace"/> at the canvas origin and advances its own pagination state.</summary>
    public abstract void Draw(Size availableSpace);
}

/// <summary>An element with a single child; the fluent API chains these.</summary>
public interface IContainer
{
    Element? Child { get; set; }
}

/// <summary>Base for single-child elements. Without an override it is transparent: it measures and draws its child.</summary>
public abstract class ContainerElement : Element, IContainer
{
    public Element? Child { get; set; }

    public override IEnumerable<Element> GetChildren()
    {
        if (Child is not null)
        {
            yield return Child;
        }
    }

    public override SpacePlan Measure(Size availableSpace) => Child?.Measure(availableSpace) ?? SpacePlan.Empty();

    public override void Draw(Size availableSpace) => Child?.Draw(availableSpace);
}

/// <summary>Draws nothing and needs no space.</summary>
public sealed class EmptyElement : Element
{
    public static readonly EmptyElement Instance = new();

    public override SpacePlan Measure(Size availableSpace) => SpacePlan.Empty();

    public override void Draw(Size availableSpace)
    {
    }
}

/// <summary>The one extension every fluent method is built on: hang a new container under <paramref name="parent"/> and return it.</summary>
public static class ContainerChaining
{
    public static TContainer Element<TContainer>(this IContainer parent, TContainer child)
        where TContainer : Element, IContainer
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        parent.Child = child;
        return child;
    }

    /// <summary>Hangs a leaf (text, image, line) under <paramref name="parent"/>.</summary>
    public static void SetChild(this IContainer parent, Element child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        parent.Child = child;
    }
}
