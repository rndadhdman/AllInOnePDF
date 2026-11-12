using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Document = AllInOnePdf.Core.Documents.Document;

namespace AllInOnePdf.Core.Tests;

/// <summary>Builds test workbooks with the Open XML SDK and wraps the Excel importer/exporter.</summary>
internal static class XlsxTestHelpers
{
    // Cell style indexes of the stylesheet every test workbook gets.
    public const uint StyleBold = 1;
    public const uint StyleFixed2 = 2;
    public const uint StyleThousands = 3;
    public const uint StylePercent = 4;
    public const uint StyleDate = 5;
    public const uint StyleCustomDate = 6;
    public const uint StyleFilled = 7;
    public const uint StyleCenter = 8;
    public const uint StyleRight = 9;

    public static async Task<Document> ImportAsync(byte[] xlsx, Action<OptionValues>? configure = null, string fileName = "book.xlsx")
    {
        var importer = new XlsxImporter();
        var options = OptionValues.FromDefaults(importer.Options);
        configure?.Invoke(options);
        using var stream = new MemoryStream(xlsx);
        return await importer.ImportAsync(new ImportContext { Input = stream, FileName = fileName, Options = options });
    }

    public static async Task<byte[]> ExportAsync(Document document, PdfTheme? theme = null, params (string Key, object Value)[] options)
    {
        var exporter = new XlsxExporter();
        var values = new OptionValues();
        foreach (var (key, value) in options)
        {
            values.Set(key, value);
        }

        var result = await exporter.ExportAsync(new ExportContext
        {
            Document = document,
            BaseName = "doc",
            Theme = theme,
            Options = values.WithDefaults(exporter.Options),
        });
        Assert.Equal("doc.xlsx", result.Primary!.FileName);
        Assert.Equal(KnownFormats.Xlsx.MediaType, result.Primary.MediaType);
        return result.Primary.Content;
    }

    public static List<string> Validate(byte[] xlsx)
    {
        using var stream = new MemoryStream(xlsx);
        using var package = SpreadsheetDocument.Open(stream, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Office2019);
        return validator.Validate(package)
            .Select(e => $"{e.ErrorType} {e.Part?.Uri} {e.Path?.XPath}: {e.Description}")
            .ToList();
    }

    public static List<TableBlock> Tables(Document doc) => doc.Blocks.OfType<TableBlock>().ToList();

    public static List<List<string>> Cells(TableBlock table) =>
        table.Rows.Select(r => r.Cells.Select(c => c.ToPlainText()).ToList()).ToList();

    public static List<List<string>> Cells(Document doc) => Cells(Tables(doc).Single());

    public static TableBlock Table(bool header, params string[][] rows)
    {
        var table = new TableBlock();
        for (var r = 0; r < rows.Length; r++)
        {
            var row = new TableRow { IsHeader = header && r == 0 };
            foreach (var value in rows[r])
            {
                row.Cells.Add(value.Length == 0 ? new TableCell() : new TableCell(value));
            }

            table.Rows.Add(row);
        }

        return table;
    }
}

/// <summary>Fluent builder for a test workbook.</summary>
internal sealed class XlsxBuilder
{
    private readonly List<(string Name, SheetStateValues? State, SheetBuilder Sheet)> _sheets = [];
    private readonly List<SharedStringItem> _strings = [];

    public bool Date1904 { get; set; }

    public string? Title { get; set; }

    public XlsxBuilder Sheet(string name, Action<SheetBuilder> build, SheetStateValues? state = null)
    {
        var sheet = new SheetBuilder(this);
        build(sheet);
        _sheets.Add((name, state, sheet));
        return this;
    }

    internal int AddString(SharedStringItem item)
    {
        _strings.Add(item);
        return _strings.Count - 1;
    }

