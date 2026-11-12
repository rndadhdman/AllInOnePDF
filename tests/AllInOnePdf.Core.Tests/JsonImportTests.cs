using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;

namespace AllInOnePdf.Core.Tests;

public sealed class JsonImportTests
{
    private static async Task<Document> ImportAsync(string text, Action<OptionValues>? configure = null)
    {
        var importer = new JsonImporter();
        var options = OptionValues.FromDefaults(importer.Options);
        configure?.Invoke(options);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await importer.ImportAsync(new ImportContext { Input = stream, FileName = "config.json", Options = options });
    }

    [Fact]
    public async Task Pretty_prints_with_two_or_four_spaces()
    {
        var doc = await ImportAsync("{\"b\":1,\"a\":[true,null,1.50],\"name\":\"Zoë <ok>\"}");
        var code = Assert.IsType<CodeBlock>(Assert.Single(doc.Blocks));
        Assert.Equal("json", code.Language);
        Assert.Equal("{\n  \"b\": 1,\n  \"a\": [\n    true,\n    null,\n    1.50\n  ],\n  \"name\": \"Zoë <ok>\"\n}", code.Code.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal("config", doc.Metadata.Title);
        Assert.Empty(doc.ImportWarnings);

        var four = await ImportAsync("{\"a\":{\"b\":1}}", o => o.Set(JsonImporter.OptIndent, "4"));
        Assert.Contains("\n        \"b\": 1", four.Blocks.OfType<CodeBlock>().Single().Code.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sorts_keys_recursively_when_asked()
    {
        var doc = await ImportAsync("{\"b\":{\"z\":1,\"y\":2},\"a\":[{\"d\":1,\"c\":2}]}", o => o.Set(JsonImporter.OptSortKeys, true));
        var code = doc.Blocks.OfType<CodeBlock>().Single().Code;
        Assert.True(code.IndexOf("\"a\"", StringComparison.Ordinal) < code.IndexOf("\"b\"", StringComparison.Ordinal));
        Assert.True(code.IndexOf("\"y\"", StringComparison.Ordinal) < code.IndexOf("\"z\"", StringComparison.Ordinal));
        Assert.True(code.IndexOf("\"c\"", StringComparison.Ordinal) < code.IndexOf("\"d\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Comments_and_trailing_commas_are_tolerated()
    {
        var doc = await ImportAsync("{\n  // note\n  \"a\": 1,\n}");
        Assert.Empty(doc.ImportWarnings);
        Assert.DoesNotContain("note", doc.Blocks.OfType<CodeBlock>().Single().Code);
    }

    [Fact]
    public async Task Invalid_json_is_kept_raw_with_a_positioned_warning()
    {
        var raw = "{\n  \"a\": 1,\n  \"b\" 2\n}";
        var doc = await ImportAsync(raw);
        var code = doc.Blocks.OfType<CodeBlock>().Single();
        Assert.Equal(raw, code.Code);
        Assert.Equal("json", code.Language);
        var warning = Assert.Single(doc.ImportWarnings);
        Assert.Contains("not valid JSON at line 3", warning);
        Assert.DoesNotContain("LineNumber:", warning);
    }

    [Fact]
    public async Task Depth_and_size_limits()
    {
        var deep = new string('[', 300) + new string(']', 300);
        var doc = await ImportAsync(deep);
        Assert.Single(doc.ImportWarnings);

        var big = "[" + string.Join(',', Enumerable.Repeat("\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\"", 40_000)) + "]";
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(big, o => o.Set(JsonImporter.OptMaxMb, 1)));
        Assert.Contains("1 MB", ex.Message);
    }
}
