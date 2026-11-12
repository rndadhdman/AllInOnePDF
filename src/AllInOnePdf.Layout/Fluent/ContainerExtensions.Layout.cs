using AllInOnePdf.Layout.Elements;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>How <c>AspectRatio</c> sizes its content.</summary>
public enum AspectRatioOption
{
    /// <summary>Take the available width; height = width / ratio.</summary>
    FitWidth,

    /// <summary>Take the available height; width = height × ratio.</summary>
    FitHeight,

    /// <summary>The largest box of the ratio that fits both dimensions.</summary>
    FitArea,
}

/// <summary>
/// The fluent API on <see cref="IContainer"/>. Each method hangs a new element under the container and returns the
/// element's inner container (QuestPDF's names and semantics). This part holds the layout containers; text, tables,
/// images and lines are in the other parts.
/// </summary>
public static partial class ContainerExtensions
{
    // ------------------------------------------------------------------ stacks

    /// <summary>A vertical stack of items.</summary>
    /// <param name="element">The container.</param>
    /// <param name="handler">Adds the items.</param>
    public static void Column(this IContainer element, Action<ColumnDescriptor> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var descriptor = new ColumnDescriptor();
        handler(descriptor);
        element.SetChild(descriptor.Column);
    }

    /// <summary>Items side by side.</summary>
    /// <param name="element">The container.</param>
    /// <param name="handler">Adds the items.</param>
    public static void Row(this IContainer element, Action<RowDescriptor> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var descriptor = new RowDescriptor();
        handler(descriptor);
        element.SetChild(descriptor.Row);
    }

    /// <summary>Stacked layers; exactly one must be the primary layer.</summary>
    /// <param name="element">The container.</param>
    /// <param name="handler">Adds the layers.</param>
    public static void Layers(this IContainer element, Action<LayersDescriptor> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var descriptor = new LayersDescriptor();
        handler(descriptor);
        descriptor.Validate();
        element.SetChild(descriptor.Layers);
    }

    // ----------------------------------------------------------------- padding

    /// <summary>Insets the content on all sides (negative values enlarge it).</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Inset.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer Padding(this IContainer element, float value, Unit unit = Unit.Point) =>
        element.Pad(value.ToPoints(unit), value.ToPoints(unit), value.ToPoints(unit), value.ToPoints(unit));

    /// <summary>Insets the content at the top and bottom.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Inset.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer PaddingVertical(this IContainer element, float value, Unit unit = Unit.Point) =>
        element.Pad(value.ToPoints(unit), 0, value.ToPoints(unit), 0);

    /// <summary>Insets the content at the left and right.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Inset.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer PaddingHorizontal(this IContainer element, float value, Unit unit = Unit.Point) =>
        element.Pad(0, value.ToPoints(unit), 0, value.ToPoints(unit));

    /// <summary>Insets the content at the top.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Inset.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer PaddingTop(this IContainer element, float value, Unit unit = Unit.Point) =>
        element.Pad(value.ToPoints(unit), 0, 0, 0);

    /// <summary>Insets the content at the right.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Inset.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer PaddingRight(this IContainer element, float value, Unit unit = Unit.Point) =>
        element.Pad(0, value.ToPoints(unit), 0, 0);

    /// <summary>Insets the content at the bottom.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Inset.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer PaddingBottom(this IContainer element, float value, Unit unit = Unit.Point) =>
        element.Pad(0, 0, value.ToPoints(unit), 0);

    /// <summary>Insets the content at the left.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Inset.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer PaddingLeft(this IContainer element, float value, Unit unit = Unit.Point) =>
        element.Pad(0, 0, 0, value.ToPoints(unit));

    private static PaddingElement Pad(this IContainer element, float top, float right, float bottom, float left)
    {
        // Consecutive paddings merge into one element (QuestPDF does the same).
        var padding = element is PaddingElement { Child: null } existing ? existing : element.Element(new PaddingElement());
        padding.Top += top;
        padding.Right += right;
        padding.Bottom += bottom;
        padding.Left += left;
        return padding;
    }

    // ------------------------------------------------------ background, border

    /// <summary>Fills the element's drawn area under its content.</summary>
    /// <param name="element">The container.</param>
    /// <param name="color">Fill colour.</param>
    public static IContainer Background(this IContainer element, Color color) => element.Box(b => b.BackgroundColor = color);

