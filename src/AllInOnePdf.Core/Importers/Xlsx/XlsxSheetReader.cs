using System.Globalization;
using AllInOnePdf.Core.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AllInOnePdf.Core.Importers.Xlsx;

/// <summary>One non-empty cell as displayed.</summary>
internal sealed class XlsxCellData
{
    public required string Text { get; init; }
    public bool Bold { get; init; }
    public bool SolidFill { get; init; }
    public ColumnAlignment Alignment { get; init; }
    public string? Link { get; set; }
}

internal readonly record struct XlsxRange(int Row1, int Column1, int Row2, int Column2);

/// <summary>What <see cref="XlsxSheetReader"/> collected from one worksheet.</summary>
internal sealed class XlsxSheetData
{
    /// <summary>Stored rows by 1-based row number; each maps 1-based column numbers to non-empty cells.</summary>
    public SortedDictionary<int, SortedDictionary<int, XlsxCellData>> Rows { get; } = [];

    public HashSet<int> HiddenRows { get; } = [];

    public List<(int Min, int Max)> HiddenColumns { get; } = [];

    public List<XlsxRange> Merges { get; } = [];

    /// <summary>The <c>dimension</c> element's range, when present and parseable.</summary>
    public XlsxRange? Dimension { get; set; }

    /// <summary>Last row/column that had a cell element (even an empty one).</summary>
    public int LastPresentRow { get; set; }

    public int LastPresentColumn { get; set; }

    public int FirstPresentRow { get; set; } = int.MaxValue;

    public int FirstPresentColumn { get; set; } = int.MaxValue;

    /// <summary>Visible rows from the first used row to the last row seen, including those past the cap that were only counted.</summary>
    public int CountedRows { get; set; }

    /// <summary>True when rows past the cap were counted but not stored.</summary>
    public bool Truncated { get; set; }

    public bool IsColumnHidden(int column) => HiddenColumns.Any(h => column >= h.Min && column <= h.Max);
}

/// <summary>
/// Streams a worksheet with <see cref="OpenXmlReader"/>: one row element is materialised at a time, so a large sheet
/// costs memory for the kept rows only. Stops storing rows once <c>maxRows + 1</c> visible rows are kept (one spare
/// for a header) and from then on only counts row elements.
/// </summary>
internal sealed class XlsxSheetReader(XlsxWorkbookInfo workbook, XlsxImportSettings settings)
{
    public XlsxSheetData Read(WorksheetPart part, CancellationToken ct)
    {
        var data = new XlsxSheetData();
        var limit = settings.MaxRows + 1L;
        var firstRow = 0;
        var hiddenSinceFirst = 0;
        var previousRow = 0;
        var rowsSeen = 0;

        using var reader = OpenXmlReader.Create(part);
        if (!reader.Read())
        {
            return data;
        }

        while (!reader.EOF)
        {
            if (!reader.IsStartElement)
            {
                if (!reader.Read())
                {
                    break;
                }

                continue;
            }

            var type = reader.ElementType;
            if (type == typeof(Row))
            {
                if (++rowsSeen % 512 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var (rowNumber, hidden) = RowAttributes(reader, previousRow);
                previousRow = rowNumber;
                var visible = !hidden || settings.IncludeHidden;
                if (!visible)
                {
                    data.HiddenRows.Add(rowNumber);
                    if (firstRow > 0)
                    {
                        hiddenSinceFirst++;
                    }

                    reader.Skip();
                    continue;
                }

                if (data.Truncated)
                {
                    data.CountedRows = Ordinal(rowNumber, firstRow, hiddenSinceFirst);
                    reader.Skip();
                    continue;
                }

                var row = (Row)reader.LoadCurrentElement()!;
                var cells = ReadCells(row, rowNumber, data);
                if (firstRow == 0 && (cells.Count > 0 || !settings.TrimEmpty))
                {
                    firstRow = !settings.TrimEmpty && data.Dimension is { } dim ? Math.Min(dim.Row1, rowNumber) : rowNumber;
                }

                if (firstRow == 0)
                {
                    continue;
                }

                var ordinal = Ordinal(rowNumber, firstRow, hiddenSinceFirst);
                if (ordinal > limit)
                {
                    data.Truncated = true;
                    data.CountedRows = ordinal;
                    continue;
                }

                if (cells.Count > 0)
                {
                    data.Rows[rowNumber] = cells;
                    data.CountedRows = ordinal;
                }
                else if (!settings.TrimEmpty)
                {
                    data.CountedRows = ordinal;
                }

                continue;
            }

            if (type == typeof(SheetDimension))
            {
                var dim = (SheetDimension)reader.LoadCurrentElement()!;
                if (XlsxCellReference.TryParseRange(dim.Reference?.Value, out var r1, out var c1, out var r2, out var c2) && r1 > 0 && c1 > 0)
                {
                    data.Dimension = new XlsxRange(r1, c1, r2, c2);
                }

                continue;
            }

            if (type == typeof(Columns))
            {
                var columns = (Columns)reader.LoadCurrentElement()!;
                if (!settings.IncludeHidden)
                {
                    foreach (var col in columns.Elements<Column>())
                    {
                        if (col.Hidden?.Value == true && col.Min?.Value is { } min)
                        {
                            data.HiddenColumns.Add(((int)min, (int)(col.Max?.Value ?? min)));
                        }
                    }
                }

                continue;
            }

            if (type == typeof(MergeCells))
            {
                var merges = (MergeCells)reader.LoadCurrentElement()!;
                foreach (var merge in merges.Elements<MergeCell>())
                {
                    if (XlsxCellReference.TryParseRange(merge.Reference?.Value, out var r1, out var c1, out var r2, out var c2) && r1 > 0 && c1 > 0)
                    {
                        data.Merges.Add(new XlsxRange(r1, c1, r2, c2));
                    }
                }

                continue;
            }

            if (type == typeof(Hyperlinks))
            {
                ReadHyperlinks((Hyperlinks)reader.LoadCurrentElement()!, part, data);
                continue;
            }

            if (!reader.Read())
            {
                break;
            }
        }

        return data;
    }

    private static int Ordinal(int rowNumber, int firstRow, int hiddenSinceFirst) =>
        rowNumber - firstRow + 1 - hiddenSinceFirst;

    private static (int Row, bool Hidden) RowAttributes(OpenXmlReader reader, int previousRow)
    {
        var row = previousRow + 1;
        var hidden = false;
        foreach (var attr in reader.Attributes)
        {
            if (attr.LocalName == "r" && int.TryParse(attr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) && r > 0)
            {
                row = r;
            }
            else if (attr.LocalName == "hidden")
            {
                hidden = attr.Value is "1" or "true";
            }
        }

        return (row, hidden);
    }

    private SortedDictionary<int, XlsxCellData> ReadCells(Row row, int rowNumber, XlsxSheetData data)
    {
        var cells = new SortedDictionary<int, XlsxCellData>();
        var previousColumn = 0;
        foreach (var cell in row.Elements<Cell>())
        {
            var column = XlsxCellReference.TryParse(cell.CellReference?.Value, out _, out var c) && c > 0 ? c : previousColumn + 1;
            previousColumn = column;
            if (!settings.IncludeHidden && data.IsColumnHidden(column))
            {
                continue;
            }

            data.LastPresentRow = Math.Max(data.LastPresentRow, rowNumber);
            data.LastPresentColumn = Math.Max(data.LastPresentColumn, column);
            data.FirstPresentRow = Math.Min(data.FirstPresentRow, rowNumber);
            data.FirstPresentColumn = Math.Min(data.FirstPresentColumn, column);

            var value = CellText(cell, out var richBold);
            if (settings.TrimEmpty)
            {
                value = value.Trim();
            }

            if (value.Length == 0)
            {
                continue;
            }

            var style = workbook.Style((int)(cell.StyleIndex?.Value ?? 0));
            cells[column] = new XlsxCellData
            {
                Text = value,
                Bold = style.Bold || richBold,
                SolidFill = style.SolidFill,
                Alignment = style.Alignment,
            };
        }

        return cells;
    }

    /// <summary>The cell's text as Excel displays it (or its formula, when formulas are shown).</summary>
    internal string CellText(Cell cell, out bool richBold)
    {
        richBold = false;
        var formula = cell.CellFormula?.Text;
        if (!settings.FormulasAsValues && !string.IsNullOrWhiteSpace(formula))
        {
            return "=" + formula;
        }

        var raw = cell.CellValue?.Text;
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString)
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                return string.Empty;
            }

