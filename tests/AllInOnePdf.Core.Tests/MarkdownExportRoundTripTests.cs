using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using static AllInOnePdf.Core.Tests.MarkdownExportTestHelpers;

namespace AllInOnePdf.Core.Tests;

/// <summary>Markdown → Document → Markdown → Document must be structurally stable, and exporting twice must be idempotent.</summary>
public class MarkdownExportRoundTripTests
{
    public static TheoryData<string> Samples => new() { "sample-document.md", "welcome" };

    private static async Task<string> LoadAsync(string sample) =>
        sample == "welcome" ? SampleDocuments.WelcomeMarkdown : await File.ReadAllTextAsync(FindSample(sample));

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Sample_round_trips_structurally(string sample)
    {
        var first = await ImportAsync(await LoadAsync(sample));
        var markdown = await ToMarkdownAsync(first);
        var second = await ImportAsync(markdown);

        Assert.Equal(Dump(first), Dump(second));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Export_is_idempotent(string sample)
    {
        var first = await ImportAsync(await LoadAsync(sample));
        var once = await ToMarkdownAsync(first);
        var twice = await ToMarkdownAsync(await ImportAsync(once));

        Assert.Equal(once, twice);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Round_trips_with_non_default_style_options(string sample)
    {
        (string, object)[] options =
        [
            (MarkdownExporter.OptHeadingStyle, "setext"),
            (MarkdownExporter.OptEmphasisMarker, "_"),
            (MarkdownExporter.OptBulletMarker, "*"),
            (MarkdownExporter.OptOrderedNumbering, "all-ones"),
            (MarkdownExporter.OptHardBreak, "backslash"),
            (MarkdownExporter.OptTableAlignment, false),
            (MarkdownExporter.OptWrapWidth, 40),
            (MarkdownExporter.OptCodeFence, "tildes"),
            (MarkdownExporter.OptLinkStyle, "reference-at-end"),
            (MarkdownExporter.OptLineEndings, "crlf"),
        ];
        var first = await ImportAsync(await LoadAsync(sample));
        var markdown = await ToMarkdownAsync(first, options);
        var second = await ImportAsync(markdown);

        Assert.Equal(Dump(first), Dump(second));
        Assert.Equal(markdown, await ToMarkdownAsync(second, options));
    }

    [Fact]
    public async Task Constructed_document_with_every_type_round_trips()
    {
        var doc = new Document();
        doc.Metadata.Title = "Everything";
        doc.Metadata.Authors.AddRange(["Ada Lovelace", "Grace Hopper"]);
        doc.Metadata.Keywords.AddRange(["a", "b"]);
        doc.Metadata.Description = "All: the blocks";
        var res = doc.AddResource("image/png", [1, 2, 3], "pic.png");

        doc.Blocks.Add(new HeadingBlock(1, "Everything"));
        doc.Blocks.Add(Para(
            new TextInline("Plain * _ ` # | < [x] text, "),
            new StyledInline(InlineStyle.Bold | InlineStyle.Italic, "both"),
            new TextInline(" "),
            new StyledInline(InlineStyle.Underline, "under"),
            new TextInline(" "),
            new StyledInline(InlineStyle.Superscript, "sup"),
            new TextInline(" "),
            new StyledInline(InlineStyle.Subscript, "sub"),
            new TextInline(" "),
            new StyledInline(InlineStyle.Highlight, "mark"),
            new TextInline(" "),
            new StyledInline(InlineStyle.Strikethrough, "gone"),
            new TextInline(" "),
            new CodeInline("a ` b"),
            new TextInline(" "),
            new LinkInline { Url = "https://example.com/a b", Title = "T \"q\"", Inlines = { new TextInline("link [1]") } },
            new LineBreakInline(),
            new TextInline("after break "),
            new MathInline { Latex = "x^2" },
            new TextInline(" "),
            new FootnoteReferenceInline { Label = "1" }));

        var list = new ListBlock { Ordered = true, Start = 3 };
        var item = new ListItem("three");
        var nested = new ListBlock();
        nested.Items.Add(new ListItem("nested") { Checked = true });
        nested.Items.Add(new ListItem("open") { Checked = false });
        item.Blocks.Add(nested);
        list.Items.Add(item);
        list.Items.Add(new ListItem("four"));
        doc.Blocks.Add(list);

        doc.Blocks.Add(new CodeBlock { Code = "```\nnested fence\n```", Language = "md" });
        var quote = new QuoteBlock();
        quote.Blocks.Add(new ParagraphBlock("quoted"));
        var quoteList = new ListBlock();
        quoteList.Items.Add(new ListItem("in quote"));
        quote.Blocks.Add(quoteList);
        doc.Blocks.Add(quote);
        var callout = new CalloutBlock { Kind = CalloutKind.Warning };
        callout.Blocks.Add(new ParagraphBlock("careful"));
        doc.Blocks.Add(callout);

        var table = new TableBlock();
        table.Alignments.AddRange([ColumnAlignment.Left, ColumnAlignment.Center, ColumnAlignment.Right]);
        var header = new TableRow { IsHeader = true };
        header.Cells.AddRange([new TableCell("A"), new TableCell("B"), new TableCell("C")]);
        var row = new TableRow();
        row.Cells.AddRange([new TableCell("a|b"), new TableCell("2"), new TableCell("3")]);
        table.Rows.AddRange([header, row]);
        doc.Blocks.Add(table);

        doc.Blocks.Add(new ImageBlock { Source = res.Reference, AltText = "A picture", Title = "A caption" });
        doc.Blocks.Add(new ThematicBreakBlock());
        doc.Blocks.Add(new PageBreakBlock());
        doc.Blocks.Add(new MathBlock { Latex = @"\frac{1}{2}" });
        doc.Blocks.Add(new HtmlBlock { Html = "<details><summary>More</summary>Hidden</details>" });
        var fn = new Footnote { Label = "1" };
        fn.Blocks.Add(new ParagraphBlock("The note."));
        doc.Footnotes.Add(fn);

        var markdown = await ToMarkdownAsync(doc);
        var back = await ImportAsync(markdown);
        var again = await ToMarkdownAsync(back);

        Assert.Equal(markdown, again);
        var expected = Dump(doc).Replace("src=" + res.Reference, "src=doc_files/pic.png");
        Assert.Equal(expected, Dump(back));
    }
}
