using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Tests;

public class RenderingTests
{
    [Fact]
    public async Task Renders_sample_to_pdf_with_every_built_in_theme()
    {
        var doc = await SampleDocuments.CreateWelcomeAsync();
        var renderer = new PdfRenderer();
        var rasterizer = new PdfiumRasterizer();

        foreach (var theme in BuiltInThemes.All())
        {
            var pdf = await renderer.RenderAsync(doc, theme);
            Assert.True(pdf.Length > 1000, $"{theme.Name} produced a tiny PDF");
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
            Assert.True(rasterizer.GetPageCount(pdf) >= 1, $"{theme.Name} has no pages");
        }
    }

    [Fact]
    public async Task Rasterizes_first_page_to_png()
    {
        var doc = await SampleDocuments.CreateWelcomeAsync();
        var pdf = await new PdfRenderer().RenderAsync(doc, BuiltInThemes.Clean());
        var page = await new PdfiumRasterizer().RasterizePageAsync(pdf, 0, new RasterizeOptions { Dpi = 72 });

        Assert.Equal("image/png", page.MediaType);
        Assert.True(page.Width > 500 && page.Height > 700);
        Assert.Equal(0x89, page.Data[0]);
    }

    [Fact]
    public void Html_renderer_escapes_text_and_drops_raw_html_by_default()
    {
        var doc = new Document();
        doc.Blocks.Add(new ParagraphBlock("<script>alert(1)</script>"));
        doc.Blocks.Add(new HtmlBlock { Html = "<b onclick=\"x()\">raw</b>" });

        var html = new DocumentHtmlRenderer().RenderFragment(doc);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("onclick", html);
        Assert.StartsWith("<article class=\"aio-doc\">", html);
    }

    [Fact]
    public void Html_renderer_adds_heading_anchors()
    {
        var doc = new Document();
        doc.Blocks.Add(new HeadingBlock(2, "Hello World!"));
        doc.Blocks.Add(new HeadingBlock(2, "Hello World!"));

        var html = new DocumentHtmlRenderer().RenderFragment(doc);

        Assert.Contains("id=\"hello-world\"", html);
        Assert.Contains("id=\"hello-world-2\"", html);
    }
}
