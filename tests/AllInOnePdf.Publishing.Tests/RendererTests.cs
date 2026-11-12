using System.Text;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Rendering;
using AllInOnePdf.Publishing.Templates;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using UglyToad.PdfPig;

namespace AllInOnePdf.Publishing.Tests;

public class RendererTests
{
    private static readonly PublicationRenderer Renderer = new();

    [Fact]
    public async Task Every_template_renders_pages_of_the_theme_size()
    {
        var rasterizer = new PdfiumRasterizer();
        foreach (var template in BuiltInTemplates.All())
        {
            foreach (var placeholders in new[] { true, false })
            {
                var publication = template.Create();
                var pdf = await Renderer.RenderAsync(publication, new PublicationRenderOptions { ShowPlaceholders = placeholders, Draft = !placeholders });
                Assert.True(rasterizer.GetPageCount(pdf) >= 1, $"{template.Id} has no pages");

                var (wMm, hMm) = publication.Theme.Page.GetSizeMm();
                var page = await rasterizer.RasterizePageAsync(pdf, 0, new RasterizeOptions { Dpi = 72 });
                Assert.InRange(page.Width, (wMm * 72 / 25.4) - 2, (wMm * 72 / 25.4) + 2);
                Assert.InRange(page.Height, (hMm * 72 / 25.4) - 2, (hMm * 72 / 25.4) + 2);
            }
        }
    }

    [Fact]
    public async Task Sample_with_every_module_renders()
    {
        var pdf = await Renderer.RenderAsync(Samples.EveryModule(), new PublicationRenderOptions { ShowPlaceholders = true });
        using var doc = PdfDocument.Open(pdf);
        Assert.True(doc.NumberOfPages >= 2);
        var text = AllText(doc);
        Assert.Contains("Mind the gap", text);
        Assert.Contains("Go", text);
    }

    [Fact]
    public async Task Two_column_row_places_text_side_by_side()
    {
        var publication = Plain();
        var words = string.Join(' ', Enumerable.Repeat("Lorem ipsum dolor sit amet", 4));
        publication.Sections.Add(new Section
        {
            Rows =
            [
                new Row
                {
                    Fractions = [0.5, 0.5],
                    Columns =
                    [
                        new Column { Modules = [new TextModule { Markdown = words }] },
                        new Column { Modules = [new TextModule { Markdown = words }] },
                    ],
                },
            ],
        });

        var pdf = await Renderer.RenderAsync(publication);
        var page = await new PdfiumRasterizer().RasterizePageAsync(pdf, 0, new RasterizeOptions { Dpi = 72 });
        using var bitmap = SKBitmap.Decode(page.Data);

        // The row is the first content: it starts at the top margin. Look at the first two lines' height.
        var top = (int)(publication.Theme.Page.MarginTopMm * 72 / 25.4);
        var left = (int)(publication.Theme.Page.MarginLeftMm * 72 / 25.4);
        var right = bitmap.Width - (int)(publication.Theme.Page.MarginRightMm * 72 / 25.4);
        var middle = bitmap.Width / 2;
        Assert.True(HasInk(bitmap, left, top, middle - 4, top + 30), "no text in the left column");
        Assert.True(HasInk(bitmap, middle + 4, top, right, top + 30), "no text in the right column");
    }

    [Fact]
    public async Task Hidden_modules_are_not_rendered()
    {
        var publication = Plain();
        publication.Sections.Add(Single(
            new TextModule { Markdown = "Visible paragraph" },
            new TextModule { Markdown = "Secret paragraph", Hidden = true },
            new HeadingModule { Text = "Hidden heading", Hidden = true }));

        var pdf = await Renderer.RenderAsync(publication);
        using var doc = PdfDocument.Open(pdf);
        var text = AllText(doc);
        Assert.Contains("Visible", text);
        Assert.DoesNotContain("Secret", text);
        Assert.DoesNotContain("Hidden", text);
    }

    [Fact]
    public async Task Contents_module_lists_registered_headings_with_page_numbers()
    {
        var publication = Plain();
        publication.Sections.Add(Single(new ContentsModule { Title = "Overview", Depth = 2 }));
        publication.Sections.Add(new Section { PageBreakBefore = true, Rows = [new Row { Columns = [new Column { Modules = [new HeadingModule { Text = "Alpha chapter", Level = 1 }, new TextModule { Markdown = "Body." }] }] }] });
        publication.Sections.Add(new Section { PageBreakBefore = true, Rows = [new Row { Columns = [new Column { Modules = [new HeadingModule { Text = "Beta chapter", Level = 2 }, new HeadingModule { Text = "Too deep", Level = 3 }] }] }] });

        var pdf = await Renderer.RenderAsync(publication);
        using var doc = PdfDocument.Open(pdf);
        Assert.Equal(3, doc.NumberOfPages);
        var first = doc.GetPage(1);
        var text = first.Text;
        Assert.Contains("Overview", text);
        Assert.Contains("Alpha", text);
        Assert.Contains("Beta", text);
        Assert.DoesNotContain("deep", text);
        var numbers = first.GetWords().Select(w => w.Text).ToList();
        Assert.Contains("2", numbers);
        Assert.Contains("3", numbers);
    }

