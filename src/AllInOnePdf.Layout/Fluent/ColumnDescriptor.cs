using AllInOnePdf.Layout.Elements;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Builds a column: a vertical stack of items that paginates item by item.</summary>
public sealed class ColumnDescriptor
{
    internal ColumnDescriptor()
    {
    }

    internal ColumnElement Column { get; } = new();

    /// <summary>The gap between consecutive items (not before the first, not after the last item on a page).</summary>
    /// <param name="value">Gap length.</param>
    /// <param name="unit">Unit of <paramref name="value"/>.</param>
    public void Spacing(float value, Unit unit = Unit.Point) => Column.Spacing = value.ToPoints(unit);

    /// <summary>
    /// Wraps every item added from now on (a composer sets it per block to track the pages of the block's items, see
    /// <c>TrackPages</c>); null adds items as they are.
    /// </summary>
    public Func<IContainer, IContainer>? ItemDecorator { get; set; }

    /// <summary>Adds an item and returns its container (decorated by <see cref="ItemDecorator"/> when one is set).</summary>
    public IContainer Item()
    {
        var slot = new SlotElement();
        Column.Items.Add(slot);
        return ItemDecorator is null ? slot : ItemDecorator(slot);
    }
}
