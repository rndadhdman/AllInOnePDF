using AllInOnePdf.Layout.Elements;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>
/// One page description: size, margins, colour, default text style and the header, content and footer slots. The
/// engine produces as many pages from it as the content needs.
/// </summary>
public sealed class PageDescriptor
{
    internal PageDescriptor()
    {
    }

    /// <summary>Page width in points (A4 by default).</summary>
    internal float PageWidth { get; private set; } = 595.4f;

    /// <summary>Page height in points (A4 by default).</summary>
    internal float PageHeight { get; private set; } = 842f;

    internal float Top { get; private set; }

    internal float Right { get; private set; }

    internal float Bottom { get; private set; }

    internal float Left { get; private set; }

    internal Color Color { get; private set; } = Color.White;

    internal TextStyle TextStyle { get; private set; } = TextStyle.Inherit;

    internal SlotElement HeaderSlot { get; } = new();

    internal SlotElement ContentSlot { get; } = new();

    internal SlotElement FooterSlot { get; } = new();

    /// <summary>The page size.</summary>
    /// <param name="width">Page width.</param>
    /// <param name="height">Page height.</param>
    /// <param name="unit">Unit of both values.</param>
    public void Size(float width, float height, Unit unit = Unit.Point)
    {
        PageWidth = width.ToPoints(unit);
        PageHeight = height.ToPoints(unit);
    }

    /// <summary>The page size.</summary>
    /// <param name="size">Page size in points.</param>
    public void Size(Size size) => Size(size.Width, size.Height);

    /// <summary>All four margins.</summary>
    /// <param name="value">Margin.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public void Margin(float value, Unit unit = Unit.Point)
    {
        MarginVertical(value, unit);
        MarginHorizontal(value, unit);
    }

    /// <summary>Top and bottom margins.</summary>
    /// <param name="value">Margin.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public void MarginVertical(float value, Unit unit = Unit.Point)
    {
        MarginTop(value, unit);
        MarginBottom(value, unit);
    }

    /// <summary>Left and right margins.</summary>
    /// <param name="value">Margin.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public void MarginHorizontal(float value, Unit unit = Unit.Point)
    {
        MarginLeft(value, unit);
        MarginRight(value, unit);
    }

    /// <summary>The top margin.</summary>
    /// <param name="value">Margin.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public void MarginTop(float value, Unit unit = Unit.Point) => Top = value.ToPoints(unit);

    /// <summary>The bottom margin.</summary>
    /// <param name="value">Margin.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public void MarginBottom(float value, Unit unit = Unit.Point) => Bottom = value.ToPoints(unit);

    /// <summary>The left margin.</summary>
    /// <param name="value">Margin.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public void MarginLeft(float value, Unit unit = Unit.Point) => Left = value.ToPoints(unit);

    /// <summary>The right margin.</summary>
    /// <param name="value">Margin.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public void MarginRight(float value, Unit unit = Unit.Point) => Right = value.ToPoints(unit);

    /// <summary>The colour the whole page is filled with (white by default).</summary>
    /// <param name="color">Page colour.</param>
    public void PageColor(Color color) => Color = color;

    /// <summary>The default text style of the header, content and footer, applied over <see cref="TextStyle.Default"/>.</summary>
    /// <param name="handler">Builds the style from an empty (fully inheriting) style.</param>
    public void DefaultTextStyle(Func<TextStyle, TextStyle> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        TextStyle = handler(TextStyle.Inherit);
    }

    /// <summary>The default text style of the header, content and footer, applied over <see cref="TextStyle.Default"/>.</summary>
    /// <param name="style">The style; unset properties inherit.</param>
    public void DefaultTextStyle(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        TextStyle = style;
    }

    /// <summary>Drawn at the top of every page; its height is taken from the content area. It does not paginate.</summary>
    public IContainer Header() => HeaderSlot;

    /// <summary>The flowing content, continued over as many pages as it needs.</summary>
    public IContainer Content() => ContentSlot;

    /// <summary>Drawn at the bottom of every page; its height is taken from the content area. It does not paginate.</summary>
    public IContainer Footer() => FooterSlot;
}
