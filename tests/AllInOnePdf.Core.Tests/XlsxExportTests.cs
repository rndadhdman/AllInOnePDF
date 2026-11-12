using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.DependencyInjection;
using static AllInOnePdf.Core.Tests.XlsxTestHelpers;
using Document = AllInOnePdf.Core.Documents.Document;
using TableCell = AllInOnePdf.Core.Documents.TableCell;
using TableRow = AllInOnePdf.Core.Documents.TableRow;

namespace AllInOnePdf.Core.Tests;

/// <summary>Document → .xlsx: valid workbook, theme-styled headers, typed values, sheet names, panes, filters, widths, metadata.</summary>
public sealed class XlsxExportTests
{
    public static TheoryData<string> ThemeIds()
    {
        var data = new TheoryData<string>();
        foreach (var theme in BuiltInThemes.All())
        {
            data.Add(theme.Id);
        }

        return data;
    }

    internal static Document SalesDocument()
    {
        var table = Table(true,
            ["Region", "Units", "Price", "Share", "Since", "Active"],
            ["North", "1,234", "12.50", "25%", "2024-01-15", "TRUE"],
            ["South", "987", "3.25", "75%", "2023-06-30", "FALSE"],
            ["West", "007", "1,234.5", "-4", "soon", ""]);
        table.Alignments.AddRange([ColumnAlignment.Default, ColumnAlignment.Right, ColumnAlignment.Right, ColumnAlignment.Center]);
        var doc = MarkdownExportTestHelpers.Doc(new HeadingBlock(1, "Sales"), table);
        doc.Metadata.Title = "Sales report";
        doc.Metadata.Authors.Add("Ada Lovelace");
        doc.Metadata.Keywords.AddRange(["sales", "2024"]);
        doc.Metadata.Subtitle = "Quarterly";
        doc.Metadata.Description = "Numbers by region";
        return doc;
    }

    internal static Document SpanDocument()
    {
        var table = new TableBlock { Caption = "Spans" };
        var header = new TableRow { IsHeader = true };
        header.Cells.Add(new TableCell("Region"));
        header.Cells.Add(new TableCell("Q1"));
        header.Cells.Add(new TableCell("Q2"));
        table.Rows.Add(header);

        var both = new TableRow();
        both.Cells.Add(new TableCell("North"));
        both.Cells.Add(new TableCell("both quarters") { ColumnSpan = 2 });
        table.Rows.Add(both);

        var tall = new TableRow();
        tall.Cells.Add(new TableCell("South") { RowSpan = 2 });
        tall.Cells.Add(new TableCell("5"));
        tall.Cells.Add(new TableCell("6"));
        table.Rows.Add(tall);

        var under = new TableRow();
        under.Cells.Add(new TableCell("7"));
        under.Cells.Add(new TableCell("8"));
        table.Rows.Add(under);
        return MarkdownExportTestHelpers.Doc(table);
    }

    private static Cell CellAt(WorksheetPart part, string reference) =>
        part.Worksheet!.Descendants<Cell>().Single(c => c.CellReference?.Value == reference);

    private static string Text(Cell cell) => cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? string.Empty;

    private static CellFormat Format(SpreadsheetDocument package, Cell cell) =>
        package.WorkbookPart!.WorkbookStylesPart!.Stylesheet!.CellFormats!.Elements<CellFormat>().ElementAt((int)(cell.StyleIndex?.Value ?? 0));

