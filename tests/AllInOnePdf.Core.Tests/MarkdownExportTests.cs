using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using static AllInOnePdf.Core.Tests.MarkdownExportTestHelpers;

namespace AllInOnePdf.Core.Tests;

public class MarkdownExportTests
{
    private static Task<string> Md(Document doc, params (string, object)[] options) => ToMarkdownAsync(doc, options);

    private static async Task<string> Body(Document doc, params (string, object)[] options) =>
        (await ToMarkdownAsync(doc, [(MarkdownExporter.OptFrontMatter, false), .. options])).TrimEnd('\n');

    // ---- exporter surface ----

    [Fact]
    public void Exporter_is_ready_and_declares_grouped_options()
    {
        var exporter = new MarkdownExporter();
        Assert.Equal(ConverterStatus.Ready, ((IExporter)exporter).Status);
        Assert.Equal(KnownFormats.Markdown, exporter.Format);
        var keys = exporter.Options.Select(o => o.Key).ToList();
        foreach (var key in new[] { "frontMatter", "titleHeading", "pageBreaks", "footnotes", "callouts", "math", "headingStyle", "emphasisMarker", "bulletMarker", "orderedNumbering", "hardBreak", "tableAlignment", "wrapWidth", "codeFence", "images", "imageFolderName", "linkStyle", "lineEndings" })
        {
            Assert.Contains(key, keys);
        }

        Assert.All(exporter.Options, o => Assert.False(string.IsNullOrEmpty(o.Group)));
        Assert.All(exporter.Options, o => Assert.False(string.IsNullOrEmpty(o.Description)));
        Assert.All(exporter.Options.Where(o => o.Type == OptionType.Choice), o => Assert.Contains(o.Choices!, c => c.Value == (string)o.DefaultValue!));
    }

    [Fact]
    public async Task Empty_document_writes_one_empty_file()
    {
        var result = await ExportAsync(new Document(), "empty");
        var file = Assert.Single(result.Files);
        Assert.Equal("empty.md", file.FileName);
        Assert.True(file.IsPrimary);
        Assert.Empty(file.Content);
    }

    [Fact]
    public async Task Output_is_utf8_without_bom_and_honours_crlf()
    {
        var doc = Doc(new ParagraphBlock("é one"), new ParagraphBlock("two"));
        var lf = (await ExportAsync(doc)).Primary!.Content;
        Assert.NotEqual(0xEF, lf[0]);
        Assert.Equal("é one\n\ntwo\n", Encoding.UTF8.GetString(lf));
        Assert.Equal("é one\r\n\r\ntwo\r\n", await Md(doc, (MarkdownExporter.OptLineEndings, "crlf")));
    }

    // ---- headings ----

    [Fact]
    public async Task Headings_atx_and_setext()
    {
        var doc = Doc(new HeadingBlock(1, "One"), new HeadingBlock(2, "Two"), new HeadingBlock(3, "Three"));
        Assert.Equal("# One\n\n## Two\n\n### Three", await Body(doc));
        Assert.Equal("One\n===\n\nTwo\n---\n\n### Three", await Body(doc, (MarkdownExporter.OptHeadingStyle, "setext")));
    }

    [Fact]
    public async Task Heading_trailing_hashes_are_escaped()
    {
        Assert.Equal("## Learn C#", await Body(Doc(new HeadingBlock(2, "Learn C#"))));
        Assert.Equal(@"## Issue \#", await Body(Doc(new HeadingBlock(2, "Issue #"))));
    }

    // ---- inlines ----

