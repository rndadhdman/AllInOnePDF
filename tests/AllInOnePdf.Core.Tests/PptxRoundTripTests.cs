using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Importers;
using static AllInOnePdf.Core.Tests.PptxTestHelpers;

namespace AllInOnePdf.Core.Tests;

/// <summary>Document → .pptx → document: headings, paragraphs, lists, code, tables and pictures survive.</summary>
public class PptxRoundTripTests
{
    /// <summary>The structural dump without metadata and slide separators (page breaks, and the dividers the importer puts between slides).</summary>
    private static string Structure(Document doc) => string.Join('\n', MarkdownExportTestHelpers.Dump(doc)
        .Split('\n')
        .SkipWhile(l => !l.StartsWith("description=", StringComparison.Ordinal))
        .Skip(1)
        .Where(l => l != nameof(PageBreakBlock) && l != nameof(ThematicBreakBlock)));

    private static async Task<Document> RoundTripAsync(Document doc, params (string Key, object Value)[] options)
    {
        var bytes = await ExportAsync(doc, null, [(PptxExporter.OptTitleSlide, "no"), .. options]);
        Assert.Empty(Validate(bytes));
        return await ImportAsync(bytes);
    }

    [Fact]
    public async Task Markdown_headings_paragraphs_lists_and_code_round_trip()
    {
        const string markdown = """
            # Introduction

            Hello **bold** and *italic* world with a [link](https://example.com/).

            - one
            - two
              - nested
            - three

            1. first
            2. second

            # Code

            ```
            var x = 1;
              return x;
            ```

            Closing words.
            """;
        var original = await MarkdownExportTestHelpers.ImportAsync(markdown);
        var back = await RoundTripAsync(original);

        Assert.Equal(Structure(original), Structure(back));
        Assert.Equal("Introduction", back.Metadata.Title);
    }

    [Fact]
    public async Task Sample_document_keeps_headings_and_list_text()
    {
        var original = await DocxTestHelpers.LoadSampleAsync("sample-document.md");
        var back = await RoundTripAsync(original, (PptxExporter.OptSplitOn, "h2"));

        // Slide titles and the headings inside slides come back as headings at their levels; "(cont.)" slides fold
        // into the slide they continue.
        // (Split at H2, every slide title comes back at the title level; the H3s inside the slides one below it.)
        var expected = original.Blocks.OfType<HeadingBlock>().Select(h => Collapse(h.ToPlainText())).ToList();
        Assert.Equal(expected, HeadingTexts(back));
        Assert.All(back.Headings.Where(h => h.ToPlainText().StartsWith("1.", StringComparison.Ordinal) && h.ToPlainText()[2] != ' '), h => Assert.Equal(2, h.Level));
        Assert.DoesNotContain(back.Headings, h => h.ToPlainText().EndsWith("(cont.)", StringComparison.Ordinal));

        var backItems = back.EnumerateAllBlocks().OfType<ListBlock>()
            .SelectMany(l => l.Items)
            .Select(i => i.Blocks.OfType<InlineContainerBlock>().FirstOrDefault()?.ToPlainText())
            .Where(t => t is not null)
            .Select(t => Collapse(t!))
            .ToHashSet();
        var originalItems = original.Blocks.OfType<ListBlock>()
            .SelectMany(l => l.Items)
            .Select(i => i.Blocks.OfType<InlineContainerBlock>().FirstOrDefault()?.ToPlainText())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => Collapse(t!))
            .ToList();
        Assert.NotEmpty(originalItems);
        Assert.All(originalItems, t => Assert.Contains(t, backItems));

        var paragraphs = original.Blocks.OfType<ParagraphBlock>().Select(p => Collapse(p.ToPlainText())).Where(t => t.Length > 0).ToList();
        var backParagraphs = back.EnumerateAllBlocks().OfType<ParagraphBlock>().Select(p => Collapse(p.ToPlainText())).ToHashSet();
        Assert.All(paragraphs, t => Assert.Contains(t, backParagraphs));
    }

    [Fact]
    public async Task Tables_round_trip_cell_text_and_spans()
    {
        var table = new TableBlock();
        var header = new TableRow { IsHeader = true };
        header.Cells.AddRange([new TableCell("Name"), new TableCell("Qty"), new TableCell("Note")]);
        table.Rows.Add(header);
        var r1 = new TableRow();
        r1.Cells.AddRange([new TableCell("Both") { ColumnSpan = 2 }, new TableCell("Tall") { RowSpan = 2 }]);
        table.Rows.Add(r1);
        var r2 = new TableRow();
        r2.Cells.AddRange([new TableCell("a"), new TableCell("b")]);
        table.Rows.Add(r2);
        var original = MarkdownExportTestHelpers.Doc(new HeadingBlock(1, "Data"), table);

        var back = await RoundTripAsync(original);
        Assert.Equal(Structure(original), Structure(back));
        var t = back.Blocks.OfType<TableBlock>().Single();
        Assert.Equal(2, t.Rows[1].Cells[0].ColumnSpan);
        Assert.Equal(2, t.Rows[1].Cells[1].RowSpan);
    }

    [Fact]
    public async Task Picture_resource_survives()
    {
        var original = new Document();
        var png = DocxTestHelpers.TinyPng(20, 10);
        var resource = original.AddResource("image/png", png, "pic.png");
        original.Blocks.Add(new HeadingBlock(1, "Photo"));
        original.Blocks.Add(new ImageBlock { Source = resource.Reference, AltText = "Red block", ShowCaption = false, WidthPercent = 40 });

        var back = await RoundTripAsync(original);
        var image = back.Blocks.OfType<ImageBlock>().Single();
        Assert.Equal("Red block", image.AltText);
        Assert.Equal(png, back.FindResource(image.Source)!.Data);
        Assert.Equal("image/png", back.FindResource(image.Source)!.MediaType);
        Assert.InRange(image.WidthPercent!.Value, 30, 40);
        Assert.Equal(["Photo"], HeadingTexts(back));
    }

    [Fact]
    public async Task Pptx_to_pptx_keeps_slides_with_page_break_split()
    {
        var deck = BuildDeck(
            new TestSlide { Shapes = Title(2, "One") + Body(3, P("first") + P("second", 1)) },
            new TestSlide { Shapes = Title(2, "Two") + Body(3, P("plain", bullet: "<a:buNone/>")) });
        var doc = await ImportAsync(deck);

        var bytes = await ExportAsync(doc, null, (PptxExporter.OptTitleSlide, "no"), (PptxExporter.OptSplitOn, "pageBreak"));
        Assert.Empty(Validate(bytes));
        var again = await ImportAsync(bytes);
        Assert.Equal(Structure(doc), Structure(again));
        Assert.Equal(2, again.Metadata.SourcePageCount);
    }
}
