using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Importers.Xlsx;
using DocumentFormat.OpenXml.Spreadsheet;
using static AllInOnePdf.Core.Tests.XlsxTestHelpers;

namespace AllInOnePdf.Core.Tests;

/// <summary>.xlsx → document: cell text as displayed, spans, headers, hidden content, sheet selection, limits, links.</summary>
public sealed class XlsxImportTests
{
    [Theory]
    [InlineData(1234.5, "General", "1234.5")]
    [InlineData(1.0 / 3, "General", "0.333333333")]
    [InlineData(123456789012.0, "General", "1.23457E+11")]
    [InlineData(-42.0, "General", "-42")]
    [InlineData(3.14159, "0.00", "3.14")]
    [InlineData(2.675, "0.00", "2.68")]
    [InlineData(1234567.891, "#,##0.00", "1,234,567.89")]
    [InlineData(-1234567.0, "#,##0", "-1,234,567")]
    [InlineData(0.125, "0%", "13%")]
    [InlineData(0.1234, "0.00%", "12.34%")]
    [InlineData(12345.0, "0.00E+00", "1.23E+04")]
    [InlineData(-1234.5, "#,##0.00;(#,##0.00)", "(1,234.50)")]
    [InlineData(0.0, "0.00;-0.00;\"zero\"", "zero")]
    [InlineData(1234.5, "\"$\"#,##0.00", "$1,234.50")]
    [InlineData(1000.0, "[$€-407]#,##0", "€1,000")]
    [InlineData(5.0, "0 \"items\"", "5 items")]
    [InlineData(0.5, "#.00", ".50")]
    [InlineData(1500000.0, "#,##0.0,,\"M\"", "1.5M")]
    [InlineData(45306.0, "dd/mm/yyyy", "15/01/2024")]
    [InlineData(45306.0, "yyyy-mm-dd", "2024-01-15")]
    [InlineData(45306.0, "d mmm yyyy", "15 Jan 2024")]
    [InlineData(45306.0, "dddd, mmmm d", "Monday, January 15")]
    [InlineData(0.75, "h:mm AM/PM", "6:00 PM")]
    [InlineData(0.5 + 1.0 / 86400 * 5, "hh:mm:ss", "12:00:05")]
    [InlineData(1.5, "[h]:mm", "36:00")]
    [InlineData(45306.5, "yyyy-mm-dd hh:mm", "2024-01-15 12:00")]
    [InlineData(1.0 / 86400 * 75.25, "mm:ss.00", "01:15.25")]
    [InlineData(1234.5, "[Red]0.0", "1234.5")]
    public void Number_format_renders_like_excel(double value, string code, string expected)
    {
        Assert.Equal(expected, XlsxNumberFormat.Format(value, code));
    }

    [Fact]
    public void Number_format_ids_and_date_detection()
    {
        Assert.Equal("2024-01-15", XlsxNumberFormat.Format(new DateTime(2024, 1, 15).ToOADate(), 14, null));
        Assert.Equal("1904-01-01", XlsxNumberFormat.Format(0, 14, null, date1904: true));
        Assert.Equal("1,235", XlsxNumberFormat.Format(1234.5, 3, null));
        Assert.Equal("1234.50", XlsxNumberFormat.Format(1234.5, 2, null));
        Assert.Equal("1900-01-01", XlsxNumberFormat.Format(1, 14, null));
        Assert.Equal("1900-03-01", XlsxNumberFormat.Format(61, 14, null));
        Assert.True(XlsxNumberFormat.IsDateFormat("dd/mm/yyyy"));
        Assert.True(XlsxNumberFormat.IsDateFormat("[h]:mm"));
        Assert.True(XlsxNumberFormat.IsDateFormat(14, null));
        Assert.False(XlsxNumberFormat.IsDateFormat("#,##0.00"));
        Assert.False(XlsxNumberFormat.IsDateFormat("\"days\" 0"));
        Assert.False(XlsxNumberFormat.IsDateFormat("General"));
        Assert.False(XlsxNumberFormat.IsDateFormat("[Red]0.00"));
        Assert.Equal("1234.5", XlsxNumberFormat.Format(1234.5, "[>100]0;0"));
        Assert.Equal("0.5", XlsxNumberFormat.Format(0.5, "# ?/?"));
    }

