using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;
using static AllInOnePdf.Core.Tests.HtmlTestHelpers;
using static AllInOnePdf.Core.Tests.MarkdownExportTestHelpers;

namespace AllInOnePdf.Core.Tests;

public class HtmlImportTests
{
    private static readonly string DotDataUrl = "data:image/png;base64," + Convert.ToBase64String(TinyPng);

    private static string RichSample => $$"""
        <!doctype html>
        <html lang="en-GB">
        <head>
          <title>Rich   sample</title>
          <meta name="author" content="Ada Lovelace">
          <meta name="description" content="Every element">
          <meta name="keywords" content="html, import">
          <meta name="date" content="2024-03-01">
          <style>p { color: red }</style>
          <script>alert("x")</script>
        </head>
        <body>
          <nav><a href="/">Home</a></nav>
          <header><p>Site banner</p></header>
          <main>
            <h1 id="top">Rich
               sample</h1>
            <p>Plain <strong>bold</strong> <em>italic</em> <u>under</u> <s>strike</s> <mark>mark</mark> x<sup>2</sup> H<sub>2</sub>O
               <small>small</small> <kbd>Ctrl</kbd> <code>code()</code> <a href="docs/page.html" title="Docs">relative</a>
               <a href="https://example.com">abs</a> <span style="font-weight: bold">spanbold</span> <custom-tag>custom</custom-tag><br>
               after break <math><annotation encoding="application/x-tex">x^2</annotation></math></p>
            <h2>Lists</h2>
            <ul><li>One</li><li>Two<ul><li>Nested</li></ul></li></ul>
            <ol start="3"><li>Three</li><li>Four</li></ol>
            <ul class="contains-task-list">
              <li class="task-list-item"><input type="checkbox" checked disabled> Done</li>
              <li class="task-list-item"><input type="checkbox" disabled> Todo</li>
            </ul>
            <pre><code class="language-csharp">var x = 1;
          Console.WriteLine(x);</code></pre>
            <pre>plain   pre
          text</pre>
            <blockquote><p>Quoted text</p><footer>— Someone Famous</footer></blockquote>
            <table>
              <caption>Spans</caption>
              <thead><tr><th align="left">A</th><th style="text-align: center">B</th><th align="right">C</th></tr></thead>
              <tbody>
                <tr><td colspan="2">wide</td><td rowspan="2">tall</td></tr>
                <tr><td>x</td><td>y</td></tr>
              </tbody>
            </table>
            <figure><img src="{{DotDataUrl}}" alt="Dot" width="50%"><figcaption>A dot</figcaption></figure>
            <hr>
            <details><summary>More info</summary><p>Hidden</p></details>
            <div class="note"><p>A note</p></div>
            <div class="admonition warning"><p class="admonition-title">Careful</p><p>Hot</p></div>
            <div class="markdown-alert markdown-alert-tip"><p class="markdown-alert-title"><svg><path d="M0"/></svg>Tip</p><p>Try it</p></div>
            <dl><dt>Term</dt><dd>Definition</dd></dl>
            <math display="block"><annotation encoding="application/x-tex">E=mc^2</annotation></math>
            <section><div><p>Deep <span>inline</span></p></div></section>
            <p><img src="https://cdn.example.com/a.png" alt="remote"></p>
            <p><img src="https://cdn.example.com/b.png" alt="remote2"></p>
            <iframe src="https://example.com/embed"></iframe>
            <noscript>no js</noscript>
            <template><p>tmpl</p></template>
          </main>
          <footer>Copyright</footer>
          <aside role="complementary">Sidebar</aside>
        </body>
        </html>
        """;

    private static string AllText(Document doc) => Dump(doc);