    [Theory]
    [InlineData(InlineStyle.Bold, "**x**")]
    [InlineData(InlineStyle.Italic, "*x*")]
    [InlineData(InlineStyle.Strikethrough, "~~x~~")]
    [InlineData(InlineStyle.Highlight, "==x==")]
    [InlineData(InlineStyle.Underline, "<u>x</u>")]
    [InlineData(InlineStyle.Superscript, "^x^")]
    [InlineData(InlineStyle.Subscript, "~x~")]
    [InlineData(InlineStyle.Bold | InlineStyle.Italic, "***x***")]
    [InlineData(InlineStyle.SmallCaps, "x")]
    public async Task Styled_inline_markers(InlineStyle style, string expected)
    {
        var doc = Doc(Para(new TextInline("a "), new StyledInline(style, "x"), new TextInline(" b")));
        Assert.Equal("a " + expected + " b", await Body(doc));

        if (style != InlineStyle.SmallCaps)
        {
            var back = await ImportAsync(await Md(doc));
            Assert.Equal(Runs(((ParagraphBlock)doc.Blocks[0]).Inlines), Runs(((ParagraphBlock)back.Blocks[0]).Inlines));
        }
    }

    [Fact]
    public async Task Nested_styles_combine_and_whitespace_moves_outside_markers()
    {
        var doc = Doc(Para(new StyledInline(InlineStyle.Bold, new TextInline("bold "), new StyledInline(InlineStyle.Italic, "both"), new TextInline(" "))));
        Assert.Equal("**bold *both***", await Body(doc));
    }

    [Fact]
    public async Task Underscore_marker_falls_back_to_asterisk_inside_words()
    {
        var doc = Doc(Para(new TextInline("un"), new StyledInline(InlineStyle.Italic, "believ"), new TextInline("able and "), new StyledInline(InlineStyle.Bold, "strong")));
        Assert.Equal("un*believ*able and __strong__", await Body(doc, (MarkdownExporter.OptEmphasisMarker, "_")));
    }

    [Fact]
    public async Task Code_span_fence_is_longer_than_inner_backticks()
    {
        Assert.Equal("`plain`", await Body(Doc(Para(new CodeInline("plain")))));
        Assert.Equal("``a ` b``", await Body(Doc(Para(new CodeInline("a ` b")))));
        Assert.Equal("``` `` x ```", await Body(Doc(Para(new CodeInline("`` x")))));
        Assert.Equal("`*not* _escaped_`", await Body(Doc(Para(new CodeInline("*not* _escaped_")))));
    }

    [Fact]
    public async Task Links_inline_autolink_and_reference_style()
    {
        var doc = Doc(Para(
            new LinkInline { Url = "https://a.example/x", Title = "Say \"hi\"", Inlines = { new TextInline("A") } },
            new TextInline(" "),
            new LinkInline { Url = "https://b.example", Inlines = { new TextInline("https://b.example") } },
            new TextInline(" "),
            new LinkInline { Url = "my file.md", Inlines = { new TextInline("B") } },
            new TextInline(" "),
            new LinkInline { Url = "https://a.example/x", Title = "Say \"hi\"", Inlines = { new TextInline("again") } }));

        Assert.Equal("[A](https://a.example/x \"Say \\\"hi\\\"\") <https://b.example> [B](<my file.md>) [again](https://a.example/x \"Say \\\"hi\\\"\")", await Body(doc));
        Assert.Equal("[A][1] <https://b.example> [B][2] [again][1]\n\n[1]: https://a.example/x \"Say \\\"hi\\\"\"\n[2]: <my file.md>",
            await Body(doc, (MarkdownExporter.OptLinkStyle, "reference-at-end")));

        var back = await ImportAsync(await Md(doc, (MarkdownExporter.OptLinkStyle, "reference-at-end")));
        Assert.Equal(Runs(((ParagraphBlock)doc.Blocks[0]).Inlines), Runs(((ParagraphBlock)back.Blocks[0]).Inlines));
    }

    [Fact]
    public async Task Line_breaks_hard_and_soft()
    {
        var doc = Doc(Para(new TextInline("a"), new LineBreakInline(), new TextInline("b"), new LineBreakInline { IsHard = false }, new TextInline("c")));
        Assert.Equal("a  \nb\nc", await Body(doc));
        Assert.Equal("a\\\nb\nc", await Body(doc, (MarkdownExporter.OptHardBreak, "backslash")));
    }

