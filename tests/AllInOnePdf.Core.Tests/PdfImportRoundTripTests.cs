using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;

namespace AllInOnePdf.Core.Tests;

/// <summary>
/// samples/sample-document.md → PdfRenderer (Clean, Technical, Manuscript) → PdfImporter: the structure must come back
/// recognisably. Technical numbers its headings ("1.1 1. Measure …") and has running headers, footers, a cover and a
/// table of contents; Manuscript is A5 serif with justified text, first-line indents, a cover and a contents page.
/// </summary>
public sealed class PdfImportRoundTripTests(PdfSampleFixture fixture) : IClassFixture<PdfSampleFixture>
{
    public static TheoryData<string> Themes => new() { "clean", "technical", "manuscript" };

    private Document Doc(string theme) => fixture.Documents[theme];

    private static HeadingBlock Heading(Document doc, string text) =>
        doc.Headings.LastOrDefault(h => h.ToPlainText().EndsWith(text, StringComparison.Ordinal))
        ?? throw new Xunit.Sdk.XunitException($"No heading ending with \"{text}\". Headings: {string.Join(" | ", doc.Headings.Select(h => $"H{h.Level} {h.ToPlainText()}"))}");

    [Theory]
    [MemberData(nameof(Themes))]
    public void Heading_hierarchy_survives(string theme)
    {
        var doc = Doc(theme);
        var title = Heading(doc, "Field Notes on Typography");
        var sections = new[] { "Measure, leading, rhythm", "Code and tables", "Callouts, notes and breaks", "Mathematics", "The closing paragraph" }
            .Select(t => Heading(doc, t)).ToList();
        var subsections = new[] { "A checklist", "Ordered steps" }.Select(t => Heading(doc, t)).ToList();

        Assert.Equal(1, title.Level);
        Assert.All(sections, h => Assert.Equal(sections[0].Level, h.Level));
        Assert.True(sections[0].Level > title.Level);
        Assert.All(subsections, h => Assert.True(h.Level > sections[0].Level, $"{h.ToPlainText()} should be below the sections"));
        Assert.All(doc.Headings, h => Assert.False(string.IsNullOrEmpty(h.Id)));
        Assert.Equal(doc.Headings.Count(), doc.Headings.Select(h => h.Id).Distinct().Count());
        Assert.InRange(doc.Headings.Count(), 8, 10);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Paragraphs_keep_text_and_inline_styles(string theme)
    {
        var doc = Doc(theme);
        var paragraphs = doc.EnumerateAllBlocks().OfType<ParagraphBlock>().ToList();
        var intro = Assert.Single(paragraphs, p => p.ToPlainText().StartsWith("Typography is the craft of arranging type", StringComparison.Ordinal));
        Assert.EndsWith("so you can judge a theme at a glance.", intro.ToPlainText());

        var measure = Assert.Single(paragraphs, p => p.ToPlainText().StartsWith("The measure is the length of a line of text.", StringComparison.Ordinal));
        Assert.Contains(measure.Inlines.OfType<StyledInline>(), s => s.Style.HasFlag(InlineStyle.Bold) && Inline.ToPlainText(s.Inlines) == "measure");
        Assert.EndsWith("break the rhythm of reading.", measure.ToPlainText());

        var inlineCode = Assert.Single(paragraphs, p => p.ToPlainText().StartsWith("Inline code like", StringComparison.Ordinal));
        Assert.Contains(inlineCode.Inlines.OfType<CodeInline>(), c => c.Code.Contains("font-feature-settings", StringComparison.Ordinal));

        Assert.Contains(paragraphs, p => p.ToPlainText().StartsWith("If a theme survives this document", StringComparison.Ordinal)
            && p.ToPlainText().EndsWith("pick a preset and start adjusting.", StringComparison.Ordinal));
        Assert.Contains(paragraphs, p => p.ToPlainText().Contains("Typography exists to honor content.", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Task_list_and_nested_ordered_list_survive(string theme)
    {
        var doc = Doc(theme);
        var lists = doc.EnumerateAllBlocks().OfType<ListBlock>().ToList();

        var tasks = Assert.Single(lists, l => l.Items.Count > 0 && l.Items[0].Checked is not null);
        Assert.False(tasks.Ordered);
        Assert.Equal([true, true, false, false], tasks.Items.Select(i => i.Checked!.Value));
        Assert.Equal("Choose a measure of 60", TextOf(tasks.Items[0])[..22]);
        Assert.Equal("Pick a paragraph style: spacing or indent, never both", TextOf(tasks.Items[3]));

        var steps = Assert.Single(lists, l => l.Ordered && l.Items.Count == 4);
        Assert.Equal(1, steps.Start);
        Assert.Equal("Pick the body face first; everything else follows from it.", TextOf(steps.Items[0]));
        var nested = Assert.Single(steps.Items[3].Blocks.OfType<ListBlock>());
        Assert.True(nested.Ordered);
        Assert.Equal(["Space above headings should exceed space below.", "Keep lists tight unless items are long."], nested.Items.Select(TextOf));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Code_blocks_keep_indentation_and_blank_lines(string theme)
    {
        var doc = Doc(theme);
        var code = doc.EnumerateAllBlocks().OfType<CodeBlock>().ToList();

        var css = Assert.Single(code, c => c.Code.StartsWith(".article {", StringComparison.Ordinal));
        Assert.Equal(".article {\n  font-family: \"Literata\", Georgia, serif;\n  font-size: 1.1875rem;\n  line-height: 1.7;\n  max-width: 68ch;\n}", css.Code);

        var python = Assert.Single(code, c => c.Code.StartsWith("def modular_scale(", StringComparison.Ordinal));
        Assert.Contains("\n    \"\"\"Return a modular scale starting at base.\"\"\"\n", python.Code);
        Assert.Contains("\n\nprint(modular_scale(16, 1.25, 6))", python.Code);

        if (theme == "technical")
        {
            // Technical prints a language label above the block and a line-number gutter inside it.
            Assert.Equal("css", css.Language);
            Assert.Equal("python", python.Language);
        }

        Assert.Contains(code, c => c.Code.Contains(@"\int_0^1", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Table_survives_with_header_and_alignment(string theme)
    {
        var table = Assert.Single(Doc(theme).EnumerateAllBlocks().OfType<TableBlock>());
        Assert.Equal(6, table.Rows.Count);
        Assert.Equal(3, table.ColumnCount);
        Assert.True(table.Rows[0].IsHeader);
        Assert.All(table.Rows.Skip(1), r => Assert.False(r.IsHeader));
        Assert.Equal(["Ratio", "Name", "Scale from 16 px"], table.Rows[0].Cells.Select(c => c.ToPlainText()));
        Assert.Equal(["1.25", "Major third", "16 · 20 · 25 · 31.3 · 39.1"], table.Rows[2].Cells.Select(c => c.ToPlainText()));
        Assert.Equal("Golden ratio", table.Rows[5].Cells[1].ToPlainText());
        Assert.Equal(ColumnAlignment.Right, table.GetAlignment(2));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Callouts_come_back_with_their_kinds(string theme)
    {
        var callouts = Doc(theme).EnumerateAllBlocks().OfType<CalloutBlock>().ToList();
        Assert.Equal([CalloutKind.Note, CalloutKind.Tip, CalloutKind.Important, CalloutKind.Warning, CalloutKind.Caution], callouts.Select(c => c.Kind));
        Assert.Equal("Front matter at the top of this file sets the title, subtitle, author and date used on the cover page.",
            Assert.IsType<ParagraphBlock>(Assert.Single(callouts[0].Blocks)).ToPlainText());

        var link = Assert.Single(callouts[3].Blocks.OfType<ParagraphBlock>().SelectMany(p => p.Inlines).OfType<LinkInline>());
        Assert.Equal("https://example.com/a/very/long/path/that/keeps/going/and/going/until/it/wraps", link.Url);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_block_has_a_source_page_and_metadata_is_read(string theme)
    {
        var doc = Doc(theme);
        Assert.All(doc.EnumerateAllBlocks(), b => Assert.NotNull(b.SourcePage));
        Assert.Equal("Field Notes on Typography", doc.Metadata.Title);
        Assert.Contains("AllInOne PDF", doc.Metadata.Authors);
        Assert.Equal("pdf", doc.Metadata.SourceFormat);
        Assert.True(doc.Metadata.SourcePageCount >= 3);
        Assert.Empty(doc.Blocks.OfType<PageBreakBlock>());
    }

    [Fact]
    public void Technical_running_headers_footers_and_contents_are_removed()
    {
        var doc = Doc("technical");
        var text = PdfImportTestData.AllText(doc);
        Assert.DoesNotContain("Page 2 of", text);
        Assert.DoesNotContain("Field Notes on Typography Field Notes on Typography", text);
        Assert.DoesNotContain(doc.Headings, h => h.ToPlainText() == "Contents");
        Assert.DoesNotContain(doc.EnumerateAllBlocks().OfType<ParagraphBlock>(), p => p.ToPlainText().TrimEnd().EndsWith("Field Notes on Typography 3", StringComparison.Ordinal));
        // The cover is folded into the metadata, so only the chapter heading carries the title text.
        Assert.Equal(1, doc.Headings.Count(h => h.ToPlainText().EndsWith("Field Notes on Typography", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Thematic_breaks_only_separate_kept_content(string theme)
    {
        var blocks = Doc(theme).Blocks;
        Assert.IsNotType<ThematicBreakBlock>(blocks[0]);
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is ThematicBreakBlock)
            {
                Assert.True(i > 0 && i < blocks.Count - 1, "a break must have content on both sides");
                Assert.IsNotType<ThematicBreakBlock>(blocks[i - 1]);
                Assert.IsNotType<ThematicBreakBlock>(blocks[i + 1]);
            }
        }

        // Cover and contents pages (Technical, Manuscript) carry decorative and header rules only.
        if (theme != "clean")
        {
            Assert.DoesNotContain(blocks.OfType<ThematicBreakBlock>(), b => b.SourcePage <= 2);
        }

        // The sample's own "---" survives.
        Assert.NotEmpty(blocks.OfType<ThematicBreakBlock>());
    }

    [Fact]
    public async Task Heading_numbers_can_be_stripped()
    {
        Assert.Contains(Doc("technical").Headings, h => h.ToPlainText() == "1.1 1. Measure, leading, rhythm");

        var doc = await PdfImportTestData.ImportPdfAsync(fixture.Pdfs["technical"], o => o.Set(PdfImporter.OptStripHeadingNumbers, true));
        var texts = doc.Headings.Select(h => h.ToPlainText()).ToList();
        Assert.Contains("1. Measure, leading, rhythm", texts);
        Assert.Contains("1.1 A checklist", texts);
        Assert.Equal(1, texts.Count(t => t == "Field Notes on Typography"));
        Assert.DoesNotContain(texts, t => t.StartsWith("1.1 1.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Headers_and_footers_stay_when_removal_is_off()
    {
        var doc = await PdfImportTestData.ImportPdfAsync(fixture.Pdfs["technical"], o => o.Set(PdfImporter.OptRemoveHeadersFooters, false));
        var text = PdfImportTestData.AllText(doc);
        Assert.Contains("Page 2 of 5", text);
    }

    [Fact]
    public async Task Page_range_limits_the_import()
    {
        var page2 = await PdfImportTestData.ImportPdfAsync(fixture.Pdfs["clean"], o => o.Set(PdfImporter.OptPages, "2"));
        Assert.All(page2.EnumerateAllBlocks(), b => Assert.Equal(2, b.SourcePage));
        Assert.Contains(page2.Headings, h => h.ToPlainText().EndsWith("Code and tables", StringComparison.Ordinal));
        Assert.DoesNotContain(page2.Headings, h => h.ToPlainText().Contains("Measure", StringComparison.Ordinal));
        Assert.Equal(3, page2.Metadata.SourcePageCount);

        var odd = await PdfImportTestData.ImportPdfAsync(fixture.Pdfs["clean"], o => o.Set(PdfImporter.OptPages, "1,3"));
        Assert.Empty(odd.EnumerateAllBlocks().OfType<TableBlock>());
        Assert.Equal([1, 3], odd.EnumerateAllBlocks().Select(b => b.SourcePage!.Value).Distinct().Order());

        await Assert.ThrowsAsync<ArgumentException>(() => PdfImportTestData.ImportPdfAsync(fixture.Pdfs["clean"], o => o.Set(PdfImporter.OptPages, "two")));
        await Assert.ThrowsAsync<ArgumentException>(() => PdfImportTestData.ImportPdfAsync(fixture.Pdfs["clean"], o => o.Set(PdfImporter.OptPages, "9-12")));
    }

    [Fact]
    public async Task Page_break_markers_separate_pages()
    {
        var doc = await PdfImportTestData.ImportPdfAsync(fixture.Pdfs["clean"], o => o.Set(PdfImporter.OptPageBreaks, "marker"));
        Assert.Equal(2, doc.Blocks.OfType<PageBreakBlock>().Count());
        Assert.IsNotType<PageBreakBlock>(doc.Blocks[0]);
        Assert.IsNotType<PageBreakBlock>(doc.Blocks[^1]);
    }

    [Fact]
    public async Task Simple_layout_keeps_text_but_skips_segmentation()
    {
        var doc = await PdfImportTestData.ImportPdfAsync(fixture.Pdfs["clean"], o => o.Set(PdfImporter.OptLayout, "simple"));
        var all = doc.EnumerateAllBlocks().ToList();
        Assert.Empty(all.OfType<ListBlock>());
        Assert.Empty(all.OfType<CodeBlock>());
        Assert.Empty(all.OfType<TableBlock>());
        Assert.Empty(all.OfType<CalloutBlock>());
        Assert.Contains(doc.Headings, h => h.ToPlainText().EndsWith("Code and tables", StringComparison.Ordinal));
        var text = PdfImportTestData.AllText(doc);
        Assert.Contains("Choose a measure of 60", text);
        Assert.Contains(".article {", text);
        Assert.Contains("Major third", text);
        Assert.Contains("Front matter at the top of this file", text);
    }

    [Fact]
    public async Task Structure_options_can_be_switched_off()
    {
        var doc = await PdfImportTestData.ImportPdfAsync(fixture.Pdfs["clean"], o => o
            .Set(PdfImporter.OptDetectHeadings, false)
            .Set(PdfImporter.OptDetectLists, false)
            .Set(PdfImporter.OptDetectCode, false)
            .Set(PdfImporter.OptDetectTables, false));
        var all = doc.EnumerateAllBlocks().ToList();
        Assert.Empty(all.OfType<HeadingBlock>());
        Assert.Empty(all.OfType<ListBlock>());
        Assert.Empty(all.OfType<CodeBlock>());
        Assert.Empty(all.OfType<TableBlock>());
        Assert.Contains("Major third", PdfImportTestData.AllText(doc));
    }

    private static string TextOf(ListItem item) =>
        string.Join(" ", item.Blocks.OfType<ParagraphBlock>().Select(p => p.ToPlainText()));
}
