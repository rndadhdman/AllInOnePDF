using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Elements;

/// <summary>
/// One table cell: a transparent container with its requested position and spans. The table measures the content
/// with (span width, <see cref="Size.Infinity"/>) and draws it with (span width, row height), so content that fills
/// the space it is drawn with (backgrounds, borders) covers the whole cell.
/// </summary>
internal sealed class TableCell : ContainerElement, ITableCellContainer
{
    /// <summary>Requested 1-based row; 0 = automatic.</summary>
    public uint RequestedRow { get; private set; }

    /// <summary>Requested 1-based column; 0 = automatic.</summary>
    public uint RequestedColumn { get; private set; }

    public uint RowSpanCount { get; private set; } = 1;

    public uint ColumnSpanCount { get; private set; } = 1;

    /// <summary>0-based row the table placed the cell in.</summary>
    internal int PlacedRow { get; set; }

    /// <summary>0-based column the table placed the cell in.</summary>
    internal int PlacedColumn { get; set; }

    /// <summary>Column span after clamping to the table's columns.</summary>
    internal int PlacedColumnSpan { get; set; } = 1;

    internal int LastRow => PlacedRow + (int)Math.Min(RowSpanCount, 100_000u) - 1;

    /// <inheritdoc />
    public ITableCellContainer ColumnSpan(uint columns)
    {
        ArgumentOutOfRangeException.ThrowIfZero(columns);
        ColumnSpanCount = columns;
        return this;
    }

    /// <inheritdoc />
    public ITableCellContainer RowSpan(uint rows)
    {
        ArgumentOutOfRangeException.ThrowIfZero(rows);
        RowSpanCount = rows;
        return this;
    }

    /// <inheritdoc />
    public ITableCellContainer Row(uint row)
    {
        ArgumentOutOfRangeException.ThrowIfZero(row);
        RequestedRow = row;
        return this;
    }

    /// <inheritdoc />
    public ITableCellContainer Column(uint column)
    {
        ArgumentOutOfRangeException.ThrowIfZero(column);
        RequestedColumn = column;
        return this;
    }
}