    [Fact]
    public async Task Shared_inline_and_rich_strings()
    {
        var bytes = new XlsxBuilder().Sheet("Data", s => s
            .Str("A1", "shared")
            .Inline("B1", "inline")
            .Rich("C1", ("All ", true), ("bold", true))
            .Rich("D1", ("Half ", true), ("plain", false))
            .Str("A2", "x_x000D_y")).Build();

        var doc = await ImportAsync(bytes, o => o.Set(XlsxImporter.OptHeaderRow, "no"));
        var table = Tables(doc).Single();
        Assert.Equal(["shared", "inline", "All bold", "Half plain"], Cells(table)[0]);
        Assert.Equal("x\ry", Cells(table)[1][0]);
        var bold = table.Rows[0].Cells[2].Blocks.OfType<ParagraphBlock>().Single().Inlines.Single();
        Assert.Equal(InlineStyle.Bold, Assert.IsType<StyledInline>(bold).Style);
        Assert.IsType<TextInline>(table.Rows[0].Cells[3].Blocks.OfType<ParagraphBlock>().Single().Inlines.Single());
    }

    [Fact]
    public async Task Numbers_render_through_their_formats()
    {
        var serial = new DateTime(2024, 1, 15).ToOADate();
        var bytes = new XlsxBuilder().Sheet("Data", s => s
            .Num("A1", 1234.5)
            .Num("B1", 3.14159, StyleFixed2)
            .Num("C1", 1234567, StyleThousands)
            .Num("D1", 0.25, StylePercent)
            .Num("E1", serial, StyleDate)
            .Num("F1", serial, StyleCustomDate)
            .Bool("A2", true)
            .Bool("B2", false)
            .Error("C2", "#DIV/0!")
            .Num("D2", 0.1 + 0.2)).Build();

        var doc = await ImportAsync(bytes, o => o.Set(XlsxImporter.OptHeaderRow, "no"));
        var cells = Cells(doc);
        Assert.Equal(["1234.5", "3.14", "1,234,567", "25%", "2024-01-15", "15/01/2024"], cells[0]);
        Assert.Equal(["TRUE", "FALSE", "#DIV/0!", "0.3", "", ""], cells[1]);
    }

    [Fact]
    public async Task Date1904_shifts_dates()
    {
        var bytes = new XlsxBuilder { Date1904 = true }.Sheet("Data", s => s.Num("A1", 0, StyleDate).Num("B1", 1, StyleCustomDate)).Build();
        var doc = await ImportAsync(bytes);
        Assert.Equal(["1904-01-01", "02/01/1904"], Cells(doc)[0]);
    }

    [Fact]
    public async Task Formulas_show_cached_values_or_formula_text()
    {
        var bytes = new XlsxBuilder().Sheet("Data", s => s
            .Row(1, "a", "b", "sum", "label", "shared")
            .Row(2, 20, 22)
            .Formula("C2", "SUM(A2:B2)", "42")
            .Formula("D2", "IF(C2>40,\"big\",\"small\")", "big", text: true)
            .Formula("E2", null, "7")).Build();

        var values = Cells(await ImportAsync(bytes));
        Assert.Equal(["20", "22", "42", "big", "7"], values[1]);

        var formulas = Cells(await ImportAsync(bytes, o => o.Set(XlsxImporter.OptFormulasAsValues, false)));
        Assert.Equal(["20", "22", "=SUM(A2:B2)", "=IF(C2>40,\"big\",\"small\")", "7"], formulas[1]);
    }

