using System.Globalization;
using System.Text;

namespace AllInOnePdf.Core.Importers.Xlsx;

/// <summary>A1-style cell references: 1-based row and column numbers to and from "B12" and "A1:C4".</summary>
internal static class XlsxCellReference
{
    /// <summary>Largest column Excel supports (XFD).</summary>
    public const int MaxColumn = 16384;

    /// <summary>Largest row Excel supports.</summary>
    public const int MaxRow = 1_048_576;

    public static string ColumnName(int column)
    {
        var sb = new StringBuilder(3);
        while (column > 0)
        {
            var rem = (column - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            column = (column - 1) / 26;
        }

        return sb.ToString();
    }

    public static string Format(int row, int column) =>
        ColumnName(column) + row.ToString(CultureInfo.InvariantCulture);

    public static string Range(int row1, int column1, int row2, int column2) =>
        Format(row1, column1) + ":" + Format(row2, column2);

    /// <summary>Absolute form used in defined names: <c>$A$1:$C$4</c>.</summary>
    public static string AbsoluteRange(int row1, int column1, int row2, int column2) =>
        string.Create(CultureInfo.InvariantCulture, $"${ColumnName(column1)}${row1}:${ColumnName(column2)}${row2}");

    /// <summary>Parses "B12" (also "$B$12"); row or column may be missing ("B", "12").</summary>
    public static bool TryParse(string? reference, out int row, out int column)
    {
        row = 0;
        column = 0;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var i = 0;
        var s = reference.Trim();
        while (i < s.Length && s[i] == '$')
        {
            i++;
        }

        while (i < s.Length && char.IsAsciiLetter(s[i]))
        {
            column = column * 26 + (char.ToUpperInvariant(s[i]) - 'A' + 1);
            if (column > MaxColumn)
            {
                return false;
            }

            i++;
        }

        if (i < s.Length && s[i] == '$')
        {
            i++;
        }

        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            row = row * 10 + (s[i] - '0');
            if (row > MaxRow)
            {
                return false;
            }

            i++;
        }

        return i == s.Length && (row > 0 || column > 0);
    }

    /// <summary>Parses "A1:C4" or a single cell "B2" (treated as a one-cell range).</summary>
    public static bool TryParseRange(string? reference, out int row1, out int column1, out int row2, out int column2)
    {
        row1 = column1 = row2 = column2 = 0;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        // Hyperlink and merge refs are single ranges; a space-separated list keeps only the first.
        var first = reference.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var parts = first.Split(':');
        if (!TryParse(parts[0], out row1, out column1))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            row2 = row1;
            column2 = column1;
        }
        else if (!TryParse(parts[1], out row2, out column2))
        {
            return false;
        }

        if (row2 < row1)
        {
            (row1, row2) = (row2, row1);
        }

        if (column2 < column1)
        {
            (column1, column2) = (column2, column1);
        }

        return true;
    }
}
