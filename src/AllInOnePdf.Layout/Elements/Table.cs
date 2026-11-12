using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Elements;

/// <summary>A column definition: a constant width in points, or a weight sharing what the constant columns leave.</summary>
internal readonly record struct TableColumn(float ConstantWidth, float RelativeWeight);

/// <summary>
/// A grid of cells with optional header rows (QuestPDF's Table).
/// <list type="bullet">
/// <item>Cells are placed in declaration order into the first free slot from a cursor that moves left to right, top
/// to bottom (QuestPDF's auto placement); <see cref="TableCell.Row(uint)"/>/<see cref="TableCell.Column(uint)"/>
/// pin a cell. Spans wider than the table are clipped to its last column.</item>
/// <item>Column widths: constant columns first, the rest of the width shared by weight. The table is as wide as the
/// space offered; Wrap when the constant columns alone do not fit.</item>
/// <item>Body cells are measured with their span's width and the height left below their row's top; a row ends at the
/// lowest bottom of the cells that end in it, and every cell is drawn as tall as the rows it covers.</item>
/// <item>Pagination follows QuestPDF: a cell whose content does not fit draws what fits (PartialRender) and the row is
/// the last one on the page; its other cells are drawn to the same height, and the row continues at the top of the
/// next page, where every cell of it is laid out again (finished text measures empty, so only its box is drawn). A row with a cell that cannot start (Wrap) moves to the
/// next page. Rows joined by row spans move together unless they are the first rows on the page. When the table is
/// continuing at the top of a page and its next row still cannot start (content that never splits, taller than a
/// page), that row is drawn clipped to the page and the table continues.</item>
/// <item>Header rows are drawn at the top of every page the table occupies; header cells are reset after each draw
/// so their content draws again.</item>
/// </list>
/// </summary>
internal sealed class Table : Element
{
    private const float Epsilon = 0.001f;

    private readonly HashSet<TableCell> _rendered = [];

    private Grid? _header;
    private Grid? _body;

    // Header row bottoms for the width last laid out.
    private float _layoutWidth = -1;
    private float[] _columnWidths = [];
    private float[] _columnOffsets = [];
    private float[] _headerBottoms = [];

    // Pagination state: the first body row with unfinished cells.
    private int _currentRow;
    private bool _headerOnlyDrawn;

    // The page on which the table last drew part of itself or answered Wrap: on any later page it starts at the top.
    private int? _pendingSincePage;

    public List<TableColumn> Columns { get; } = [];

    public List<TableCell> HeaderCells { get; } = [];

    public List<TableCell> Cells { get; } = [];

    /// <summary>On every page, the last cell of each column is stretched down to the table's bottom on that page.</summary>
    public bool ExtendLastCellsToTableBottom { get; set; }

    public override IEnumerable<Element> GetChildren() => HeaderCells.Concat(Cells);

    public override void Reset()
    {
        base.Reset();
        _rendered.Clear();
        _currentRow = 0;
        _headerOnlyDrawn = false;
        _pendingSincePage = null;
        _layoutWidth = -1;
    }

    public override SpacePlan Measure(Size availableSpace)
    {
        var plan = Plan(availableSpace, markWrap: true);
        return plan.Type switch
        {
            SpacePlanType.PartialRender => SpacePlan.PartialRender(plan.Width, plan.Height),
            SpacePlanType.FullRender => SpacePlan.FullRender(plan.Width, plan.Height),
            SpacePlanType.Wrap => SpacePlan.Wrap(),
            _ => SpacePlan.Empty(),
        };
    }