    [Fact]
    public async Task Reads_metadata_from_head()
    {
        var (doc, _) = await ImportHtmlAsync(RichSample);

        Assert.Equal("Rich sample", doc.Metadata.Title);
        Assert.Equal(["Ada Lovelace"], doc.Metadata.Authors);
        Assert.Equal("Every element", doc.Metadata.Description);
        Assert.Equal(["html", "import"], doc.Metadata.Keywords);
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), doc.Metadata.Date);
        Assert.Equal("en-GB", doc.Metadata.Language);
        Assert.Equal("html", doc.Metadata.SourceFormat);
    }

    [Fact]
    public async Task Main_content_only_drops_chrome_and_non_content()
    {
        var (doc, _) = await ImportHtmlAsync(RichSample);
        var text = AllText(doc);

        foreach (var absent in new[] { "Home", "Site banner", "Copyright", "Sidebar", "alert", "no js", "tmpl", "color: red" })
        {
            Assert.DoesNotContain(absent, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Without_main_content_only_the_whole_body_is_kept()
    {
        var (doc, _) = await ImportHtmlAsync(RichSample, options: (HtmlImporter.OptMainContentOnly, false));
        var text = AllText(doc);

        Assert.Contains("Home", text, StringComparison.Ordinal);
        Assert.Contains("Copyright", text, StringComparison.Ordinal);
        Assert.Contains("Sidebar", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Converts_headings_and_inline_styles()
    {
        var (doc, _) = await ImportHtmlAsync(RichSample);
        var h1 = Assert.IsType<HeadingBlock>(doc.Blocks[0]);
        Assert.Equal(1, h1.Level);
        Assert.Equal("top", h1.Id);
        Assert.Equal("Rich sample", h1.ToPlainText());

        var runs = Runs(Assert.IsType<ParagraphBlock>(doc.Blocks[1]).Inlines);
        Assert.StartsWith("Plain {Bold}bold {Italic}italic {Underline}under {Strikethrough}strike {Highlight}mark x{Superscript}2 H{Subscript}2O small <code:Ctrl> <code:code()>", runs);
        Assert.Contains("<link:docs/page.html \"Docs\">relative</link:>", runs);
        Assert.Contains("<link:https://example.com>abs</link:>", runs);
        Assert.Contains("{Bold}spanbold custom<br:>after break <math:x^2>", runs);
    }

    [Fact]
    public async Task Converts_lists_with_nesting_start_and_tasks()
    {
        var (doc, _) = await ImportHtmlAsync(RichSample);
        var lists = doc.Blocks.OfType<ListBlock>().ToList();
        Assert.Equal(3, lists.Count);

        Assert.False(lists[0].Ordered);
        Assert.True(lists[0].Tight);
        Assert.Equal(2, lists[0].Items.Count);
        Assert.Equal("Two", Assert.IsType<ParagraphBlock>(lists[0].Items[1].Blocks[0]).ToPlainText());
        Assert.Equal("Nested", Assert.IsType<ParagraphBlock>(Assert.IsType<ListBlock>(lists[0].Items[1].Blocks[1]).Items[0].Blocks[0]).ToPlainText());

        Assert.True(lists[1].Ordered);
        Assert.Equal(3, lists[1].Start);

        Assert.Equal([true, false], lists[2].Items.Select(i => i.Checked));
        Assert.Equal("Done", Assert.IsType<ParagraphBlock>(lists[2].Items[0].Blocks[0]).ToPlainText());
    }

    [Fact]
    public async Task Converts_code_quotes_tables_and_figures()
    {
        var (doc, _) = await ImportHtmlAsync(RichSample);

        var code = doc.Blocks.OfType<CodeBlock>().ToList();
        Assert.Equal(2, code.Count);
        Assert.Equal("csharp", code[0].Language);
        Assert.Equal("var x = 1;\n  Console.WriteLine(x);", code[0].Code);
        Assert.Null(code[1].Language);
        Assert.Equal("plain   pre\n  text", code[1].Code);

        var quote = doc.Blocks.OfType<QuoteBlock>().First(q => q.Attribution is not null);
        Assert.Equal("Someone Famous", quote.Attribution);
        Assert.Equal("Quoted text", Assert.IsType<ParagraphBlock>(Assert.Single(quote.Blocks)).ToPlainText());

        var table = Assert.Single(doc.Blocks.OfType<TableBlock>());
        Assert.Equal("Spans", table.Caption);
        Assert.True(table.Rows[0].IsHeader);
        Assert.Equal([ColumnAlignment.Left, ColumnAlignment.Center, ColumnAlignment.Right], table.Alignments);
        Assert.Equal(2, table.Rows[1].Cells[0].ColumnSpan);
        Assert.Equal(2, table.Rows[1].Cells[1].RowSpan);
        Assert.Equal("tall", table.Rows[1].Cells[1].ToPlainText());
        Assert.Equal(3, table.ColumnCount);

        var image = doc.Blocks.OfType<ImageBlock>().First();
        Assert.StartsWith(Document.ResourceScheme, image.Source, StringComparison.Ordinal);
        Assert.Equal("image/png", doc.FindResource(image.Source)!.MediaType);
        Assert.Equal("Dot", image.AltText);
        Assert.Equal("A dot", image.Title);
        Assert.Equal(50, image.WidthPercent);

        Assert.Single(doc.Blocks.OfType<ThematicBreakBlock>());
    }

    [Fact]
    public async Task Converts_callouts_definitions_and_math()
    {
        var (doc, _) = await ImportHtmlAsync(RichSample);

        var callouts = doc.Blocks.OfType<CalloutBlock>().ToList();
        Assert.Equal(4, callouts.Count);
        Assert.Equal((CalloutKind.Note, "More info"), (callouts[0].Kind, callouts[0].Title));
        Assert.Equal("Hidden", Assert.IsType<ParagraphBlock>(Assert.Single(callouts[0].Blocks)).ToPlainText());
        Assert.Equal((CalloutKind.Note, (string?)null), (callouts[1].Kind, callouts[1].Title));
        Assert.Equal((CalloutKind.Warning, "Careful"), (callouts[2].Kind, callouts[2].Title));
        Assert.Equal("Hot", Assert.IsType<ParagraphBlock>(Assert.Single(callouts[2].Blocks)).ToPlainText());
        Assert.Equal((CalloutKind.Tip, (string?)null), (callouts[3].Kind, callouts[3].Title));

        var term = doc.Blocks.OfType<ParagraphBlock>().First(p => p.ToPlainText() == "Term");
        Assert.IsType<StyledInline>(term.Inlines[0]);
        Assert.IsType<QuoteBlock>(doc.Blocks[doc.Blocks.IndexOf(term) + 1]);

        Assert.Equal("E=mc^2", Assert.Single(doc.Blocks.OfType<MathBlock>()).Latex);
        Assert.Contains(doc.Blocks.OfType<ParagraphBlock>(), p => p.ToPlainText() == "Deep inline");
    }

    [Fact]
    public async Task Remote_images_are_kept_as_urls_with_one_warning()
    {
        var (doc, warnings) = await ImportHtmlAsync(RichSample);

        var remote = doc.Blocks.OfType<ImageBlock>().Where(i => i.Source.StartsWith("https://", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, remote.Count);
        Assert.Single(warnings, w => w.Contains("2 remote images", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("embedded element", StringComparison.Ordinal));
        Assert.DoesNotContain(doc.EnumerateAllBlocks(), b => b is HtmlBlock);
    }

    [Fact]
    public async Task Keep_raw_html_keeps_unsupported_elements()
    {
        var (doc, _) = await ImportHtmlAsync(RichSample, options: (HtmlImporter.OptKeepRawHtml, true));

        Assert.Contains(doc.Blocks.OfType<HtmlBlock>(), h => h.Html.StartsWith("<iframe", StringComparison.Ordinal));
        var para = Assert.IsType<ParagraphBlock>(doc.Blocks[1]);
        Assert.Contains(para.Inlines, i => i is HtmlInline { Html: var h } && h.StartsWith("<custom-tag", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Local_images_are_resolved_against_the_source_folder()
    {
        var dir = Directory.CreateTempSubdirectory("aio-html-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "img"));
            await File.WriteAllBytesAsync(Path.Combine(dir.FullName, "img", "dot one.png"), TinyPng);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "not-an-image.png"), "text");
            const string html = """
                <p><img src="img/dot%20one.png" alt="a"></p>
                <p><img src="./img/dot one.png" alt="same file"></p>
                <p><img src="missing.png" alt="gone"></p>
                <p><img src="not-an-image.png" alt="fake"></p>
                """;

            var (doc, warnings) = await ImportHtmlAsync(html, Path.Combine(dir.FullName, "page.html"));
            var images = doc.Blocks.OfType<ImageBlock>().ToList();

            Assert.Equal(4, images.Count);
            Assert.StartsWith(Document.ResourceScheme, images[0].Source, StringComparison.Ordinal);
            Assert.Equal(images[0].Source, images[1].Source);
            Assert.Single(doc.Resources);
            Assert.Equal("missing.png", images[2].Source);
            Assert.Equal("not-an-image.png", images[3].Source);
            Assert.Contains(warnings, w => w.Contains("could not be found", StringComparison.Ordinal));

            var (notEmbedded, _) = await ImportHtmlAsync(html, Path.Combine(dir.FullName, "page.html"), options: (HtmlImporter.OptEmbedImages, false));
            Assert.Empty(notEmbedded.Resources);
            Assert.Equal("img/dot%20one.png", notEmbedded.Blocks.OfType<ImageBlock>().First().Source);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Whitespace_is_collapsed_across_inline_boundaries()
    {
        var (doc, _) = await ImportHtmlAsync("<p>  a   <b> b </b>  c\n d <br>  e  </p><div>loose <em>text</em></div>");

        Assert.Equal("a {Bold}b c d<br:>e", Runs(Assert.IsType<ParagraphBlock>(doc.Blocks[0]).Inlines));
        Assert.Equal("loose {Italic}text", Runs(Assert.IsType<ParagraphBlock>(doc.Blocks[1]).Inlines));
    }

    [Fact]
    public async Task Preserve_line_breaks_turns_newlines_into_breaks()
    {
        var (doc, _) = await ImportHtmlAsync("<p>one\ntwo</p>", options: (HtmlImporter.OptPreserveLineBreaks, true));

        Assert.Equal("one<br:>two", Runs(Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks)).Inlines));
    }

    [Fact]
    public async Task Ignores_permalink_icons_and_hidden_content()
    {
        const string html = """
            <h2 id="install"><a class="anchor" aria-hidden="true" href="#install"><svg></svg></a>Install</h2>
            <p hidden>secret</p><p style="display:none">gone</p><p>Shown</p>
            """;
        var (doc, _) = await ImportHtmlAsync(html);

        Assert.Equal("Install", Assert.IsType<HeadingBlock>(doc.Blocks[0]).ToPlainText());
        Assert.Equal("Shown", Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks.Skip(1))).ToPlainText());
    }

    [Fact]
    public async Task Reads_footnotes_and_page_breaks()
    {
        const string html = """
            <p>Claim<sup class="footnote-ref"><a href="#fn1" id="fnref1">[1]</a></sup>.</p>
            <div style="page-break-after: always"></div>
            <p>Next page</p>
            <section class="footnotes"><ol><li id="fn1"><p>Source. <a href="#fnref1" class="footnote-backref">↩</a></p></li></ol></section>
            """;
        var (doc, _) = await ImportHtmlAsync(html);

        Assert.Equal("Claim<fn:1>.", Runs(Assert.IsType<ParagraphBlock>(doc.Blocks[0]).Inlines));
        Assert.IsType<PageBreakBlock>(doc.Blocks[1]);
        var footnote = Assert.Single(doc.Footnotes);
        Assert.Equal("1", footnote.Label);
        Assert.Equal("Source.", Assert.IsType<ParagraphBlock>(Assert.Single(footnote.Blocks)).ToPlainText());
        Assert.Equal(3, doc.Blocks.Count);
    }

    [Fact]
    public async Task Xhtml_self_closing_tags_do_not_swallow_content()
    {
        const string xhtml = """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>X</title></head>
            <body><p><a id="anchor"/>First</p><p>Second</p></body></html>
            """;
        var (doc, _) = await ImportHtmlAsync(xhtml, fileName: "page.xhtml");

        Assert.Equal(["First", "Second"], doc.Blocks.OfType<ParagraphBlock>().Select(p => p.ToPlainText()));
    }
}