    /// <summary>A border of <paramref name="value"/> on all sides, centred on the edges and drawn over the content.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Border width.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer Border(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var w = value.ToPoints(unit);
        return element.Box(b => b.BorderTop = b.BorderRight = b.BorderBottom = b.BorderLeft = w);
    }

    /// <summary>A border at the top and bottom.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Border width.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer BorderVertical(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var w = value.ToPoints(unit);
        return element.Box(b => b.BorderTop = b.BorderBottom = w);
    }

    /// <summary>A border at the left and right.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Border width.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer BorderHorizontal(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var w = value.ToPoints(unit);
        return element.Box(b => b.BorderLeft = b.BorderRight = w);
    }

    /// <summary>A border at the top.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Border width.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer BorderTop(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var w = value.ToPoints(unit);
        return element.Box(b => b.BorderTop = w);
    }

    /// <summary>A border at the bottom.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Border width.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer BorderBottom(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var w = value.ToPoints(unit);
        return element.Box(b => b.BorderBottom = w);
    }

    /// <summary>A border at the left.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Border width.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer BorderLeft(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var w = value.ToPoints(unit);
        return element.Box(b => b.BorderLeft = w);
    }

    /// <summary>A border at the right.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Border width.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer BorderRight(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var w = value.ToPoints(unit);
        return element.Box(b => b.BorderRight = w);
    }

    /// <summary>The colour of the borders of this box (default black).</summary>
    /// <param name="element">The container.</param>
    /// <param name="color">Border colour.</param>
    public static IContainer BorderColor(this IContainer element, Color color) => element.Box(b => b.BorderColor = color);

    /// <summary>Rounds the corners of the background and border and clips the content to them.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Corner radius.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer CornerRadius(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var r = value.ToPoints(unit);
        return element.Box(b => b.CornerRadius = r);
    }

    private static StyledBoxElement Box(this IContainer element, Action<StyledBoxElement> configure)
    {
        // Background, borders and radius chained on one container configure the same box (QuestPDF's StyledBox).
        var box = element is StyledBoxElement { Child: null } existing ? existing : element.Element(new StyledBoxElement());
        configure(box);
        return box;
    }

    // --------------------------------------------------------------- alignment

    /// <summary>Places the content at its natural width on the left.</summary>
    /// <param name="element">The container.</param>
    public static IContainer AlignLeft(this IContainer element) => element.Align(a => a.Horizontal = HorizontalAlignment.Left);

    /// <summary>Places the content at its natural width in the horizontal centre.</summary>
    /// <param name="element">The container.</param>
    public static IContainer AlignCenter(this IContainer element) => element.Align(a => a.Horizontal = HorizontalAlignment.Center);

    /// <summary>Places the content at its natural width on the right.</summary>
    /// <param name="element">The container.</param>
    public static IContainer AlignRight(this IContainer element) => element.Align(a => a.Horizontal = HorizontalAlignment.Right);

    /// <summary>Places the content at its natural height at the top.</summary>
    /// <param name="element">The container.</param>
    public static IContainer AlignTop(this IContainer element) => element.Align(a => a.Vertical = VerticalAlignment.Top);

    /// <summary>Places the content at its natural height in the vertical middle.</summary>
    /// <param name="element">The container.</param>
    public static IContainer AlignMiddle(this IContainer element) => element.Align(a => a.Vertical = VerticalAlignment.Middle);

    /// <summary>Places the content at its natural height at the bottom.</summary>
    /// <param name="element">The container.</param>
    public static IContainer AlignBottom(this IContainer element) => element.Align(a => a.Vertical = VerticalAlignment.Bottom);

    private static AlignmentElement Align(this IContainer element, Action<AlignmentElement> configure)
    {
        var alignment = element is AlignmentElement { Child: null } existing ? existing : element.Element(new AlignmentElement());
        configure(alignment);
        return alignment;
    }

    // ------------------------------------------------------------- constraints

    /// <summary>Exactly this width (moves to the next page when the available width is smaller).</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Width.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer Width(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var v = value.ToPoints(unit);
        return element.Constrain(c => c.MinWidth = c.MaxWidth = v);
    }

    /// <summary>At least this width.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Minimum width.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer MinWidth(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var v = value.ToPoints(unit);
        return element.Constrain(c => c.MinWidth = v);
    }

    /// <summary>At most this width.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Maximum width.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer MaxWidth(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var v = value.ToPoints(unit);
        return element.Constrain(c => c.MaxWidth = v);
    }

    /// <summary>Exactly this height (moves to the next page when the available height is smaller).</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Height.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer Height(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var v = value.ToPoints(unit);
        return element.Constrain(c => c.MinHeight = c.MaxHeight = v);
    }

    /// <summary>At least this height.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Minimum height.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer MinHeight(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var v = value.ToPoints(unit);
        return element.Constrain(c => c.MinHeight = v);
    }

    /// <summary>At most this height.</summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Maximum height.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer MaxHeight(this IContainer element, float value, Unit unit = Unit.Point)
    {
        var v = value.ToPoints(unit);
        return element.Constrain(c => c.MaxHeight = v);
    }

    private static ConstrainedElement Constrain(this IContainer element, Action<ConstrainedElement> configure)
    {
        var constrained = element is ConstrainedElement { Child: null } existing ? existing : element.Element(new ConstrainedElement());
        configure(constrained);
        return constrained;
    }

    /// <summary>Sizes the content by a width-to-height ratio.</summary>
    /// <param name="element">The container.</param>
    /// <param name="ratio">Width divided by height.</param>
    /// <param name="option">Which dimension the box takes from the available space.</param>
    public static IContainer AspectRatio(this IContainer element, float ratio, AspectRatioOption option = AspectRatioOption.FitWidth) =>
        element.Element(new AspectRatioElement(ratio, option));

    // ---------------------------------------------------------------- position

    /// <summary>
    /// Shifts where the content paints horizontally by <paramref name="value"/> (positive to the right) without
    /// changing its measured size or the position of anything else. Nothing is clipped: the content may paint outside
    /// its slot and over its neighbours. Consecutive translates add up.
    /// </summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Horizontal offset.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer TranslateX(this IContainer element, float value, Unit unit = Unit.Point) =>
        element.Shift(value.ToPoints(unit), 0);

    /// <summary>
    /// Shifts where the content paints vertically by <paramref name="value"/> (positive downwards) without changing
    /// its measured size or the position of anything else. Nothing is clipped: the content may paint outside its slot
    /// and over its neighbours. Consecutive translates add up.
    /// </summary>
    /// <param name="element">The container.</param>
    /// <param name="value">Vertical offset.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public static IContainer TranslateY(this IContainer element, float value, Unit unit = Unit.Point) =>
        element.Shift(0, value.ToPoints(unit));

    private static TranslateElement Shift(this IContainer element, float x, float y)
    {
        var translate = element is TranslateElement { Child: null } existing ? existing : element.Element(new TranslateElement());
        translate.X += x;
        translate.Y += y;
        return translate;
    }

    /// <summary>
    /// Paints the content on layer <paramref name="z"/>. The content keeps its layout slot and measures exactly as
    /// before. When <paramref name="z"/> is above 0 its drawing on each page is deferred and painted after everything
    /// else that page draws at z 0 (content, header and footer), in ascending z, ties in document order, with the
    /// translation and clipping it would have had in place; 0 and below draw in place. Content at z above 0 that is
    /// split across pages is deferred page by page (each page's part paints above that page's normal content).
    /// </summary>
    /// <param name="element">The container.</param>
    /// <param name="z">The layer; above 0 paints over the normal content.</param>
    public static IContainer ZIndex(this IContainer element, int z) => element.Element(new ZIndexElement(z));

    /// <summary>
    /// A fixed canvas with absolutely placed children (<see cref="FreeformDescriptor.Item"/>). It takes the whole space
    /// it is offered (constrain it with <c>Width</c>/<c>Height</c>) and does not paginate; content outside it is clipped.
    /// </summary>
    /// <param name="element">The container.</param>
    /// <param name="handler">Places the items.</param>
    public static void Freeform(this IContainer element, Action<FreeformDescriptor> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var descriptor = new FreeformDescriptor();
        handler(descriptor);
        element.SetChild(descriptor.Freeform);
    }

    // -------------------------------------------------------------- pagination

    /// <summary>Ends the page here; the following content starts on the next page.</summary>
    /// <param name="element">The container.</param>
    public static void PageBreak(this IContainer element) => element.SetChild(new PageBreakElement());

    /// <summary>
    /// Starts the content on the next page when less than <paramref name="minHeight"/> is left and the content would
    /// not fit whole.
    /// </summary>
    /// <param name="element">The container.</param>
    /// <param name="minHeight">The space the content needs to start here.</param>
    /// <param name="unit">Unit of <paramref name="minHeight"/>.</param>
    public static IContainer EnsureSpace(this IContainer element, float minHeight = 150, Unit unit = Unit.Point) =>
        element.Element(new EnsureSpaceElement(minHeight.ToPoints(unit)));

    /// <summary>Draws the content only where it fits whole; content taller than a page fails the layout.</summary>
    /// <param name="element">The container.</param>
    public static IContainer PreventPageBreak(this IContainer element) => element.Element(new PreventPageBreakElement());

    /// <summary>Alias of <see cref="PreventPageBreak"/>.</summary>
    /// <param name="element">The container.</param>
    public static IContainer ShowEntire(this IContainer element) => element.PreventPageBreak();

    /// <summary>Skips the content the first time it would be drawn (headers and footers not shown on the first page).</summary>
    /// <param name="element">The container.</param>
    public static IContainer SkipOnce(this IContainer element) => element.Element(new SkipOnceElement());

    // ------------------------------------------------------------ navigation

    /// <summary>Names the content as a link target and records the page it starts on.</summary>
    /// <param name="element">The container.</param>
    /// <param name="sectionName">The section name links point to.</param>
    public static IContainer Section(this IContainer element, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(sectionName);
        return element.Element(new SectionElement(sectionName));
    }

    /// <summary>Records the pages the content is drawn on under <paramref name="key"/>; <see cref="IDocument.GeneratePdf(out DocumentLayoutInfo)"/> reports them.</summary>
    /// <param name="element">The container.</param>
    /// <param name="key">The key the pages are reported under (unique per element; a key used twice merges the spans).</param>
    public static IContainer TrackPages(this IContainer element, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return element.Element(new PageTrackerElement(key));
    }

    /// <summary>Records the page and top-left position of the content on every page it is drawn on.</summary>
    /// <param name="element">The container.</param>
    /// <param name="id">The capture id dynamic components look up.</param>
    public static IContainer CaptureContentPosition(this IContainer element, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return element.Element(new CaptureContentPositionElement(id));
    }

    /// <summary>Makes the content's area a link to <paramref name="url"/>.</summary>
    /// <param name="element">The container.</param>
    /// <param name="url">The external URL.</param>
    public static IContainer Hyperlink(this IContainer element, string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return element.Element(new HyperlinkElement(url));
    }

    // ------------------------------------------------------------- composition

    /// <summary>Runs <paramref name="handler"/> on this container (for conditional chains and reusable fragments).</summary>
    /// <param name="element">The container.</param>
    /// <param name="handler">Composes the content.</param>
    public static void Element(this IContainer element, Action<IContainer> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler(element);
    }

    /// <summary>Runs <paramref name="handler"/> on this container and continues the chain with the container it returns.</summary>
    /// <param name="element">The container.</param>
    /// <param name="handler">Wraps the container.</param>
    public static IContainer Element(this IContainer element, Func<IContainer, IContainer> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return handler(element);
    }

    /// <summary>The default text style for the content: applied over the enclosing default.</summary>
    /// <param name="element">The container.</param>
    /// <param name="handler">Builds the style from an empty (fully inheriting) style.</param>
    public static IContainer DefaultTextStyle(this IContainer element, Func<TextStyle, TextStyle> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return element.DefaultTextStyle(handler(TextStyle.Inherit));
    }

    /// <summary>The default text style for the content: applied over the enclosing default.</summary>
    /// <param name="element">The container.</param>
    /// <param name="style">The style; unset properties inherit.</param>
    public static IContainer DefaultTextStyle(this IContainer element, TextStyle style) => element.Element(new TextStyleScope(style));

    /// <summary>Content composed per page by <paramref name="dynamicElement"/>.</summary>
    /// <param name="element">The container.</param>
    /// <param name="dynamicElement">The component.</param>
    public static void Dynamic(this IContainer element, IDynamicComponent dynamicElement)
    {
        ArgumentNullException.ThrowIfNull(dynamicElement);
        element.SetChild(new DynamicHostElement(dynamicElement));
    }
}