    [Fact]
    public async Task Page_break_modules_add_pages()
    {
        var publication = Plain();
        publication.Sections.Add(Single(
            new TextModule { Markdown = "One" },
            new PageBreakModule(),
            new TextModule { Markdown = "Two" },
            new PageBreakModule(),
            new TextModule { Markdown = "Three" }));

        var pdf = await Renderer.RenderAsync(publication);
        using var doc = PdfDocument.Open(pdf);
        Assert.Equal(3, doc.NumberOfPages);
        Assert.Contains("Three", doc.GetPage(3).Text);
    }

    [Fact]
    public async Task Missing_image_draws_a_placeholder_only_when_asked()
    {
        var publication = Plain();
        publication.Sections.Add(Single(new TextModule { Markdown = "Before" }, new ImageModule { Alt = "Photo", Caption = "Under it" }));

        var withPlaceholder = await Renderer.RenderAsync(publication, new PublicationRenderOptions { ShowPlaceholders = true });
        var without = await Renderer.RenderAsync(publication, new PublicationRenderOptions { ShowPlaceholders = false });

        using (var doc = PdfDocument.Open(withPlaceholder))
        {
            var text = AllText(doc);
            Assert.Contains("Image", text);
            Assert.Contains("Under it", text);
        }

        using (var doc = PdfDocument.Open(without))
        {
            var text = AllText(doc);
            Assert.Contains("Before", text);
            Assert.DoesNotContain("Image", text);
            Assert.DoesNotContain("Under it", text);
        }
    }

    [Fact]
    public async Task Kept_together_row_taller_than_a_page_does_not_throw()
    {
        var publication = Plain();

        // Hard line breaks: few characters (a tiny estimate) but hundreds of lines (several pages).
        var lines = string.Join("  \n", Enumerable.Range(1, 260).Select(i => "x" + i));
        var row = new Row
        {
            KeepTogether = true,
            Fractions = [0.5, 0.5],
            Columns =
            [
                new Column { Modules = [new TextModule { Markdown = lines }] },
                new Column { Modules = [new TextModule { Markdown = string.Join("\n\n", Enumerable.Repeat("A long paragraph of ordinary words that wraps.", 80)) }] },
            ],
        };
        publication.Sections.Add(new Section { Rows = [row] });
        publication.Sections.Add(Single(new SpacerModule { HeightPt = 600 }, new ImageModule { WidthPercent = 100 }));

        var pdf = await Renderer.RenderAsync(publication, new PublicationRenderOptions { ShowPlaceholders = true });
        using var doc = PdfDocument.Open(pdf);
        Assert.True(doc.NumberOfPages >= 3);
        Assert.Contains("x260", AllText(doc));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var publication = Plain();
        publication.Sections.Add(Single(new TextModule { Markdown = "Hello" }));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Renderer.RenderAsync(publication, ct: cancelled.Token));

        var big = Plain();
        for (var i = 0; i < 400; i++)
        {
            big.Sections.Add(Single(
                new HeadingModule { Text = "Section " + i, Level = 2 },
                new TextModule { Markdown = string.Join("\n\n", Enumerable.Repeat("Some words that fill a paragraph of the document body.", 12)) }));
        }