    [Fact]
    public async Task Footnotes_math_and_html_inlines()
    {
        var doc = Doc(Para(new TextInline("See"), new FootnoteReferenceInline { Label = "1" }, new TextInline(" "), new MathInline { Latex = "a+b" }, new TextInline(" "), new HtmlInline { Html = "<kbd>" }, new TextInline("K"), new HtmlInline { Html = "</kbd>" }));
        var fn = new Footnote { Label = "1" };
        fn.Blocks.Add(new ParagraphBlock("First."));
        fn.Blocks.Add(new ParagraphBlock("Second."));
        doc.Footnotes.Add(fn);

        Assert.Equal("See[^1] $a+b$ <kbd>K</kbd>\n\n[^1]: First.\n\n    Second.", await Body(doc));
        Assert.Equal("See^1^ `a+b` <kbd>K</kbd>\n\n^1^ First.\n\nSecond.", await Body(doc, (MarkdownExporter.OptFootnotes, false), (MarkdownExporter.OptMath, "codeblock")));

        var back = await ImportAsync(await Md(doc));
        Assert.Equal(2, Assert.Single(back.Footnotes).Blocks.Count);
    }

    // ---- escaping ----

    [Fact]
    public async Task Plain_text_specials_are_escaped_and_survive_a_round_trip()
    {
        const string text = "2 * 3 _x_ `tick` [not](a link) <b>tag</b> a|b $5 ~tilde~ x^2 a==b &amp; snake_case {attr}";
        var doc = Doc(new ParagraphBlock(text));
        var md = await Body(doc);

        Assert.Contains(@"2 \* 3 \_x\_ \`tick\` \[not](a link) \<b>tag\</b> a|b \$5 \~tilde\~ x\^2 a\=\=b \&amp; snake_case \{attr}", md);
        var back = await ImportAsync(md);
        Assert.Equal(text, ((ParagraphBlock)Assert.Single(back.Blocks)).ToPlainText());
    }

    [Theory]
    [InlineData("# not a heading", @"\# not a heading")]
    [InlineData("- not a list", @"\- not a list")]
    [InlineData("+ not a list", @"\+ not a list")]
    [InlineData("1. not a list", @"1\. not a list")]
    [InlineData("> not a quote", @"\> not a quote")]
    [InlineData("C# is fine", "C# is fine")]
    public async Task Block_markers_at_line_start_are_escaped(string text, string expected)
    {
        var md = await Body(Doc(new ParagraphBlock(text)));
        Assert.Equal(expected, md);
        Assert.Equal(text, Assert.IsType<ParagraphBlock>(Assert.Single((await ImportAsync(md)).Blocks)).ToPlainText());
    }

    [Fact]
    public async Task Setext_underline_lookalike_after_soft_break_is_escaped()
    {
        var doc = Doc(Para(new TextInline("Title"), new LineBreakInline { IsHard = false }, new TextInline("---")));
        var md = await Body(doc);
        Assert.Equal("Title\n\\---", md);
        Assert.IsType<ParagraphBlock>(Assert.Single((await ImportAsync(md)).Blocks));
    }

    // ---- blocks ----

    [Fact]
    public async Task Code_block_with_inner_fence_uses_longer_fence()
    {
        var doc = Doc(new CodeBlock { Code = "```js\nx\n```\n", Language = "markdown" });
        Assert.Equal("````markdown\n```js\nx\n```\n````", await Body(doc));
        Assert.Equal("~~~markdown\n```js\nx\n```\n~~~", await Body(doc, (MarkdownExporter.OptCodeFence, "tildes")));

        var back = Assert.IsType<CodeBlock>(Assert.Single((await ImportAsync(await Md(doc))).Blocks));
        Assert.Equal("markdown", back.Language);
        Assert.Equal("```js\nx\n```", back.Code.TrimEnd('\n'));
    }

