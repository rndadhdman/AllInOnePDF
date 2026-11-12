using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Tests;

public partial class LayoutDocumentTests
{
    [GeneratedRegex(@"/Type\s*/Page[^s]")]
    private static partial Regex PageObject();

    private static int PageCount(byte[] pdf) => PageObject().Count(Encoding.Latin1.GetString(pdf));

    private static string Text(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    private static void A4(PageDescriptor page, float margin = 0)
    {
        page.Size(210, 297, Unit.Millimetre);
        page.Margin(margin);
    }

    [Fact]
    public void Content_taller_than_a_page_continues_on_new_pages()
    {
        var pdf = Document.Create(doc => doc.Page(page =>
        {
            A4(page);
            page.Content().Column(col =>
            {
                for (var i = 0; i < 30; i++)
                {
                    col.Item().SetChild(new Block(0, 100));
                }
            });
        })).GeneratePdf();

        Assert.StartsWith("%PDF-", Text(pdf));
        Assert.Equal(4, PageCount(pdf)); // 8 blocks of 100 pt per 842 pt page
    }

    [Fact]
    public void Header_and_footer_are_drawn_on_every_page_and_skip_once_skips_the_first()
    {
        var header = new Block(0, 20) { Once = false };
        var footer = new Block(0, 20) { Once = false };
        Document.Create(doc => doc.Page(page =>
        {
            A4(page, 30);
            page.Header().SetChild(header);
            page.Footer().SkipOnce().SetChild(footer);
            page.Content().SetChild(new Lines(200, 10));
        })).GeneratePdf();

        // 842 - 60 margins - 20 header: 76 lines on page 1 (no footer), then 74 per page with the footer: 3 pages.
        Assert.Equal([1, 2, 3], header.Draws.Where(d => !d.FirstPass).Select(d => d.Page));
        Assert.Equal([2, 3], footer.Draws.Where(d => !d.FirstPass).Select(d => d.Page));
    }

    [Fact]
    public void Multiple_page_descriptions_continue_the_page_numbers()
    {
        var second = new Block(0, 10);
        var pdf = Document.Create(doc =>
        {
            doc.Page(page => page.Content().SetChild(new Block(0, 10)));
            doc.Page(page => page.Content().SetChild(second));
        }).GeneratePdf();

        Assert.Equal(2, PageCount(pdf));
        Assert.Equal(2, second.Draws.Last().Page);
    }

    [Fact]
    public void Sections_and_captured_positions_are_recorded()
    {
        int? sectionPage = null;
        IReadOnlyList<CapturedPosition> captures = [];
        Document.Create(doc => doc.Page(page =>
        {
            A4(page);
            page.MarginLeft(20);
            page.MarginTop(30);
            page.Footer().SetChild(new ContextProbe(ctx =>
            {
                if (!ctx.IsFirstPass)
                {
                    sectionPage = ctx.GetSectionPage("chapter-2");
                    captures = ctx.GetCaptures("heading");
                }
            }));
            page.Content().Column(col =>
            {
                col.Item().SetChild(new Block(0, 100));
                col.Item().CaptureContentPosition("heading").SetChild(new Block(0, 10));
                col.Item().PageBreak();
                col.Item().Section("chapter-2").PaddingLeft(15).CaptureContentPosition("heading").SetChild(new Block(0, 10));
            });
        })).GeneratePdf();

        Assert.Equal(2, sectionPage);
        Assert.Equal([new CapturedPosition(1, 20, 130), new CapturedPosition(2, 35, 30)], captures);
    }

    [Fact]
    public void Hyperlink_writes_a_link_annotation()
    {
        var pdf = Document.Create(doc => doc.Page(page =>
        {
            page.Size(600, 800);
            page.Content().AlignLeft().AlignTop().PaddingLeft(100).PaddingTop(200).Hyperlink("https://example.com/layout").Width(50).Height(20).Background(Color.Black);
        })).GeneratePdf();

        var text = Text(pdf);
        Assert.Contains("/URI", text);
        Assert.Contains("https://example.com/layout", text);

        // y is flipped: 800 − 220 .. 800 − 200. Skia rounds the page size to whole pixels at the raster DPI, so the page
        // (and everything on it) can sit up to 72/220 pt lower than the exact value.
        var rect = Regex.Match(text, @"/Rect\s*\[([^\]]+)\]").Groups[1].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(100, rect[0], 0.01f);
        Assert.Equal(580, rect[1], 0.35f);
        Assert.Equal(150, rect[2], 0.01f);
        Assert.Equal(20, rect[3] - rect[1], 0.01f);
    }

    [Fact]
    public void Section_destinations_are_written()
    {
        var pdf = Document.Create(doc => doc.Page(page =>
            page.Content().Section("intro").Height(20).Background(Color.Black))).GeneratePdf();

        Assert.Contains("intro", Text(pdf));
    }

    [Fact]
    public void Content_that_may_not_break_and_is_taller_than_a_page_fails_the_layout()
    {
        var unbreakable = Assert.Throws<DocumentLayoutException>(() => Document.Create(doc => doc.Page(page =>
            page.Content().Column(col =>
            {
                col.Item().SetChild(new Block(0, 100));
                col.Item().PreventPageBreak().SetChild(new Lines(200, 10));
            }))).GeneratePdf());
        Assert.Contains("page 2", unbreakable.Message);

        Assert.Throws<DocumentLayoutException>(() => Document.Create(doc => doc.Page(page =>
            page.Content().Height(2000))).GeneratePdf());
    }

    [Fact]
    public void Metadata_is_written()
    {
        var pdf = Document.Create(doc => doc.Page(page => page.Content().Height(10)))
            .WithMetadata(new DocumentMetadata { Title = "Layout Title", Author = "Ada Lovelace", Subject = "Engines", Keywords = "pdf, layout" })
            .WithSettings(new DocumentSettings { ImageCompressionQuality = ImageCompressionQuality.Medium })
            .GeneratePdf();

        var text = Text(pdf);
        Assert.Contains("Layout Title", text);
        Assert.Contains("Ada Lovelace", text);
        Assert.Contains("Engines", text);
    }

    [Fact]
    public void Compose_exceptions_are_wrapped_and_nothing_is_written()
    {
        var inner = new InvalidOperationException("boom");
        using var stream = new MemoryStream();
        var ex = Assert.Throws<DocumentComposeException>(() => Document.Create(doc => doc.Page(page =>
            page.Content().Column(_ => throw inner))).GeneratePdf(stream));

        Assert.Same(inner, ex.InnerException);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void Leading_page_breaks_make_no_blank_page_but_consecutive_breaks_do()
    {
        var pdf = Document.Create(doc => doc.Page(page => page.Content().Column(col =>
        {
            col.Item().PageBreak();
            col.Item().Height(10).Background(Color.Black);
            col.Item().PageBreak();
            col.Item().PageBreak();
            col.Item().Height(10).Background(Color.Black);
            col.Item().PageBreak();
        }))).GeneratePdf();

        Assert.Equal(3, PageCount(pdf));
    }

    [Fact]
    public void Empty_content_still_produces_one_page()
    {
        var pdf = Document.Create(doc => doc.Page(_ => { })).GeneratePdf();
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void Dynamic_components_are_composed_per_page_with_the_final_page_count_in_pass_two()
    {
        var component = new PageStamp();
        var pdf = Document.Create(doc => doc.Page(page =>
        {
            page.DefaultTextStyle(s => s.FontSize(9));
            page.Header().Dynamic(component);
            page.Content().SetChild(new Lines(200, 10));
        })).GeneratePdf();

        var pages = PageCount(pdf);
        Assert.True(pages >= 3);
        var secondPass = component.Calls.Where(c => c.Total > 0).ToList();
        Assert.All(secondPass, c => Assert.Equal(pages, c.Total));
        Assert.Equal(Enumerable.Range(1, pages), secondPass.Select(c => c.Page).Distinct());
        Assert.All(component.Styles, s => Assert.Equal(9f, s.Size));
        Assert.All(component.Sizes, s => Assert.Equal(15f, s.Height));
    }

    [Fact]
    public void Dynamic_component_failures_surface_as_compose_exceptions()
    {
        Assert.Throws<DocumentComposeException>(() => Document.Create(doc => doc.Page(page =>
        {
            page.Header().Dynamic(new Failing());
            page.Content().Height(10);
        })).GeneratePdf());
    }

    [Fact]
    public void Documents_render_concurrently()
    {
        var counts = new int[8];
        Parallel.For(0, counts.Length, i =>
        {
            var pdf = Document.Create(doc => doc.Page(page =>
            {
                page.Header().SkipOnce().Height(10).Background(Color.Black);
                page.Content().Column(col =>
                {
                    for (var n = 0; n < 40 + (i * 10); n++)
                    {
                        col.Item().Height(50).Background(Color.FromHex("#336699"));
                    }
                });
            })).GeneratePdf();
            counts[i] = PageCount(pdf);
        });

        for (var i = 0; i < counts.Length; i++)
        {
            // 842 pt minus a 10 pt header after page 1: 16 rows per page.
            Assert.True(counts[i] >= (40 + (i * 10)) / 17, $"document {i}: {counts[i]} pages");
        }
    }

    [Fact]
    public void Four_hundred_pages_lay_out_quickly()
    {
        var watch = Stopwatch.StartNew();
        var pdf = Document.Create(doc => doc.Page(page =>
        {
            A4(page, 40);
            page.Footer().Height(12).Background(Color.Black);
            page.Content().Column(col =>
            {
                col.Spacing(4);
                for (var n = 0; n < 400 * 25; n++)
                {
                    col.Item().PaddingLeft(10).Row(row =>
                    {
                        row.ConstantItem(20).Height(24).Background(Color.FromHex("#CCCCCC"));
                        row.RelativeItem().Height(24).Border(0.5f).Background(Color.FromHex("#EEEEEE"));
                    });
                }
            });
        })).GeneratePdf();
        watch.Stop();

        Assert.True(PageCount(pdf) >= 300);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    private sealed class PageStamp : IDynamicComponent
    {
        public List<(int Page, int Total)> Calls { get; } = [];

        public List<TextStyle> Styles { get; } = [];

        public List<Size> Sizes { get; } = [];

        public DynamicComponentComposeResult Compose(DynamicContext context)
        {
            Calls.Add((context.PageNumber, context.TotalPages));
            Styles.Add(context.TextStyle);
            var probe = new StyleProbe();
            var element = context.CreateElement(e => e.PaddingBottom(5).Height(10).SetChild(probe));
            Styles.Add(probe.Seen!);
            Sizes.Add(element.Size);
            return new DynamicComponentComposeResult { Content = element, HasMoreContent = false };
        }
    }

    private sealed class Failing : IDynamicComponent
    {
        public DynamicComponentComposeResult Compose(DynamicContext context) => throw new InvalidOperationException("dynamic boom");
    }
}
