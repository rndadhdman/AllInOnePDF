using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml.Packaging;
using static AllInOnePdf.Core.Tests.PptxTestHelpers;
using A = DocumentFormat.OpenXml.Drawing;
using Document = AllInOnePdf.Core.Documents.Document;
using P = DocumentFormat.OpenXml.Presentation;

namespace AllInOnePdf.Core.Tests;

/// <summary>Document → .pptx: valid Open XML, slides per section, continuation slides, theme parts, tables and pictures.</summary>
public class PptxExportTests
{
    public static TheoryData<string, string> SamplesAndThemes()
    {
        var data = new TheoryData<string, string>();
        foreach (var sample in new[] { "welcome", "sample-document.md" })
        {
            foreach (var theme in BuiltInThemes.All())
            {
                data.Add(sample, theme.Id);
            }
        }

        return data;
    }

    private static Document Doc(params Block[] blocks) => MarkdownExportTestHelpers.Doc(blocks);

    private static ParagraphBlock Para(params Inline[] inlines) => MarkdownExportTestHelpers.Para(inlines);

    private static List<P.Slide> Slides(PresentationDocument package)
    {
        var part = package.PresentationPart!;
        return part.Presentation!.SlideIdList!.Elements<P.SlideId>()
            .Select(id => ((SlidePart)part.GetPartById(id.RelationshipId!.Value!)).Slide!)
            .ToList();
    }

    private static string SlideTitle(P.Slide slide) => string.Concat(slide.CommonSlideData!.ShapeTree!.Elements<P.Shape>()
        .First(s => s.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape is not null)
        .TextBody!.Descendants<A.Text>().Select(t => t.Text));

    private static List<string> Titles(byte[] bytes)
    {
        using var package = PresentationDocument.Open(new MemoryStream(bytes), false);
        return Slides(package).Select(SlideTitle).ToList();
    }

    [Theory]
    [MemberData(nameof(SamplesAndThemes))]
    public async Task Sample_exports_to_valid_pptx(string sample, string themeId)
    {
        var theme = BuiltInThemes.Find(themeId)!;
        var doc = await DocxTestHelpers.LoadSampleAsync(sample);
        var bytes = await ExportAsync(doc, theme);

        Assert.Empty(Validate(bytes));

        using var package = PresentationDocument.Open(new MemoryStream(bytes), false);
        var slides = Slides(package);
        Assert.True(slides.Count >= 3);
        Assert.Equal(doc.ResolveTitle(), SlideTitle(slides[0]));
        foreach (var h1 in doc.Blocks.OfType<HeadingBlock>().Where(h => h.Level == 1))
        {
            Assert.Contains(slides, s => SlideTitle(s) == Collapse(h1.ToPlainText()));
        }

        var master = package.PresentationPart!.SlideMasterParts.Single();
        Assert.Equal(2, master.SlideLayoutParts.Count());
        var bg = master.SlideMaster!.CommonSlideData!.Background!.Descendants<A.RgbColorModelHex>().First().Val!.Value;
        Assert.Equal(theme.Page.Background.TrimStart('#').ToUpperInvariant(), bg);
    }

    [Fact]
    public async Task Theme_fonts_and_colours_reach_the_theme_part()
    {
        var theme = BuiltInThemes.Night();
        theme.Headings.Font = "Literata";
        var bytes = await ExportAsync(Doc(new HeadingBlock(1, "Hi"), new ParagraphBlock("Body")), theme);
        Assert.Empty(Validate(bytes));

        using var package = PresentationDocument.Open(new MemoryStream(bytes), false);
        var themePart = package.PresentationPart!.ThemePart!;
        var fonts = themePart.Theme!.ThemeElements!.FontScheme!;
        Assert.Equal("Literata", fonts.MajorFont!.LatinFont!.Typeface!.Value);
        Assert.Equal(theme.Typography.BodyFont, fonts.MinorFont!.LatinFont!.Typeface!.Value);
        var colors = themePart.Theme.ThemeElements.ColorScheme!;
        Assert.Equal("7FB4FF", colors.Accent1Color!.RgbColorModelHex!.Val!.Value);
        Assert.Equal("1B1E24", colors.Dark1Color!.RgbColorModelHex!.Val!.Value);
        Assert.Equal("dk1", package.PresentationPart.SlideMasterParts.Single().SlideMaster!.ColorMap!.Background1!.InnerText);

        var slide = Slides(package)[1];
        var bodyRun = slide.Descendants<A.Run>().Single(r => r.Text!.Text == "Body");
        Assert.Equal("C9CDD4", bodyRun.RunProperties!.GetFirstChild<A.SolidFill>()!.RgbColorModelHex!.Val!.Value);
        Assert.Equal(theme.Typography.BodyFont, bodyRun.RunProperties.GetFirstChild<A.LatinFont>()!.Typeface!.Value);
        var titleRun = slide.Descendants<A.Run>().Single(r => r.Text!.Text == "Hi");
        Assert.Equal("Literata", titleRun.RunProperties!.GetFirstChild<A.LatinFont>()!.Typeface!.Value);
        Assert.Equal("E6E8EC", titleRun.RunProperties.GetFirstChild<A.SolidFill>()!.RgbColorModelHex!.Val!.Value);
    }