    [Fact]
    public async Task Lists_nest_with_marker_width_indentation_and_tasks()
    {
        var inner = new ListBlock();
        inner.Items.Add(new ListItem("done") { Checked = true });
        inner.Items.Add(new ListItem("todo") { Checked = false });
        var list = new ListBlock { Ordered = true, Start = 9 };
        list.Items.Add(new ListItem("nine"));
        var ten = new ListItem("ten");
        ten.Blocks.Add(inner);
        list.Items.Add(ten);

        Assert.Equal("9. nine\n10. ten\n    - [x] done\n    - [ ] todo", await Body(Doc(list)));
        Assert.Equal("9. nine\n9. ten\n   * [x] done\n   * [ ] todo", await Body(Doc(list), (MarkdownExporter.OptOrderedNumbering, "all-ones"), (MarkdownExporter.OptBulletMarker, "*")));

        var back = Assert.IsType<ListBlock>(Assert.Single((await ImportAsync(await Md(Doc(list)))).Blocks));
        Assert.Equal(9, back.Start);
        var nested = Assert.IsType<ListBlock>(back.Items[1].Blocks[1]);
        Assert.Equal([true, false], nested.Items.Select(i => i.Checked));
    }

    [Fact]
    public async Task Loose_list_items_are_separated_by_blank_lines()
    {
        var list = new ListBlock { Tight = false };
        var first = new ListItem("one");
        first.Blocks.Add(new ParagraphBlock("more"));
        list.Items.Add(first);
        list.Items.Add(new ListItem("two"));

        Assert.Equal("- one\n\n  more\n\n- two", await Body(Doc(list)));
        Assert.False(Assert.IsType<ListBlock>(Assert.Single((await ImportAsync(await Md(Doc(list)))).Blocks)).Tight);
    }

    [Fact]
    public async Task Adjacent_lists_do_not_merge()
    {
        var a = new ListBlock();
        a.Items.Add(new ListItem("a"));
        var b = new ListBlock();
        b.Items.Add(new ListItem("b"));

        Assert.Equal("- a\n\n* b", await Body(Doc(a, b)));
        Assert.Equal(2, (await ImportAsync(await Md(Doc(a, b)))).Blocks.Count);
    }

    [Fact]
    public async Task Nested_quote_and_list_prefix_every_line()
    {
        var innerQuote = new QuoteBlock();
        innerQuote.Blocks.Add(new ParagraphBlock("deeper"));
        var list = new ListBlock();
        list.Items.Add(new ListItem("item"));
        var quote = new QuoteBlock();
        quote.Blocks.AddRange([new ParagraphBlock("top"), list, innerQuote]);

        Assert.Equal("> top\n>\n> - item\n>\n> > deeper", await Body(Doc(quote)));

        var back = Assert.IsType<QuoteBlock>(Assert.Single((await ImportAsync(await Md(Doc(quote)))).Blocks));
        Assert.Collection(back.Blocks, b => Assert.IsType<ParagraphBlock>(b), b => Assert.IsType<ListBlock>(b), b => Assert.IsType<QuoteBlock>(b));
    }

    [Fact]
    public async Task Callout_styles()
    {
        var callout = new CalloutBlock { Kind = CalloutKind.Tip };
        callout.Blocks.Add(new ParagraphBlock("Body"));
        var doc = Doc(callout);

        Assert.Equal("> [!TIP]\n> Body", await Body(doc));
        Assert.Equal("> **Tip**\n>\n> Body", await Body(doc, (MarkdownExporter.OptCallouts, "blockquote-with-bold-title")));
        Assert.Equal("> Body", await Body(doc, (MarkdownExporter.OptCallouts, "plain-blockquote")));
        Assert.Equal(CalloutKind.Tip, Assert.IsType<CalloutBlock>(Assert.Single((await ImportAsync(await Md(doc))).Blocks)).Kind);
    }

