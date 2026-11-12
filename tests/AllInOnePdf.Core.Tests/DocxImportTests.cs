using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using static AllInOnePdf.Core.Tests.DocxTestHelpers;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using WFootnote = DocumentFormat.OpenXml.Wordprocessing.Footnote;
using WTable = DocumentFormat.OpenXml.Wordprocessing.Table;
using WTableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;
using WTableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;

namespace AllInOnePdf.Core.Tests;

/// <summary>Import of Word files not written by the exporter: a document built here with Open XML, plus damaged input.</summary>
public class DocxImportTests
{
    [Fact]
    public void Declares_the_documented_options()
    {
        var importer = new DocxImporter();
        Assert.Equal("docx", importer.Format.Id);
        foreach (var key in new[] { "detectCode", "pageBreaks", "extractImages", "includeHeadersFooters", "footnotes", "skipToc", "maxImageMb" })
        {
            Assert.Contains(importer.Options, o => o.Key == key);
        }
    }

    [Fact]
    public async Task Hand_built_document_imports_every_construct()
    {
        var warnings = new List<string>();
        var doc = await ImportAsync(BuildDocx(), warnings: warnings);

        // The visible Title/Subtitle paragraphs win over the core properties; authors and keywords come from the properties.
        Assert.Equal("Visible title", doc.Metadata.Title);
        Assert.Equal("Paragraph subtitle", doc.Metadata.Subtitle);
        Assert.Equal(["Jane Doe", "John Roe"], doc.Metadata.Authors);
        Assert.Equal(["alpha", "beta"], doc.Metadata.Keywords);

        var dump = MarkdownExportTestHelpers.Dump(doc);
        var blocks = doc.Blocks;

        // Title style → H1 (+ metadata title kept from the core properties), headings by id and by localised outline level.
        Assert.Equal("Visible title", Assert.IsType<HeadingBlock>(blocks[0]).ToPlainText());
        Assert.Contains("H2 Localised heading", dump);
        Assert.Contains("H3 Third", dump);
        Assert.DoesNotContain("Contents entry", dump);

        Assert.Contains("P Plain {Bold}bold ", dump, StringComparison.Ordinal);
        Assert.Contains("{Italic}italic", dump);
        Assert.Contains("{Underline}under", dump);
        Assert.Contains("{Strikethrough}strike", dump);
        Assert.Contains("{Superscript}up", dump);
        Assert.Contains("{Subscript}down", dump);
        Assert.Contains("{Highlight}marked", dump);
        Assert.Contains("{Bold}strong style", dump);
        Assert.Contains("<code:mono()>", dump);
        Assert.Contains("<br:>", dump);
        Assert.Contains("<link:https://example.com/>site</link:>", dump);
        Assert.Contains("<link:https://field.example/>field link</link:>", dump);
        Assert.Contains("<link:#localised-heading>to heading</link:>", dump);
        Assert.Contains("<fn:1> end", dump);
        Assert.Contains("<fn:1>", dump);

        var footnote = Assert.Single(doc.Footnotes);
        Assert.Equal("1", footnote.Label);
        Assert.Equal("The footnote text.", Assert.IsType<ParagraphBlock>(footnote.Blocks.Single()).ToPlainText());

        // Lists from numbering definitions: nested bullets, an ordered list starting at 3, and bullet text.
        var bullets = blocks.OfType<ListBlock>().First();
        Assert.False(bullets.Ordered);
        Assert.Equal(2, bullets.Items.Count);
        var nested = Assert.IsType<ListBlock>(bullets.Items[0].Blocks[1]);
        Assert.Equal("child", Assert.IsType<ParagraphBlock>(nested.Items[0].Blocks[0]).ToPlainText());
        var ordered = blocks.OfType<ListBlock>().Single(l => l.Ordered);
        Assert.Equal(3, ordered.Start);
        Assert.Equal(2, ordered.Items.Count);
        var textBullets = blocks.OfType<ListBlock>().Last();
        Assert.Equal("typed bullet", textBullets.Items[0].Blocks.OfType<ParagraphBlock>().Single().ToPlainText());
        Assert.True(textBullets.Items[1].Checked);

        // Table: header row, gridSpan, vMerge, column alignment, caption before the table.
        var table = blocks.OfType<TableBlock>().Single();
        Assert.Equal("Table 1: Results", table.Caption);
        Assert.True(table.Rows[0].IsHeader);
        Assert.False(table.Rows[1].IsHeader);
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal(2, table.Rows[1].Cells[0].ColumnSpan);
        Assert.Equal(2, table.Rows[1].Cells[1].RowSpan);
        Assert.Equal(2, table.Rows[2].Cells.Count);
        Assert.Equal(ColumnAlignment.Right, table.GetAlignment(2));

        // Image with alt text and a following caption; the tiny one stays inline.
        var image = blocks.OfType<ImageBlock>().Single();
        Assert.Equal("A red box", image.AltText);
        Assert.Equal("Figure 1: Red", image.Title);
        Assert.Equal(100, image.PixelWidth);
        Assert.Equal(50, image.PixelHeight);
        Assert.Single(doc.Resources); // the inline dot reuses the same image part; the EMF is left out
        Assert.Equal("image/png", doc.FindResource(image.Source)!.MediaType);
        Assert.Contains("<img:res:", dump);

        // Consecutive monospace paragraphs merge into one code block with indentation kept.
        var code = blocks.OfType<CodeBlock>().Single();
        Assert.Equal("int x = 1;\n    return x;", code.Code);

        var quote = blocks.OfType<QuoteBlock>().Single();
        Assert.Equal(2, quote.Blocks.Count);
        Assert.Contains(blocks, b => b is PageBreakBlock);
        Assert.Equal(Documents.TextAlignment.Center, blocks.OfType<ParagraphBlock>().Single(p => p.ToPlainText() == "Centred").Alignment);
        Assert.Contains("EMF", string.Join(" ", warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Options_turn_features_off()
    {
        var doc = await ImportAsync(BuildDocx(), o => o
            .Set(DocxImporter.OptDetectCode, false)
            .Set(DocxImporter.OptPageBreaks, "none")
            .Set(DocxImporter.OptExtractImages, false)
            .Set(DocxImporter.OptFootnotes, false)
            .Set(DocxImporter.OptSkipToc, false)
            .Set(DocxImporter.OptIncludeHeadersFooters, true));

        Assert.DoesNotContain(doc.Blocks, b => b is CodeBlock or PageBreakBlock or ImageBlock);
        Assert.Empty(doc.Resources);
        Assert.Empty(doc.Footnotes);
        var dump = MarkdownExportTestHelpers.Dump(doc);
        Assert.Contains("Contents entry", dump);
        Assert.StartsWith("P Running header", dump.Split('\n').First(l => l.StartsWith("P ", StringComparison.Ordinal) || l.StartsWith('H')),StringComparison.Ordinal);
    }

    [Fact]
    public async Task Password_protected_file_gives_a_clear_error()
    {
        var bytes = new byte[2048];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(bytes));
        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not a zip at all")]
    [InlineData("PK\u0003\u0004 broken zip")]
    public async Task Corrupt_file_gives_a_clear_error(string content)
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(System.Text.Encoding.ASCII.GetBytes(content)));
        Assert.Contains("Word document", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zip_without_a_document_part_gives_a_clear_error()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("hello.txt").Open());
            w.Write("hi");
        }

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(ms.ToArray()));
        Assert.Contains("Word document", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var importer = new DocxImporter();
        using var stream = new MemoryStream(BuildDocx());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportAsync(new Conversion.ImportContext { Input = stream, FileName = "x.docx" }, cts.Token));
    }

    // ----------------------------------------------------------------- hand-built document

    private static Run R(string text, RunProperties? props = null)
    {
        var run = new Run();
        if (props is not null)
        {
            run.AppendChild(props);
        }

        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    private static Paragraph P(string? style, params OpenXmlElement[] content)
    {
        var p = new Paragraph();
        if (style is not null)
        {
            p.AppendChild(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        }

        p.Append(content);
        return p;
    }

    private static Paragraph ListP(int numId, int level, string text) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = "ListParagraph" }, new NumberingProperties(new NumberingLevelReference { Val = level }, new NumberingId { Val = numId })), R(text));

    private static Style ParaStyle(string id, string name, int? outline = null, string? font = null)
    {
        var style = new Style(new StyleName { Val = name }) { Type = StyleValues.Paragraph, StyleId = id };
        if (outline is not null)
        {
            style.AppendChild(new StyleParagraphProperties(new OutlineLevel { Val = outline }));
        }

        if (font is not null)
        {
            style.AppendChild(new StyleRunProperties(new RunFonts { Ascii = font, HighAnsi = font }));
        }

        return style;
    }

    private static Level Lvl(int index, NumberFormatValues format, string text, int start = 1) => new(
        new StartNumberingValue { Val = start },
        new NumberingFormat { Val = format },
        new LevelText { Val = text },
        new PreviousParagraphProperties(new Indentation { Left = (720 * (index + 1)).ToString(), Hanging = "360" }))
    { LevelIndex = index };

    internal static byte[] BuildDocx()
    {
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = package.AddMainDocumentPart();
            main.AddNewPart<StyleDefinitionsPart>().Styles = new Styles(
                ParaStyle("Normal", "Normal"),
                ParaStyle("Title", "Title"),
                ParaStyle("Subtitle", "Subtitle"),
                ParaStyle("berschrift2", "Überschrift 2", outline: 1),
                ParaStyle("Heading3", "heading 3"),
                ParaStyle("TOC1", "toc 1"),
                ParaStyle("Quote", "Quote"),
                ParaStyle("Caption", "caption"),
                ParaStyle("ListParagraph", "List Paragraph"),
                new Style(new StyleName { Val = "Strong" }, new StyleRunProperties(new Bold())) { Type = StyleValues.Character, StyleId = "Strong" },
                new Style(new StyleName { Val = "Table Grid" }) { Type = StyleValues.Table, StyleId = "TableGrid" });

            main.AddNewPart<NumberingDefinitionsPart>().Numbering = new Numbering(
                new AbstractNum(Lvl(0, NumberFormatValues.Bullet, "•"), Lvl(1, NumberFormatValues.Bullet, "o")) { AbstractNumberId = 0 },
                new AbstractNum(Lvl(0, NumberFormatValues.Decimal, "%1.", start: 3)) { AbstractNumberId = 1 },
                new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 },
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 2 });

            var footnotes = main.AddNewPart<FootnotesPart>();
            footnotes.Footnotes = new Footnotes(
                new WFootnote(new Paragraph(new Run(new SeparatorMark()))) { Type = FootnoteEndnoteValues.Separator, Id = -1 },
                new WFootnote(new Paragraph(new Run(new ContinuationSeparatorMark()))) { Type = FootnoteEndnoteValues.ContinuationSeparator, Id = 0 },
                new WFootnote(new Paragraph(new Run(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }), new FootnoteReferenceMark()), R(" The footnote text."))) { Id = 1 });

            var image = main.AddImagePart(ImagePartType.Png);
            image.FeedData(new MemoryStream(TinyPng(100, 50)));
            var imageId = main.GetIdOfPart(image);
            var emf = main.AddNewPart<ImagePart>("image/x-emf", "rIdEmf");
            emf.FeedData(new MemoryStream([1, 0, 0, 0, 0, 0, 0, 0]));
            var link = main.AddHyperlinkRelationship(new Uri("https://example.com/"), true);

            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(P(null, R("Running header")));

            var body = new Body(
                P("Title", R("Visible title")),
                P("Subtitle", R("Paragraph subtitle")),
                P("TOC1", new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }), new Run(new FieldCode(" TOC \\o \"1-3\" \\h ")), new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }), R("Contents entry")),
                P("TOC1", R("Contents entry two"), new Run(new FieldChar { FieldCharType = FieldCharValues.End })),
                P("berschrift2", new BookmarkStart { Name = "_Toc1", Id = "1" }, R("Localised heading"), new BookmarkEnd { Id = "1" }),
                P("Heading3", R("Third")),
                P(null,
                    R("Plain "),
                    R("bold", new RunProperties(new Bold())),
                    R(" "),
                    R("italic", new RunProperties(new Italic())),
                    R(" "),
                    R("under", new RunProperties(new Underline { Val = UnderlineValues.Single })),
                    R(" "),
                    R("strike", new RunProperties(new Strike())),
                    R(" "),
                    R("up", new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript })),
                    R(" "),
                    R("down", new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Subscript })),
                    R(" "),
                    R("marked", new RunProperties(new Highlight { Val = HighlightColorValues.Yellow })),
                    R(" "),
                    R("strong style", new RunProperties(new RunStyle { Val = "Strong" })),
                    R(" "),
                    R("mono()", new RunProperties(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" })),
                    new Run(new Break()),
                    new Hyperlink(R("site", new RunProperties(new RunStyle { Val = "Hyperlink" }))) { Id = link.Id },
                    R(" "),
                    new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
                    new Run(new FieldCode(" HYPERLINK \"https://field.example/\" ")),
                    new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
                    R("field link"),
                    new Run(new FieldChar { FieldCharType = FieldCharValues.End }),
                    R(" "),
                    new Hyperlink(R("to heading")) { Anchor = "_Toc1" },
                    R(" note"),
                    new Run(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }), new FootnoteReference { Id = 1 }),
                    new Run(new TabChar()),
                    R("end")),
                ListP(1, 0, "parent"),
                ListP(1, 1, "child"),
                ListP(1, 0, "second"),
                ListP(2, 0, "three"),
                ListP(2, 0, "four"),
                P("ListParagraph", R("• typed bullet")),
                P("ListParagraph", R("☑ done item")),
                P("Caption", R("Table 1: Results")),
                new WTable(
                    new TableProperties(new TableStyle { Val = "TableGrid" }, new TableLook { Val = "04A0", FirstRow = true }),
                    new TableGrid(new GridColumn { Width = "2000" }, new GridColumn { Width = "2000" }, new GridColumn { Width = "2000" }),
                    new WTableRow(
                        new TableRowProperties(new TableHeader()),
                        new WTableCell(P(null, R("H1"))), new WTableCell(P(null, R("H2"))), new WTableCell(new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Right }), R("H3")))),
                    new WTableRow(
                        new WTableCell(new TableCellProperties(new GridSpan { Val = 2 }), P(null, R("wide"))),
                        new WTableCell(new TableCellProperties(new VerticalMerge { Val = MergedCellValues.Restart }), new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Right }), R("tall")))),
                    new WTableRow(
                        new WTableCell(P(null, R("a"))), new WTableCell(P(null, R("b"))), new WTableCell(new TableCellProperties(new VerticalMerge()), new Paragraph()))),
                new Paragraph(new Run(Picture(imageId, 100, 50, "A red box", 10))),
                P("Caption", R("Figure 1: Red")),
                new Paragraph(R("Inline "), new Run(Picture(imageId, 8, 8, "dot", 11)), R(" picture")),
                new Paragraph(new Run(Picture("rIdEmf", 100, 50, "vector", 12))),
                new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Normal" }), R("int x = 1;", new RunProperties(new RunFonts { Ascii = "Consolas" }))),
                new Paragraph(R("    return x;", new RunProperties(new RunFonts { Ascii = "Courier New" }))),
                P("Quote", R("First quoted")),
                P("Quote", R("Second quoted")),
                new Paragraph(new Run(new Break { Type = BreakValues.Page })),
                new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Center }), R("Centred")),
                new SectionProperties(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) }));
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(body);

            package.PackageProperties.Title = "Core title";
            package.PackageProperties.Creator = "Jane Doe; John Roe";
            package.PackageProperties.Keywords = "alpha, beta";
            package.PackageProperties.Subject = "Sub title";
        }

        return ms.ToArray();
    }

    private static Drawing Picture(string relId, int widthPx, int heightPx, string alt, uint id)
    {
        long cx = widthPx * 9525L, cy = heightPx * 9525L;
        return new Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.DocProperties { Id = id, Name = "Picture " + id, Description = alt },
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 0, Name = "p.png" }, new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }), new A.PresetGeometry { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));
    }
}