        // Cancel from a dedicated thread: a thread-pool timer can be starved by the render itself on a two-core CI
        // runner and fire only after the render has finished.
        using var soon = new CancellationTokenSource();
        var canceller = new Thread(() =>
        {
            Thread.Sleep(30);
            soon.Cancel();
        })
        { IsBackground = true };
        canceller.Start();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Renderer.RenderAsync(big, ct: soon.Token));
        canceller.Join();
    }

    [Fact]
    public async Task Metadata_is_embedded_only_when_asked()
    {
        var publication = Plain();
        publication.Name = "Fallback name";
        publication.Metadata.Authors.Add("Ada Writer");
        publication.Sections.Add(Single(new TextModule { Markdown = "Text" }));

        using (var doc = PdfDocument.Open(await Renderer.RenderAsync(publication)))
        {
            Assert.Equal("Fallback name", doc.Information.Title);
            Assert.Equal("Ada Writer", doc.Information.Author);
        }

        publication.Metadata.Title = "Real title";
        using (var doc = PdfDocument.Open(await Renderer.RenderAsync(publication)))
        {
            Assert.Equal("Real title", doc.Information.Title);
        }

        using (var doc = PdfDocument.Open(await Renderer.RenderAsync(publication, new PublicationRenderOptions { EmbedMetadata = false })))
        {
            Assert.NotEqual("Real title", doc.Information.Title);
            Assert.NotEqual("Ada Writer", doc.Information.Author);
        }
    }

    [Fact]
    public async Task Svg_and_bitmap_assets_render_and_buttons_link()
    {
        var publication = Plain();
        var svg = new PublicationAsset
        {
            FileName = "dot.svg",
            MediaType = "image/svg+xml",
            Data = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='40' height='20'><rect width='40' height='20' fill='#c00'/></svg>"),
        };
        using var bitmap = new SKBitmap(30, 60);
        bitmap.Erase(SKColors.Navy);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var png = new PublicationAsset { FileName = "tall.png", MediaType = "image/png", Data = encoded.ToArray() };
        publication.Assets.AddRange([svg, png]);
        publication.Sections.Add(Single(
            new ImageModule { AssetId = svg.Id, WidthPercent = 40, Align = BlockAlign.Left, CornerRadiusPt = 6 },
            new ImageModule { AssetId = png.Id, WidthPercent = 100, MaxHeightPt = 120, Caption = "Tall picture" },
            new DividerModule { Style = DividerStyle.Dashed, WidthPercent = 50 },
            new ButtonModule { Text = "Visit", Url = "https://example.com/x", Style = ButtonStyle.Outline },
            new ButtonModule { Text = "Unsafe", Url = "javascript:alert(1)", Style = ButtonStyle.Link }));

        var pdf = await Renderer.RenderAsync(publication);
        using var doc = PdfDocument.Open(pdf);
        Assert.Equal(1, doc.NumberOfPages);
        var page = doc.GetPage(1);
        Assert.Contains("Tall picture", page.Text);
        var links = page.GetHyperlinks().Select(l => l.Uri).ToList();
        Assert.Contains("https://example.com/x", links);
        Assert.DoesNotContain(links, l => l?.StartsWith("javascript", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Di_registers_the_renderer()
    {
        var services = new ServiceCollection();
        services.AddAllInOnePdfPublishing();
        using var provider = services.BuildServiceProvider();
        Assert.IsType<PublicationRenderer>(provider.GetRequiredService<IPublicationRenderer>());
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Clean theme without header and footer, so text extraction sees only the content.</summary>
    [Fact]
    public async Task Render_with_pages_reports_where_every_section_row_column_and_module_lands()
    {
        var publication = Plain();
        var paragraph = string.Join(' ', Enumerable.Repeat("Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor.", 6));
        var modules = Enumerable.Range(0, 14).Select(i => (Module)new TextModule { Markdown = $"Paragraph {i + 1}. {paragraph}" }).ToList();
        var section = Single([.. modules]);
        publication.Sections.Add(section);
        var row = section.Rows[0];
        var column = row.Columns[0];

        var result = await Renderer.RenderWithPagesAsync(publication, new PublicationRenderOptions { Draft = true });

        using var doc = PdfDocument.Open(result.Pdf);
        Assert.Equal(doc.NumberOfPages, result.PageCount);
        Assert.True(result.PageCount >= 2, "the fourteen paragraphs should need more than one page");

        Assert.Equal(new PublicationPageSpan(1, result.PageCount), result.Pages[section.Id]);
        Assert.Equal(new PublicationPageSpan(1, result.PageCount), result.Pages[row.Id]);
        Assert.Equal(new PublicationPageSpan(1, result.PageCount), result.Pages[column.Id]);

        var spans = modules.Select(m => result.Pages[m.Id]).ToList();
        Assert.Equal(1, spans[0].First);
        Assert.Equal(result.PageCount, spans[^1].Last);
        for (var i = 1; i < spans.Count; i++)
        {
            Assert.True(spans[i].First >= spans[i - 1].First, $"module {i + 1} starts before module {i}");
            Assert.True(spans[i].Last >= spans[i].First);
        }

        Assert.Contains(spans, s => s.First == 2);
    }

    private static Publication Plain()
    {
        var theme = BuiltInThemes.Clean();
        theme.Header.Left = theme.Header.Center = theme.Header.Right = null;
        theme.Footer.Left = theme.Footer.Center = theme.Footer.Right = null;
        return new Publication { Name = "Test", Theme = theme };
    }

    private static Section Single(params Module[] modules) =>
        new() { Rows = [new Row { Columns = [new Column { Modules = [.. modules] }] }] };

    private static string AllText(PdfDocument doc) => string.Join("\n", doc.GetPages().Select(p => p.Text));

    private static bool HasInk(SKBitmap bitmap, int x0, int y0, int x1, int y1)
    {
        for (var y = Math.Max(0, y0); y < Math.Min(bitmap.Height, y1); y++)
        {
            for (var x = Math.Max(0, x0); x < Math.Min(bitmap.Width, x1); x++)
            {
                var c = bitmap.GetPixel(x, y);
                if ((c.Red + c.Green + c.Blue) / 3 < 128)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