    [Fact]
    public async Task Split_mode_decides_the_slide_count()
    {
        var doc = Doc(
            new HeadingBlock(1, "A"), new ParagraphBlock("a"),
            new HeadingBlock(2, "B"), new ParagraphBlock("b"),
            new PageBreakBlock(), new ParagraphBlock("after break"),
            new HeadingBlock(1, "C"), new ParagraphBlock("c"),
            new HeadingBlock(1, "Empty"));

        Assert.Equal(["A", "C", "Empty"], Titles(await ExportAsync(doc, null, (PptxExporter.OptTitleSlide, "no"))));
        Assert.Equal(["A", "B", "C", "Empty"], Titles(await ExportAsync(doc, null, (PptxExporter.OptTitleSlide, "no"), (PptxExporter.OptSplitOn, "h2"))));
        Assert.Equal(["A", "A (cont.)", "C", "Empty"], Titles(await ExportAsync(doc, null, (PptxExporter.OptTitleSlide, "no"), (PptxExporter.OptSplitOn, "pageBreak"))));
    }

    [Fact]
    public async Task Title_slide_carries_title_subtitle_authors_and_date()
    {
        var doc = Doc(new HeadingBlock(1, "Chapter"), new ParagraphBlock("text"));
        doc.Metadata.Title = "My deck";
        doc.Metadata.Subtitle = "A subtitle";
        doc.Metadata.Authors.AddRange(["Ann", "Bob"]);
        doc.Metadata.Date = new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

        var bytes = await ExportAsync(doc);
        Assert.Empty(Validate(bytes));
        using (var package = PresentationDocument.Open(new MemoryStream(bytes), false))
        {
            var slides = Slides(package);
            Assert.Equal(2, slides.Count);
            var phTypes = slides[0].Descendants<P.PlaceholderShape>().Select(p => p.Type!.Value).ToList();
            Assert.Contains(P.PlaceholderValues.CenteredTitle, phTypes);
            Assert.Contains(P.PlaceholderValues.SubTitle, phTypes);
            var texts = slides[0].Descendants<A.Paragraph>().Select(p => string.Concat(p.Descendants<A.Text>().Select(t => t.Text))).ToList();
            Assert.Contains("My deck", texts);
            Assert.Contains("A subtitle", texts);
            Assert.Contains("Ann, Bob", texts);
            Assert.Contains("9 July 2026", texts);
            var layout = ((SlidePart)package.PresentationPart!.GetPartById(package.PresentationPart.Presentation!.SlideIdList!.Elements<P.SlideId>().First().RelationshipId!.Value!)).SlideLayoutPart!;
            Assert.Equal(P.SlideLayoutValues.Title, layout.SlideLayout!.Type!.Value);
        }

        Assert.Equal(["Chapter"], Titles(await ExportAsync(doc, null, (PptxExporter.OptTitleSlide, "no"))));
        var themed = BuiltInThemes.Clean();
        Assert.False(themed.Cover.Enabled);
        Assert.Equal(["Chapter"], Titles(await ExportAsync(doc, themed, (PptxExporter.OptTitleSlide, "theme"))));
    }

    [Fact]
    public async Task Content_before_the_first_heading_gets_an_overview_slide()
    {
        var doc = Doc(new ParagraphBlock("Intro text"), new HeadingBlock(1, "Next"), new ParagraphBlock("more"));
        Assert.Equal(["Overview", "Next"], Titles(await ExportAsync(doc, null, (PptxExporter.OptTitleSlide, "no"))));
        doc.Metadata.Title = "Named";
        Assert.Equal(["Named", "Named", "Next"], Titles(await ExportAsync(doc)));
    }

