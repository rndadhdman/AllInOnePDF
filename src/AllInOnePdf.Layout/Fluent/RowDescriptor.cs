using AllInOnePdf.Layout.Elements;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Builds a row: items side by side, all drawn with the height of the tallest.</summary>
public sealed class RowDescriptor
{
    internal RowDescriptor()
    {
    }

    internal RowElement Row { get; } = new();

    /// <summary>The gap between consecutive items.</summary>
    /// <param name="value">Gap width.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public void Spacing(float value, Unit unit = Unit.Point) => Row.Spacing = value.ToPoints(unit);

    /// <summary>An item sharing the width left after constant and auto items, in proportion to <paramref name="size"/>.</summary>
    /// <param name="size">The item's weight.</param>
    public IContainer RelativeItem(float size = 1) => Add(RowItemKind.Relative, size);

    /// <summary>An item of exactly <paramref name="size"/> width.</summary>
    /// <param name="size">The item's width.</param>
    /// <param name="unit">Unit of <paramref name="size"/>.</param>
    public IContainer ConstantItem(float size, Unit unit = Unit.Point) => Add(RowItemKind.Constant, size.ToPoints(unit));

    /// <summary>An item as wide as its content (measured with unlimited space).</summary>
    public IContainer AutoItem() => Add(RowItemKind.Auto, 0);

    private SlotElement Add(RowItemKind kind, float value)
    {
        var item = new RowItem(kind, value);
        Row.Items.Add(item);
        return item.Content;
    }
}