    public override void Draw(Size availableSpace)
    {
        var plan = Plan(availableSpace, markWrap: false);
        if (plan.Type is SpacePlanType.Empty or SpacePlanType.Wrap)
        {
            return;
        }

        var header = _header!;
        if (plan.Clipped)
        {
            Canvas.Save();
            Canvas.ClipRectangle(Position.Zero, new Size(plan.Width, availableSpace.Height));
        }

        foreach (var cell in header.Cells)
        {
            var top = cell.PlacedRow == 0 ? 0 : _headerBottoms[cell.PlacedRow - 1];
            DrawCell(cell, top, _headerBottoms[cell.LastRow] - top);

            // Headers repeat: their content must draw again on the next page.
            cell.Reset();
        }

        var headerHeight = header.Height(_headerBottoms);
        if (plan.Commands.Count > 0)
        {
            foreach (var command in plan.Commands)
            {
                if (command.Done)
                {
                    _rendered.Add(command.Cell);
                }

                DrawCell(command.Cell, headerHeight + command.Top, command.Height);
            }

            _currentRow = plan.NextRow;
        }
        else
        {
            _headerOnlyDrawn = true;
        }

        if (plan.Clipped)
        {
            Canvas.Restore();
        }

        _pendingSincePage = plan.Type == SpacePlanType.PartialRender ? PageContext.CurrentPage : null;
    }

    private void DrawCell(TableCell cell, float top, float height)
    {
        var offset = new Position(_columnOffsets[cell.PlacedColumn], top);
        Canvas.Translate(offset);
        cell.Draw(new Size(SpanWidth(cell), Math.Max(0, height)));
        Canvas.Translate(offset.Reverse());
    }

    /// <summary>One body cell to draw on this page: its top (below the header), drawn height and whether it finishes.</summary>
    private readonly record struct CellCommand(TableCell Cell, float Top, float Height, bool Done);

    private readonly record struct LayoutPlan(SpacePlanType Type, float Width, float Height, IReadOnlyList<CellCommand> Commands, int NextRow, bool Clipped);

    private static readonly IReadOnlyList<CellCommand> NoCommands = [];

    private LayoutPlan Plan(Size available, bool markWrap)
    {
        if (Columns.Count == 0 || (HeaderCells.Count == 0 && Cells.Count == 0))
        {
            return default;
        }

        _header ??= Grid.Place(HeaderCells, Columns.Count);
        _body ??= Grid.Place(Cells, Columns.Count);
        var header = _header;
        var body = _body;

        var bodyDone = body.Cells.Length == 0 ? _headerOnlyDrawn : _rendered.Count >= body.Cells.Length;
        if (bodyDone)
        {
            return default;
        }

        var constant = Columns.Sum(c => c.ConstantWidth);
        if (constant > available.Width + Epsilon)
        {
            return new LayoutPlan(SpacePlanType.Wrap, 0, 0, NoCommands, _currentRow, false);
        }

        EnsureLayout(available.Width);
        var width = Columns.Any(c => c.RelativeWeight > 0) ? Math.Max(0, available.Width) : constant;
        var headerHeight = header.Height(_headerBottoms);
        if (headerHeight > available.Height + Epsilon)
        {
            return WrapOrClip(available, width, markWrap);
        }

        if (body.Cells.Length == 0)
        {
            return new LayoutPlan(SpacePlanType.FullRender, width, headerHeight, NoCommands, 0, false);
        }

        var (commands, nextRow, complete) = PlanBody(available.Height - headerHeight);
        if (commands.Count == 0)
        {
            return WrapOrClip(available, width, markWrap);
        }

        var height = headerHeight + commands.Max(c => c.Top + c.Height);
        return new LayoutPlan(complete ? SpacePlanType.FullRender : SpacePlanType.PartialRender, width, height, commands, nextRow, false);
    }

    /// <summary>
    /// Nothing of the body can start here. When the table is continuing at the top of a page the next rows are taller
    /// than any page: they are drawn clipped (measured with infinite height) so the document still finishes.
    /// </summary>
    private LayoutPlan WrapOrClip(Size available, float width, bool markWrap)
    {
        if (_pendingSincePage is { } page && PageContext.CurrentPage > page)
        {
            var (commands, nextRow, complete) = PlanClipped();
            var type = complete ? SpacePlanType.FullRender : SpacePlanType.PartialRender;
            return new LayoutPlan(type, width, Math.Max(0, available.Height), commands, nextRow, true);
        }

        if (markWrap)
        {
            _pendingSincePage ??= PageContext.CurrentPage;
        }

        return new LayoutPlan(SpacePlanType.Wrap, 0, 0, NoCommands, _currentRow, false);
    }