    [Fact]
    public async Task Table_alignment_padding_and_spans()
    {
        var table = new TableBlock();
        table.Alignments.AddRange([ColumnAlignment.Left, ColumnAlignment.Center, ColumnAlignment.Right]);
        var header = new TableRow { IsHeader = true };
        header.Cells.AddRange([new TableCell("Name"), new TableCell("Mid"), new TableCell("N")]);
        var row = new TableRow();
        row.Cells.AddRange([new TableCell("wide span") { ColumnSpan = 2 }, new TableCell("12")]);
        table.Rows.AddRange([header, row]);

        Assert.Equal(
            "| Name      | Mid |   N |\n" +
            "| :-------- | :-: | --: |\n" +
            "| wide span |     |  12 |",
            await Body(Doc(table)));
        Assert.Equal(
            "| Name | Mid | N |\n| :-- | :-: | --: |\n| wide span |  | 12 |",
            await Body(Doc(table), (MarkdownExporter.OptTableAlignment, false)));
    }

    [Fact]
    public async Task Table_cell_with_pipe_and_line_break()
    {
        var table = new TableBlock();
        var header = new TableRow { IsHeader = true };
        header.Cells.AddRange([new TableCell("a"), new TableCell("b")]);
        var row = new TableRow();
        var cell = new TableCell();
        cell.Blocks.Add(Para(new TextInline("x | y"), new LineBreakInline(), new TextInline("z")));
        row.Cells.AddRange([cell, new TableCell("2")]);
        table.Rows.AddRange([header, row]);

        var md = await Body(Doc(table));
        Assert.Contains(@"| x \| y<br>z |", md);

        var back = Assert.IsType<TableBlock>(Assert.Single((await ImportAsync(md)).Blocks));
        Assert.Equal(2, back.Rows[1].Cells.Count);
        Assert.Equal("x | y z", back.Rows[1].Cells[0].ToPlainText());
        Assert.Contains(((ParagraphBlock)back.Rows[1].Cells[0].Blocks[0]).Inlines, i => i is LineBreakInline { IsHard: true });
    }

    [Fact]
    public async Task Table_without_header_row_gets_an_empty_header()
    {
        var table = new TableBlock();
        var row = new TableRow();
        row.Cells.AddRange([new TableCell("1"), new TableCell("2")]);
        table.Rows.Add(row);

        Assert.Equal("|     |     |\n| --- | --- |\n| 1   | 2   |", await Body(Doc(table)));
        var back = Assert.IsType<TableBlock>(Assert.Single((await ImportAsync(await Md(Doc(table)))).Blocks));
        Assert.Equal("2", back.Rows[^1].Cells[1].ToPlainText());
    }

    [Fact]
    public async Task Breaks_rules_html_and_math_blocks()
    {
        var doc = Doc(new ParagraphBlock("a"), new ThematicBreakBlock(), new PageBreakBlock(), new HtmlBlock { Html = "<aside>x</aside>\n" }, new MathBlock { Latex = "x^2" });
        Assert.Equal("a\n\n---\n\n<div style=\"page-break-after: always;\"></div>\n\n<aside>x</aside>\n\n$$\nx^2\n$$", await Body(doc));
        Assert.Equal("a\n\n---\n\n<aside>x</aside>\n\n```math\nx^2\n```", await Body(doc, (MarkdownExporter.OptPageBreaks, "omit"), (MarkdownExporter.OptMath, "codeblock")));
        Assert.Equal("a\n\n---\n\n---", (await Body(doc, (MarkdownExporter.OptPageBreaks, "horizontal-rule"))).Split("\n\n<aside>")[0]);

        var back = await ImportAsync(await Md(doc));
        Assert.Collection(back.Blocks,
            b => Assert.IsType<ParagraphBlock>(b),
            b => Assert.IsType<ThematicBreakBlock>(b),
            b => Assert.IsType<PageBreakBlock>(b),
            b => Assert.IsType<HtmlBlock>(b),
            b => Assert.Equal("x^2", Assert.IsType<MathBlock>(b).Latex.Trim()));
    }

