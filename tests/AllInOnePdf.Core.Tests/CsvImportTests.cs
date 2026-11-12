using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Importers.Data;

namespace AllInOnePdf.Core.Tests;

public sealed class CsvImportTests
{
    private static async Task<Document> ImportAsync(byte[] data, Action<OptionValues>? configure = null, string fileName = "sales.csv")
    {
        var importer = new CsvImporter();
        var options = OptionValues.FromDefaults(importer.Options);
        configure?.Invoke(options);
        using var stream = new MemoryStream(data);
        return await importer.ImportAsync(new ImportContext { Input = stream, FileName = fileName, Options = options });
    }

    private static Task<Document> ImportAsync(string text, Action<OptionValues>? configure = null) =>
        ImportAsync(Encoding.UTF8.GetBytes(text), configure);

    private static List<List<string>> Cells(Document doc) =>
        doc.Blocks.OfType<TableBlock>().Single().Rows.Select(r => r.Cells.Select(c => c.ToPlainText()).ToList()).ToList();

    [Fact]
    public void Parser_handles_quotes_doubled_quotes_and_embedded_newlines()
    {
        var rows = CsvParser.Parse("a,\"b,c\",\"say \"\"hi\"\"\"\r\n\"multi\nline\",,x\r\nlast,row", ',').ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(["a", "b,c", "say \"hi\""], rows[0]);
        Assert.Equal(["multi\nline", "", "x"], rows[1]);
        Assert.Equal(["last", "row"], rows[2]);

        Assert.Equal([["1", "2"], ["3", "4"]], CsvParser.Parse("1,2\r3,4\n", ','));
        Assert.Equal([["", ""]], CsvParser.Parse(",", ','));
        Assert.Empty(CsvParser.Parse("", ','));
    }

    [Theory]
    [InlineData("a,b,c\n1,2,3\n4,5,6\n", ',')]
    [InlineData("a;b;c\n1,5;2,5;3\n4;5;6\n", ';')]
    [InlineData("a\tb\tc\n1\t2\t3\n", '\t')]
    [InlineData("a|b|c\n1|2|3\n", '|')]
    [InlineData("\"x;y\",b\n1,2\n", ',')]
    [InlineData("single column\nvalue\n", ',')]
    public void Delimiter_is_detected(string text, char expected) =>
        Assert.Equal(expected, CsvParser.DetectDelimiter(text));

    [Fact]
    public async Task Imports_a_table_with_header_and_right_aligned_numbers()
    {
        var doc = await ImportAsync("Name, Amount ,Note\nApples,1.50,fresh\nPears, 2 ,\nPlums,1000,ripe\n");

        var table = doc.Blocks.OfType<TableBlock>().Single();
        Assert.True(table.Rows[0].IsHeader);
        Assert.False(table.Rows[1].IsHeader);
        Assert.Equal(["Name", "Amount", "Note"], Cells(doc)[0]);
        Assert.Equal(["Pears", "2", ""], Cells(doc)[2]);
        Assert.Equal([ColumnAlignment.Default, ColumnAlignment.Right, ColumnAlignment.Default], table.Alignments);
        Assert.Equal("sales", doc.Metadata.Title);
        Assert.Equal("sales", table.Caption);
        Assert.Equal("csv", doc.Metadata.SourceFormat);
    }

    [Fact]
    public async Task Header_detection_and_overrides()
    {
        Assert.False((await ImportAsync("1,2\n3,4\n")).Blocks.OfType<TableBlock>().Single().Rows[0].IsHeader);
        Assert.True((await ImportAsync("city,country\nParis,France\nRome,Italy\n")).Blocks.OfType<TableBlock>().Single().Rows[0].IsHeader);
        Assert.False((await ImportAsync("x,x\nParis,France\n")).Blocks.OfType<TableBlock>().Single().Rows[0].IsHeader);
        Assert.False((await ImportAsync("a,b\n1,2\n", o => o.Set(CsvImporter.OptHasHeader, "no"))).Blocks.OfType<TableBlock>().Single().Rows[0].IsHeader);
        Assert.True((await ImportAsync("1,2\n3,4\n", o => o.Set(CsvImporter.OptHasHeader, "yes"))).Blocks.OfType<TableBlock>().Single().Rows[0].IsHeader);
    }

    [Fact]
    public async Task Explicit_delimiter_trim_off_and_ragged_rows()
    {
        var doc = await ImportAsync("a; b\n c ;d;e\n", o => o.Set(CsvImporter.OptDelimiter, "semicolon").Set(CsvImporter.OptTrim, false).Set(CsvImporter.OptHasHeader, "no"));
        var cells = Cells(doc);
        Assert.Equal(["a", " b", ""], cells[0]);
        Assert.Equal([" c ", "d", "e"], cells[1]);
    }

    [Fact]
    public async Task Numeric_alignment_needs_eighty_percent()
    {
        var mostly = await ImportAsync("v\n1\n2\n3\n4\nn/a\n");
        Assert.Equal(ColumnAlignment.Right, mostly.Blocks.OfType<TableBlock>().Single().Alignments[0]);

        var half = await ImportAsync("v\n1\n2\nx\ny\n", o => o.Set(CsvImporter.OptHasHeader, "yes"));
        Assert.Equal(ColumnAlignment.Default, half.Blocks.OfType<TableBlock>().Single().Alignments[0]);

        Assert.True(CsvParser.IsNumeric("-1,234.5"));
        Assert.True(CsvParser.IsNumeric("12 %"));
        Assert.True(CsvParser.IsNumeric("1e3"));
        Assert.False(CsvParser.IsNumeric("12abc"));
        Assert.False(CsvParser.IsNumeric(""));
    }

    [Fact]
    public async Task Max_rows_truncates_with_a_warning()
    {
        var sb = new StringBuilder("id,name\n");
        for (var i = 1; i <= 30; i++)
        {
            sb.Append(i).Append(",row ").Append(i).Append('\n');
        }

        var doc = await ImportAsync(sb.ToString(), o => o.Set(CsvImporter.OptMaxRows, 10));
        var table = doc.Blocks.OfType<TableBlock>().Single();
        Assert.Equal(11, table.Rows.Count);
        Assert.Equal("10", table.Rows[^1].Cells[0].ToPlainText());
        var warning = Assert.Single(doc.ImportWarnings);
        Assert.Contains("10 of 30", warning);
    }

    [Fact]
    public async Task Utf16_bom_utf8_bom_and_latin1_fallback()
    {
        var text = "Stadt;Größe\nKöln;405\nMünchen;310\n";
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
        var doc = await ImportAsync(utf16);
        Assert.Equal(["Stadt", "Größe"], Cells(doc)[0]);
        Assert.Equal(ColumnAlignment.Right, doc.Blocks.OfType<TableBlock>().Single().Alignments[1]);

        var utf8Bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        Assert.Equal("Stadt", Cells(await ImportAsync(utf8Bom))[0][0]);

        var latin1 = await ImportAsync(Encoding.Latin1.GetBytes(text));
        Assert.Equal("Köln", Cells(latin1)[1][0]);
        Assert.Contains(latin1.ImportWarnings, w => w.Contains("Latin-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_file_and_title_override()
    {
        var empty = await ImportAsync("\n\n");
        Assert.Empty(empty.Blocks.OfType<TableBlock>());
        Assert.NotEmpty(empty.ImportWarnings);

        var titled = await ImportAsync("a,b\n1,2\n", o => o.Set(CsvImporter.OptTitle, "Quarterly"));
        Assert.Equal("Quarterly", titled.Metadata.Title);
    }
}
