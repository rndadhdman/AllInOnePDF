using AllInOnePdf.Core.Documents;
using AllInOnePdf.Publishing.Content;
using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Tests;

public class ContentTests
{
    [Fact]
    public void Each_module_maps_to_the_expected_block_type()
    {
        var p = Samples.EveryModule();
        var asset = p.Assets[0];

        var text = ModuleBlocks.ToBlocks(new TextModule { Markdown = "Hello\n\n- a\n- b" }, p);
        Assert.Collection(text, b => Assert.IsType<ParagraphBlock>(b), b => Assert.IsType<ListBlock>(b));

        var heading = Assert.IsType<HeadingBlock>(Assert.Single(ModuleBlocks.ToBlocks(new HeadingModule { Text = "Getting **started**", Level = 3 }, p)));
        Assert.Equal(3, heading.Level);
        Assert.Equal("Getting started", heading.ToPlainText());
        Assert.Contains(heading.Inlines, i => i is StyledInline { Style: InlineStyle.Bold });
        Assert.Equal("getting-started", heading.Id);

        var image = Assert.IsType<ImageBlock>(Assert.Single(ModuleBlocks.ToBlocks(new ImageModule { AssetId = asset.Id, Caption = "Cap", WidthPercent = 50 }, p)));
        Assert.Equal("res:" + asset.Id, image.Source);
        Assert.Equal(50, image.WidthPercent);
        Assert.Equal("Cap", image.ResolveCaption());
        Assert.Equal(640, image.PixelWidth);
        Assert.Empty(ModuleBlocks.ToBlocks(new ImageModule(), p));

        var quote = Assert.IsType<QuoteBlock>(Assert.Single(ModuleBlocks.ToBlocks(new QuoteModule { Markdown = "Words", Attribution = "Me" }, p)));
        Assert.Equal("Me", quote.Attribution);
        Assert.IsType<ParagraphBlock>(Assert.Single(quote.Blocks));

        var callout = Assert.IsType<CalloutBlock>(Assert.Single(ModuleBlocks.ToBlocks(new CalloutModule { Kind = CalloutKind.Tip, Title = "T", Markdown = "Body" }, p)));
        Assert.Equal(CalloutKind.Tip, callout.Kind);
        Assert.Equal("T", callout.Title);
        Assert.Single(callout.Blocks);

        var code = Assert.IsType<CodeBlock>(Assert.Single(ModuleBlocks.ToBlocks(new CodeModule { Code = "x = 1", Language = "python" }, p)));
        Assert.Equal("python", code.Language);

        var table = Assert.IsType<TableBlock>(Assert.Single(ModuleBlocks.ToBlocks(
            new TableModule { Cells = [["A", "B"], ["**1**", ""]], Alignments = [ColumnAlignment.Left, ColumnAlignment.Right], Caption = "C" }, p)));
        Assert.Equal(2, table.Rows.Count);
        Assert.True(table.Rows[0].IsHeader);
        Assert.False(table.Rows[1].IsHeader);
        Assert.Equal("1", table.Rows[1].Cells[0].ToPlainText());
        Assert.Equal(ColumnAlignment.Right, table.GetAlignment(1));
        Assert.Equal("C", table.Caption);

        Assert.IsType<ThematicBreakBlock>(Assert.Single(ModuleBlocks.ToBlocks(new DividerModule(), p)));
        Assert.IsType<PageBreakBlock>(Assert.Single(ModuleBlocks.ToBlocks(new PageBreakModule(), p)));

        var button = Assert.IsType<ParagraphBlock>(Assert.Single(ModuleBlocks.ToBlocks(new ButtonModule { Text = "Buy", Url = "https://x.example", Align = BlockAlign.Right }, p)));
        var link = Assert.IsType<LinkInline>(Assert.Single(button.Inlines));
        Assert.Equal("https://x.example", link.Url);
        Assert.Equal(TextAlignment.Right, button.Alignment);

        Assert.Empty(ModuleBlocks.ToBlocks(new SpacerModule(), p));
        Assert.Empty(ModuleBlocks.ToBlocks(new ContentsModule(), p));
    }

    [Theory]
    [InlineData("# Not a heading", "# Not a heading")]
    [InlineData("1. First", "1. First")]
    [InlineData("- item", "- item")]
    [InlineData("> quoted", "> quoted")]
    [InlineData("***", "***")]
    [InlineData("**Bold** start", "Bold start")]
    public void Heading_text_is_inline_only(string text, string expected)
    {
        var block = Assert.IsType<HeadingBlock>(Assert.Single(ModuleBlocks.ToBlocks(new HeadingModule { Text = text }, new Publication())));
        Assert.Equal(expected, block.ToPlainText());
    }

    [Fact]
    public void Flattener_yields_blocks_in_reading_order_with_page_breaks()
    {
        var p = Samples.EveryModule();
        p.Metadata.Authors.Add("Ada");
        var hidden = new TextModule { Markdown = "Secret", Hidden = true };
        p.Sections[0].Rows[0].Columns[0].Modules.Add(hidden);
        p.Sections[0].PageBreakAfter = true;

        var doc = PublicationFlattener.ToDocument(p);

        Assert.Equal("Sample", doc.Metadata.Title);
        Assert.Equal(["Ada"], doc.Metadata.Authors);
        Assert.Equal(p.Assets[0].Id, Assert.Single(doc.Resources).Id);
        Assert.Equal(
            [
                typeof(HeadingBlock), typeof(ParagraphBlock), typeof(ListBlock), typeof(ImageBlock), typeof(ThematicBreakBlock),
                typeof(QuoteBlock),
                typeof(CalloutBlock), typeof(CodeBlock), typeof(TableBlock), typeof(ParagraphBlock),
                typeof(PageBreakBlock),
                typeof(HeadingBlock),
            ],
            doc.Blocks.Select(b => b.GetType()));
        Assert.DoesNotContain(doc.EnumerateAllBlocks().OfType<ParagraphBlock>(), b => b.ToPlainText() == "Secret");
        Assert.NotNull(doc.FindResource("res:" + p.Assets[0].Id));
    }

    [Fact]
    public void Flattener_makes_heading_ids_unique_and_uses_metadata_title()
    {
        var p = new Publication { Name = "Fallback" };
        p.Metadata.Title = "Real title";
        p.Sections.Add(new Section
        {
            Rows = [new Row { Columns = [new Column { Modules = [new HeadingModule { Text = "Same" }, new TextModule { Markdown = "## Same" }, new HeadingModule { Text = "Same" }] }] }],
        });

        var doc = PublicationFlattener.ToDocument(p);

        Assert.Equal("Real title", doc.Metadata.Title);
        var ids = doc.Headings.Select(h => h.Id).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal("same", ids[0]);
    }

    [Fact]
    public void Flattener_carries_footnotes_from_text_modules()
    {
        var p = new Publication();
        p.Sections.Add(new Section { Rows = [new Row { Columns = [new Column { Modules = [new TextModule { Markdown = "Claim.[^1]\n\n[^1]: Source." }] }] }] });
        var doc = PublicationFlattener.ToDocument(p);
        Assert.Single(doc.Footnotes);
    }
}
