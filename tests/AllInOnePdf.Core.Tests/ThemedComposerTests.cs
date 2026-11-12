using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Highlighting;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Layout.Fluent;
using UglyToad.PdfPig;
using Document = AllInOnePdf.Core.Documents.Document;
using QDocument = AllInOnePdf.Layout.Fluent.Document;

namespace AllInOnePdf.Core.Tests;

/// <summary>The themed drawing facade used outside <see cref="PdfRenderer"/> (publications).</summary>
public class ThemedComposerTests
{
    private static string AllText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join(" ", Enumerable.Range(1, doc.NumberOfPages).Select(n => string.Join(" ", doc.GetPage(n).GetWords().Select(w => w.Text))));
    }

    private static int PageCount(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.NumberOfPages;
    }

    [Fact]
    public void Composes_blocks_header_and_contents_into_a_standalone_document()
    {
        var theme = BuiltInThemes.Clean();
        theme.Header.Enabled = true;
        theme.Header.Left = "{title}";
        theme.Header.Right = "{section}";
        theme.Headings.NumberHeadings = true;
        var context = new Document();
        context.Metadata.Title = "Composer test";

        var blocks = MarkdownImporter.Parse("""
            # Alpha chapter

            Some **bold** text with `code` and a [link](https://example.com).

            ```csharp
            var thisIsAVeryLongLineOfCodeThatWouldNeedShrinkingInANarrowColumn = Enumerable.Range(0, 100).Select(i => i * 2).ToList();
            ```

            > [!TIP]
            > A callout.

            # Beta chapter

            | A | B |
            |---|---|
            | 1 | 2 |
            """).Blocks;
        var headings = blocks.OfType<HeadingBlock>().ToList();

        var composer = new ThemedComposer(theme, context, new SyntaxHighlighter());
        composer.RegisterHeadings(headings);
        var columnWidth = (composer.ContentWidthPt - 16) / 2;

        var pdf = QDocument.Create(container => container.Page(page =>
        {
            composer.ConfigurePage(page);
            page.Header().Element(composer.ComposeHeader);
            page.Content().Column(col =>
            {
                composer.ComposeToc(col.Item(), headings, "In this test");
                col.Item().Row(row =>
                {
                    row.Spacing(16);
                    row.RelativeItem().Element(c => composer.ComposeBlocks(c, blocks, columnWidth));
                    row.RelativeItem().Text(t => composer.ComposeInlines(t, [new TextInline("Side column")], uppercase: true));
                });
            });
        })).GeneratePdf();

        Assert.True(PageCount(pdf) >= 1);
        var text = AllText(pdf);
        Assert.Contains("In this test", text);
        Assert.Contains("Composer test", text);
        Assert.Contains("Alpha", text);
        Assert.Contains("Beta", text);
        Assert.Contains("SIDE", text);
        Assert.Equal("Composer test", composer.Title);
        Assert.True(composer.BodyHeightPt > 0 && composer.BodyHeightPt < 900);
        // Numbering is assigned at registration: "1" and "2" for the two H1s.
        Assert.Contains("2 Beta", text.Replace("  ", " ", StringComparison.Ordinal));
    }

    [Fact]
    public void Unregistered_headings_still_draw()
    {
        var theme = BuiltInThemes.Clean();
        var composer = new ThemedComposer(theme, new Document());
        var heading = new HeadingBlock { Level = 2 };
        heading.Inlines.Add(new TextInline("Loose heading"));

        var pdf = QDocument.Create(container => container.Page(page =>
        {
            composer.ConfigurePage(page);
            page.Content().Column(col =>
            {
                composer.ComposeToc(col.Item(), [heading]);
                composer.ComposeBlocks(col.Item(), [heading, new ParagraphBlock("Body")]);
            });
        })).GeneratePdf();

        Assert.Contains("Loose heading", AllText(pdf));
        Assert.Equal("Document", composer.Title);
    }

    [Fact]
    public void ThemeDrawing_helpers_read_theme_values()
    {
        Assert.True(ThemeDrawing.IsDark("#111111"));
        Assert.False(ThemeDrawing.IsDark("#FFFFFF"));
        Assert.False(ThemeDrawing.IsDark("not a colour"));
        Assert.Equal(AllInOnePdf.Layout.Infrastructure.Color.FromHex("#123456"), ThemeDrawing.Hex("bogus", "#123456"));
        Assert.Equal(["Lora", Fonts.BundledFonts.Sans], ThemeDrawing.FontChain("Lora"));
        Assert.Single(ThemeDrawing.FontChain(Fonts.BundledFonts.Sans));
    }

    [Fact]
    public void Draft_lowers_image_settings()
    {
        var draft = new ThemedComposer(BuiltInThemes.Clean(), new Document(), options: new ThemedComposerOptions { Draft = true });
        var full = new ThemedComposer(BuiltInThemes.Clean(), new Document());

        Assert.Equal(96, draft.CreateDocumentSettings().ImageRasterDpi);
        Assert.Equal(220, full.CreateDocumentSettings().ImageRasterDpi);
    }
}

