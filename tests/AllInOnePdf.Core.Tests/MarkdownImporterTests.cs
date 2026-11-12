using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;

namespace AllInOnePdf.Core.Tests;

public class MarkdownImporterTests
{
    private static async Task<Document> ImportAsync(string markdown, OptionValues? options = null)
    {
        var importer = new MarkdownImporter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
        return await importer.ImportAsync(new ImportContext
        {
            Input = stream,
            FileName = "test.md",
            Options = (options ?? OptionValues.Empty).WithDefaults(importer.Options),
        });
    }

    [Fact]
    public async Task Parses_headings_paragraphs_and_inline_styles()
    {
        var doc = await ImportAsync("# Title\n\nSome **bold** and *italic* text with `code`.\n");

        Assert.Equal(2, doc.Blocks.Count);
        var h = Assert.IsType<HeadingBlock>(doc.Blocks[0]);
        Assert.Equal(1, h.Level);
        Assert.Equal("Title", h.ToPlainText());
        Assert.Equal("Title", doc.Metadata.Title);

        var p = Assert.IsType<ParagraphBlock>(doc.Blocks[1]);
        Assert.Contains(p.Inlines, i => i is StyledInline { Style: InlineStyle.Bold });
        Assert.Contains(p.Inlines, i => i is StyledInline { Style: InlineStyle.Italic });
        Assert.Contains(p.Inlines, i => i is CodeInline { Code: "code" });
    }

    [Fact]
    public async Task Parses_front_matter_into_metadata()
    {
        var doc = await ImportAsync("---\ntitle: Hello\nauthor: Ada, Grace\ndate: 2026-01-02\n---\n\nBody\n");

        Assert.Equal("Hello", doc.Metadata.Title);
        Assert.Equal(new[] { "Ada", "Grace" }, doc.Metadata.Authors);
        Assert.Equal(2026, doc.Metadata.Date?.Year);
        Assert.Single(doc.Blocks);
    }

    [Fact]
    public async Task Parses_task_lists_and_nested_lists()
    {
        var doc = await ImportAsync("- [x] done\n- [ ] todo\n  - nested\n");

        var list = Assert.IsType<ListBlock>(Assert.Single(doc.Blocks));
        Assert.Equal(2, list.Items.Count);
        Assert.True(list.Items[0].Checked);
        Assert.False(list.Items[1].Checked);
        Assert.Equal("done", ((ParagraphBlock)list.Items[0].Blocks[0]).ToPlainText());
        Assert.Contains(list.Items[1].Blocks, b => b is ListBlock);
    }

    [Fact]
    public async Task Parses_pipe_tables_with_alignment()
    {
        var doc = await ImportAsync("| a | b |\n|:--|--:|\n| 1 | 2 |\n");

        var table = Assert.IsType<TableBlock>(Assert.Single(doc.Blocks));
        Assert.Equal(2, table.Rows.Count);
        Assert.True(table.Rows[0].IsHeader);
        Assert.Equal(ColumnAlignment.Left, table.GetAlignment(0));
        Assert.Equal(ColumnAlignment.Right, table.GetAlignment(1));
        Assert.Equal("2", table.Rows[1].Cells[1].ToPlainText());
    }

    [Fact]
    public async Task Converts_github_callouts()
    {
        var doc = await ImportAsync("> [!WARNING]\n> Careful now.\n");

        var callout = Assert.IsType<CalloutBlock>(Assert.Single(doc.Blocks));
        Assert.Equal(CalloutKind.Warning, callout.Kind);
        Assert.Equal("Careful now.", ((ParagraphBlock)callout.Blocks[0]).ToPlainText());
    }

    [Fact]
    public async Task Image_only_paragraph_becomes_image_block()
    {
        var doc = await ImportAsync("![alt text](pic.png \"A title\")\n");

        var img = Assert.IsType<ImageBlock>(Assert.Single(doc.Blocks));
        Assert.Equal("pic.png", img.Source);
        Assert.Equal("alt text", img.AltText);
        Assert.Equal("A title", img.Title);
    }

    [Fact]
    public async Task Fenced_code_keeps_language()
    {
        var doc = await ImportAsync("```csharp\nvar x = 1;\n```\n");

        var code = Assert.IsType<CodeBlock>(Assert.Single(doc.Blocks));
        Assert.Equal("csharp", code.Language);
        Assert.Equal("var x = 1;", code.Code.TrimEnd());
    }

    [Fact]
    public async Task Welcome_sample_parses_every_block_type()
    {
        var doc = await SampleDocuments.CreateWelcomeAsync();
        var types = doc.EnumerateAllBlocks().Select(b => b.GetType()).Distinct().ToList();

        Assert.Contains(typeof(HeadingBlock), types);
        Assert.Contains(typeof(ListBlock), types);
        Assert.Contains(typeof(CodeBlock), types);
        Assert.Contains(typeof(TableBlock), types);
        Assert.Contains(typeof(QuoteBlock), types);
        Assert.Contains(typeof(CalloutBlock), types);
        Assert.Contains(typeof(ThematicBreakBlock), types);
        Assert.Equal("Designing a Theme", doc.Metadata.Title);
        Assert.True(doc.CountWords() > 100);
    }
}