    /// <summary>QuestPDF's table layout for the body cells that are not finished, within <paramref name="availableHeight"/>.</summary>
    private (List<CellCommand> Commands, int NextRow, bool Complete) PlanBody(float availableHeight)
    {
        var body = _body!;
        var start = _currentRow;
        var bottoms = new float[body.RowCount];
        float Bottom(int row) => row < start ? 0 : bottoms[row];

        var measured = new List<(TableCell Cell, SpacePlan Plan, float Top)>();
        var current = start;
        var maxRow = int.MaxValue;
        foreach (var cell in body.Cells)
        {
            // Finished cells of a continuing row are laid out again (QuestPDF does): their content measures empty, but
            // their backgrounds and borders are drawn to the row's height on this page too.
            if (cell.LastRow < start)
            {
                continue;
            }

            if (cell.PlacedRow > current)
            {
                // The rows between the previous cell's row and this one end no lower than the row above them.
                for (var r = Math.Max(start, current); r < cell.PlacedRow; r++)
                {
                    bottoms[r] = Math.Max(bottoms[r], Bottom(r - 1));
                }

                if (Bottom(cell.PlacedRow - 1) > availableHeight + Epsilon)
                {
                    maxRow = Math.Min(maxRow, cell.PlacedRow - 1);
                    break;
                }

                current = cell.PlacedRow;
            }

            if (cell.PlacedRow > maxRow)
            {
                break;
            }

            var top = Bottom(cell.PlacedRow - 1);
            var plan = cell.Measure(new Size(SpanWidth(cell), Math.Max(0, availableHeight - top)));
            if (plan.IsWrap)
            {
                maxRow = Math.Min(maxRow, cell.PlacedRow - 1);
                continue;
            }

            if (plan.IsPartial)
            {
                maxRow = Math.Min(maxRow, cell.LastRow);
            }

            var height = plan.HasContent ? plan.Height : 0;
            bottoms[cell.LastRow] = Math.Max(bottoms[cell.LastRow], top + height);
            measured.Add((cell, plan, top));
        }

        for (var r = Math.Max(start, current); r < body.RowCount; r++)
        {
            bottoms[r] = Math.Max(bottoms[r], Bottom(r - 1));
        }

        // Rows joined by a row span stay together unless they are the first rows on the page.
        if (maxRow < body.RowCount - 1)
        {
            foreach (var (groupStart, groupEnd) in body.Groups)
            {
                if (groupStart <= maxRow && maxRow < groupEnd && groupStart > start)
                {
                    maxRow = groupStart - 1;
                    break;
                }
            }
        }

        var commands = new List<CellCommand>();
        foreach (var (cell, plan, top) in measured)
        {
            if (cell.PlacedRow > maxRow)
            {
                continue;
            }

            commands.Add(new CellCommand(cell, top, Bottom(cell.LastRow) - top, !plan.IsPartial));
        }

        if (ExtendLastCellsToTableBottom && commands.Count > 0)
        {
            var tableBottom = commands.Max(c => c.Top + c.Height);
            var last = new int[body.ColumnCount];
            Array.Fill(last, -1);
            for (var i = 0; i < commands.Count; i++)
            {
                var cell = commands[i].Cell;
                for (var c = cell.PlacedColumn; c < cell.PlacedColumn + cell.PlacedColumnSpan; c++)
                {
                    if (last[c] < 0 || cell.LastRow >= commands[last[c]].Cell.LastRow)
                    {
                        last[c] = i;
                    }
                }
            }

            foreach (var i in last.Where(i => i >= 0).Distinct())
            {
                commands[i] = commands[i] with { Height = tableBottom - commands[i].Top };
            }
        }

        // The next page starts at the first row that still has unfinished cells.
        var done = new HashSet<TableCell>(_rendered);
        foreach (var command in commands)
        {
            if (command.Done)
            {
                done.Add(command.Cell);
            }
        }

        var nextRow = body.RowCount;
        foreach (var cell in body.Cells)
        {
            if (!done.Contains(cell))
            {
                nextRow = Math.Min(nextRow, cell.PlacedRow);
            }
        }

        return (commands, Math.Max(start, nextRow), done.Count >= body.Cells.Length);
    }

