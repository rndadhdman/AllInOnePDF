using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;
using static AllInOnePdf.Core.Tests.PptxTestHelpers;

namespace AllInOnePdf.Core.Tests;

/// <summary>.pptx → document: slide order, titles, bullets, runs, code, tables, pictures, notes, metadata and guard rails.</summary>
public class PptxImportTests
{
    private static TestSlide Slide(string title, string body = "", bool hidden = false) => new()
    {
        Shapes = Title(2, title) + body,
        Hidden = hidden,
    };

    [Fact]
    public async Task Slides_become_sections_in_order_with_dividers_or_page_breaks()
    {
        var deck = BuildDeck(
            Slide("First", Body(3, P("one", bullet: "<a:buNone/>"))),
            Slide("Second", Body(3, P("two", bullet: "<a:buNone/>"))),
            Slide("Third"));

        var doc = await ImportAsync(deck);

        Assert.Equal(["First", "Second", "Third"], HeadingTexts(doc));
        Assert.All(doc.Headings, h => Assert.Equal(1, h.Level));

        // By default a divider (--- in Markdown) separates the slides; page breaks are an option.
        Assert.Equal(2, doc.Blocks.OfType<ThematicBreakBlock>().Count());
        Assert.Empty(doc.Blocks.OfType<PageBreakBlock>());
        Assert.IsType<HeadingBlock>(doc.Blocks[0]);
        Assert.IsType<ThematicBreakBlock>(doc.Blocks[2]);

        var paged = await ImportAsync(deck, o => o.Set(PptxImporter.OptPageBreaks, "marker"));
        Assert.Equal(2, paged.Blocks.OfType<PageBreakBlock>().Count());
        Assert.Empty(paged.Blocks.OfType<ThematicBreakBlock>());
        Assert.IsType<PageBreakBlock>(paged.Blocks[2]);
        Assert.Equal(3, doc.Metadata.SourcePageCount);
        Assert.Equal("pptx", doc.Metadata.SourceFormat);
        Assert.All(doc.EnumerateAllBlocks(), b => Assert.NotNull(b.SourcePage));
        Assert.Equal(2, doc.Blocks.OfType<ParagraphBlock>().Single(p => p.ToPlainText() == "two").SourcePage);
        Assert.Equal(3, doc.Blocks.OfType<HeadingBlock>().Last().SourcePage);

        var joined = await ImportAsync(deck, o => o.Set(PptxImporter.OptPageBreaks, "none"));
        Assert.Empty(joined.Blocks.OfType<PageBreakBlock>());
        Assert.Empty(joined.Blocks.OfType<ThematicBreakBlock>());
    }

    [Fact]
    public async Task Untitled_slides_are_numbered_or_left_without_heading()
    {
        var deck = BuildDeck(
            Slide("Intro"),
            new TestSlide { Shapes = TextBox(2, Inch, P("Just text")) });

        var numbered = await ImportAsync(deck);
        Assert.Equal(["Intro", "Slide 2"], HeadingTexts(numbered));

        var none = await ImportAsync(deck, o => o.Set(PptxImporter.OptUntitledSlides, "none"));
        Assert.Equal(["Intro"], HeadingTexts(none));
        Assert.Contains(none.Blocks.OfType<ParagraphBlock>(), p => p.ToPlainText() == "Just text");

        var level2 = await ImportAsync(deck, o => o.Set(PptxImporter.OptHeadingLevel, 2));
        Assert.All(level2.Headings, h => Assert.Equal(2, h.Level));
    }

