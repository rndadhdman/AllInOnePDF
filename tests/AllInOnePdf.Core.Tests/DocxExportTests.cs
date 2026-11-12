using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.DependencyInjection;
using static AllInOnePdf.Core.Tests.DocxTestHelpers;
using Document = AllInOnePdf.Core.Documents.Document;
using Footnote = AllInOnePdf.Core.Documents.Footnote;
using ListItem = AllInOnePdf.Core.Documents.ListItem;
using Paragraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using TableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;

namespace AllInOnePdf.Core.Tests;

/// <summary>Document → .docx: valid Open XML, Word styles from the theme, page setup, header/footer, cover and TOC.</summary>
public class DocxExportTests
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

    [Theory]
    [MemberData(nameof(SamplesAndThemes))]
    public async Task Sample_exports_to_valid_docx_with_theme_styles_and_page_setup(string sample, string themeId)
    {
        var theme = BuiltInThemes.Find(themeId)!;
        var doc = await LoadSampleAsync(sample);
        var bytes = await ExportAsync(doc, theme);

        Assert.Empty(Validate(bytes));

        using var package = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var main = package.MainDocumentPart!;
        var styles = main.StyleDefinitionsPart!.Styles!.Elements<Style>().Select(s => s.StyleId!.Value).ToHashSet();
        foreach (var id in new[] { "Normal", "Heading1", "Heading2", "Heading3", "Heading4", "Heading5", "Heading6", "Title", "Subtitle", "Quote", "Code", "CodeChar", "Hyperlink", "Caption", "CalloutTip", "CalloutWarning", "FootnoteText", "AllInOneTable" })
        {
            Assert.Contains(id, styles);
        }

        var normal = main.StyleDefinitionsPart.Styles.Elements<Style>().Single(s => s.StyleId == "Normal");
        Assert.Equal(((int)Math.Round(theme.Typography.BodySizePt * 2)).ToString(), normal.StyleRunProperties!.FontSize!.Val!.Value);
        Assert.Equal(theme.Typography.BodyFont, normal.StyleRunProperties.RunFonts!.Ascii!.Value);

        var sect = main.Document!.Body!.Elements<SectionProperties>().Single();
        var (w, h) = theme.Page.GetSizeMm();
        var size = sect.GetFirstChild<PageSize>()!;
        Assert.InRange((int)size.Width!.Value, (int)(w / 25.4 * 1440) - 2, (int)(w / 25.4 * 1440) + 2);
        Assert.InRange((int)size.Height!.Value, (int)(h / 25.4 * 1440) - 2, (int)(h / 25.4 * 1440) + 2);
        var margin = sect.GetFirstChild<PageMargin>()!;
        Assert.Equal((int)Math.Round(theme.Page.MarginTopMm / 25.4 * 1440), margin.Top!.Value);

        Assert.Equal(theme.Header.HasContent, main.HeaderParts.Any());
        Assert.Equal(theme.Footer.HasContent, main.FooterParts.Any());
        Assert.Equal(theme.Header.HasContent, sect.Elements<HeaderReference>().Any());

        var body = main.Document.Body;
        var hasToc = body.Descendants<FieldCode>().Any(f => f.Text.Contains("TOC \\o", StringComparison.Ordinal));
        Assert.Equal(theme.Toc.Enabled, hasToc);
        Assert.Equal(theme.Toc.Enabled, main.DocumentSettingsPart!.Settings!.Elements<UpdateFieldsOnOpen>().Any());
        Assert.Equal(theme.Cover.Enabled, body.Descendants<Tag>().Any(t => t.Val == "aiop:cover"));

        Assert.True(body.Descendants<Paragraph>().Count(p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value?.StartsWith("Heading", StringComparison.Ordinal) == true) >= 4);
        Assert.NotNull(main.NumberingDefinitionsPart);
        Assert.Equal(doc.Footnotes.Count > 0, main.FootnotesPart is not null);
        Assert.Equal(doc.ResolveTitle(), package.PackageProperties.Title);
        Assert.Equal(string.Join("; ", doc.Metadata.Authors), package.PackageProperties.Creator);
    }

    [Fact]
    public async Task Footer_fields_and_section_header_are_word_fields()
    {
        var bytes = await ExportAsync(await LoadSampleAsync("welcome"), BuiltInThemes.Technical());
        using var package = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var main = package.MainDocumentPart!;

        var footer = string.Join("|", main.FooterParts.SelectMany(f => f.Footer!.Descendants<SimpleField>()).Select(f => f.Instruction!.Value!.Trim()));
        Assert.Contains("PAGE", footer);
        Assert.Contains("NUMPAGES", footer);
        var header = main.HeaderParts.SelectMany(p => p.Header!.Descendants<SimpleField>()).Select(f => f.Instruction!.Value!.Trim()).ToList();
        Assert.Contains("STYLEREF \"Heading 1\"", header);
        Assert.Contains(main.HeaderParts, p => p.Header!.InnerText.Contains("Designing a Theme", StringComparison.Ordinal));

        // Technical numbers its headings through a multilevel list linked to the heading styles.
        var h1 = main.StyleDefinitionsPart!.Styles!.Elements<Style>().Single(s => s.StyleId == "Heading1");
        Assert.NotNull(h1.StyleParagraphProperties!.NumberingProperties);
        Assert.NotNull(h1.StyleParagraphProperties.ParagraphBorders?.BottomBorder);
    }

    [Fact]
    public async Task Options_override_cover_toc_title_and_author()
    {
        var doc = await LoadSampleAsync("welcome");
        var bytes = await ExportAsync(doc, BuiltInThemes.Clean(),
            (DocxExporter.OptCover, "yes"), (DocxExporter.OptToc, "yes"), (DocxExporter.OptTitle, "Custom title"), (DocxExporter.OptAuthor, "Ada, Grace"));
        Assert.Empty(Validate(bytes));
        using var package = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var body = package.MainDocumentPart!.Document!.Body!;
        Assert.Contains(body.Descendants<Tag>(), t => t.Val == "aiop:cover");
        Assert.Contains(body.Descendants<FieldCode>(), f => f.Text.Contains("TOC \\o \"1-3\" \\h \\z \\u", StringComparison.Ordinal));
        Assert.Equal("Custom title", package.PackageProperties.Title);
        Assert.Equal("Ada; Grace", package.PackageProperties.Creator);

        var none = await ExportAsync(await LoadSampleAsync("welcome"), BuiltInThemes.Manuscript(), (DocxExporter.OptCover, "no"), (DocxExporter.OptToc, "no"), (DocxExporter.OptMetadata, false));
        using var plain = WordprocessingDocument.Open(new MemoryStream(none), false);
        Assert.DoesNotContain(plain.MainDocumentPart!.Document!.Body!.Descendants<FieldCode>(), f => f.Text.Contains("TOC", StringComparison.Ordinal));
        Assert.DoesNotContain(plain.MainDocumentPart.Document.Body!.Descendants<Tag>(), t => t.Val == "aiop:cover");
        Assert.Null(plain.PackageProperties.Title);
    }

    [Fact]
    public async Task Code_blocks_get_syntax_colours_and_dark_pages_a_background()
    {
        var bytes = await ExportAsync(await LoadSampleAsync("welcome"), BuiltInThemes.Night());
        using var package = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var main = package.MainDocumentPart!;
        var code = main.Document!.Body!.Descendants<Paragraph>().First(p => p.ParagraphProperties?.ParagraphStyleId?.Val == "Code");
        var colours = code.Descendants<Run>().Select(r => r.RunProperties?.Color?.Val?.Value).Where(c => c is not null).Distinct().ToList();
        Assert.True(colours.Count >= 2, "highlighted code should use several colours");
        Assert.Contains("public static IEnumerable<int> Fibonacci()", code.InnerText.Replace(" ", " "), StringComparison.Ordinal);
        Assert.Equal("1B1E24", main.Document.DocumentBackground!.Color!.Value);
        Assert.NotEmpty(main.DocumentSettingsPart!.Settings!.Elements<DisplayBackgroundShape>());
    }

    [Fact]
    public async Task Constructed_document_with_every_block_type_is_valid()
    {
        var doc = EveryBlockDocument();
        var bytes = await ExportAsync(doc, BuiltInThemes.Technical());
        Assert.Empty(Validate(bytes));

        using var package = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var main = package.MainDocumentPart!;
        Assert.Single(main.ImageParts);
        var extent = main.Document!.Body!.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().First();
        var (contentW, _) = (BuiltInThemes.Technical().Page.GetSizeMm().WidthMm - 40, 0);
        Assert.InRange(extent.Cx!.Value, 1, (long)(contentW / 25.4 * 914400 * 0.5) + 10);
        Assert.Contains(main.Document.Body.Descendants<TableCell>(), c => c.TableCellProperties?.GridSpan?.Val?.Value == 2);
        Assert.Contains(main.Document.Body.Descendants<TableCell>(), c => c.TableCellProperties?.VerticalMerge is not null);
        Assert.Contains(main.Document.Body.Descendants<Hyperlink>(), l => l.Anchor is not null);
        Assert.Contains(main.Document.Body.Descendants<BookmarkStart>(), b => b.Name == "everything");
        Assert.Equal(2, main.FootnotesPart!.Footnotes!.Elements<DocumentFormat.OpenXml.Wordprocessing.Footnote>().Count(f => f.Id!.Value > 0));
    }

    [Fact]
    public void Exporter_is_registered_and_themed()
    {
        var services = new ServiceCollection();
        services.AddAllInOnePdfCore(Path.Combine(Path.GetTempPath(), "aio-docx-tests", Guid.NewGuid().ToString("N")));
        using var provider = services.BuildServiceProvider();
        var exporter = Assert.Single(provider.GetServices<IExporter>(), e => e.Format.Id == "docx");
        Assert.True(exporter.SupportsTheme);
        Assert.Equal(ConverterStatus.Ready, exporter.Status);
        Assert.Single(provider.GetServices<IImporter>(), i => i is DocxImporter);
        foreach (var key in new[] { "theme", "cover", "toc", "embedMetadata", "title", "author" })
        {
            Assert.Contains(exporter.Options, o => o.Key == key);
        }
    }

    internal static Document EveryBlockDocument()
    {
        var doc = new Document();
        doc.Metadata.Title = "Everything";
        doc.Metadata.Subtitle = "All the blocks";
        doc.Metadata.Authors.AddRange(["Ada Lovelace", "Grace Hopper"]);
        doc.Metadata.Keywords.AddRange(["a", "b"]);
        doc.Metadata.Description = "Description text";
        var png = doc.AddResource("image/png", TinyPng(200, 100), "pic.png");

        doc.Blocks.Add(new HeadingBlock(1, "Everything") { Id = "everything" });
        var p = new ParagraphBlock();
        p.Inlines.AddRange(
        [
            new TextInline("Plain "),
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
            new LinkInline { Url = "https://example.com/a", Title = "Tip", Inlines = { new TextInline("link") } },
            new TextInline(" "),
            new LinkInline { Url = "#everything", Inlines = { new TextInline("back") } },
            new LineBreakInline(),
            new TextInline("after break "),
            new MathInline { Latex = "x^2" },
            new TextInline(" "),
            new FootnoteReferenceInline { Label = "1" },
            new TextInline(" and "),
            new FootnoteReferenceInline { Label = "2" },
        ]);
        doc.Blocks.Add(p);
        doc.Blocks.Add(new ParagraphBlock("Centred text") { Alignment = Documents.TextAlignment.Center });

        var list = new ListBlock { Ordered = true, Start = 3 };
        var item = new ListItem("three");
        var nested = new ListBlock();
        nested.Items.Add(new ListItem("nested done") { Checked = true });
        nested.Items.Add(new ListItem("nested open") { Checked = false });
        item.Blocks.Add(nested);
        item.Blocks.Add(new ParagraphBlock("continuation of three"));
        list.Items.Add(item);
        list.Items.Add(new ListItem("four"));
        doc.Blocks.Add(list);
        var bullets = new ListBlock();
        bullets.Items.Add(new ListItem("bullet"));
        doc.Blocks.Add(bullets);

        doc.Blocks.Add(new CodeBlock { Code = "var x = 1;\n\tif (x > 0)\n\t\treturn;", Language = "csharp" });
        doc.Blocks.Add(new CodeBlock { Code = "plain text\n  indented", Caption = "notes.txt" });
        var quote = new QuoteBlock { Attribution = "Someone" };
        quote.Blocks.Add(new ParagraphBlock("quoted"));
        var quoteList = new ListBlock();
        quoteList.Items.Add(new ListItem("in quote"));
        quote.Blocks.Add(quoteList);
        doc.Blocks.Add(quote);
        var callout = new CalloutBlock { Kind = CalloutKind.Warning };
        callout.Blocks.Add(new ParagraphBlock("careful"));
        doc.Blocks.Add(callout);
        var titled = new CalloutBlock { Kind = CalloutKind.Tip, Title = "Pro tip" };
        titled.Blocks.Add(new ParagraphBlock("first"));
        titled.Blocks.Add(new ParagraphBlock("second"));
        doc.Blocks.Add(titled);

        var table = new TableBlock { Caption = "Table caption" };
        table.Alignments.AddRange([ColumnAlignment.Left, ColumnAlignment.Center, ColumnAlignment.Right]);
        var header = new Documents.TableRow { IsHeader = true };
        header.Cells.AddRange([new Documents.TableCell("A"), new Documents.TableCell("B"), new Documents.TableCell("C")]);
        var row1 = new Documents.TableRow();
        row1.Cells.AddRange([new Documents.TableCell("spans two") { ColumnSpan = 2 }, new Documents.TableCell("tall") { RowSpan = 2 }]);
        var row2 = new Documents.TableRow();
        row2.Cells.AddRange([new Documents.TableCell("a"), new Documents.TableCell("b")]);
        table.Rows.AddRange([header, row1, row2]);
        doc.Blocks.Add(table);

        doc.Blocks.Add(new ImageBlock { Source = png.Reference, AltText = "A picture", Title = "A caption", WidthPercent = 50 });
        doc.Blocks.Add(new ThematicBreakBlock());
        doc.Blocks.Add(new PageBreakBlock());
        doc.Blocks.Add(new MathBlock { Latex = @"\frac{1}{2}" });
        doc.Blocks.Add(new HtmlBlock { Html = "<details>Hidden</details>" });
        doc.Blocks.Add(new HeadingBlock(2, "Second level"));
        doc.Blocks.Add(new ParagraphBlock("End."));

        var fn1 = new Footnote { Label = "1" };
        fn1.Blocks.Add(new ParagraphBlock("The first note."));
        var fn2 = new Footnote { Label = "2" };
        fn2.Blocks.Add(new ParagraphBlock("The second note."));
        doc.Footnotes.AddRange([fn1, fn2]);
        return doc;
    }
}
