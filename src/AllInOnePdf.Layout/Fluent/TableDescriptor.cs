using AllInOnePdf.Layout.Elements;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Builds a table: <c>container.Table(t =&gt; { t.ColumnsDefinition(...); t.Header(...); t.Cell()...; })</c>.</summary>
public sealed class TableDescriptor
{
    internal TableDescriptor(Table table)
    {
        Table = table;
    }

    internal Table Table { get; }

    /// <summary>Declares the columns, left to right. Calling it again adds more columns.</summary>
    public void ColumnsDefinition(Action<TableColumnsDefinitionDescriptor> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler(new TableColumnsDefinitionDescriptor(Table));
    }

    /// <summary>Header cells: laid out like body cells in their own rows and drawn again at the top of every page the table continues on.</summary>
    public void Header(Action<TableCellDescriptor> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler(new TableCellDescriptor(Table.HeaderCells));
    }

    /// <summary>Adds a body cell in the next free slot (or where <see cref="ITableCellContainer.Row"/>/<see cref="ITableCellContainer.Column"/> put it).</summary>
    public ITableCellContainer Cell() => TableCellDescriptor.Add(Table.Cells);

    /// <summary>Stretches the last cell of every column down to the table's bottom on each page.</summary>
    public void ExtendLastCellsToTableBottom() => Table.ExtendLastCellsToTableBottom = true;
}

/// <summary>Column definitions of a table.</summary>
public sealed class TableColumnsDefinitionDescriptor
{
    private readonly Table _table;

    internal TableColumnsDefinitionDescriptor(Table table)
    {
        _table = table;
    }

    /// <summary>A column sharing the width left after constant columns, in proportion to <paramref name="weight"/>.</summary>
    public void RelativeColumn(float weight = 1)
    {
        if (!(weight >= 0) || !float.IsFinite(weight))
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "A relative column's weight must be a finite, non-negative number.");
        }

        _table.Columns.Add(new TableColumn(0, weight));
    }

    /// <summary>A column of exactly <paramref name="width"/>.</summary>
    public void ConstantColumn(float width, Unit unit = Unit.Point)
    {
        if (!(width >= 0) || !float.IsFinite(width))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A constant column's width must be a finite, non-negative number.");
        }

        _table.Columns.Add(new TableColumn(width.ToPoints(unit), 0));
    }
}

/// <summary>Adds cells to a table part (the header).</summary>
public sealed class TableCellDescriptor
{
    private readonly List<TableCell> _cells;

    internal TableCellDescriptor(List<TableCell> cells)
    {
        _cells = cells;
    }

    /// <summary>Adds a cell in the next free slot of this part.</summary>
    public ITableCellContainer Cell() => Add(_cells);

    internal static TableCell Add(List<TableCell> cells)
    {
        var cell = new TableCell();
        cells.Add(cell);
        return cell;
    }
}

/// <summary>A table cell: a container (put the cell's content in it) with its placement. Row and column numbers are 1-based.</summary>
public interface ITableCellContainer : IContainer
{
    /// <summary>The cell covers <paramref name="columns"/> columns (clipped at the table's last column).</summary>
    ITableCellContainer ColumnSpan(uint columns);

    /// <summary>The cell covers <paramref name="rows"/> rows; those rows move to the next page together unless they start the page.</summary>
    ITableCellContainer RowSpan(uint rows);

    /// <summary>Pins the cell to a row (1-based).</summary>
    ITableCellContainer Row(uint row);

    /// <summary>Pins the cell to a column (1-based).</summary>
    ITableCellContainer Column(uint column);
}
