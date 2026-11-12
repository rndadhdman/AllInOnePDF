using System.Text;
using AllInOnePdf.Core.Documents;
using static AllInOnePdf.Core.Exporters.Markdown.MarkdownEscaper;

namespace AllInOnePdf.Core.Exporters.Markdown;

/// <summary>GFM pipe tables. Cells are already flattened to one line by the caller.</summary>
internal static class MarkdownTableWriter
{
    public static string? Write(TableBlock table, Func<TableCell, string> renderCell, bool align)
    {
        var columns = table.ColumnCount;
        if (columns == 0)
        {
            return null;
        }

        var rows = new List<string[]>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var cells = new string[columns];
            Array.Fill(cells, string.Empty);
            var c = 0;
            foreach (var cell in row.Cells)
            {
                if (c >= columns)
                {
                    break;
                }

                cells[c] = renderCell(cell);
                c += Math.Max(1, cell.ColumnSpan);
            }

            rows.Add(cells);
        }

        // GFM requires a header row; synthesise an empty one when the source has none.
        var hasHeader = table.Rows[0].IsHeader;
        var header = hasHeader ? rows[0] : Enumerable.Repeat(string.Empty, columns).ToArray();
        var body = hasHeader ? rows.Skip(1).ToList() : rows;

        var widths = new int[columns];
        for (var i = 0; i < columns; i++)
        {
            widths[i] = align ? Math.Max(3, rows.Max(r => DisplayWidth(r[i]))) : 3;
        }

        var sb = new StringBuilder();
        AppendRow(sb, header, widths, table, align);
        sb.Append('\n').Append('|');
        for (var i = 0; i < columns; i++)
        {
            var w = widths[i];
            sb.Append(' ').Append(table.GetAlignment(i) switch
            {
                ColumnAlignment.Left => ":" + new string('-', w - 1),
                ColumnAlignment.Center => ":" + new string('-', w - 2) + ":",
                ColumnAlignment.Right => new string('-', w - 1) + ":",
                _ => new string('-', w),
            }).Append(" |");
        }

        foreach (var row in body)
        {
            sb.Append('\n');
            AppendRow(sb, row, widths, table, align);
        }

        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string[] cells, int[] widths, TableBlock table, bool align)
    {
        sb.Append('|');
        for (var i = 0; i < cells.Length; i++)
        {
            var text = cells[i];
            sb.Append(' ');
            if (align)
            {
                var gap = widths[i] - DisplayWidth(text);
                var left = table.GetAlignment(i) switch
                {
                    ColumnAlignment.Right => gap,
                    ColumnAlignment.Center => gap / 2,
                    _ => 0,
                };
                sb.Append(' ', left).Append(text).Append(' ', gap - left);
            }
            else
            {
                sb.Append(text);
            }

            sb.Append(" |");
        }
    }
}