    [Fact]
    public async Task Leading_thematic_break_is_not_mistaken_for_front_matter()
    {
        var md = await Body(Doc(new ThematicBreakBlock(), new ParagraphBlock("x"), new ThematicBreakBlock()));
        Assert.Equal("***\n\nx\n\n---", md);
    }

    // ---- images ----

    private static Document ImageDoc(out DocumentResource resource)
    {
        var doc = new Document();
        resource = doc.AddResource("image/png", [137, 80, 78, 71], "My Chart (final).PNG");
        var unnamed = doc.AddResource("image/jpeg", [255, 216]);
        doc.Blocks.Add(new ImageBlock { Source = resource.Reference, AltText = "Chart", Title = "Sales by month" });
        doc.Blocks.Add(Para(new TextInline("Inline "), new ImageInline { Source = unnamed.Reference, AltText = "dot" }, new TextInline(" and again "), new ImageInline { Source = resource.Reference, AltText = "chart" }));
        doc.Blocks.Add(new ImageBlock { Source = "https://example.com/remote.png", AltText = "Remote" });
        return doc;
    }

    [Fact]
    public async Task Images_extract_to_folder_as_secondary_outputs()
    {
        var result = await ExportAsync(ImageDoc(out _), "report");
        var md = Encoding.UTF8.GetString(result.Primary!.Content);

        Assert.Equal(["report.md", "report_files/My-Chart-final.png", "report_files/image-001.jpg"], result.Files.Select(f => f.FileName));
        Assert.Equal([true, false, false], result.Files.Select(f => f.IsPrimary));
        Assert.Equal("image/png", result.Files[1].MediaType);
        Assert.Contains("![Chart](report_files/My-Chart-final.png \"Sales by month\")\n\n*Sales by month*", md);
        Assert.Contains("Inline ![dot](report_files/image-001.jpg) and again ![chart](report_files/My-Chart-final.png)", md);
        Assert.Contains("![Remote](https://example.com/remote.png)", md);

        // The caption paragraph folds back into the image title.
        var back = await ImportAsync(md);
        var image = Assert.IsType<ImageBlock>(back.Blocks[0]);
        Assert.Equal("Sales by month", image.Title);
        Assert.IsType<ParagraphBlock>(back.Blocks[1]);
    }

    [Fact]
    public async Task Image_folder_name_is_configurable_and_sanitised()
    {
        var result = await ExportAsync(ImageDoc(out _), "My Report", (MarkdownExporter.OptImageFolder, "assets/{name}"));
        Assert.Equal("assets_My Report/My-Chart-final.png", result.Files[1].FileName);
        Assert.Contains("](<assets_My Report/My-Chart-final.png>", Encoding.UTF8.GetString(result.Primary!.Content));
    }

    [Fact]
    public async Task Images_as_data_urls()
    {
        var result = await ExportAsync(ImageDoc(out _), "doc", (MarkdownExporter.OptImages, "data-url"));
        var md = Encoding.UTF8.GetString(result.Primary!.Content);

        Assert.Single(result.Files);
        Assert.Contains("![Chart](data:image/png;base64,iVBORw== \"Sales by month\")", md);
        Assert.Contains("![dot](data:image/jpeg;base64,/9g=)", md);
    }

    [Fact]
    public async Task Images_omitted_leave_alt_text()
    {
        var result = await ExportAsync(ImageDoc(out _), "doc", (MarkdownExporter.OptImages, "omit"));
        var md = Encoding.UTF8.GetString(result.Primary!.Content);

        Assert.Single(result.Files);
        Assert.Equal("Chart\n\nInline dot and again chart\n\nRemote\n", md);
    }

    [Fact]
    public async Task Missing_resource_warns_and_writes_alt_text()
    {
        var result = await ExportAsync(Doc(new ImageBlock { Source = "res:gone", AltText = "Lost" }));
        Assert.Equal("Lost\n", Encoding.UTF8.GetString(result.Primary!.Content));
        Assert.Single(result.Warnings);
    }

    // ---- options ----

