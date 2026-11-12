using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Highlighting;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using UglyToad.PdfPig;

namespace AllInOnePdf.Core.Tests;

/// <summary>Checks that theme settings change the PDF the way the theme builder promises.</summary>
public class RenderingFidelityTests
{
    private static readonly PdfRenderer Renderer = new(new SyntaxHighlighter());

    private static PdfTheme Plain()
    {
        var t = BuiltInThemes.Clean();
        t.Footer.Enabled = false;
        t.Header.Enabled = false;
        t.Cover.Enabled = false;
        t.Toc.Enabled = false;
        return t;
    }

    private static List<string> PageTexts(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return Enumerable.Range(1, doc.NumberOfPages).Select(n => string.Join(" ", doc.GetPage(n).GetWords().Select(w => w.Text))).ToList();
    }

    private static string Filler(int words) =>
        string.Join(" ", Enumerable.Range(0, words).Select(i => (i % 7) switch { 0 => "lorem", 1 => "ipsum", 2 => "dolor", 3 => "sit", 4 => "amet", 5 => "consectetur", _ => "elit" }));

    private static string? FindSampleFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "sample-document.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    [Fact]
    public async Task Sample_document_renders_with_every_built_in_theme()
    {
        var path = FindSampleFile();
        Assert.NotNull(path);
        var importer = new MarkdownImporter();
        foreach (var theme in BuiltInThemes.All())
        {
            await using var fs = File.OpenRead(path!);
            var doc = await importer.ImportAsync(new ImportContext { Input = fs, FileName = "sample-document.md", SourcePath = path, Options = OptionValues.FromDefaults(importer.Options) });
            var pdf = await Renderer.RenderAsync(doc, theme);
            var pages = PageTexts(pdf);
            Assert.True(pages.Count >= 2, theme.Name);
            Assert.Contains(pages, p => p.Contains("Bringhurst", StringComparison.Ordinal));
            Assert.Contains(pages, p => p.Contains("modular_scale", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Cover_and_toc_with_dot_leaders_come_first()
    {
        var doc = await SampleDocuments.CreateWelcomeAsync();
        var theme = Plain();
        theme.Cover.Enabled = true;
        theme.Toc.Enabled = true;
        theme.Toc.DotLeaders = true;

        var pages = PageTexts(await Renderer.RenderAsync(doc, theme));

        Assert.True(pages.Count >= 3);
        Assert.Contains("Designing a Theme", pages[0], StringComparison.Ordinal);
        Assert.Contains("Contents", pages[1], StringComparison.Ordinal);
        Assert.Contains("Typography", pages[1], StringComparison.Ordinal);
        Assert.Contains("Callouts", pages[1], StringComparison.Ordinal);
        Assert.Matches(@"Typography\s+\d", pages[1]);

        theme.Toc.DotLeaders = false;
        var withoutLeaders = PageTexts(await Renderer.RenderAsync(doc, theme));
        Assert.Contains("Contents", withoutLeaders[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Headings_are_never_the_last_thing_on_a_page()
    {
        const string heading = "Zephyrheading";
        var orphansWithoutKeep = 0;
        for (var filler = 30; filler < 70; filler++)
        {
            foreach (var keep in new[] { true, false })
            {
                var doc = new Document();
                for (var i = 0; i < filler; i++)
                {
                    doc.Blocks.Add(new ParagraphBlock("Line " + i));
                }

                doc.Blocks.Add(new HeadingBlock(2, heading));
                doc.Blocks.Add(new ParagraphBlock(Filler(400)));
                var theme = Plain();
                theme.Headings.KeepWithNext = keep;
                theme.Typography.OrphanControl = keep;

                var pdf = await Renderer.RenderAsync(doc, theme);
                using var pig = PdfDocument.Open(pdf);
                var orphaned = false;
                for (var n = 1; n <= pig.NumberOfPages; n++)
                {
                    var words = pig.GetPage(n).GetWords().ToList();
                    var h = words.FirstOrDefault(w => w.Text == heading);
                    if (h is null)
                    {
                        continue;
                    }

                    // PDF y grows upwards: body text after the heading has a lower baseline on the same page.
                    orphaned = !words.Any(w => w.BoundingBox.Bottom < h.BoundingBox.Bottom - 2 && (w.Text == "lorem" || w.Text == "ipsum"));
                }

                if (keep)
                {
                    Assert.False(orphaned, $"heading orphaned after {filler} filler lines");
                }
                else if (orphaned)
                {
                    orphansWithoutKeep++;
                }
            }
        }

        // Sanity check of the test itself: without keep-with-next some layouts do orphan the heading.
        Assert.True(orphansWithoutKeep > 0);
    }

    [Fact]
    public async Task Section_token_tracks_the_current_chapter()
    {
        var doc = new Document();
        foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
        {
            doc.Blocks.Add(new HeadingBlock(1, name));
            doc.Blocks.Add(new ParagraphBlock(Filler(name == "Alpha" ? 1100 : 150)));
        }

        var theme = Plain();
        theme.Header.Enabled = true;
        theme.Header.Left = "S:{section}";
        theme.Header.Center = null;
        theme.Headings.PageBreakBeforeH1 = true;

        var pages = PageTexts(await Renderer.RenderAsync(doc, theme));

        Assert.True(pages.Count >= 4, $"expected Alpha to span two pages, got {pages.Count} pages");
        Assert.Contains("S:Alpha", pages[0], StringComparison.Ordinal);
        Assert.Contains("S:Alpha", pages[1], StringComparison.Ordinal);
        Assert.Contains("S:Beta", pages[^2], StringComparison.Ordinal);
        Assert.Contains("S:Gamma", pages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Footer_shows_on_cover_only_when_asked()
    {
        var doc = await SampleDocuments.CreateWelcomeAsync();
        var theme = Plain();
        theme.Cover.Enabled = true;
        theme.Footer.Enabled = true;
        theme.Footer.Center = "Folio{page}";

        theme.Footer.ShowOnCover = false;
        var hidden = PageTexts(await Renderer.RenderAsync(doc, theme));
        theme.Footer.ShowOnCover = true;
        var shown = PageTexts(await Renderer.RenderAsync(doc, theme));

        Assert.DoesNotContain("Folio", hidden[0], StringComparison.Ordinal);
        Assert.Contains("Folio2", hidden[1], StringComparison.Ordinal);
        Assert.Contains("Folio1", shown[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Table_header_repeats_only_when_enabled()
    {
        var doc = new Document();
        var table = new TableBlock();
        var header = new TableRow { IsHeader = true };
        header.Cells.Add(new TableCell("Headcol"));
        header.Cells.Add(new TableCell("Other"));
        table.Rows.Add(header);
        for (var i = 0; i < 120; i++)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell("r" + i));
            row.Cells.Add(new TableCell("v" + i));
            table.Rows.Add(row);
        }

        doc.Blocks.Add(table);
        var theme = Plain();

        theme.Tables.RepeatHeaderOnNewPage = true;
        var repeated = PageTexts(await Renderer.RenderAsync(doc, theme));
        theme.Tables.RepeatHeaderOnNewPage = false;
        var once = PageTexts(await Renderer.RenderAsync(doc, theme));

        Assert.True(repeated.Count >= 2);
        Assert.Contains("Headcol", repeated[1], StringComparison.Ordinal);
        Assert.Contains("Headcol", once[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Headcol", once[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_code_lines_wrap_or_clip()
    {
        var doc = new Document();
        doc.Blocks.Add(new CodeBlock { Code = "var x = \"" + new string('a', 300) + "ENDMARK\";\nshort", Language = "csharp" });
        var theme = Plain();

        theme.Code.WrapLongLines = true;
        var wrapped = PageTexts(await Renderer.RenderAsync(doc, theme));
        theme.Code.WrapLongLines = false;
        var clipped = PageTexts(await Renderer.RenderAsync(doc, theme));

        Assert.Contains("ENDMARK", wrapped[0], StringComparison.Ordinal);
        Assert.DoesNotContain("ENDMARK", clipped[0], StringComparison.Ordinal);
        Assert.Contains("…", clipped[0], StringComparison.Ordinal);
        Assert.Contains("short", clipped[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callout_labels_and_icons_follow_the_theme()
    {
        var doc = new Document();
        var callout = new CalloutBlock { Kind = CalloutKind.Warning };
        callout.Blocks.Add(new ParagraphBlock("Body text"));
        doc.Blocks.Add(callout);
        var theme = Plain();
        theme.Callouts.Warning.Label = "Achtung";
        theme.Callouts.Warning.Accent = "#123456";
        theme.Callouts.Warning.Background = "#FFF4E5";
        theme.Callouts.ShowIcon = true;

        var labelled = PageTexts(await Renderer.RenderAsync(doc, theme));
        theme.Callouts.ShowLabel = false;
        var unlabelled = PageTexts(await Renderer.RenderAsync(doc, theme));

        Assert.Contains("Achtung", labelled[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Achtung", unlabelled[0], StringComparison.Ordinal);
        Assert.Contains("Body", unlabelled[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quote_attribution_is_right_aligned()
    {
        var doc = new Document();
        var quote = new QuoteBlock();
        quote.Blocks.Add(new ParagraphBlock("Less is more."));
        quote.Blocks.Add(new ParagraphBlock("— Mies"));
        doc.Blocks.Add(quote);

        var pdf = await Renderer.RenderAsync(doc, Plain());
        using var pig = PdfDocument.Open(pdf);
        var page = pig.GetPage(1);
        var word = page.GetWords().First(w => w.Text == "Mies");

        Assert.True(word.BoundingBox.Left > page.Width / 2, "attribution should sit at the right");
    }

    [Fact]
    public async Task Hyphenation_and_images_render()
    {
        var doc = new Document();
        doc.Blocks.Add(new ParagraphBlock(string.Concat(Enumerable.Repeat("internationalization extraordinarily characteristically ", 20))));
        doc.Blocks.Add(new ImageBlock { Source = "data:image/png;base64," + OnePixelPng, AltText = "dot" });
        var theme = Plain();
        theme.Typography.Hyphenate = true;
        theme.Typography.Alignment = TextAlign.Justify;
        theme.Images.Border = true;
        theme.Images.CornerRadiusPt = 6;

        var pages = PageTexts(await Renderer.RenderAsync(doc, theme));

        Assert.NotEmpty(pages);
    }

    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    [Fact]
    public void Theme_json_without_new_properties_gets_defaults()
    {
        const string json = """
            {
              "id": "old",
              "name": "Old theme",
              "schemaVersion": 1,
              "typography": { "bodyFont": "Inter", "bodySizePt": 12 },
              "headings": { "color": "#000000", "h1": { "sizePt": 30, "underline": true } },
              "toc": { "enabled": true }
            }
            """;

        var theme = PdfTheme.FromJson(json);

        Assert.Equal(12, theme.Typography.BodySizePt);
        Assert.True(theme.Typography.OrphanControl);
        Assert.True(theme.Headings.KeepWithNext);
        Assert.True(theme.Headings.H1.Underline);
        Assert.Null(theme.Headings.H1.RuleColor);
        Assert.Equal(0.75, theme.Headings.H1.RuleWidthPt);
        Assert.NotNull(theme.Callouts);
        Assert.True(theme.Callouts.ShowLabel);
        Assert.False(theme.Callouts.ShowIcon);
        Assert.Equal(3, theme.Callouts.BorderWidthPt);
        Assert.Null(theme.Callouts.Note.Accent);
        Assert.Equal("#1E8E3E", theme.Callouts.Tip.Accent);
        Assert.Equal("#7C3AED", theme.Callouts.Important.Accent);
        Assert.Equal("#B45309", theme.Callouts.Warning.Accent);
        Assert.Equal("#B91C1C", theme.Callouts.Caution.Accent);
        Assert.True(theme.Toc.DotLeaders);
    }

    [Fact]
    public void New_theme_properties_round_trip()
    {
        var theme = BuiltInThemes.Night();
        theme.Callouts.Tip.Label = "Hint";
        theme.Headings.H2.RuleColor = "#FF0000";

        var back = PdfTheme.FromJson(theme.ToJson());

        Assert.Equal("Hint", back.Callouts.Tip.Label);
        Assert.Equal("#6CCB8A", back.Callouts.Tip.Accent);
        Assert.True(back.Callouts.ShowIcon);
        Assert.Equal("#FF0000", back.Headings.H2.RuleColor);
    }
}