    [Fact]
    public async Task Body_placeholder_paragraphs_become_nested_lists()
    {
        var body = P("Alpha") + P("Alpha one", 1) + P("Alpha two", 1) + P("Deep", 2) + P("Beta")
            + P("Not a bullet", bullet: "<a:buNone/>")
            + P("Third", bullet: "<a:buAutoNum type=\"arabicPeriod\" startAt=\"3\"/>")
            + P("Fourth", bullet: "<a:buAutoNum type=\"arabicPeriod\" startAt=\"3\"/>");
        var doc = await ImportAsync(BuildDeck(Slide("Lists", Body(3, body))));

        var lists = doc.Blocks.OfType<ListBlock>().ToList();
        Assert.Equal(2, lists.Count);
        var bullets = lists[0];
        Assert.False(bullets.Ordered);
        Assert.Equal(2, bullets.Items.Count);
        Assert.Equal("Alpha", ((ParagraphBlock)bullets.Items[0].Blocks[0]).ToPlainText());
        var nested = Assert.IsType<ListBlock>(bullets.Items[0].Blocks[1]);
        Assert.Equal(["Alpha one", "Alpha two"], nested.Items.Select(i => ((ParagraphBlock)i.Blocks[0]).ToPlainText()));
        var deep = Assert.IsType<ListBlock>(nested.Items[1].Blocks[1]);
        Assert.Equal("Deep", ((ParagraphBlock)deep.Items[0].Blocks[0]).ToPlainText());

        Assert.Contains(doc.Blocks.OfType<ParagraphBlock>(), p => p.ToPlainText() == "Not a bullet");

        var numbered = lists[1];
        Assert.True(numbered.Ordered);
        Assert.Equal(3, numbered.Start);
        Assert.Equal(2, numbered.Items.Count);
    }

    [Fact]
    public async Task Text_boxes_are_plain_unless_bulleted_and_empty_paragraphs_end_lists()
    {
        var paragraphs = P("Plain text") + P("Bulleted", bullet: "<a:buChar char=\"•\"/>") + "<a:p/>"
            + P("Another", bullet: "<a:buChar char=\"•\"/>") + P("Centered", attrs: "algn=\"ctr\"");
        var doc = await ImportAsync(BuildDeck(Slide("Boxes", TextBox(3, 2 * Inch, paragraphs))));

        var blocks = doc.Blocks.Skip(1).ToList();
        Assert.Equal("Plain text", Assert.IsType<ParagraphBlock>(blocks[0]).ToPlainText());
        Assert.Single(Assert.IsType<ListBlock>(blocks[1]).Items);
        Assert.Single(Assert.IsType<ListBlock>(blocks[2]).Items);
        var centered = Assert.IsType<ParagraphBlock>(blocks[3]);
        Assert.Equal(TextAlignment.Center, centered.Alignment);
    }