    [Theory]
    [InlineData("16:9", 12192000)]
    [InlineData("4:3", 9144000)]
    public async Task Aspect_sets_the_slide_size(string aspect, int width)
    {
        var bytes = await ExportAsync(Doc(new HeadingBlock(1, "x"), new ParagraphBlock("y")), null, (PptxExporter.OptAspect, aspect));
        Assert.Empty(Validate(bytes));
        using var package = PresentationDocument.Open(new MemoryStream(bytes), false);
        var size = package.PresentationPart!.Presentation!.SlideSize!;
        Assert.Equal(width, size.Cx!.Value);
        Assert.Equal(6858000, size.Cy!.Value);
    }

    [Fact]
    public async Task Long_sections_continue_on_further_slides()
    {
        var blocks = new List<Block> { new HeadingBlock(1, "Long") };
        for (var i = 0; i < 30; i++)
        {
            blocks.Add(new ParagraphBlock($"Paragraph {i} " + string.Concat(Enumerable.Repeat("word ", 30))));
        }

        var list = new ListBlock { Ordered = true };
        for (var i = 0; i < 20; i++)
        {
            list.Items.Add(new ListItem($"Item {i}"));
        }

        blocks.Add(list);
        blocks.Add(new CodeBlock { Code = string.Join('\n', Enumerable.Range(0, 40).Select(i => $"line {i};")) });
        var doc = Doc([.. blocks]);

        var bytes = await ExportAsync(doc, null, (PptxExporter.OptTitleSlide, "no"));
        Assert.Empty(Validate(bytes));
        var titles = Titles(bytes);
        Assert.Equal("Long", titles[0]);
        Assert.True(titles.Count >= 8, $"Expected continuation slides, got {titles.Count}");
        Assert.All(titles.Skip(1), t => Assert.Equal("Long (cont.)", t));

        var fewer = Titles(await ExportAsync(doc, null, (PptxExporter.OptTitleSlide, "no"), (PptxExporter.OptMaxLines, 30)));
        Assert.True(fewer.Count < titles.Count);

        using var package = PresentationDocument.Open(new MemoryStream(bytes), false);
        var autoNums = Slides(package).SelectMany(s => s.Descendants<A.AutoNumberedBullet>()).ToList();
        Assert.Equal(20, autoNums.Count);
        Assert.Contains(autoNums, a => a.StartAt?.Value > 1);
    }

    [Fact]
    public async Task Lists_runs_and_links_are_written_as_drawingml()
    {
        var nested = new ListBlock();
        nested.Items.Add(new ListItem("inner"));
        var item = new ListItem("outer");
        item.Blocks.Add(nested);
        var list = new ListBlock();
        list.Items.Add(item);
        var link = new LinkInline { Url = "https://example.com/" };
        link.Inlines.Add(new TextInline("site"));
        var doc = Doc(
            new HeadingBlock(1, "Runs"),
            Para(new TextInline("a "), new StyledInline(InlineStyle.Bold, "b"), new StyledInline(InlineStyle.Superscript, "2"), new LineBreakInline(), new CodeInline("x()"), link),
            list,
            new QuoteBlock { Attribution = "Someone", Blocks = { new ParagraphBlock("Quoted") } },
            new CalloutBlock { Kind = CalloutKind.Warning, Blocks = { new ParagraphBlock("Careful") } },
            new ThematicBreakBlock(),
            new HtmlBlock { Html = "<b>raw</b>" },
            new MathBlock { Latex = "e=mc^2" });

        var bytes = await ExportAsync(doc, null, (PptxExporter.OptTitleSlide, "no"));
        Assert.Empty(Validate(bytes));
        using var package = PresentationDocument.Open(new MemoryStream(bytes), false);
        var slidePart = (SlidePart)package.PresentationPart!.GetPartById(package.PresentationPart.Presentation!.SlideIdList!.Elements<P.SlideId>().Single().RelationshipId!.Value!);
        var slide = slidePart.Slide!;
        var runs = slide.Descendants<A.Run>().ToList();
        Assert.True(runs.Single(r => r.Text!.Text == "b").RunProperties!.Bold!.Value);
        Assert.Equal(30000, runs.Single(r => r.Text!.Text == "2").RunProperties!.Baseline!.Value);
        Assert.Single(slide.Descendants<A.Break>());
        Assert.Equal("JetBrains Mono", runs.Single(r => r.Text!.Text == "x()").RunProperties!.GetFirstChild<A.LatinFont>()!.Typeface!.Value);
        var linkId = runs.Single(r => r.Text!.Text == "site").RunProperties!.GetFirstChild<A.HyperlinkOnClick>()!.Id!.Value;
        Assert.Equal("https://example.com/", slidePart.HyperlinkRelationships.Single(r => r.Id == linkId).Uri.ToString());

        var outer = slide.Descendants<A.Paragraph>().Single(p => p.InnerText == "outer");
        Assert.NotNull(outer.ParagraphProperties!.GetFirstChild<A.CharacterBullet>());
        var inner = slide.Descendants<A.Paragraph>().Single(p => p.InnerText == "inner");
        Assert.Equal(1, inner.ParagraphProperties!.Level!.Value);
        Assert.Equal(342900 * 2, inner.ParagraphProperties.LeftMargin!.Value);

        Assert.True(runs.Single(r => r.Text!.Text == "Quoted").RunProperties!.Italic!.Value);
        Assert.Contains(runs, r => r.Text!.Text == "— Someone");
        Assert.True(runs.Single(r => r.Text!.Text == "Warning: ").RunProperties!.Bold!.Value);
        Assert.Contains(runs, r => r.Text!.Text == "raw");
        Assert.Contains(runs, r => r.Text!.Text == "e=mc^2");
    }