    /// <summary>The next row group measured with infinite height, for drawing clipped to a page it cannot fit.</summary>
    private (List<CellCommand> Commands, int NextRow, bool Complete) PlanClipped()
    {
        var body = _body!;
        var commands = new List<CellCommand>();
        if (body.RowCount == 0)
        {
            return (commands, 0, true);
        }

        var (_, groupEnd) = body.Groups.First(g => g.End >= _currentRow);
        var start = _currentRow;
        var bottoms = new float[body.RowCount];
        float Bottom(int row) => row < start ? 0 : bottoms[row];
        var cells = body.Cells.Where(c => c.PlacedRow <= groupEnd && c.LastRow >= start && !_rendered.Contains(c)).ToList();
        for (var r = start; r <= groupEnd; r++)
        {
            bottoms[r] = Bottom(r - 1);
            foreach (var cell in cells.Where(c => c.LastRow == r))
            {
                var top = Bottom(cell.PlacedRow - 1);
                var plan = cell.Measure(new Size(SpanWidth(cell), Size.Infinity));
                bottoms[r] = Math.Max(bottoms[r], top + (plan.HasContent ? plan.Height : 0));
            }
        }

        foreach (var cell in cells)
        {
            var top = Bottom(cell.PlacedRow - 1);
            commands.Add(new CellCommand(cell, top, Bottom(cell.LastRow) - top, Done: true));
        }

        var remaining = body.Cells.Count(c => !_rendered.Contains(c)) - cells.Count;
        return (commands, groupEnd + 1, remaining <= 0);
    }

    /// <summary>Column widths and header row bottoms for <paramref name="width"/>; cached until the width changes or the table is reset.</summary>
    private void EnsureLayout(float width)
    {
        if (Math.Abs(_layoutWidth - width) < Epsilon)
        {
            return;
        }

        var constant = Columns.Sum(c => c.ConstantWidth);
        var weights = Columns.Sum(c => c.RelativeWeight);
        var share = weights > 0 ? Math.Max(0, width - constant) / weights : 0;
        _columnWidths = [.. Columns.Select(c => c.ConstantWidth + c.RelativeWeight * share)];
        _columnOffsets = new float[_columnWidths.Length];
        for (var c = 1; c < _columnWidths.Length; c++)
        {
            _columnOffsets[c] = _columnOffsets[c - 1] + _columnWidths[c - 1];
        }

        _headerBottoms = RowBottoms(_header!);
        _layoutWidth = width;
    }

    private float SpanWidth(TableCell cell)
    {
        var width = 0f;
        for (var c = cell.PlacedColumn; c < cell.PlacedColumn + cell.PlacedColumnSpan; c++)
        {
            width += _columnWidths[c];
        }

        return width;
    }

    /// <summary>Cumulative header row bottoms (cells measured with infinite height): each row ends at the lowest bottom of the cells ending in it.</summary>
    private float[] RowBottoms(Grid grid)
    {
        var bottoms = new float[grid.RowCount];
        for (var r = 0; r < grid.RowCount; r++)
        {
            var bottom = r == 0 ? 0 : bottoms[r - 1];
            foreach (var cell in grid.EndingIn[r])
            {
                var top = cell.PlacedRow == 0 ? 0 : bottoms[cell.PlacedRow - 1];
                var plan = cell.Measure(new Size(SpanWidth(cell), Size.Infinity));
                bottom = Math.Max(bottom, top + (plan.HasContent ? Math.Min(plan.Height, Size.Infinity) : 0));
            }

            bottoms[r] = bottom;
        }

        return bottoms;
    }

    /// <summary>Cells with their final positions, rows grouped by row spans.</summary>
    private sealed class Grid
    {
        private Grid(TableCell[] cells, int columnCount, int rowCount)
        {
            Cells = cells;
            ColumnCount = columnCount;
            RowCount = rowCount;
            EndingIn = new List<TableCell>[rowCount];
            var startingIn = new int[rowCount];
            for (var r = 0; r < rowCount; r++)
            {
                EndingIn[r] = [];
                startingIn[r] = r;
            }

            foreach (var cell in cells)
            {
                EndingIn[cell.LastRow].Add(cell);
                startingIn[cell.PlacedRow] = Math.Max(startingIn[cell.PlacedRow], cell.LastRow);
            }

            for (var start = 0; start < rowCount;)
            {
                var end = startingIn[start];
                for (var r = start + 1; r <= end; r++)
                {
                    end = Math.Max(end, startingIn[r]);
                }

                Groups.Add((start, end));
                start = end + 1;
            }
        }