    [Fact]
    public async Task Merged_cells_become_spans_and_covered_cells_are_omitted()
    {
        var bytes = new XlsxBuilder().Sheet("Data", s => s
            .StyledRow(1, StyleBold, "Region", "Q1", "Q2")
            .Row(2, "North", "both quarters", null)
            .Row(3, "South", 5, 6)
            .Row(4, null, 7, 8)
            .Merge("B2:C2")
            .Merge("A3:A4")).Build();

        var table = Tables(await ImportAsync(bytes)).Single();
        Assert.True(table.Rows[0].IsHeader);
        Assert.Equal(3, table.ColumnCount);

        var merged = table.Rows[1].Cells;
        Assert.Equal(2, merged.Count);
        Assert.Equal(2, merged[1].ColumnSpan);
        Assert.Equal("both quarters", merged[1].ToPlainText());

        Assert.Equal(2, table.Rows[2].Cells[0].RowSpan);
        Assert.Equal(3, table.Rows[2].Cells.Count);
        Assert.Equal(["7", "8"], table.Rows[3].Cells.Select(c => c.ToPlainText()));
    }

    [Fact]
    public async Task Merged_banner_row_becomes_the_sheet_heading()
    {
        var bytes = new XlsxBuilder().Sheet("Data", s => s
            .Str("A1", "Quarterly sales", StyleBold)
            .Merge("A1:C1")
            .StyledRow(2, StyleBold, "Region", "Q1", "Q2")
            .Row(3, "North", 1, 2)).Build();

        var doc = await ImportAsync(bytes);
        Assert.Equal("Quarterly sales", doc.Blocks.OfType<HeadingBlock>().Single().ToPlainText());
        var table = Tables(doc).Single();
        Assert.Equal("Data", table.Caption);
        Assert.True(table.Rows[0].IsHeader);
        Assert.Equal(["Region", "Q1", "Q2"], Cells(table)[0]);
    }

    [Fact]
    public async Task Bold_or_filled_first_row_is_detected_as_header_unless_overridden()
    {
        // Duplicate text in the first row: the text heuristic alone would not call it a header.
        static byte[] Book(uint style) => new XlsxBuilder().Sheet("Data", s => s
            .StyledRow(1, style, "Name", "Name")
            .Row(2, "a", "b")
            .Row(3, "c", "d")).Build();

        Assert.True(Tables(await ImportAsync(Book(StyleBold))).Single().Rows[0].IsHeader);
        Assert.True(Tables(await ImportAsync(Book(StyleFilled))).Single().Rows[0].IsHeader);
        Assert.False(Tables(await ImportAsync(Book(0))).Single().Rows[0].IsHeader);
        Assert.False(Tables(await ImportAsync(Book(StyleBold), o => o.Set(XlsxImporter.OptHeaderRow, "no"))).Single().Rows[0].IsHeader);
        Assert.True(Tables(await ImportAsync(Book(0), o => o.Set(XlsxImporter.OptHeaderRow, "yes"))).Single().Rows[0].IsHeader);

        // Text above numbers is a header even without bold.
        var numeric = new XlsxBuilder().Sheet("Data", s => s.Row(1, "Item", "Price").Row(2, "Tea", 3.5).Row(3, "Cake", 4)).Build();
        var table = Tables(await ImportAsync(numeric)).Single();
        Assert.True(table.Rows[0].IsHeader);
        Assert.Equal(ColumnAlignment.Right, table.GetAlignment(1));
        Assert.Equal(ColumnAlignment.Default, table.GetAlignment(0));
    }

    [Fact]
    public async Task Explicit_cell_alignment_is_kept()
    {
        var bytes = new XlsxBuilder().Sheet("Data", s => s
            .StyledRow(1, StyleBold, "A", "B", "C")
            .Str("A2", "x", StyleCenter).Str("B2", "y", StyleRight).Str("C2", "z")
            .Str("A3", "x", StyleCenter).Str("B3", "y", StyleRight).Str("C3", "z")).Build();

        var table = Tables(await ImportAsync(bytes)).Single();
        Assert.Equal(ColumnAlignment.Center, table.GetAlignment(0));
        Assert.Equal(ColumnAlignment.Right, table.GetAlignment(1));
        Assert.Equal(ColumnAlignment.Default, table.GetAlignment(2));
    }

