using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Importers.Pdf;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Tests;

/// <summary>
/// The QA pass of 6 November 2026 (C16-C22): what the app writes as PDF, EPUB and PowerPoint must come back
/// without the cover as content, tables split per page, broken code and links, lost inline styles, stray task
/// glyphs and back-links, or "(cont.)" slides.
/// </summary>
public sealed class ImportFidelityTests(PdfSampleFixture fixture) : IClassFixture<PdfSampleFixture>
{
    public static TheoryData<string> Themes => new() { "clean", "technical", "manuscript" };

    private const string Subtitle = "A longer sample for testing conversion, reading and themes";

    private static readonly Lazy<Task<Document>> Table70 = new(async () =>
    {
        var md = new StringBuilder("# Big table\n\nIntro paragraph.\n\n| Name | City | Amount |\n|:-----|:-----|-------:|\n");
        for (var i = 1; i <= 70; i++)
        {
            md.Append($"| Item {i} | Town {i} | {i * 13}.50 |\n");
        }

        md.Append("\nAfter the table.\n");
        var pdf = await PdfImportTestData.RenderMarkdownAsync(md.ToString(), BuiltInThemes.Manuscript());
        return await PdfImportTestData.ImportPdfAsync(pdf);
    });

    // ------------------------------------------------------------ C16 cover

    [Theory]
    [MemberData(nameof(Themes))]
    public void Cover_page_becomes_metadata_not_content(string theme)
    {
        var doc = fixture.Documents[theme];
        var text = PdfImportTestData.AllText(doc);

        Assert.DoesNotContain("AllInOne PDF ·", text);
        Assert.DoesNotContain(doc.EnumerateAllBlocks().OfType<ParagraphBlock>(), p => p.ToPlainText().StartsWith("A longer sample", StringComparison.Ordinal));
        Assert.Equal(1, doc.Headings.Count(h => h.ToPlainText().EndsWith("Field Notes on Typography", StringComparison.Ordinal)));
        Assert.Equal("Field Notes on Typography", doc.Metadata.Title);
        Assert.Contains("AllInOne PDF", doc.Metadata.Authors);
    }

    [Fact]
    public void Cover_subtitle_and_document_date_reach_the_metadata()
    {
        var doc = fixture.Documents["manuscript"];
        Assert.Equal(Subtitle, doc.Metadata.Subtitle);
        Assert.Null(doc.Metadata.Description);

        // The sample's front matter says 2026-11-05; the PDF's creation time is the moment it was rendered.
        Assert.NotNull(doc.Metadata.Date);
        Assert.Equal(new DateTime(2026, 11, 5), doc.Metadata.Date!.Value.Date);
        Assert.Equal(TimeSpan.Zero, doc.Metadata.Date.Value.TimeOfDay);
    }

    [Fact]
    public async Task Cover_without_the_apps_marker_is_recognised_by_its_title()
    {
        // Another writer's cover: a big title and an "author · date" line, the title repeated on page 2.
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.HelveticaBold);
        var cover = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        cover.AddText("Quarterly Report", 32, new UglyToad.PdfPig.Core.PdfPoint(150, 500), bold);
        cover.AddText("Jane Doe · 2026-03-14", 12, new UglyToad.PdfPig.Core.PdfPoint(200, 450), regular);
        var body = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        body.AddText("Quarterly Report", 20, new UglyToad.PdfPig.Core.PdfPoint(72, 760), bold);
        for (var i = 0; i < 6; i++)
        {
            body.AddText($"Body line number {i} of the report with enough words to be ordinary text.", 11, new UglyToad.PdfPig.Core.PdfPoint(72, 720 - i * 15), regular);
        }

        var doc = await PdfImportTestData.ImportPdfAsync(builder.Build());

