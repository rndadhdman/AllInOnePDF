using System.Globalization;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers.Xlsx;

/// <summary>A sheet turned into a table, plus the title taken from a merged banner row above it (if any).</summary>
internal sealed record XlsxSheetTable(TableBlock Table, string? Title);

/// <summary>
/// Builds a <see cref="TableBlock"/> from <see cref="XlsxSheetData"/>: used range (trimmed or raw), hidden rows and
/// columns left out, merged cells as spans, header detection, column alignment and the row cap.
/// </summary>
internal static class XlsxTableBuilder
{
    /// <summary>Columns beyond this many are left out (a stray cell far to the right would otherwise explode the table).</summary>
    public const int MaxColumns = 512;

    public static XlsxSheetTable? Build(XlsxSheetData data, XlsxImportSettings settings, string sheetName, List<string> warnings)
    {
        if (!Bounds(data, settings.TrimEmpty, out var rowStart, out var colStart, out var rowEnd, out var colEnd))
        {
            return null;
        }

        var rowNumbers = Enumerable.Range(rowStart, rowEnd - rowStart + 1).Where(r => !data.HiddenRows.Contains(r)).ToList();
        var colNumbers = Enumerable.Range(colStart, colEnd - colStart + 1).Where(c => settings.IncludeHidden || !data.IsColumnHidden(c)).ToList();
        if (colNumbers.Count > MaxColumns)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"Sheet \"{sheetName}\": only the first {MaxColumns} of {colNumbers.Count} columns were imported."));
            colNumbers.RemoveRange(MaxColumns, colNumbers.Count - MaxColumns);
        }

        if (rowNumbers.Count == 0 || colNumbers.Count == 0)
        {
            return null;
        }

        var rowIndex = new Dictionary<int, int>();
        for (var i = 0; i < rowNumbers.Count; i++)
        {
            rowIndex[rowNumbers[i]] = i;
        }

        var colIndex = new Dictionary<int, int>();
        for (var i = 0; i < colNumbers.Count; i++)
        {
            colIndex[colNumbers[i]] = i;
        }

        var rows = rowNumbers.Count;
        var cols = colNumbers.Count;
        var grid = new XlsxCellData?[rows, cols];
        for (var r = 0; r < rows; r++)
        {
            if (data.Rows.TryGetValue(rowNumbers[r], out var cells))
            {
                foreach (var (column, cell) in cells)
                {
                    if (colIndex.TryGetValue(column, out var c))
                    {
                        grid[r, c] = cell;
                    }
                }
            }
        }

        // Merged ranges: span on the visible top-left cell, covered positions omitted.
        var rowSpan = new int[rows, cols];
        var colSpan = new int[rows, cols];
        var covered = new bool[rows, cols];
        foreach (var merge in data.Merges)
        {
            if (!rowIndex.TryGetValue(merge.Row1, out var r0) || !colIndex.TryGetValue(merge.Column1, out var c0) || covered[r0, c0])
            {
                continue;
            }

            var rs = rowNumbers.Count(n => n >= merge.Row1 && n <= merge.Row2);
            var cs = colNumbers.Count(n => n >= merge.Column1 && n <= merge.Column2);
            if (rs * cs <= 1)
            {
                continue;
            }

            var overlaps = false;
            for (var r = r0; r < r0 + rs && !overlaps; r++)
            {
                for (var c = c0; c < c0 + cs; c++)
                {
                    if (covered[r, c] || rowSpan[r, c] > 0)
                    {
                        overlaps = true;
                        break;
                    }
                }
            }

            if (overlaps)
            {
                continue;
            }

            rowSpan[r0, c0] = rs;
            colSpan[r0, c0] = cs;
            for (var r = r0; r < r0 + rs; r++)
            {
                for (var c = c0; c < c0 + cs; c++)
                {
                    if (r != r0 || c != c0)
                    {
                        covered[r, c] = true;
                        grid[r, c] = null;
                    }
                }
            }
        }

        // A first row that is one non-empty cell merged across the full width is a title banner, not data.
        var firstRow = 0;
        string? title = null;
        if (rows > 1 && cols > 1 && colSpan[0, 0] == cols && rowSpan[0, 0] == 1 && grid[0, 0] is { } banner)
        {
            title = banner.Text.Replace('\n', ' ').Trim();
            firstRow = 1;
        }

        var texts = new List<List<string>>();
        for (var r = firstRow; r < rows; r++)
        {
            var line = new List<string>(cols);
            for (var c = 0; c < cols; c++)
            {
                line.Add(grid[r, c]?.Text ?? string.Empty);
            }

            texts.Add(line);
        }

        var hasHeader = settings.HeaderRow switch
        {
            XlsxHeaderMode.Yes => true,
            XlsxHeaderMode.No => false,
            _ => DetectHeader(grid, firstRow, rows, cols) || CsvImporter.DetectHeader(texts),
        };

        var dataStart = hasHeader ? 1 : 0;
        var keepRows = Math.Min(texts.Count, dataStart + settings.MaxRows);
        var dataTotal = Math.Max(texts.Count, data.CountedRows - firstRow) - dataStart;
        if (dataTotal > settings.MaxRows)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"Sheet \"{sheetName}\": only the first {settings.MaxRows:N0} of {dataTotal:N0} rows were imported. Raise \"Maximum rows\" to include more."));
        }

        var kept = texts.Take(keepRows).ToList();
        var table = new TableBlock { Caption = sheetName };
        for (var c = 0; c < cols; c++)
        {
            table.Alignments.Add(ColumnAlignmentFor(grid, firstRow + dataStart, firstRow + keepRows, c, kept, dataStart));
        }

        var lastRow = firstRow + keepRows;
        for (var r = firstRow; r < lastRow; r++)
        {
            var header = hasHeader && r == firstRow;
            var row = new TableRow { IsHeader = header };
            for (var c = 0; c < cols; c++)
            {
                if (covered[r, c])
                {
                    continue;
                }

                var cell = ToCell(grid[r, c], header, settings.TrimEmpty);
                if (rowSpan[r, c] > 0)
                {
                    cell.RowSpan = Math.Min(rowSpan[r, c], lastRow - r);
                    cell.ColumnSpan = colSpan[r, c];
                }

                row.Cells.Add(cell);
            }

            table.Rows.Add(row);
        }

        return new XlsxSheetTable(table, string.IsNullOrEmpty(title) ? null : title);
    }

    private static bool Bounds(XlsxSheetData data, bool trim, out int rowStart, out int colStart, out int rowEnd, out int colEnd)
    {
        rowStart = colStart = rowEnd = colEnd = 0;
        if (data.Rows.Count == 0)
        {
            return false;
        }

        rowStart = data.Rows.Keys.First();
        rowEnd = data.Rows.Keys.Last();
        colStart = data.Rows.Values.Min(r => r.Keys.First());
        colEnd = data.Rows.Values.Max(r => r.Keys.Last());
        if (!trim)
        {
            rowStart = Math.Min(rowStart, data.Dimension?.Row1 ?? data.FirstPresentRow);
            colStart = Math.Min(colStart, data.Dimension?.Column1 ?? data.FirstPresentColumn);
            rowEnd = Math.Max(rowEnd, data.LastPresentRow);
            colEnd = Math.Max(colEnd, data.LastPresentColumn);
        }

        return true;
    }

    /// <summary>Header when every non-empty cell of the first row is bold, or every one has a solid fill.</summary>
    private static bool DetectHeader(XlsxCellData?[,] grid, int firstRow, int rows, int cols)
    {
        if (rows - firstRow < 2)
        {
            return false;
        }

        var cells = Enumerable.Range(0, cols).Select(c => grid[firstRow, c]).OfType<XlsxCellData>().ToList();
        return cells.Count > 0 && (cells.All(c => c.Bold) || cells.All(c => c.SolidFill));
    }

    private static ColumnAlignment ColumnAlignmentFor(XlsxCellData?[,] grid, int from, int to, int column, List<List<string>> texts, int dataStart)
    {
        ColumnAlignment? explicitAlignment = null;
        var consistent = true;
        for (var r = from; r < to; r++)
        {
            if (grid[r, column] is not { } cell)
            {
                continue;
            }

            if (explicitAlignment is null)
            {
                explicitAlignment = cell.Alignment;
            }
            else if (explicitAlignment != cell.Alignment)
            {
                consistent = false;
                break;
            }
        }

        if (consistent && explicitAlignment is { } a && a != ColumnAlignment.Default)
        {
            return a;
        }

        return CsvImporter.IsNumericColumn(texts, column, dataStart) ? ColumnAlignment.Right : ColumnAlignment.Default;
    }

    private static TableCell ToCell(XlsxCellData? data, bool header, bool trim)
    {
        var cell = new TableCell();
        if (data is null)
        {
            return cell;
        }

        foreach (var raw in data.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = trim ? raw.Trim() : raw;
            if (trim && line.Length == 0)
            {
                continue;
            }

            Inline inline = new TextInline(line);
            if (data.Bold && !header)
            {
                inline = new StyledInline(InlineStyle.Bold, inline);
            }

            if (data.Link is { } url)
            {
                var link = new LinkInline { Url = url };
                link.Inlines.Add(inline);
                inline = link;
            }

            var paragraph = new ParagraphBlock();
            paragraph.Inlines.Add(inline);
            cell.Blocks.Add(paragraph);
        }

        return cell;
    }
}