        /// <summary>Sorted by row, then column: the drawing order.</summary>
        public TableCell[] Cells { get; }

        public int ColumnCount { get; }

        public int RowCount { get; }

        /// <summary>Per row, the cells whose last row it is.</summary>
        public List<TableCell>[] EndingIn { get; }

        /// <summary>Row ranges (inclusive) joined by row spans.</summary>
        public List<(int Start, int End)> Groups { get; } = [];

        public float Height(float[] bottoms) => RowCount == 0 ? 0 : bottoms[RowCount - 1];

        public static Grid Place(List<TableCell> cells, int columnCount)
        {
            var occupied = new List<bool[]>();
            bool Free(int row, int column, int columnSpan, int rowSpan)
            {
                for (var r = row; r < row + rowSpan; r++)
                {
                    if (r >= occupied.Count)
                    {
                        return true;
                    }

                    for (var c = column; c < column + columnSpan; c++)
                    {
                        if (occupied[r][c])
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            void Occupy(TableCell cell, int row, int column, int columnSpan)
            {
                cell.PlacedRow = row;
                cell.PlacedColumn = column;
                cell.PlacedColumnSpan = columnSpan;
                while (occupied.Count <= cell.LastRow)
                {
                    occupied.Add(new bool[columnCount]);
                }

                for (var r = row; r <= cell.LastRow; r++)
                {
                    for (var c = column; c < column + columnSpan; c++)
                    {
                        occupied[r][c] = true;
                    }
                }
            }

            int SpanAt(TableCell cell, int column) => (int)Math.Min(cell.ColumnSpanCount, (uint)(columnCount - column));

            // Pinned cells first, so automatic ones flow around them.
            foreach (var cell in cells.Where(c => c.RequestedRow > 0 && c.RequestedColumn > 0))
            {
                var column = (int)Math.Min(cell.RequestedColumn - 1, (uint)(columnCount - 1));
                Occupy(cell, (int)Math.Min(cell.RequestedRow - 1, 100_000u), column, SpanAt(cell, column));
            }

            var cursorRow = 0;
            var cursorColumn = 0;
            foreach (var cell in cells.Where(c => c.RequestedRow == 0 || c.RequestedColumn == 0))
            {
                var rowSpan = (int)Math.Min(cell.RowSpanCount, 100_000u);
                if (cell.RequestedColumn > 0)
                {
                    // Column pinned: the first free row in that column from the cursor's row.
                    var column = (int)Math.Min(cell.RequestedColumn - 1, (uint)(columnCount - 1));
                    var span = SpanAt(cell, column);
                    var row = cursorRow;
                    while (!Free(row, column, span, rowSpan))
                    {
                        row++;
                    }

                    Occupy(cell, row, column, span);
                    continue;
                }

                var r0 = cell.RequestedRow > 0 ? (int)Math.Min(cell.RequestedRow - 1, 100_000u) : cursorRow;
                var c0 = cell.RequestedRow > 0 && r0 != cursorRow ? 0 : cursorColumn;
                var wanted = (int)Math.Min(cell.ColumnSpanCount, (uint)columnCount);
                while (true)
                {
                    if (c0 + wanted <= columnCount && Free(r0, c0, wanted, rowSpan))
                    {
                        break;
                    }

                    c0++;
                    if (c0 + wanted > columnCount)
                    {
                        c0 = 0;
                        r0++;
                    }
                }

                Occupy(cell, r0, c0, wanted);
                if (cell.RequestedRow == 0)
                {
                    cursorRow = r0;
                    cursorColumn = c0 + wanted;
                    if (cursorColumn >= columnCount)
                    {
                        cursorRow++;
                        cursorColumn = 0;
                    }
                }
            }

            var sorted = cells.OrderBy(c => c.PlacedRow).ThenBy(c => c.PlacedColumn).ToArray();
            var rowCount = sorted.Length == 0 ? 0 : sorted.Max(c => c.LastRow) + 1;
            return new Grid(sorted, columnCount, rowCount);
        }
    }
}