        Assert.Equal(["Quarterly Report"], doc.Headings.Select(h => h.ToPlainText()));
        Assert.DoesNotContain("Jane Doe", PdfImportTestData.AllText(doc));
        Assert.Contains("Jane Doe", doc.Metadata.Authors);
        Assert.Equal(new DateTime(2026, 3, 14), doc.Metadata.Date!.Value.Date);
    }

    [Fact]
    public async Task One_entry_contents_page_is_dropped()
    {
        var doc = await Table70.Value;
        Assert.DoesNotContain(doc.Headings, h => h.ToPlainText() == "Contents");
        Assert.DoesNotContain(doc.EnumerateAllBlocks().OfType<ParagraphBlock>(), p => p.ToPlainText().StartsWith("Big table", StringComparison.Ordinal));
        Assert.Equal(["Big table"], doc.Headings.Select(h => h.ToPlainText()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    public async Task Unreadable_pdf_reports_a_plain_message(int length)
    {
        // QA C9: no parser internals ("Could not find the version header comment …") in the message.
        var bytes = Enumerable.Repeat((byte)'x', length).ToArray();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => PdfImportTestData.ImportPdfAsync(bytes));
        Assert.DoesNotContain("version header", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(", ex.Message, StringComparison.Ordinal);
        Assert.Contains(length == 0 ? "empty" : "not a PDF", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------ C17 long tables

    [Fact]
    public async Task Table_continued_over_pages_comes_back_as_one_table()
    {
        var doc = await Table70.Value;
        var table = Assert.Single(doc.EnumerateAllBlocks().OfType<TableBlock>());

        Assert.Equal(71, table.Rows.Count);
        Assert.True(table.Rows[0].IsHeader);
        Assert.All(table.Rows.Skip(1), r => Assert.False(r.IsHeader));
        Assert.Equal(["Name", "City", "Amount"], table.Rows[0].Cells.Select(c => c.ToPlainText()));
        Assert.Equal(Enumerable.Range(1, 70).Select(i => $"Item {i}"), table.Rows.Skip(1).Select(r => r.Cells[0].ToPlainText()));
        Assert.Equal(ColumnAlignment.Right, table.GetAlignment(2));

        // Header cells are bold by the table style, not emphasis.
        Assert.All(table.Rows[0].Cells, c => Assert.DoesNotContain(c.Blocks.OfType<ParagraphBlock>().SelectMany(p => p.Inlines), i => i is StyledInline));
        Assert.Equal("After the table.", doc.Blocks.OfType<ParagraphBlock>().Last().ToPlainText());
    }

    [Fact]
    public void Tables_with_different_headers_stay_apart()
    {
        static TableBlock Table(string header, params string[] rows)
        {
            var table = new TableBlock();
            var h = new TableRow { IsHeader = true };
            h.Cells.AddRange([new TableCell(header), new TableCell("B")]);
            table.Rows.Add(h);
            foreach (var row in rows)
            {
                var r = new TableRow();
                r.Cells.AddRange([new TableCell(row), new TableCell("x")]);
                table.Rows.Add(r);
            }

            return table;
        }

        Assert.False(PdfImporter.TryMergeTables(Table("A", "1"), Table("Other", "2")));

        var first = Table("A", "1");
        Assert.True(PdfImporter.TryMergeTables(first, Table("A", "2", "3")));
        Assert.Equal(["A", "1", "2", "3"], first.Rows.Select(r => r.Cells[0].ToPlainText()));
    }

    // ------------------------------------------------------------ C18 code

    [Theory]
    [MemberData(nameof(Themes))]
    public void Wrapped_code_lines_are_rejoined_and_keep_their_language(string theme)
    {
        var code = fixture.Documents[theme].EnumerateAllBlocks().OfType<CodeBlock>().ToList();

        var python = Assert.Single(code, c => c.Code.StartsWith("def modular_scale(", StringComparison.Ordinal));
        Assert.Equal(
            "def modular_scale(base: float, ratio: float, steps: int) -> list[float]:\n"
            + "    \"\"\"Return a modular scale starting at base.\"\"\"\n"
            + "    return [round(base * ratio ** i, 2) for i in range(steps)]\n"
            + "\n"
            + "print(modular_scale(16, 1.25, 6))",
            python.Code);
        Assert.Equal("python", python.Language);
        Assert.Equal("css", Assert.Single(code, c => c.Code.StartsWith(".article {", StringComparison.Ordinal)).Language);
    }

    [Fact]
    public void Code_marker_language_is_read_from_the_name()
    {
        Assert.Equal("python", PdfMarkers.LanguageOf("aio-code-3-python"));
        Assert.Equal("c#", PdfMarkers.LanguageOf("aio-code-12-c#"));
        Assert.Null(PdfMarkers.LanguageOf("aio-code-x-python"));
        Assert.Null(PdfMarkers.LanguageOf("sec-intro"));
    }

    // ------------------------------------------------------------ C19 links

    [Theory]
    [MemberData(nameof(Themes))]
    public void Wrapped_url_link_text_comes_from_the_link_target(string theme)
    {
        const string url = "https://example.com/a/very/long/path/that/keeps/going/and/going/until/it/wraps";
        var links = fixture.Documents[theme].EnumerateAllBlocks().OfType<InlineContainerBlock>()
            .SelectMany(b => b.Inlines).OfType<LinkInline>().Where(l => l.Url == url).ToList();
        var link = Assert.Single(links);
        Assert.Equal(url, Inline.ToPlainText(link.Inlines));
    }

    [Fact]
    public void Url_text_rules()
    {
        Assert.Equal("https://example.com/a/b", InlineBuilder.UrlText("https:/example.com/a// b", "https://example.com/a/b"));
        Assert.Equal("www.example.com/docs", InlineBuilder.UrlText("www.exa mple.com/docs", "https://www.example.com/docs"));
        Assert.Null(InlineBuilder.UrlText("our website", "https://example.com/"));
        Assert.Null(InlineBuilder.UrlText("https://example.com/", "https://example.com/"));
        Assert.Equal("someone@example.com", InlineBuilder.UrlText("some one@example.com", "mailto:someone@example.com"));
    }

    // ---------------------------------------------------- C20 inline styles

    [Theory]
    [MemberData(nameof(Themes))]
    public void Strikethrough_highlight_and_footnotes_come_back(string theme)
    {
        var doc = fixture.Documents[theme];
        var paragraph = Assert.Single(doc.EnumerateAllBlocks().OfType<ParagraphBlock>(), p => p.ToPlainText().StartsWith("Footnotes work too.", StringComparison.Ordinal));

        var styled = paragraph.Inlines.OfType<StyledInline>().ToList();
        Assert.Contains(styled, s => s.Style == InlineStyle.Strikethrough && Inline.ToPlainText(s.Inlines) == "strikethrough");
        Assert.Contains(styled, s => s.Style == InlineStyle.Highlight && Inline.ToPlainText(s.Inlines) == "highlight");
        Assert.Contains(styled, s => s.Style == InlineStyle.Subscript && Inline.ToPlainText(s.Inlines) == "2");
        Assert.Contains(styled, s => s.Style == InlineStyle.Superscript && Inline.ToPlainText(s.Inlines) == "2");

        var reference = Assert.Single(paragraph.Inlines.OfType<FootnoteReferenceInline>());
        var note = Assert.Single(doc.Footnotes);
        Assert.Equal(reference.Label, note.Label);
        Assert.Equal("Footnotes are collected at the end of the document and numbered in order of appearance.", Assert.Single(note.Blocks.OfType<ParagraphBlock>()).ToPlainText());
        Assert.DoesNotContain(doc.EnumerateAllBlocks().OfType<ParagraphBlock>(), p => p.ToPlainText().StartsWith("1 Footnotes", StringComparison.Ordinal));
    }

    [Fact]
    public void Highlight_needs_a_fill_that_hugs_the_text()
    {
        // A shaded table cell (padding around the text) is not a highlight; a fill that starts and ends at the glyphs is.
        PdfWord Word(string text, double left) => new()
        {
            Text = text,
            Glyphs = text.Select((c, k) => new GlyphInfo(c.ToString(), FontFlags.None, left + k * 5, left + (k + 1) * 5, 10)).ToList(),
            Left = left,
            Right = left + text.Length * 5,
            Baseline = 100,
            Bottom = 97.5,
            Top = 107.5,
            Size = 10,
        };

        var page = new PageContent { Number = 1, Width = 600, Height = 800 };
        var line = new PdfLine();
        line.Words.Add(Word("plain", 50));
        line.Words.Add(Word("marked", 90));
        line.Update();
        page.Lines.Add(line);
        page.Fills.Add(new Box(90, 96, 120, 111));    // hugs "marked"
        page.Fills.Add(new Box(40, 95, 85, 112));     // padded box around "plain"

        PageExtractor.MarkDecorations(page);

        Assert.All(line.Words[0].Glyphs, g => Assert.Equal(FontFlags.None, g.Flags & FontFlags.Highlight));
        Assert.All(line.Words[1].Glyphs, g => Assert.Equal(FontFlags.Highlight, g.Flags & FontFlags.Highlight));
    }

    // --------------------------------------------------------------- C21 EPUB

    [Fact]
    public async Task Epub_task_lists_footnotes_and_subtitle_round_trip()
    {
        var original = await EpubExportTestHelpers.LoadAsync("sample-document.md");
        var package = await EpubExportTestHelpers.ExportAsync(original);
        var importer = new EpubImporter();
        using var stream = new MemoryStream(package.Bytes);
        var doc = await importer.ImportAsync(new ImportContext { Input = stream, FileName = "book.epub", Options = OptionValues.FromDefaults(importer.Options) });

        Assert.Equal(Subtitle, doc.Metadata.Subtitle);

        var tasks = Assert.Single(doc.EnumerateAllBlocks().OfType<ListBlock>(), l => l.Items.Count > 0 && l.Items[0].Checked is not null);
        Assert.Equal([true, true, false, false], tasks.Items.Select(i => i.Checked!.Value));
        Assert.All(tasks.Items, i =>
        {
            var p = Assert.Single(i.Blocks);
            Assert.DoesNotMatch("[☐☑☒]", ((ParagraphBlock)p).ToPlainText());
        });

        var markdown = await MarkdownExportTestHelpers.ToMarkdownAsync(doc);
        Assert.Contains("- [x] Choose a measure of 60–75 characters\n", markdown);
        Assert.Contains("[^1]: Footnotes are collected", markdown);
        Assert.DoesNotContain("#ref-fn-1", markdown);
    }

    // ---------------------------------------------------------- C22 PowerPoint

    [Fact]
    public async Task Pptx_headings_separators_and_continuation_slides()
    {
        var md = new StringBuilder("# Deck\n\nIntro.\n\n## Section\n\nText under the section.\n\n### Detail\n\nDetail text.\n\n# Long list\n\n");
        for (var i = 1; i <= 30; i++)
        {
            md.Append($"{i}. Item number {i}\n");
        }

        md.Append("\n# Last\n\nThe end.\n");
        var original = await MarkdownExportTestHelpers.ImportAsync(md.ToString());
        var bytes = await PptxTestHelpers.ExportAsync(original, null, (PptxExporter.OptTitleSlide, "no"));
        var doc = await PptxTestHelpers.ImportAsync(bytes);

        Assert.Equal(["H1 Deck", "H2 Section", "H3 Detail", "H1 Long list", "H1 Last"], doc.Headings.Select(h => $"H{h.Level} {h.ToPlainText()}"));
        Assert.Empty(doc.Blocks.OfType<PageBreakBlock>());
        Assert.Equal(2, doc.Blocks.OfType<ThematicBreakBlock>().Count());

        var list = Assert.Single(doc.Blocks.OfType<ListBlock>());
        Assert.True(list.Ordered);
        Assert.Equal(Enumerable.Range(1, 30).Select(i => $"Item number {i}"), list.Items.Select(i => i.Blocks.OfType<ParagraphBlock>().Single().ToPlainText()));

        var markdown = await MarkdownExportTestHelpers.ToMarkdownAsync(doc);
        Assert.DoesNotContain("page-break", markdown);
        Assert.DoesNotContain("(cont.)", markdown);
        Assert.Contains("\n---\n", markdown);
    }
}