    [Fact]
    public async Task Tables_with_spans_are_valid_and_split_when_long()
    {
        var table = new TableBlock();
        var header = new TableRow { IsHeader = true };
        header.Cells.AddRange([new TableCell("A"), new TableCell("B"), new TableCell("C")]);
        table.Rows.Add(header);
        var r1 = new TableRow();
        r1.Cells.AddRange([new TableCell("wide") { ColumnSpan = 2 }, new TableCell("tall") { RowSpan = 2 }]);
        table.Rows.Add(r1);
        var r2 = new TableRow();
        r2.Cells.AddRange([new TableCell("x"), new TableCell("y")]);
        table.Rows.Add(r2);

        var bytes = await ExportAsync(Doc(new HeadingBlock(1, "T"), table), null, (PptxExporter.OptTitleSlide, "no"));
        Assert.Empty(Validate(bytes));
        using (var package = PresentationDocument.Open(new MemoryStream(bytes), false))
        {
            var tbl = Slides(package)[0].Descendants<A.Table>().Single();
            Assert.True(tbl.TableProperties!.FirstRow!.Value);
            Assert.Equal(3, tbl.TableGrid!.Elements<A.GridColumn>().Count());
            var rows = tbl.Elements<A.TableRow>().ToList();
            Assert.All(rows, r => Assert.Equal(3, r.Elements<A.TableCell>().Count()));
            var row1 = rows[1].Elements<A.TableCell>().ToList();
            Assert.Equal(2, row1[0].GridSpan!.Value);
            Assert.True(row1[1].HorizontalMerge!.Value);
            Assert.Equal(2, row1[2].RowSpan!.Value);
            Assert.True(rows[2].Elements<A.TableCell>().ElementAt(2).VerticalMerge!.Value);
            Assert.NotNull(rows[0].Elements<A.TableCell>().First().TableCellProperties!.GetFirstChild<A.SolidFill>());
        }

        var big = new TableBlock();
        var h = new TableRow { IsHeader = true };
        h.Cells.AddRange([new TableCell("Key"), new TableCell("Value")]);
        big.Rows.Add(h);
        for (var i = 0; i < 40; i++)
        {
            var row = new TableRow();
            row.Cells.AddRange([new TableCell($"k{i}"), new TableCell($"v{i}")]);
            big.Rows.Add(row);
        }

        var bigBytes = await ExportAsync(Doc(new HeadingBlock(1, "Big"), big), null, (PptxExporter.OptTitleSlide, "no"), (PptxExporter.OptMaxLines, 30));
        Assert.Empty(Validate(bigBytes));
        using var bigPackage = PresentationDocument.Open(new MemoryStream(bigBytes), false);
        var tables = Slides(bigPackage).SelectMany(s => s.Descendants<A.Table>()).ToList();
        Assert.True(tables.Count >= 3);
        Assert.All(tables, t =>
        {
            Assert.True(t.Elements<A.TableRow>().Count() <= 15);
            Assert.Equal("Key", t.Elements<A.TableRow>().First().Descendants<A.Text>().First().Text);
        });
        Assert.Equal(41 + tables.Count - 1, tables.Sum(t => t.Elements<A.TableRow>().Count()));
    }

