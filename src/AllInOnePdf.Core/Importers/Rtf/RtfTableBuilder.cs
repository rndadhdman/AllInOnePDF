using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers.Rtf;

/// <summary>
/// Collects RTF table rows (<c>\trowd … \cellx … \cell … \row</c>) and turns them into a <see cref="TableBlock"/>:
/// column spans come from the cell boundaries of all rows (so both <c>\clmgf</c>/<c>\clmrg</c> merges and wider
/// cells work), vertical merges (<c>\clvmgf</c>/<c>\clvmrg</c>) become row spans.
/// </summary>
internal sealed class RtfTableBuilder
{
    private const int SnapTwips = 20;

    private sealed record CellDefinition(int Right, bool MergeFirst, bool MergeNext, bool VerticalFirst, bool VerticalNext);

    private sealed record RawCell(RtfBlockSink Content);

    private sealed record RawRow(List<RawCell> Cells, List<CellDefinition> Definitions, bool Header);

    private readonly List<RawRow> _rows = [];
    private List<CellDefinition> _definitions = [];
    private List<RawCell> _cells = [];
    private bool _header;
    private bool _pendingMergeFirst;
    private bool _pendingMergeNext;
    private bool _pendingVerticalFirst;
    private bool _pendingVerticalNext;

    /// <summary>Where the paragraphs of the cell being read go.</summary>
    public RtfBlockSink Cell { get; private set; } = new();

    public bool IsEmpty => _rows.Count == 0 && _cells.Count == 0 && Cell.Blocks.Count == 0;

    /// <summary><c>\trowd</c>: a new row definition (Word repeats it at the end of each row; that is harmless).</summary>
    public void StartRowDefinition()
    {
        _definitions = [];
        _header = false;
        ResetPending();
    }

    public void MarkHeader() => _header = true;

    public void MergeFirst() => _pendingMergeFirst = true;

    public void MergeNext() => _pendingMergeNext = true;

    public void VerticalFirst() => _pendingVerticalFirst = true;

    public void VerticalNext() => _pendingVerticalNext = true;

    public void CellBoundary(int right)
    {
        _definitions.Add(new CellDefinition(right, _pendingMergeFirst, _pendingMergeNext, _pendingVerticalFirst, _pendingVerticalNext));
        ResetPending();
    }

    public void EndCell()
    {
        _cells.Add(new RawCell(Cell));
        Cell = new RtfBlockSink();
    }

    public void EndRow()
    {
        if (Cell.Blocks.Count > 0)
        {
            EndCell();
        }

        if (_cells.Count > 0)
        {
            _rows.Add(new RawRow(_cells, _definitions, _header));
        }

        _cells = [];
    }

    public TableBlock? Build()
    {
        EndRow();
        if (_rows.Count == 0)
        {
            return null;
        }

        // Lay out every row as (left, right, cell) with horizontal merges folded into the first cell.
        var laidOut = new List<(bool Header, List<(int Left, int Right, CellDefinition Def, TableCell Cell, TextAlignment? Align)> Cells)>();
        var boundaries = new SortedSet<int> { 0 };
        foreach (var row in _rows)
        {
            var cells = new List<(int Left, int Right, CellDefinition Def, TableCell Cell, TextAlignment? Align)>();
            var left = 0;
            var lastRight = 0;
            for (var i = 0; i < row.Cells.Count; i++)
            {
                var def = i < row.Definitions.Count
                    ? row.Definitions[i]
                    : new CellDefinition(lastRight + 1440, false, false, false, false);
                var right = Math.Max(def.Right, left + 1);
                lastRight = right;
                var content = row.Cells[i].Content;
                if (def.MergeNext && cells.Count > 0)
                {
                    var previous = cells[^1];
                    previous.Cell.Blocks.AddRange(content.Blocks);
                    cells[^1] = previous with { Right = right };
                    left = right;
                    continue;
                }

                var cell = new TableCell();
                cell.Blocks.AddRange(content.Blocks);
                cells.Add((left, right, def, cell, CommonAlignment(content)));
                left = right;
            }

            foreach (var c in cells)
            {
                boundaries.Add(c.Right);
            }

            laidOut.Add((row.Header, cells));
        }

        var columns = Snap(boundaries);
        var table = new TableBlock();
        var verticalOrigins = new Dictionary<int, TableCell>();
        var alignments = new Dictionary<int, List<TextAlignment?>>();
        foreach (var (header, cells) in laidOut)
        {
            var row = new TableRow { IsHeader = header };
            var seen = new HashSet<int>();
            foreach (var (left, right, def, cell, align) in cells)
            {
                var start = ColumnIndex(columns, left);
                var end = Math.Max(ColumnIndex(columns, right), start + 1);
                cell.ColumnSpan = end - start;
                seen.Add(start);
                if (def.VerticalNext && verticalOrigins.TryGetValue(start, out var origin))
                {
                    origin.RowSpan++;
                    origin.Blocks.AddRange(cell.Blocks);
                    continue;
                }

                if (def.VerticalFirst)
                {
                    verticalOrigins[start] = cell;
                }
                else
                {
                    verticalOrigins.Remove(start);
                }

                if (!header && cell.ColumnSpan == 1 && cell.Blocks.Count > 0)
                {
                    if (!alignments.TryGetValue(start, out var list))
                    {
                        alignments[start] = list = [];
                    }

                    list.Add(align);
                }

                row.Cells.Add(cell);
            }

            foreach (var column in verticalOrigins.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                verticalOrigins.Remove(column);
            }

            if (row.Cells.Count > 0)
            {
                table.Rows.Add(row);
            }
        }

        if (table.Rows.Count == 0)
        {
            return null;
        }

        var columnCount = table.ColumnCount;
        var any = false;
        for (var c = 0; c < columnCount; c++)
        {
            var alignment = ColumnAlignment.Default;
            if (alignments.TryGetValue(c, out var list) && list.Count > 0)
            {
                if (list.All(a => a == TextAlignment.Right))
                {
                    alignment = ColumnAlignment.Right;
                }
                else if (list.All(a => a == TextAlignment.Center))
                {
                    alignment = ColumnAlignment.Center;
                }
            }

            any |= alignment != ColumnAlignment.Default;
            table.Alignments.Add(alignment);
        }

        if (!any)
        {
            table.Alignments.Clear();
        }

        return table;
    }

    private void ResetPending()
    {
        _pendingMergeFirst = false;
        _pendingMergeNext = false;
        _pendingVerticalFirst = false;
        _pendingVerticalNext = false;
    }

    private static TextAlignment? CommonAlignment(RtfBlockSink content)
    {
        if (content.Alignments.Count == 0)
        {
            return null;
        }

        var first = content.Alignments[0];
        return content.Alignments.All(a => a == first) ? first : null;
    }

    private static List<int> Snap(SortedSet<int> boundaries)
    {
        var result = new List<int>();
        foreach (var b in boundaries)
        {
            if (result.Count == 0 || b - result[^1] > SnapTwips)
            {
                result.Add(b);
            }
        }

        return result;
    }

    /// <summary>Index of the boundary nearest to <paramref name="value"/>.</summary>
    private static int ColumnIndex(List<int> columns, int value)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < columns.Count; i++)
        {
            var d = Math.Abs(columns[i] - value);
            if (d < bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }

        return best;
    }
}