    [Fact]
    public async Task Run_styles_and_hyperlinks_are_kept_and_adjacent_runs_merge()
    {
        var runs = Run("Hello ") + Run("bold", "b=\"1\"") + Run(" and", "b=\"1\"") + Run(" italic", "i=\"1\"")
            + Run("under", "u=\"sng\"") + Run("gone", "strike=\"sngStrike\"") + Run("2", "baseline=\"30000\"") + Run("x", "baseline=\"-25000\"")
            + Run("link", inner: "<a:hlinkClick r:id=\"{link0}\"/>") + Run("bad", inner: "<a:hlinkClick r:id=\"{link1}\"/>")
            + "<a:br><a:rPr lang=\"en-US\"/></a:br>" + Run("after")
            + "<a:fld id=\"{B6F15528-21DE-4FAA-801E-634DDDAF4B2B}\" type=\"slidenum\"><a:rPr lang=\"en-US\"/><a:t>4</a:t></a:fld>";
        var slide = new TestSlide { Shapes = Title(2, "Runs") + TextBox(3, Inch, Para(runs)), Links = ["https://example.com/a", "javascript:alert(1)"] };
        var doc = await ImportAsync(BuildDeck(slide));

        var p = doc.Blocks.OfType<ParagraphBlock>().Single();
        Assert.Equal(
            "Hello {Bold}bold and{Italic} italic{Underline}under{Strikethrough}gone{Superscript}2{Subscript}x<link:https://example.com/a>link</link:>bad<br:>after4",
            MarkdownExportTestHelpers.Runs(p.Inlines));
        Assert.Contains(p.Inlines, i => i is LineBreakInline);
        Assert.Equal(1, p.Inlines.OfType<StyledInline>().Count(s => s.Style == InlineStyle.Bold));
        Assert.DoesNotContain(p.Inlines.OfType<LinkInline>(), l => l.Url.StartsWith("javascript", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Monospace_shapes_become_code_blocks()
    {
        var code = Para(Run("var x = 1;", inner: "<a:latin typeface=\"Consolas\"/>")) + Para(Run("  return x;", inner: "<a:latin typeface=\"Consolas\"/>"));
        var mixed = Para(Run("Call ") + Run("Run()", inner: "<a:latin typeface=\"Courier New\"/>") + Run(" now"));
        var deck = BuildDeck(Slide("Code", TextBox(3, 2 * Inch, code) + TextBox(4, 4 * Inch, mixed)));

        var doc = await ImportAsync(deck);
        var block = doc.Blocks.OfType<CodeBlock>().Single();
        Assert.Equal("var x = 1;\n  return x;", block.Code);
        var p = doc.Blocks.OfType<ParagraphBlock>().Single();
        Assert.Equal("Call <code:Run()> now", MarkdownExportTestHelpers.Runs(p.Inlines));

        var off = await ImportAsync(deck, o => o.Set(PptxImporter.OptDetectCode, false));
        Assert.Empty(off.Blocks.OfType<CodeBlock>());
        Assert.DoesNotContain(off.EnumerateAllBlocks().OfType<ParagraphBlock>().SelectMany(b => b.Inlines), i => i is CodeInline);
    }

    [Fact]
    public async Task Tables_keep_spans_and_header()
    {
        var grid = "<a:gridCol w=\"2000000\"/><a:gridCol w=\"2000000\"/><a:gridCol w=\"2000000\"/>";
        var rows =
            "<a:tr h=\"370840\">" + Cell("Name") + Cell("Score") + Cell("Notes") + "</a:tr>"
            + "<a:tr h=\"370840\">" + Cell("Wide", "gridSpan=\"2\"") + Cell(string.Empty, "hMerge=\"1\"") + Cell("Tall", "rowSpan=\"2\"") + "</a:tr>"
            + "<a:tr h=\"370840\">" + Cell("A") + Cell("B") + Cell(string.Empty, "vMerge=\"1\"") + "</a:tr>";
        var doc = await ImportAsync(BuildDeck(Slide("Table", Table(3, rows, grid))));

        var table = doc.Blocks.OfType<TableBlock>().Single();
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(3, table.ColumnCount);
        Assert.True(table.Rows[0].IsHeader);
        Assert.False(table.Rows[1].IsHeader);
        Assert.Equal(["Name", "Score", "Notes"], table.Rows[0].Cells.Select(c => c.ToPlainText()));
        Assert.Equal(2, table.Rows[1].Cells.Count);
        Assert.Equal(2, table.Rows[1].Cells[0].ColumnSpan);
        Assert.Equal(2, table.Rows[1].Cells[1].RowSpan);
        Assert.Equal(["A", "B"], table.Rows[2].Cells.Select(c => c.ToPlainText()));

        // Without the firstRow flag, an all-bold first row still counts as the header (and loses the bold).
        var boldRows = "<a:tr h=\"370840\">" + Cell("H1", runAttrs: "b=\"1\"") + Cell("H2", runAttrs: "b=\"1\"") + "</a:tr><a:tr h=\"370840\">" + Cell("1") + Cell("2") + "</a:tr>";
        var bold = await ImportAsync(BuildDeck(Slide("Bold", Table(3, boldRows, "<a:gridCol w=\"2000000\"/><a:gridCol w=\"2000000\"/>", firstRow: false))));
        var t2 = bold.Blocks.OfType<TableBlock>().Single();
        Assert.True(t2.Rows[0].IsHeader);
        Assert.Equal("H1", MarkdownExportTestHelpers.Runs(((ParagraphBlock)t2.Rows[0].Cells[0].Blocks[0]).Inlines));
    }

    [Fact]
    public async Task Pictures_become_resources_with_width_relative_to_slide()
    {
        var png = DocxTestHelpers.TinyPng(8, 6);
        var deck = BuildDeck(new TestSlide { Shapes = Title(2, "Picture") + Picture(3, 12192000 / 2), Image = png });

        var doc = await ImportAsync(deck);
        var image = doc.Blocks.OfType<ImageBlock>().Single();
        var resource = doc.FindResource(image.Source);
        Assert.NotNull(resource);
        Assert.Equal("image/png", resource.MediaType);
        Assert.Equal(png, resource.Data);
        Assert.Equal(50, image.WidthPercent);
        Assert.Equal(8, image.PixelWidth);
        Assert.Equal(6, image.PixelHeight);
        Assert.Equal("A red square", image.AltText);

        var none = await ImportAsync(deck, o => o.Set(PptxImporter.OptExtractImages, false));
        Assert.Empty(none.Blocks.OfType<ImageBlock>());
        Assert.Empty(none.Resources);
    }

    [Fact]
    public async Task Oversized_and_vector_pictures_are_skipped_with_warnings()
    {
        var big = DocxTestHelpers.TinyPng().Concat(new byte[(int)(1.5 * 1024 * 1024)]).ToArray();
        var deck = BuildDeck(
            new TestSlide { Shapes = Title(2, "Big") + Picture(3, Inch), Image = big },
            new TestSlide { Shapes = Title(2, "Vector") + Picture(3, Inch), Image = [1, 2, 3, 4], ImageType = "image/x-emf" },
            new TestSlide { Shapes = Title(2, "Chart") + Chart(3) + Chart(4) });

        var warnings = new List<string>();
        var doc = await ImportAsync(deck, o => o.Set(PptxImporter.OptMaxImageMb, 1), warnings);

        Assert.Empty(doc.Blocks.OfType<ImageBlock>());
        Assert.Contains(warnings, w => w.Contains("1 picture larger than 1 MB", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("EMF", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("2 charts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hidden_slides_are_skipped_unless_included()
    {
        var deck = BuildDeck(Slide("Shown"), Slide("Secret", hidden: true), Slide("Last"));

        var warnings = new List<string>();
        var doc = await ImportAsync(deck, warnings: warnings);
        Assert.Equal(["Shown", "Last"], HeadingTexts(doc));
        Assert.Equal(3, doc.Headings.Last().SourcePage);
        Assert.Contains(warnings, w => w.Contains("hidden slide", StringComparison.Ordinal));

        var all = await ImportAsync(deck, o => o.Set(PptxImporter.OptIncludeHidden, true));
        Assert.Equal(["Shown", "Secret", "Last"], HeadingTexts(all));
    }

    [Fact]
    public async Task Speaker_notes_become_a_callout_when_asked()
    {
        var deck = BuildDeck(new TestSlide { Shapes = Title(2, "Talk") + TextBox(3, Inch, P("On the slide")), Notes = P("Say hello") + P("Then wave") });

        var off = await ImportAsync(deck);
        Assert.Empty(off.EnumerateAllBlocks().OfType<CalloutBlock>());

        var on = await ImportAsync(deck, o => o.Set(PptxImporter.OptSpeakerNotes, true));
        var callout = Assert.IsType<CalloutBlock>(on.Blocks[^1]);
        Assert.Equal(CalloutKind.Note, callout.Kind);
        Assert.Equal("Speaker notes", callout.Title);
        Assert.Equal(["Say hello", "Then wave"], callout.Blocks.OfType<ParagraphBlock>().Select(p => p.ToPlainText()));
        Assert.DoesNotContain(on.EnumerateAllBlocks().OfType<ParagraphBlock>(), p => p.ToPlainText() == "7");
    }

    [Fact]
    public async Task Shapes_are_read_by_position_or_as_stored()
    {
        // Stored order: bottom box, title, top box, right box; the body placeholder (no xfrm) inherits y = 1.75 in from the layout.
        var shapes = TextBox(3, 5 * Inch, P("Bottom")) + Title(2, "Order") + TextBox(4, Inch, P("Top left"), x: 0)
            + TextBox(5, Inch, P("Top right"), x: 5 * Inch) + Body(6, P("Body", bullet: "<a:buNone/>"));
        var deck = BuildDeck(new TestSlide { Shapes = shapes });

        var byPosition = await ImportAsync(deck);
        Assert.Equal(["Order", "Top left", "Top right", "Body", "Bottom"], Texts(byPosition));

        var asStored = await ImportAsync(deck, o => o.Set(PptxImporter.OptReadingOrder, "xml"));
        Assert.Equal(["Order", "Bottom", "Top left", "Top right", "Body"], Texts(asStored));

        static List<string> Texts(Document d) => d.Blocks.OfType<InlineContainerBlock>().Select(b => b.ToPlainText()).ToList();
    }

    [Fact]
    public async Task Grouped_shapes_and_subtitles_are_read()
    {
        var group = "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"10\" name=\"Group\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>"
            + "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"100\" cy=\"100\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"100\" cy=\"100\"/></a:xfrm></p:grpSpPr>"
            + TextBox(11, 3 * Inch, P("Inside group")) + "</p:grpSp>";
        var subtitle = "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Subtitle\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"subTitle\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>"
            + $"<p:txBody><a:bodyPr/><a:lstStyle/>{P("The subtitle")}</p:txBody></p:sp>";
        var doc = await ImportAsync(BuildDeck(new TestSlide { Shapes = Title(2, "Deck title", "ctrTitle") + subtitle + group }));

        Assert.Equal(["Deck title"], HeadingTexts(doc));
        var sub = doc.Blocks.OfType<ParagraphBlock>().First();
        Assert.Equal("{Italic}The subtitle", MarkdownExportTestHelpers.Runs(sub.Inlines));
        Assert.Contains(doc.Blocks.OfType<ParagraphBlock>(), p => p.ToPlainText() == "Inside group");
    }

    [Fact]
    public async Task Metadata_comes_from_package_properties_with_title_fallbacks()
    {
        var deck = BuildDeck([Slide("Opening")], p =>
        {
            p.PackageProperties.Title = "Quarterly review";
            p.PackageProperties.Subject = "Numbers";
            p.PackageProperties.Creator = "Ann; Bob";
            p.PackageProperties.Keywords = "q3, finance";
            p.PackageProperties.Created = new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc);
        });
        var doc = await ImportAsync(deck);
        Assert.Equal("Quarterly review", doc.Metadata.Title);
        Assert.Equal("Numbers", doc.Metadata.Subtitle);
        Assert.Equal(["Ann", "Bob"], doc.Metadata.Authors);
        Assert.Equal(["q3", "finance"], doc.Metadata.Keywords);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero), doc.Metadata.Date!.Value.ToUniversalTime());

        var untitled = await ImportAsync(BuildDeck(Slide("Opening")));
        Assert.Equal("Opening", untitled.Metadata.Title);

        var empty = await ImportAsync(BuildDeck(new TestSlide { Shapes = TextBox(2, Inch, P("x")) }));
        Assert.Equal("test", empty.Metadata.Title);
    }

    [Fact]
    public async Task Protected_and_non_zip_files_fail_with_clear_messages()
    {
        byte[] cfb = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(cfb));
        Assert.Contains("password-protected", ex.Message, StringComparison.Ordinal);

        var text = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync("hello world"u8.ToArray()));
        Assert.Contains("not a PowerPoint presentation", text.Message, StringComparison.Ordinal);

        var docx = await DocxTestHelpers.ExportAsync(MarkdownExportTestHelpers.Doc(new ParagraphBlock("x")));
        var wrong = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(docx));
        Assert.Contains("PowerPoint", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Importer_is_registered_for_pptx()
    {
        Conversion.IImporter importer = new PptxImporter();
        Assert.Equal("pptx", importer.Format.Id);
        Assert.True(importer.CanImport("deck.PPTX"));
        Assert.Contains(importer.Options, o => o.Key == PptxImporter.OptSpeakerNotes && o.Group == "Content");
        Assert.Contains(importer.Options, o => o.Key == PptxImporter.OptMaxImageMb && o.VisibleWhen == PptxImporter.OptExtractImages && o.Advanced);
    }
}
