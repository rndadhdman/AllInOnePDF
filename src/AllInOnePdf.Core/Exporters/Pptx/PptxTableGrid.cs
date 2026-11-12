using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Exporters.Pptx;

/// <summary>
/// Places model table cells on a grid (row spans cover the cells below, which the model omits), so a DrawingML
/// table can be written with one <c>a:tc</c> per grid position: origins carry gridSpan/rowSpan, covered positions
/// hMerge/vMerge.
/// </summary>
internal sealed class PptxTableGrid
{
    private readonly Dictionary<(int Row, int Col), (int Row, int Col, TableCell Cell)> _owners = [];

    private PptxTableGrid(int rows)
    {
        Rows = rows;
    }

    public int Rows { get; }

    public int Columns { get; private set; }

    public static PptxTableGrid Layout(TableBlock table)
    {
        var grid = new PptxTableGrid(table.Rows.Count);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var col = 0;
            foreach (var cell in table.Rows[r].Cells)
            {
                while (grid._owners.ContainsKey((r, col)))
                {
                    col++;
                }

                var rowSpan = Math.Clamp(cell.RowSpan, 1, table.Rows.Count - r);
                var colSpan = Math.Max(1, cell.ColumnSpan);
                for (var dr = 0; dr < rowSpan; dr++)
                {
                    for (var dc = 0; dc < colSpan; dc++)
                    {
                        grid._owners.TryAdd((r + dr, col + dc), (r, col, cell));
                    }
                }

                col += colSpan;
                grid.Columns = Math.Max(grid.Columns, col);
            }
        }

        return grid;
    }

    /// <summary>The cell covering a grid position and the position of its top-left corner; null when the row is short.</summary>
    public (int Row, int Col, TableCell Cell)? Owner(int row, int col) =>
        _owners.TryGetValue((row, col), out var owner) ? owner : null;

    /// <summary>Column span of an origin cell, limited to the grid.</summary>
    public int ColumnSpan(int row, int col)
    {
        var owner = Owner(row, col);
        if (owner is null)
        {
            return 1;
        }

        var span = 0;
        while (col + span < Columns && Owner(row, col + span) is { } o && ReferenceEquals(o.Cell, owner.Value.Cell))
        {
            span++;
        }

        return Math.Max(1, span);
    }

    /// <summary>Row span of an origin cell, limited to the grid.</summary>
    public int RowSpan(int row, int col)
    {
        var owner = Owner(row, col);
        if (owner is null)
        {
            return 1;
        }

        var span = 0;
        while (row + span < Rows && Owner(row + span, col) is { } o && ReferenceEquals(o.Cell, owner.Value.Cell))
        {
            span++;
        }

        return Math.Max(1, span);
    }
}