            var rich = workbook.SharedString(index);
            richBold = rich.AllBold;
            return rich.Text;
        }

        if (type == CellValues.InlineString)
        {
            var rich = XlsxWorkbookInfo.ReadRichText(cell.InlineString);
            richBold = rich.AllBold;
            return rich.Text;
        }

        if (raw is null)
        {
            return string.Empty;
        }

        if (type == CellValues.Boolean)
        {
            return raw.Trim() is "1" or "true" ? "TRUE" : "FALSE";
        }

        if (type == CellValues.Error || type == CellValues.String)
        {
            return XlsxWorkbookInfo.Decode(raw);
        }

        var style = workbook.Style((int)(cell.StyleIndex?.Value ?? 0));
        if (type == CellValues.Date)
        {
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
            {
                var serial = ToSerial(date, workbook.Date1904);
                if (serial < 0)
                {
                    return raw;
                }

                return style.IsDate
                    ? XlsxNumberFormat.Format(serial, style.NumberFormatId, style.FormatCode, workbook.Date1904)
                    : XlsxNumberFormat.Format(serial, "yyyy-mm-dd", workbook.Date1904);
            }

            return raw;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? XlsxNumberFormat.Format(number, style.NumberFormatId, style.FormatCode, workbook.Date1904)
            : raw;
    }

    private static double ToSerial(DateTime date, bool date1904)
    {
        var origin = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);
        return (date - origin).TotalDays;
    }

    private static void ReadHyperlinks(Hyperlinks links, WorksheetPart part, XlsxSheetData data)
    {
        var relationships = part.HyperlinkRelationships.ToDictionary(r => r.Id, r => r.Uri, StringComparer.Ordinal);
        foreach (var link in links.Elements<Hyperlink>())
        {
            string? url = null;
            if (link.Id?.Value is { } id && relationships.TryGetValue(id, out var uri))
            {
                url = uri.OriginalString;
                if (!string.IsNullOrEmpty(link.Location?.Value))
                {
                    url += "#" + link.Location.Value;
                }
            }
            else if (!string.IsNullOrWhiteSpace(link.Location?.Value))
            {
                url = "#" + link.Location.Value;
            }

            if (url is null || !XlsxCellReference.TryParseRange(link.Reference?.Value, out var r1, out var c1, out var r2, out var c2))
            {
                continue;
            }

            for (var r = r1; r <= r2; r++)
            {
                if (!data.Rows.TryGetValue(r, out var row))
                {
                    continue;
                }

                for (var c = c1; c <= c2; c++)
                {
                    if (row.TryGetValue(c, out var cell))
                    {
                        cell.Link = url;
                    }
                }
            }
        }
    }
}
