using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;

namespace AllInOnePdf.Core.Tests;

public sealed class SourceCodeImportTests
{
    private static async Task<Document> ImportAsync(string text, string fileName = "Program.cs", Action<OptionValues>? configure = null)
    {
        var importer = new SourceCodeImporter();
        var options = OptionValues.FromDefaults(importer.Options);
        configure?.Invoke(options);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await importer.ImportAsync(new ImportContext { Input = stream, FileName = fileName, Options = options });
    }

    [Theory]
    [InlineData("a.cs", "csharp")]
    [InlineData("a.js", "javascript")]
    [InlineData("a.mjs", "javascript")]
    [InlineData("a.ts", "typescript")]
    [InlineData("a.py", "python")]
    [InlineData("A.java", "java")]
    [InlineData("a.go", "go")]
    [InlineData("a.rs", "rust")]
    [InlineData("a.cpp", "cpp")]
    [InlineData("a.cc", "cpp")]
    [InlineData("a.h", "cpp")]
    [InlineData("a.hpp", "cpp")]
    [InlineData("a.c", "c")]
    [InlineData("a.css", "css")]
    [InlineData("a.scss", "scss")]
    [InlineData("a.xml", "xml")]
    [InlineData("a.yaml", "yaml")]
    [InlineData("a.YML", "yaml")]
    [InlineData("a.sh", "bash")]
    [InlineData("a.ps1", "powershell")]
    [InlineData("a.sql", "sql")]
    [InlineData("a.rb", "ruby")]
    [InlineData("a.php", "php")]
    [InlineData("a.kt", "kotlin")]
    [InlineData("a.swift", "swift")]
    [InlineData("a.toml", "toml")]
    [InlineData("a.ini", "ini")]
    [InlineData("a.diff", "diff")]
    [InlineData("a.patch", "diff")]
    public void Maps_extensions_to_languages(string fileName, string language)
    {
        Assert.Equal(language, SourceCodeImporter.LanguageFor(fileName));
        Assert.True(((IImporter)new SourceCodeImporter()).CanImport(fileName));
    }

    [Theory]
    [InlineData("data.json")]
    [InlineData("data.csv")]
    [InlineData("notes.txt")]
    [InlineData("README.md")]
    [InlineData("page.html")]
    [InlineData("noext")]
    public void Leaves_data_and_prose_formats_to_their_importers(string fileName) =>
        Assert.False(((IImporter)new SourceCodeImporter()).CanImport(fileName));

    [Fact]
    public void Every_known_source_extension_is_importable()
    {
        IImporter importer = new SourceCodeImporter();
        Assert.All(KnownFormats.SourceCode.Extensions, e => Assert.True(importer.CanImport("x" + e), e));
    }

    [Fact]
    public async Task Whole_file_becomes_one_captioned_code_block()
    {
        var doc = await ImportAsync("\uFEFFusing System;\r\n\r\nclass P\r\n{\r\n}\r\n");
        var code = Assert.IsType<CodeBlock>(Assert.Single(doc.Blocks));
        Assert.Equal("using System;\n\nclass P\n{\n}", code.Code);
        Assert.Equal("csharp", code.Language);
        Assert.Equal("Program.cs", code.Caption);
        Assert.Equal("Program.cs", doc.Metadata.Title);

        var bare = await ImportAsync("x = 1\n", "a.py", o => o.Set(SourceCodeImporter.OptShowFileName, false));
        Assert.Null(bare.Blocks.OfType<CodeBlock>().Single().Caption);
    }

    [Fact]
    public async Task Tabs_expand_to_the_next_tab_stop()
    {
        Assert.Equal("    x", SourceCodeImporter.ExpandTabs("\tx", 4));
        Assert.Equal("ab  x", SourceCodeImporter.ExpandTabs("ab\tx", 4));
        Assert.Equal("abcd    x", SourceCodeImporter.ExpandTabs("abcd\tx", 4));
        Assert.Equal("a x", SourceCodeImporter.ExpandTabs("a\tx", 2));

        var doc = await ImportAsync("if x:\n\treturn 1\n", "a.py", o => o.Set(SourceCodeImporter.OptTabWidth, 8));
        Assert.Equal("if x:\n        return 1", doc.Blocks.OfType<CodeBlock>().Single().Code);
    }

    [Fact]
    public async Task Long_files_are_truncated_or_split()
    {
        var text = string.Join('\n', Enumerable.Range(1, 1200).Select(i => $"line {i}"));

        var truncated = await ImportAsync(text, "a.sql", o => o.Set(SourceCodeImporter.OptMaxLines, 100));
        var code = truncated.Blocks.OfType<CodeBlock>().Single().Code;
        Assert.EndsWith("line 100", code);
        Assert.Contains("100 of 1,200", Assert.Single(truncated.ImportWarnings));

        var split = await ImportAsync(text, "a.sql", o => o.Set(SourceCodeImporter.OptSplitLongFiles, true));
        var blocks = split.Blocks.OfType<CodeBlock>().ToList();
        Assert.Equal(3, blocks.Count);
        Assert.StartsWith("line 501\n", blocks[1].Code);
        Assert.EndsWith("line 1200", blocks[2].Code);
        Assert.Equal("a.sql", blocks[0].Caption);
        Assert.Contains("501", blocks[1].Caption);
        Assert.All(blocks, b => Assert.Equal("sql", b.Language));
    }
}