    [Fact]
    public async Task Images_get_their_own_slide_and_are_embedded()
    {
        var doc = new Document();
        var res = doc.AddResource("image/png", DocxTestHelpers.TinyPng(40, 20), "red.png");
        doc.Blocks.Add(new HeadingBlock(1, "Pictures"));
        doc.Blocks.Add(new ParagraphBlock("Some text first"));
        doc.Blocks.Add(new ImageBlock { Source = res.Reference, AltText = "Red", Caption = "A red picture", WidthPercent = 50 });
        doc.Blocks.Add(new ParagraphBlock("After the picture"));
        doc.Blocks.Add(new ImageBlock { Source = "https://example.com/x.png" });
        var svg = doc.AddResource("image/svg+xml", "<svg/>"u8.ToArray());
        doc.Blocks.Add(new ImageBlock { Source = svg.Reference });

        var (bytes, warnings) = await ExportWithWarningsAsync(doc, null, (PptxExporter.OptTitleSlide, "no"));
        Assert.Empty(Validate(bytes));
        Assert.Contains(warnings, w => w.Contains("not embedded", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("image/svg+xml", StringComparison.Ordinal));

        using var package = PresentationDocument.Open(new MemoryStream(bytes), false);
        var slides = Slides(package);
        Assert.Equal(["Pictures", "Pictures (cont.)", "Pictures (cont.)"], slides.Select(SlideTitle));
        var picSlide = slides[1];
        var pic = picSlide.Descendants<P.Picture>().Single();
        Assert.Equal("Red", pic.NonVisualPictureProperties!.NonVisualDrawingProperties!.Description!.Value);
        var ext = pic.ShapeProperties!.Transform2D!.Extents!;
        Assert.InRange(ext.Cx!.Value, (12192000 - 2 * 457200) / 2 - 10, (12192000 - 2 * 457200) / 2 + 10);
        Assert.InRange(ext.Cy!.Value, ext.Cx.Value / 2 - 10, ext.Cx.Value / 2 + 10);
        Assert.Contains(picSlide.Descendants<A.Text>(), t => t.Text == "A red picture");
        Assert.Contains(slides[2].Descendants<A.Text>(), t => t.Text == "After the picture");
        var slidePart = package.PresentationPart!.SlideParts.Single(s => s.Slide == picSlide);
        Assert.Equal("image/png", slidePart.ImageParts.Single().ContentType);
    }

    [Fact]
    public async Task Metadata_is_written_when_on()
    {
        var doc = Doc(new HeadingBlock(1, "Head"), new ParagraphBlock("x"));
        doc.Metadata.Title = "Meta title";
        doc.Metadata.Authors.Add("Ann");
        doc.Metadata.Keywords.AddRange(["a", "b"]);
        doc.Metadata.Subtitle = "Sub";

        var bytes = await ExportAsync(doc);
        using (var package = PresentationDocument.Open(new MemoryStream(bytes), false))
        {
            Assert.Equal("Meta title", package.PackageProperties.Title);
            Assert.Equal("Ann", package.PackageProperties.Creator);
            Assert.Equal("a, b", package.PackageProperties.Keywords);
            Assert.Equal("Sub", package.PackageProperties.Subject);
        }

        var off = await ExportAsync(doc, null, (PptxExporter.OptMetadata, false));
        using var offPackage = PresentationDocument.Open(new MemoryStream(off), false);
        Assert.Null(offPackage.PackageProperties.Title);
    }

    [Fact]
    public async Task Empty_document_still_produces_one_slide()
    {
        var bytes = await ExportAsync(new Document(), null, (PptxExporter.OptTitleSlide, "no"));
        Assert.Empty(Validate(bytes));
        Assert.Equal(["deck"], Titles(bytes));
    }

    [Fact]
    public void Exporter_declares_its_options()
    {
        var exporter = new PptxExporter();
        Assert.True(exporter.SupportsTheme);
        Assert.Equal("pptx", exporter.Format.Id);
        var keys = exporter.Options.Select(o => o.Key).ToList();
        Assert.Equal([PdfExporter.OptTheme, PptxExporter.OptAspect, PptxExporter.OptSplitOn, PptxExporter.OptTitleSlide, PptxExporter.OptMaxLines, PptxExporter.OptMetadata], keys);
        Assert.Equal(12, exporter.Options.Single(o => o.Key == PptxExporter.OptMaxLines).DefaultValue);
    }
}