    [Fact]
    public async Task Wrapping_breaks_at_words_but_never_inside_code_or_links()
    {
        var doc = Doc(Para(
            new TextInline("one two three four five six seven eight nine ten - eleven "),
            new CodeInline("keep this code together"),
            new TextInline(" twelve "),
            new LinkInline { Url = "https://example.com", Inlines = { new TextInline("a long link text") } },
            new TextInline(" end")));
        var md = await Body(doc, (MarkdownExporter.OptWrapWidth, 24));
        var lines = md.Split('\n');

        Assert.True(lines.Length > 3);
        Assert.All(lines, l => Assert.True(l.Length <= 24 || !l.Contains(' ', StringComparison.Ordinal) || l.Contains('`', StringComparison.Ordinal) || l.Contains('[', StringComparison.Ordinal), l));
        Assert.Contains("`keep this code together`", md);
        Assert.Contains("[a long link text](https://example.com)", md);
        Assert.Contains(@"\- eleven", md);

        var back = Assert.IsType<ParagraphBlock>(Assert.Single((await ImportAsync(md)).Blocks));
        Assert.Equal(Runs(((ParagraphBlock)doc.Blocks[0]).Inlines), Runs(back.Inlines));
    }

    [Fact]
    public async Task Wrapping_inside_lists_and_quotes_respects_the_indent()
    {
        var list = new ListBlock();
        list.Items.Add(new ListItem(string.Join(' ', Enumerable.Repeat("word", 12))));
        var quote = new QuoteBlock();
        quote.Blocks.Add(new ParagraphBlock(string.Join(' ', Enumerable.Repeat("word", 12))));

        var md = await Body(Doc(list, quote), (MarkdownExporter.OptWrapWidth, 30));
        Assert.Equal("- word word word word word\n  word word word word word\n  word word\n\n> word word word word word\n> word word word word word\n> word word", md);
    }

    [Fact]
    public async Task Front_matter_on_off_and_title_heading()
    {
        var doc = Doc(new ParagraphBlock("Body"));
        doc.Metadata.Title = "Notes: draft";
        doc.Metadata.Subtitle = "Sub";
        doc.Metadata.Authors.AddRange(["Ada", "Grace"]);
        doc.Metadata.Date = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        doc.Metadata.Keywords.AddRange(["x", "y"]);
        doc.Metadata.Description = "About things";

        var md = await Md(doc);
        Assert.Equal("---\ntitle: 'Notes: draft'\nsubtitle: Sub\nauthor: [Ada, Grace]\ndate: 2026-01-02\nkeywords: [x, y]\ndescription: About things\n---\n\nBody\n", md);

        var back = await ImportAsync(md);
        Assert.Equal("Notes: draft", back.Metadata.Title);
        Assert.Equal(["Ada", "Grace"], back.Metadata.Authors);
        Assert.Equal(["x", "y"], back.Metadata.Keywords);
        Assert.Equal(2026, back.Metadata.Date?.Year);

        Assert.Equal("Body\n", await Md(doc, (MarkdownExporter.OptFrontMatter, false)));
        Assert.Equal("# Notes: draft\n\nBody\n", await Md(doc, (MarkdownExporter.OptFrontMatter, false), (MarkdownExporter.OptTitleHeading, true)));

        doc.Blocks.Insert(0, new HeadingBlock(1, "Existing"));
        Assert.Equal("# Existing\n\nBody\n", await Md(doc, (MarkdownExporter.OptFrontMatter, false), (MarkdownExporter.OptTitleHeading, true)));
    }

    [Fact]
    public async Task Html_style_tags_import_as_styles()
    {
        var doc = await ImportAsync("a <u>under</u> <sup>up</sup> <mark>hi</mark> <kbd>k</kbd>\n");
        Assert.Equal("a {Underline}under {Superscript}up {Highlight}hi <html:<kbd>>k<html:</kbd>>", Runs(((ParagraphBlock)doc.Blocks[0]).Inlines));
    }
}
