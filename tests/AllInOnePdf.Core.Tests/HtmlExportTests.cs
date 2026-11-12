using System.Text;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Fonts;
using AllInOnePdf.Core.Themes;
using AngleSharp.Html.Parser;
using static AllInOnePdf.Core.Tests.HtmlTestHelpers;
using static AllInOnePdf.Core.Tests.MarkdownExportTestHelpers;

namespace AllInOnePdf.Core.Tests;

public class HtmlExportTests
{
    public static TheoryData<string> ThemeIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var theme in BuiltInThemes.All())
            {
                data.Add(theme.Id);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public async Task Welcome_sample_exports_a_valid_themed_page(string themeId)
    {
        var theme = BuiltInThemes.Find(themeId)!;
        var html = await ToHtmlAsync(await SampleDocuments.CreateWelcomeAsync(), theme, (HtmlExporter.OptToc, "yes"));

        var dom = new HtmlParser().ParseDocument(html);
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Equal("Designing a Theme", dom.Title);
        Assert.Equal("AllInOne PDF", dom.QuerySelector("meta[name=author]")?.GetAttribute("content"));
        Assert.NotNull(dom.QuerySelector("article.aio-doc"));

        var css = dom.QuerySelector("head style")!.TextContent;
        Assert.Contains($"\"{theme.Typography.BodyFont}\"", css, StringComparison.Ordinal);
        Assert.Contains("@page{size:", css, StringComparison.Ordinal);
        Assert.Contains("@media print{", css, StringComparison.Ordinal);
        if (theme.Code.SyntaxHighlighting)
        {
            Assert.Contains(".aio-doc .hl-kw{", css, StringComparison.Ordinal);
        }

        var code = dom.QuerySelector("figure.aio-codeblock pre code.language-csharp");
        Assert.NotNull(code);
        Assert.NotEmpty(code.QuerySelectorAll("span.hl.hl-kw"));
        Assert.Contains("yield return a;", code.TextContent, StringComparison.Ordinal);

        var toc = dom.QuerySelector("nav.aio-toc");
        Assert.NotNull(toc);
        var links = toc.QuerySelectorAll("a").Select(a => a.GetAttribute("href")!).ToList();
        Assert.Contains("#typography", links);
        Assert.All(links, href => Assert.NotNull(dom.GetElementById(href[1..])));

        Assert.Equal(theme.Cover.Enabled, dom.QuerySelector("header.aio-cover") is not null);
    }

    [Fact]
    public async Task Toc_and_cover_follow_options()
    {
        var doc = await SampleDocuments.CreateWelcomeAsync();
        var theme = BuiltInThemes.Clean();
        theme.Toc.Enabled = false;
        theme.Cover.Enabled = false;

        var plain = new HtmlParser().ParseDocument(await ToHtmlAsync(doc, theme));
        Assert.Null(plain.QuerySelector(".aio-toc"));
        Assert.Null(plain.QuerySelector(".aio-cover"));

        var full = new HtmlParser().ParseDocument(await ToHtmlAsync(doc, theme, (HtmlExporter.OptToc, "yes"), (HtmlExporter.OptCover, "yes")));
        Assert.NotNull(full.QuerySelector(".aio-toc"));
        Assert.Equal("Designing a Theme", full.QuerySelector(".aio-cover-title")?.TextContent);
        Assert.Equal("A short sample document for previewing PDF themes", full.QuerySelector(".aio-cover-subtitle")?.TextContent);
    }

    [Fact]
    public async Task Theme_settings_reach_the_stylesheet()
    {
        var theme = BuiltInThemes.Clean();
        theme.Headings.NumberHeadings = true;
        theme.Tables.Style = TableStyle.Striped;
        theme.Page.Size = PageSizeName.Letter;
        theme.Code.WrapLongLines = false;
        theme.Callouts.Warning.Accent = "#123456";
        theme.Typography.BodyFont = "Evil\"};body{x:url(javascript:1)";

        var html = await ToHtmlAsync(Doc(new HeadingBlock(1, "A")), theme);
        var css = new HtmlParser().ParseDocument(html).QuerySelector("style")!.TextContent;

        Assert.Contains("counter(aioh1)", css, StringComparison.Ordinal);
        Assert.Contains("nth-child(even)", css, StringComparison.Ordinal);
        Assert.Contains("@page{size:215.9mm 279.4mm", css, StringComparison.Ordinal);
        Assert.Contains("white-space:pre;", css, StringComparison.Ordinal);
        Assert.Contains("#123456", css, StringComparison.Ordinal);
        Assert.DoesNotContain("url(javascript", css, StringComparison.Ordinal);
        Assert.Contains("font-family:\"Evilbodyxurljavascript1\",", css, StringComparison.Ordinal);
        Assert.Contains("max-width:175.9mm", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Images_as_data_urls_folder_or_omitted()
    {
        var doc = new Document();
        var res = doc.AddResource("image/png", TinyPng, "My Picture.png");
        doc.Blocks.Add(new ImageBlock { Source = res.Reference, AltText = "Pic" });
        doc.Blocks.Add(new ImageBlock { Source = "https://example.com/x.png", AltText = "Remote" });

        var inline = await ToHtmlAsync(doc);
        Assert.Contains("src=\"data:image/png;base64,", inline, StringComparison.Ordinal);
        Assert.Contains("src=\"https://example.com/x.png\"", inline, StringComparison.Ordinal);

        var folder = await ExportHtmlAsync(doc, null, "my page", (HtmlExporter.OptImages, "folder"));
        var extra = Assert.Single(folder.Files, f => !f.IsPrimary);
        Assert.Equal("my page_files/My-Picture.png", extra.FileName);
        Assert.Equal(TinyPng, extra.Content);
        Assert.Contains("src=\"my%20page_files/My-Picture.png\"", Encoding.UTF8.GetString(folder.Primary!.Content), StringComparison.Ordinal);

        var omitted = await ToHtmlAsync(doc, null, (HtmlExporter.OptImages, "omit"));
        Assert.DoesNotContain("<img", omitted, StringComparison.Ordinal);
        Assert.Contains("aio-image-missing", omitted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embedding_fonts_adds_font_faces_and_a_size_warning()
    {
        var result = await ExportHtmlAsync(Doc(Para(new TextInline("x"))), BuiltInThemes.Clean(), "doc", (HtmlExporter.OptFonts, "embed"));
        var html = Encoding.UTF8.GetString(result.Primary!.Content);

        Assert.Contains("@font-face{font-family:\"Inter\";font-style:normal;font-weight:400;", html, StringComparison.Ordinal);
        Assert.Contains("font-family:\"JetBrains Mono\"", html, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("Embedded fonts add", StringComparison.Ordinal));
        Assert.True(BundledFonts.IsBundled("Inter"));
    }

    [Fact]
    public async Task Raw_html_is_text_only_unless_enabled_and_text_is_encoded()
    {
        var doc = Doc(new HtmlBlock { Html = "<script>alert(1)</script><b>raw</b>" }, Para(new TextInline("<i>not markup</i>")));
        doc.Metadata.Title = "A <title> & \"quotes\"";

        var safe = await ToHtmlAsync(doc);
        Assert.DoesNotContain("<script>", safe, StringComparison.Ordinal);
        Assert.Contains("&lt;i&gt;not markup&lt;/i&gt;", safe, StringComparison.Ordinal);
        Assert.Contains("<title>A &lt;title&gt; &amp; &quot;quotes&quot;</title>", safe, StringComparison.Ordinal);

        var raw = await ToHtmlAsync(doc, null, (HtmlExporter.OptRawHtml, true));
        Assert.Contains("<b>raw</b>", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Minify_and_line_endings()
    {
        var doc = await SampleDocuments.CreateWelcomeAsync();
        var pretty = await ToHtmlAsync(doc);
        var minified = await ToHtmlAsync(doc, null, (HtmlExporter.OptMinify, true));
        var crlf = await ToHtmlAsync(doc, null, (HtmlExporter.OptLineEndings, "crlf"));

        Assert.True(minified.Length < pretty.Length);
        Assert.Contains("\n<h2 class=\"aio-h aio-h2\"", pretty, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", pretty, StringComparison.Ordinal);
        Assert.Contains("\r\n", crlf, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\r", crlf, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("welcome")]
    [InlineData("sample-document.md")]
    public async Task Markdown_to_html_to_document_round_trips_structurally(string sample)
    {
        var markdown = sample == "welcome" ? SampleDocuments.WelcomeMarkdown : await File.ReadAllTextAsync(FindSample(sample));
        var first = await ImportAsync(markdown);
        var expected = Dump(first);

        var html = await ToHtmlAsync(first, BuiltInThemes.Find("technical"), (HtmlExporter.OptToc, "yes"), (HtmlExporter.OptCover, "yes"));
        var (second, _) = await ImportHtmlAsync(html);

        Assert.Equal(NormalizeResources(expected), NormalizeResources(Dump(second)));
    }

    [Fact]
    public async Task Round_trip_keeps_footnotes_math_and_page_breaks()
    {
        const string markdown = """
            # Notes

            Text with a note[^a] and $x^2$ math.

            <div style="page-break-after: always"></div>

            $$
            E = mc^2
            $$

            [^a]: The note.
            """;
        var first = await ImportAsync(markdown);
        var (second, _) = await ImportHtmlAsync(await ToHtmlAsync(first));

        Assert.Equal(Dump(first), Dump(second));
        Assert.Contains(second.Blocks, b => b is PageBreakBlock);
        Assert.Single(second.Footnotes);
    }

    /// <summary>Embedded images get new resource ids on import; compare them by position instead.</summary>
    private static string NormalizeResources(string dump) =>
        System.Text.RegularExpressions.Regex.Replace(dump, "res:[0-9a-f]{32}", "res:*");
}