    [Fact]
    public async Task Hidden_sheets_rows_and_columns_are_skipped_unless_included()
    {
        var bytes = new XlsxBuilder()
            .Sheet("Visible", s => s
                .Row(1, "a", "secret", "c")
                .Row(2, "1", "2", "3")
                .Row(3, "hidden", "row", "here")
                .Row(4, "4", "5", "6")
                .HideRow(3)
                .HideColumns(2, 2))
            .Sheet("Hidden", s => s.Row(1, "x"), SheetStateValues.Hidden)
            .Sheet("VeryHidden", s => s.Row(1, "y"), SheetStateValues.VeryHidden)
            .Build();

        var doc = await ImportAsync(bytes, o => o.Set(XlsxImporter.OptHeaderRow, "no"));
        Assert.Equal([["a", "c"], ["1", "3"], ["4", "6"]], Cells(doc));
        Assert.Empty(doc.Blocks.OfType<HeadingBlock>());

        var all = await ImportAsync(bytes, o => o.Set(XlsxImporter.OptHeaderRow, "no").Set(XlsxImporter.OptIncludeHidden, true));
        var tables = Tables(all);
        Assert.Equal(3, tables.Count);
        Assert.Equal(["Visible", "Hidden", "VeryHidden"], all.Blocks.OfType<HeadingBlock>().Select(h => h.ToPlainText()));
        Assert.Equal(4, tables[0].Rows.Count);
        Assert.Equal(["a", "secret", "c"], Cells(tables[0])[0]);
        Assert.Equal(2, all.Blocks.OfType<PageBreakBlock>().Count());
    }

    [Fact]
    public async Task Sheets_are_selected_by_name_or_index_with_a_warning_for_unknown_names()
    {
        var bytes = new XlsxBuilder()
            .Sheet("First", s => s.Row(1, "one"))
            .Sheet("Second", s => s.Row(1, "two"))
            .Sheet("Third", s => s.Row(1, "three"))
            .Build();

        var doc = await ImportAsync(bytes, o => o.Set(XlsxImporter.OptSheets, "third, 1, Nope"));
        Assert.Equal(["Third", "First"], doc.Blocks.OfType<HeadingBlock>().Select(h => h.ToPlainText()));
        Assert.Equal("three", Tables(doc)[0].Rows[0].Cells[0].ToPlainText());
        Assert.All(Tables(doc), t => Assert.Null(t.Caption)); // the heading already carries the sheet name
        Assert.Contains(doc.ImportWarnings, w => w.Contains("\"Nope\"", StringComparison.Ordinal));

        var single = await ImportAsync(bytes, o => o.Set(XlsxImporter.OptSheets, "Second"));
        Assert.Empty(single.Blocks.OfType<HeadingBlock>());
        Assert.Equal("Second", Tables(single).Single().Caption);

        var always = await ImportAsync(bytes, o => o.Set(XlsxImporter.OptSheets, "2").Set(XlsxImporter.OptSheetHeadings, "always"));
        Assert.Equal("Second", always.Blocks.OfType<HeadingBlock>().Single().ToPlainText());

        var noBreaks = await ImportAsync(bytes, o => o.Set(XlsxImporter.OptPageBreaks, false));
        Assert.Empty(noBreaks.Blocks.OfType<PageBreakBlock>());
        Assert.Equal(3, Tables(noBreaks).Count);
    }

    [Fact]
    public async Task Rows_beyond_the_maximum_are_counted_and_reported()
    {
        var bytes = new XlsxBuilder().Sheet("Big", s =>
        {
            s.Row(1, "n", "square");
            for (var i = 1; i <= 10; i++)
            {
                s.Row(i + 1, i, i * i);
            }
        }).Build();

        var doc = await ImportAsync(bytes, o => o.Set(XlsxImporter.OptMaxRows, 3));
        var table = Tables(doc).Single();
        Assert.Equal(4, table.Rows.Count);
        Assert.True(table.Rows[0].IsHeader);
        Assert.Contains("Sheet \"Big\": only the first 3 of 10 rows were imported. Raise \"Maximum rows\" to include more.", doc.ImportWarnings);

        var full = await ImportAsync(bytes);
        Assert.Equal(11, Tables(full).Single().Rows.Count);
        Assert.Empty(full.ImportWarnings);
    }