    public byte[] Build()
    {
        using var stream = new MemoryStream();
        using (var package = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = package.AddWorkbookPart();
            workbookPart.AddNewPart<WorkbookStylesPart>().Stylesheet = Stylesheet();
            if (_strings.Count > 0)
            {
                workbookPart.AddNewPart<SharedStringTablePart>().SharedStringTable =
                    new SharedStringTable(_strings.Select(s => (SharedStringItem)s.CloneNode(true))) { Count = (uint)_strings.Count, UniqueCount = (uint)_strings.Count };
            }

            var sheets = new Sheets();
            uint id = 1;
            foreach (var (name, state, sheet) in _sheets)
            {
                var part = workbookPart.AddNewPart<WorksheetPart>();
                part.Worksheet = sheet.Build(part);
                var element = new Sheet { Name = name, SheetId = id++, Id = workbookPart.GetIdOfPart(part) };
                if (state is not null)
                {
                    element.State = state;
                }

                sheets.Append(element);
            }

            var workbook = new Workbook();
            if (Date1904)
            {
                workbook.Append(new WorkbookProperties { Date1904 = true });
            }

            workbook.Append(sheets);
            workbookPart.Workbook = workbook;
            if (Title is not null)
            {
                package.PackageProperties.Title = Title;
            }
        }

        return stream.ToArray();
    }

    private static Stylesheet Stylesheet()
    {
        var fonts = new DocumentFormat.OpenXml.Spreadsheet.Fonts(
            new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
            new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" }));
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFDDEEFF" }) { PatternType = PatternValues.Solid }));
        var borders = new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder()));
        var numFmts = new NumberingFormats(new NumberingFormat { NumberFormatId = 164, FormatCode = "dd/mm/yyyy" });
        var xfs = new CellFormats(
            new CellFormat(),
            new CellFormat { FontId = 1, ApplyFont = true },
            new CellFormat { NumberFormatId = 2, ApplyNumberFormat = true },
            new CellFormat { NumberFormatId = 3, ApplyNumberFormat = true },
            new CellFormat { NumberFormatId = 9, ApplyNumberFormat = true },
            new CellFormat { NumberFormatId = 14, ApplyNumberFormat = true },
            new CellFormat { NumberFormatId = 164, ApplyNumberFormat = true },
            new CellFormat { FillId = 2, ApplyFill = true },
            new CellFormat(new Alignment { Horizontal = HorizontalAlignmentValues.Center }) { ApplyAlignment = true },
            new CellFormat(new Alignment { Horizontal = HorizontalAlignmentValues.Right }) { ApplyAlignment = true });
        return new Stylesheet(numFmts, fonts, fills, borders, xfs);
    }
}

/// <summary>Cells, merges, hidden rows/columns and hyperlinks of one test worksheet.</summary>
internal sealed class SheetBuilder(XlsxBuilder book)
{
    private readonly SortedDictionary<int, SortedDictionary<int, Cell>> _cells = [];
    private readonly HashSet<int> _hiddenRows = [];
    private readonly List<(uint Min, uint Max)> _hiddenColumns = [];
    private readonly List<string> _merges = [];
    private readonly List<(string Ref, string? Url, string? Location)> _links = [];

    public string? Dimension { get; set; }