    private static (SpreadsheetDocument Package, List<WorksheetPart> Sheets, List<string> Names) Open(byte[] bytes)
    {
        var package = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        var wb = package.WorkbookPart!;
        var sheets = wb.Workbook!.Sheets!.Elements<Sheet>().ToList();
        return (package, sheets.Select(s => (WorksheetPart)wb.GetPartById(s.Id!.Value!)).ToList(), sheets.Select(s => s.Name!.Value!).ToList());
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public async Task Table_document_exports_to_a_valid_workbook(string themeId)
    {
        var bytes = await ExportAsync(SalesDocument(), BuiltInThemes.Find(themeId), (XlsxExporter.OptAutoFilter, true));
        Assert.Empty(Validate(bytes));
    }

    [Fact]
    public async Task Spans_and_table_less_documents_are_valid()
    {
        var spans = await ExportAsync(SpanDocument(), BuiltInThemes.Technical());
        Assert.Empty(Validate(spans));
        var (package, sheets, names) = Open(spans);
        using (package)
        {
            Assert.Equal(["Spans"], names);
            var merges = sheets[0].Worksheet!.Descendants<MergeCell>().Select(m => m.Reference!.Value).ToList();
            Assert.Equal(["B2:C2", "A3:A4"], merges);
            Assert.Equal(3, sheets[0].Worksheet!.Descendants<Row>().ElementAt(3).Elements<Cell>().Count());
        }

        var prose = await LoadSampleForProseAsync();
        var bytes = await ExportAsync(prose, BuiltInThemes.Night());
        Assert.Empty(Validate(bytes));
        var (p2, s2, n2) = Open(bytes);
        using (p2)
        {
            Assert.Equal(["Document"], n2);
            var texts = s2[0].Worksheet!.Descendants<Cell>().Select(Text).ToList();
            Assert.Equal("Title", texts[0]);
            Assert.Contains("• first", texts);
            Assert.Contains("2. second", texts);
            Assert.Contains("var x = 1;", texts);
            Assert.Contains("A quote", texts);
            Assert.Equal(100, s2[0].Worksheet!.Descendants<Column>().Single().Width!.Value);
            var heading = Format(p2, CellAt(s2[0], "A1"));
            var font = p2.WorkbookPart!.WorkbookStylesPart!.Stylesheet!.Fonts!.Elements<Font>().ElementAt((int)heading.FontId!.Value);
            Assert.NotNull(font.Bold);
        }
    }

    private static Task<Document> LoadSampleForProseAsync()
    {
        var list = new ListBlock();
        list.Items.Add(new Documents.ListItem("first"));
        var ordered = new ListBlock { Ordered = true, Start = 2 };
        ordered.Items.Add(new Documents.ListItem("second"));
        var quote = new QuoteBlock();
        quote.Blocks.Add(new ParagraphBlock("A quote"));
        return Task.FromResult(MarkdownExportTestHelpers.Doc(
            new HeadingBlock(1, "Title"),
            new ParagraphBlock("Body text"),
            list,
            ordered,
            new CodeBlock { Code = "var x = 1;\nvar y = 2;" },
            quote,
            new ThematicBreakBlock()));
    }

    [Fact]
    public async Task Header_row_is_styled_from_the_theme()
    {
        var theme = BuiltInThemes.Night();
        var bytes = await ExportAsync(SalesDocument(), theme);
        var (package, sheets, _) = Open(bytes);
        using (package)
        {
            var stylesheet = package.WorkbookPart!.WorkbookStylesPart!.Stylesheet!;
            // Row 1 is the title row from the heading; the header follows.
            Assert.Equal("Sales", Text(CellAt(sheets[0], "A1")));
            var header = Format(package, CellAt(sheets[0], "A2"));
            var font = stylesheet.Fonts!.Elements<Font>().ElementAt((int)header.FontId!.Value);
            Assert.NotNull(font.Bold);
            Assert.Equal("Inter", font.GetFirstChild<FontName>()!.Val!.Value);
            Assert.Equal(10.5, font.GetFirstChild<FontSize>()!.Val!.Value);
            var fill = stylesheet.Fills!.Elements<Fill>().ElementAt((int)header.FillId!.Value).PatternFill!;
            Assert.Equal(PatternValues.Solid, fill.PatternType!.Value);
            Assert.Equal("FF242830", fill.ForegroundColor!.Rgb!.Value);

            // Striped theme: the second data row has the stripe colour.
            var stripe = Format(package, CellAt(sheets[0], "A4"));
            Assert.Equal("FF1F2329", stylesheet.Fills!.Elements<Fill>().ElementAt((int)stripe.FillId!.Value).PatternFill!.ForegroundColor!.Rgb!.Value);
        }

        var grid = await ExportAsync(SalesDocument(), BuiltInThemes.Technical());
        var (p2, s2, _) = Open(grid);
        using (p2)
        {
            var data = Format(p2, CellAt(s2[0], "B3"));
            var border = p2.WorkbookPart!.WorkbookStylesPart!.Stylesheet!.Borders!.Elements<Border>().ElementAt((int)data.BorderId!.Value);
            Assert.Equal(BorderStyleValues.Thin, border.LeftBorder!.Style!.Value);
            Assert.Equal(BorderStyleValues.Thin, border.TopBorder!.Style!.Value);
            Assert.Equal(HorizontalAlignmentValues.Right, data.Alignment!.Horizontal!.Value);
        }
    }

    [Fact]
    public async Task Numbers_booleans_dates_and_percentages_are_typed()
    {
        var bytes = await ExportAsync(SalesDocument());
        var (package, sheets, _) = Open(bytes);
        using (package)
        {
            var s = sheets[0];
            var units = CellAt(s, "B3");
            Assert.Null(units.DataType);
            Assert.Equal("1234", units.CellValue!.Text);
            Assert.Equal(3u, Format(package, units).NumberFormatId!.Value);

            Assert.Equal("12.5", CellAt(s, "C3").CellValue!.Text);
            Assert.Equal(2u, Format(package, CellAt(s, "C3")).NumberFormatId!.Value);

            var share = CellAt(s, "D3");
            Assert.Equal("0.25", share.CellValue!.Text);
            Assert.Equal(9u, Format(package, share).NumberFormatId!.Value);

            var since = CellAt(s, "E3");
            Assert.Equal("45306", since.CellValue!.Text);
            Assert.Equal(14u, Format(package, since).NumberFormatId!.Value);

            Assert.Equal(CellValues.Boolean, CellAt(s, "F3").DataType!.Value);
            Assert.Equal("1", CellAt(s, "F3").CellValue!.Text);
            Assert.Equal("0", CellAt(s, "F4").CellValue!.Text);

            Assert.Equal(CellValues.InlineString, CellAt(s, "B5").DataType!.Value);
            Assert.Equal("007", Text(CellAt(s, "B5")));
            Assert.Equal("1234.5", CellAt(s, "C5").CellValue!.Text);
            Assert.Equal(4u, Format(package, CellAt(s, "C5")).NumberFormatId!.Value);
            Assert.Equal("-4", CellAt(s, "D5").CellValue!.Text);
            Assert.Equal(CellValues.InlineString, CellAt(s, "E5").DataType!.Value);

            // Header cells stay text.
            Assert.Equal(CellValues.InlineString, CellAt(s, "B2").DataType!.Value);
        }

        var text = await ExportAsync(SalesDocument(), null, (XlsxExporter.OptNumbersAsValues, false));
        var (p2, s2, _) = Open(text);
        using (p2)
        {
            Assert.All(s2[0].Worksheet!.Descendants<Cell>().Where(c => c.CellValue is not null || c.InlineString is not null),
                c => Assert.Equal(CellValues.InlineString, c.DataType!.Value));
        }
    }

    [Fact]
    public void Plain_text_values_parse_only_when_they_display_the_same()
    {
        static (Exporters.Xlsx.XlsxValueKind Kind, double Number) P(string s)
        {
            var v = Exporters.Xlsx.XlsxCellValue.Parse(s);
            return (v.Kind, v.Number);
        }

        Assert.Equal((Exporters.Xlsx.XlsxValueKind.Number, 1234.5), P("1234.5"));
        Assert.Equal((Exporters.Xlsx.XlsxValueKind.Number, 1234.5), P("1,234.5"));
        Assert.Equal(0.12, P("12%").Number, 10);
        Assert.Equal((Exporters.Xlsx.XlsxValueKind.Number, 9.99), P("$9.99"));
        Assert.Equal(Exporters.Xlsx.XlsxValueKind.Number, P("3.10").Kind);
        Assert.Equal(Exporters.Xlsx.XlsxValueKind.Boolean, P("TRUE").Kind);
        Assert.Equal(Exporters.Xlsx.XlsxValueKind.Text, P("true").Kind);
        Assert.Equal(Exporters.Xlsx.XlsxValueKind.Text, P("0042").Kind);
        Assert.Equal(Exporters.Xlsx.XlsxValueKind.Text, P("12345678901234567").Kind);
        Assert.Equal(Exporters.Xlsx.XlsxValueKind.Text, P("1.2.3").Kind);
        Assert.Equal(Exporters.Xlsx.XlsxValueKind.Text, P("(5)").Kind);
        Assert.Equal(Exporters.Xlsx.XlsxValueKind.Text, P("2024-02-30").Kind);
        Assert.Equal(Exporters.Xlsx.XlsxValueKind.Text, P("1e5").Kind);
    }

    [Fact]
    public async Task Sheet_names_are_sanitised_and_unique()
    {
        var doc = MarkdownExportTestHelpers.Doc(
            new HeadingBlock(2, "Sales: Q1/Q2"),
            Table(true, ["a", "b"], ["1", "2"]),
            Table(true, ["a", "b"], ["3", "4"]),
            new TableBlock { Caption = "A very long caption that is far beyond thirty-one characters", Rows = { Row("x") } },
            new TableBlock { Caption = "[*?]", Rows = { Row("y") } },
            new TableBlock { Caption = "History", Rows = { Row("z") } });
        var bytes = await ExportAsync(doc);
        Assert.Empty(Validate(bytes));
        var (package, sheets, names) = Open(bytes);
        using (package)
        {
            Assert.Equal(["Sales Q1 Q2", "Sales Q1 Q2 (2)", "A very long caption that is far", "Table 4", "History (2)"], names);
            Assert.All(names, n => Assert.InRange(n.Length, 1, 31));

            // Only the first table after a heading gets the title row.
            Assert.Equal("Sales: Q1/Q2", Text(CellAt(sheets[0], "A1")));
            Assert.Equal("a", Text(CellAt(sheets[1], "A1")));
        }

        static TableRow Row(string text)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(text));
            return row;
        }
    }

    [Fact]
    public async Task Freeze_pane_and_auto_filter_follow_the_options()
    {
        var bytes = await ExportAsync(SalesDocument(), null, (XlsxExporter.OptAutoFilter, true));
        var (package, sheets, names) = Open(bytes);
        using (package)
        {
            var pane = sheets[0].Worksheet!.Descendants<Pane>().Single();
            Assert.Equal(PaneStateValues.Frozen, pane.State!.Value);
            Assert.Equal(2, pane.VerticalSplit!.Value);
            Assert.Equal("A3", pane.TopLeftCell!.Value);

            Assert.Equal("A2:F5", sheets[0].Worksheet!.Descendants<AutoFilter>().Single().Reference!.Value);
            var name = package.WorkbookPart!.Workbook!.DefinedNames!.Elements<DefinedName>().Single();
            Assert.Equal("_xlnm._FilterDatabase", name.Name!.Value);
            Assert.Equal("'Sales'!$A$2:$F$5", name.Text);
            Assert.Equal(["Sales"], names);
        }

        var plain = await ExportAsync(SalesDocument(), null, (XlsxExporter.OptFreezeHeader, false), (XlsxExporter.OptIncludeHeadings, false));
        var (p2, s2, _) = Open(plain);
        using (p2)
        {
            Assert.Empty(s2[0].Worksheet!.Descendants<Pane>());
            Assert.Empty(s2[0].Worksheet!.Descendants<AutoFilter>());
            Assert.Null(p2.WorkbookPart!.Workbook!.DefinedNames);
            Assert.Equal("Region", Text(CellAt(s2[0], "A1")));
        }
    }

    [Fact]
    public async Task Column_widths_fit_the_content_within_bounds()
    {
        var doc = MarkdownExportTestHelpers.Doc(Table(true, ["id", "description"], ["1", new string('x', 200)], ["2", "short"]));
        var bytes = await ExportAsync(doc);
        var (package, sheets, _) = Open(bytes);
        using (package)
        {
            var widths = sheets[0].Worksheet!.Descendants<Column>().Select(c => c.Width!.Value).ToList();
            Assert.Equal(2, widths.Count);
            Assert.All(widths, w => Assert.InRange(w, 8, 60));
            Assert.Equal(8, widths[0]);
            Assert.Equal(60, widths[1]);
            Assert.All(sheets[0].Worksheet!.Descendants<Column>(), c => Assert.True(c.CustomWidth!.Value));
        }
    }

    [Fact]
    public async Task Metadata_is_written_to_the_package_properties()
    {
        var bytes = await ExportAsync(SalesDocument());
        using var package = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        var props = package.PackageProperties;
        Assert.Equal("Sales report", props.Title);
        Assert.Equal("Ada Lovelace", props.Creator);
        Assert.Equal("Quarterly", props.Subject);
        Assert.Equal("sales, 2024", props.Keywords);
        Assert.Equal("Numbers by region", props.Description);

        var none = await ExportAsync(SalesDocument(), null, (XlsxExporter.OptMetadata, false));
        using var bare = SpreadsheetDocument.Open(new MemoryStream(none), false);
        Assert.Null(bare.PackageProperties.Title);
    }

    [Fact]
    public void Exporter_and_importer_are_registered()
    {
        using var provider = new ServiceCollection().AddAllInOnePdfCore(Path.Combine(Path.GetTempPath(), "aiop-xlsx-tests")).BuildServiceProvider();
        Assert.Contains(provider.GetServices<IImporter>(), i => i is XlsxImporter);
        var exporter = Assert.Single(provider.GetServices<IExporter>(), e => e is XlsxExporter);
        Assert.True(exporter.SupportsTheme);
        Assert.Contains(exporter.Options, o => o.Key == PdfExporter.OptTheme && o.Type == OptionType.Theme);
    }

    // ---- Round trips -----------------------------------------------------------------------------------------

    internal static Document RoundTripDocument()
    {
        var table = new TableBlock { Caption = "Inventory" };
        table.Alignments.AddRange([ColumnAlignment.Default, ColumnAlignment.Right, ColumnAlignment.Center, ColumnAlignment.Default]);
        var header = new TableRow { IsHeader = true };
        foreach (var h in new[] { "Item", "Count", "Status", "Notes" })
        {
            header.Cells.Add(new TableCell(h));
        }

        table.Rows.Add(header);

        var r1 = new TableRow();
        r1.Cells.Add(new TableCell("Tea"));
        r1.Cells.Add(new TableCell("1,200"));
        r1.Cells.Add(new TableCell("TRUE"));
        var notes = new TableCell();
        notes.Blocks.Add(new ParagraphBlock("Line one"));
        notes.Blocks.Add(new ParagraphBlock("Line two"));
        r1.Cells.Add(notes);
        table.Rows.Add(r1);

        var r2 = new TableRow();
        var bold = new TableCell();
        bold.Blocks.Add(MarkdownExportTestHelpers.Para(new StyledInline(InlineStyle.Bold, "Cake")));
        r2.Cells.Add(bold);
        r2.Cells.Add(new TableCell("12.50"));
        r2.Cells.Add(new TableCell("pending") { RowSpan = 2 });
        var link = new LinkInline { Url = "https://example.com/cake" };
        link.Inlines.Add(new TextInline("recipe"));
        var linkCell = new TableCell();
        linkCell.Blocks.Add(MarkdownExportTestHelpers.Para(link));
        r2.Cells.Add(linkCell);
        table.Rows.Add(r2);

        var r3 = new TableRow();
        r3.Cells.Add(new TableCell("Scones"));
        r3.Cells.Add(new TableCell("25%"));
        r3.Cells.Add(new TableCell("2024-01-15"));
        table.Rows.Add(r3);

        var r4 = new TableRow();
        r4.Cells.Add(new TableCell("Total across items") { ColumnSpan = 3 });
        r4.Cells.Add(new TableCell());
        table.Rows.Add(r4);

        var doc = MarkdownExportTestHelpers.Doc(new HeadingBlock(1, "Stock"), table);
        doc.Metadata.Title = "Stock list";
        return doc;
    }

    [Fact]
    public async Task Table_document_round_trips_through_xlsx()
    {
        var source = RoundTripDocument();
        var expected = MarkdownExportTestHelpers.Dump(source);
        var bytes = await ExportAsync(source, BuiltInThemes.Clean());
        Assert.Empty(Validate(bytes));
        var back = await ImportAsync(bytes);

        Assert.Equal(expected, MarkdownExportTestHelpers.Dump(back));
        Assert.Equal("Inventory", Tables(back).Single().Caption);
    }

    [Fact]
    public async Task Imported_workbook_round_trips_through_every_theme()
    {
        var first = await ImportAsync(new XlsxBuilder()
            .Sheet("Sales", s => s.StyledRow(1, StyleBold, "Region", "Units").Row(2, "North", 10).Row(3, "South", 20))
            .Sheet("Costs", s => s.StyledRow(1, StyleBold, "Item", "Cost").Row(2, "Rent", 1000.5).Row(3, "Power", 99))
            .Build());
        var expected = MarkdownExportTestHelpers.Dump(first);
        foreach (var theme in BuiltInThemes.All())
        {
            var back = await ImportAsync(await ExportAsync(first, theme));
            Assert.Equal(expected, MarkdownExportTestHelpers.Dump(back));
        }
    }

    [Fact]
    public async Task Csv_round_trips_through_xlsx()
    {
        const string csv = "Region,Units,Price,Share,Opened\nNorth,1200,3.50,12%,2021-03-01\nSouth,980,4.25,7.5%,2019-11-20\nEast,15,10,80%,n/a\n";
        var importer = new CsvImporter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var fromCsv = await importer.ImportAsync(new ImportContext { Input = stream, FileName = "sales.csv", Options = OptionValues.FromDefaults(importer.Options) });

        var bytes = await ExportAsync(fromCsv);
        Assert.Empty(Validate(bytes));
        var (package, sheets, names) = Open(bytes);
        using (package)
        {
            Assert.Equal(["sales"], names);
            Assert.Equal("1200", CellAt(sheets[0], "B2").CellValue!.Text);
        }

        var back = await ImportAsync(bytes);
        var table = Tables(back).Single();
        Assert.Equal(Cells(Tables(fromCsv).Single()), Cells(table));
        Assert.True(table.Rows[0].IsHeader);
        Assert.Equal(ColumnAlignment.Right, table.GetAlignment(1));
    }
}