    [Fact]
    public async Task Empty_rows_and_columns_around_the_data_are_trimmed()
    {
        var bytes = new XlsxBuilder().Sheet("Data", s =>
        {
            s.Dimension = "A1:E10";
            s.Str("B2", "  name  ").Str("C2", "value");
            s.Str("B3", "x").Num("C3", 1);
            s.Blank("E10", StyleBold);
            s.Str("D5", "   ");
        }).Build();

        var trimmed = await ImportAsync(bytes, o => o.Set(XlsxImporter.OptHeaderRow, "no"));
        Assert.Equal([["name", "value"], ["x", "1"]], Cells(trimmed));

        var raw = Tables(await ImportAsync(bytes, o => o.Set(XlsxImporter.OptHeaderRow, "no").Set(XlsxImporter.OptTrimEmpty, false))).Single();
        Assert.Equal(10, raw.Rows.Count);
        Assert.Equal(5, raw.ColumnCount);
        Assert.Equal("  name  ", raw.Rows[1].Cells[1].ToPlainText());
        Assert.Equal("   ", raw.Rows[4].Cells[3].ToPlainText());
    }

    [Fact]
    public async Task Hyperlinks_become_links()
    {
        var bytes = new XlsxBuilder().Sheet("Data", s => s
            .Row(1, "site", "jump")
            .Link("A1", "https://example.com/docs")
            .LinkTo("B1", "Other!A1")).Build();

        var table = Tables(await ImportAsync(bytes, o => o.Set(XlsxImporter.OptHeaderRow, "no"))).Single();
        var external = Assert.IsType<LinkInline>(table.Rows[0].Cells[0].Blocks.OfType<ParagraphBlock>().Single().Inlines.Single());
        Assert.Equal("https://example.com/docs", external.Url);
        Assert.Equal("site", Inline.ToPlainText(external.Inlines));
        var internalLink = Assert.IsType<LinkInline>(table.Rows[0].Cells[1].Blocks.OfType<ParagraphBlock>().Single().Inlines.Single());
        Assert.Equal("#Other!A1", internalLink.Url);
    }

    [Fact]
    public async Task Password_protected_and_non_zip_files_fail_with_a_friendly_message()
    {
        var encrypted = new byte[512];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(encrypted, 0);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(encrypted));
        Assert.Contains("password-protected", ex.Message, StringComparison.Ordinal);

        var text = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync("hello, world"u8.ToArray()));
        Assert.Contains("not an Excel workbook", text.Message, StringComparison.Ordinal);

        var zip = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync([(byte)'P', (byte)'K', 3, 4, 0, 0, 0, 0]));
        Assert.Contains("Excel workbook", zip.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metadata_title_comes_from_properties_or_the_file_name()
    {
        var titled = new XlsxBuilder { Title = "Budget 2026" }.Sheet("Data", s => s.Row(1, "x")).Build();
        var doc = await ImportAsync(titled);
        Assert.Equal("Budget 2026", doc.Metadata.Title);
        Assert.Equal("xlsx", doc.Metadata.SourceFormat);

        var untitled = new XlsxBuilder().Sheet("Data", s => s.Row(1, "x")).Build();
        Assert.Equal("report", (await ImportAsync(untitled, fileName: "report.xlsx")).Metadata.Title);
    }

    [Fact]
    public async Task Empty_workbook_gives_a_placeholder_and_a_warning()
    {
        var bytes = new XlsxBuilder().Sheet("Empty", _ => { }).Build();
        var doc = await ImportAsync(bytes);
        Assert.Equal("(empty workbook)", Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks)).ToPlainText());
        Assert.NotEmpty(doc.ImportWarnings);

        var excluded = new XlsxBuilder().Sheet("Data", s => s.Row(1, "x")).Build();
        var none = await ImportAsync(excluded, o => o.Set(XlsxImporter.OptSheets, "Missing"));
        Assert.Equal("(empty workbook)", Assert.IsType<ParagraphBlock>(Assert.Single(none.Blocks)).ToPlainText());
    }

    [Fact]
    public void Importer_is_registered_and_ready()
    {
        var importer = new XlsxImporter();
        Assert.Equal(Conversion.ConverterStatus.Ready, importer.Status);
        Assert.True(((Conversion.IImporter)importer).CanImport("Book1.XLSX"));
        Assert.All(importer.Options, o => Assert.Contains(o.Group, new[] { "Sheets", "Table", "Limits" }));
    }
}