    public SheetBuilder Str(string reference, string text, uint style = 0) =>
        Add(reference, new Cell
        {
            DataType = CellValues.SharedString,
            CellValue = new CellValue(book.AddString(new SharedStringItem(new Text(text) { Space = SpaceProcessingModeValues.Preserve })).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        }, style);

    public SheetBuilder Inline(string reference, string text, uint style = 0) =>
        Add(reference, new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new Text(text)) }, style);

    public SheetBuilder Rich(string reference, params (string Text, bool Bold)[] runs)
    {
        var item = new SharedStringItem();
        foreach (var (text, bold) in runs)
        {
            var run = new Run();
            if (bold)
            {
                run.Append(new RunProperties(new Bold()));
            }

            run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            item.Append(run);
        }

        return Add(reference, new Cell { DataType = CellValues.SharedString, CellValue = new CellValue(book.AddString(item).ToString(System.Globalization.CultureInfo.InvariantCulture)) }, 0);
    }

    public SheetBuilder Num(string reference, double value, uint style = 0) =>
        Add(reference, new Cell { CellValue = new CellValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture)) }, style);

    public SheetBuilder Bool(string reference, bool value) =>
        Add(reference, new Cell { DataType = CellValues.Boolean, CellValue = new CellValue(value ? "1" : "0") }, 0);

    public SheetBuilder Error(string reference, string error) =>
        Add(reference, new Cell { DataType = CellValues.Error, CellValue = new CellValue(error) }, 0);

    public SheetBuilder Formula(string reference, string? formula, string cached, bool text = false)
    {
        var cell = new Cell { CellValue = new CellValue(cached) };
        if (text)
        {
            cell.DataType = CellValues.String;
        }

        cell.CellFormula = formula is null ? new CellFormula { FormulaType = CellFormulaValues.Shared, SharedIndex = 0 } : new CellFormula(formula);
        // CellFormula must precede CellValue.
        var value = cell.CellValue;
        value.Remove();
        cell.Append(value);
        return Add(reference, cell, 0);
    }

    /// <summary>An empty cell element (formatting only), which still counts for the used range.</summary>
    public SheetBuilder Blank(string reference, uint style = 0) => Add(reference, new Cell(), style);

    /// <summary>Writes a row from column A: strings become shared strings, numbers numbers, bools booleans, null skips a column.</summary>
    public SheetBuilder Row(int row, params object?[] values) => StyledRow(row, 0, values);

    public SheetBuilder StyledRow(int row, uint style, params object?[] values)
    {
        for (var c = 0; c < values.Length; c++)
        {
            var reference = Importers.Xlsx.XlsxCellReference.Format(row, c + 1);
            switch (values[c])
            {
                case null:
                    break;
                case string s:
                    Str(reference, s, style);
                    break;
                case bool b:
                    Bool(reference, b);
                    break;
                case IConvertible n:
                    Num(reference, n.ToDouble(System.Globalization.CultureInfo.InvariantCulture), style);
                    break;
            }
        }

        return this;
    }

    public SheetBuilder Merge(string range)
    {
        _merges.Add(range);
        return this;
    }

    public SheetBuilder HideRow(int row)
    {
        _hiddenRows.Add(row);
        return this;
    }

    public SheetBuilder HideColumns(uint min, uint max)
    {
        _hiddenColumns.Add((min, max));
        return this;
    }

    public SheetBuilder Link(string reference, string url)
    {
        _links.Add((reference, url, null));
        return this;
    }

    public SheetBuilder LinkTo(string reference, string location)
    {
        _links.Add((reference, null, location));
        return this;
    }

    private SheetBuilder Add(string reference, Cell cell, uint style)
    {
        Assert.True(Importers.Xlsx.XlsxCellReference.TryParse(reference, out var row, out var column));
        cell.CellReference = reference;
        if (style != 0)
        {
            cell.StyleIndex = style;
        }

        if (!_cells.TryGetValue(row, out var cells))
        {
            _cells[row] = cells = [];
        }

        cells[column] = cell;
        return this;
    }

    internal Worksheet Build(WorksheetPart part)
    {
        var worksheet = new Worksheet();
        if (Dimension is not null)
        {
            worksheet.Append(new SheetDimension { Reference = Dimension });
        }

        if (_hiddenColumns.Count > 0)
        {
            var columns = new Columns();
            foreach (var (min, max) in _hiddenColumns)
            {
                columns.Append(new Column { Min = min, Max = max, Width = 9, Hidden = true, CustomWidth = true });
            }

            worksheet.Append(columns);
        }

        var data = new SheetData();
        foreach (var r in _cells.Keys.Union(_hiddenRows).Order())
        {
            var row = new Row { RowIndex = (uint)r };
            if (_hiddenRows.Contains(r))
            {
                row.Hidden = true;
            }

            if (_cells.TryGetValue(r, out var cells))
            {
                foreach (var cell in cells.Values)
                {
                    row.Append(cell);
                }
            }

            data.Append(row);
        }

        worksheet.Append(data);
        if (_merges.Count > 0)
        {
            worksheet.Append(new MergeCells(_merges.Select(m => new MergeCell { Reference = m })));
        }

        if (_links.Count > 0)
        {
            var links = new Hyperlinks();
            foreach (var (reference, url, location) in _links)
            {
                var link = new Hyperlink { Reference = reference };
                if (url is not null)
                {
                    link.Id = part.AddHyperlinkRelationship(new Uri(url, UriKind.Absolute), true).Id;
                }
                else
                {
                    link.Location = location;
                }

                links.Append(link);
            }

            worksheet.Append(links);
        }

        return worksheet;
    }
}