public class MarkdownParseTests
{
    [Fact]
    public void Parses_every_common_block_and_inline_formatting()
    {
        var doc = MarkdownImporter.Parse("""
            # Title

            ## Section

            Text with **bold**, *italic*, `code` and [a link](https://example.com).

            - one
            - two

            1. first
            2. second

            | A | B |
            |---|--:|
            | 1 | 2 |

            ```python
            print("hi")
            ```

            > [!WARNING]
            > Careful.
            """);

        var blocks = doc.Blocks;
        Assert.Equal([1, 2], blocks.OfType<HeadingBlock>().Select(h => h.Level));
        var para = Assert.Single(blocks.OfType<ParagraphBlock>());
        Assert.Contains(para.Inlines, i => i is StyledInline { Style: InlineStyle.Bold });
        Assert.Contains(para.Inlines, i => i is StyledInline { Style: InlineStyle.Italic });
        Assert.Contains(para.Inlines, i => i is CodeInline);
        Assert.Contains(para.Inlines, i => i is LinkInline { Url: "https://example.com" });
        var lists = blocks.OfType<ListBlock>().ToList();
        Assert.Equal(2, lists.Count);
        Assert.False(lists[0].Ordered);
        Assert.True(lists[1].Ordered);
        var table = Assert.Single(blocks.OfType<TableBlock>());
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(ColumnAlignment.Right, table.GetAlignment(1));
        var code = Assert.Single(blocks.OfType<CodeBlock>());
        Assert.Equal("python", code.Language);
        var callout = Assert.Single(blocks.OfType<CalloutBlock>());
        Assert.Equal(CalloutKind.Warning, callout.Kind);
        Assert.Null(doc.Metadata.SourceFormat);
        Assert.Null(doc.Metadata.SourcePath);
        Assert.Null(doc.Metadata.Title);
    }

    [Fact]
    public void Ignores_front_matter_by_default_and_reads_it_on_request()
    {
        const string markdown = """
            ---
            title: From YAML
            author: Ada
            ---
            # Heading

            Body.
            """;

        var plain = MarkdownImporter.Parse(markdown);
        Assert.Null(plain.Metadata.Title);
        Assert.Empty(plain.Metadata.Authors);
        Assert.IsType<HeadingBlock>(plain.Blocks[0]);
        Assert.DoesNotContain(plain.Blocks, b => b is ParagraphBlock p && p.ToPlainText().Contains("title:", StringComparison.Ordinal));

        var withYaml = MarkdownImporter.Parse(markdown, new MarkdownParseOptions { FrontMatter = true });
        Assert.Equal("From YAML", withYaml.Metadata.Title);
        Assert.Equal(["Ada"], withYaml.Metadata.Authors);

        var titled = MarkdownImporter.Parse(markdown, new MarkdownParseOptions { TitleFromHeading = true });
        Assert.Equal("Heading", titled.Metadata.Title);
    }

    [Fact]
    public void Never_reads_local_images_and_honours_parse_switches()
    {
        // A real file on an absolute path: ImportAsync would embed it, Parse must leave the reference alone.
        var path = Path.Combine(Path.GetTempPath(), "aio-parse-" + Guid.NewGuid().ToString("N") + ".png").Replace('\\', '/');
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0]);
        try
        {
            var doc = MarkdownImporter.Parse($"![pic]({path})\n\nline one\nline two \"quoted\"", new MarkdownParseOptions { HardLineBreaks = true, SmartQuotes = true });

            var image = Assert.IsType<ImageBlock>(doc.Blocks[0]);
            Assert.Equal(path, image.Source);
            Assert.Empty(doc.Resources);
            var para = Assert.IsType<ParagraphBlock>(doc.Blocks[1]);
            Assert.Contains(para.Inlines, i => i is LineBreakInline { IsHard: true });
            Assert.Contains("quoted", para.ToPlainText(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_without_options_keeps_soft_breaks_and_straight_quotes()
    {
        var doc = MarkdownImporter.Parse("line one\nline two \"quoted\"");
        var para = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
        Assert.DoesNotContain(para.Inlines, i => i is LineBreakInline { IsHard: true });
        Assert.Contains("\"quoted\"", para.ToPlainText(), StringComparison.Ordinal);
    }
}
