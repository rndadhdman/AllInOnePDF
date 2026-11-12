using System.Text;
using System.Xml.Linq;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Highlighting;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using Microsoft.Extensions.DependencyInjection;
using static AllInOnePdf.Core.Tests.EpubExportTestHelpers;

namespace AllInOnePdf.Core.Tests;

public class EpubExportTests
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
    public async Task Sample_exports_to_a_valid_package(string sample, string themeId)
    {
        var doc = await LoadAsync(sample);
        var epub = await ExportAsync(doc, BuiltInThemes.Find(themeId), o => o.Set(EpubExporter.OptCover, "yes"));

        AssertValidPackage(epub);
        Assert.Contains(epub.Manifest, m => m.Path == "OEBPS/text/cover.xhtml");
        Assert.Equal("cover", epub.Spine[0]);
        Assert.Contains(epub.Manifest, m => m.Properties == "nav");
        Assert.Contains("nav", epub.Spine);
    }

    [Fact]
    public async Task Mimetype_is_the_first_entry_and_stored()
    {
        var epub = await ExportAsync(await LoadAsync("welcome"));

        Assert.Equal("mimetype", epub.EntryOrder[0]);
        var b = epub.Bytes;
        Assert.Equal(0x04034b50, BitConverter.ToInt32(b, 0));
        Assert.Equal(0, BitConverter.ToInt16(b, 8));   // compression method: stored
        Assert.Equal(8, BitConverter.ToInt16(b, 26));  // file name length
        Assert.Equal(0, BitConverter.ToInt16(b, 28));  // no extra field
        Assert.Equal("mimetype", Encoding.ASCII.GetString(b, 30, 8));
        Assert.Equal("application/epub+zip", Encoding.ASCII.GetString(b, 38, 20));
        Assert.Equal(20, epub.MimetypeCompressedLength);
        Assert.Equal("application/epub+zip", epub.Text("mimetype"));
    }

    [Fact]
    public async Task Opf_carries_the_metadata()
    {
        var doc = await LoadAsync("sample-document.md");
        var epub = await ExportAsync(doc, configure: o => o.Set(EpubExporter.OptLanguage, "en-GB"));
        var root = epub.Opf.Root!;
        var meta = root.Element(Opf + "metadata")!;

        Assert.Equal("3.0", (string)root.Attribute("version")!);
        var uid = (string)root.Attribute("unique-identifier")!;
        var identifier = meta.Elements(Dc + "identifier").Single(e => (string?)e.Attribute("id") == uid);
        Assert.StartsWith("urn:uuid:", identifier.Value);
        Assert.True(Guid.TryParse(identifier.Value["urn:uuid:".Length..], out _));
        Assert.Equal("Field Notes on Typography", meta.Element(Dc + "title")!.Value);
        Assert.Equal("AllInOne PDF", meta.Element(Dc + "creator")!.Value);
        Assert.Equal("en-GB", meta.Element(Dc + "language")!.Value);
        Assert.Equal("2026-11-05", meta.Element(Dc + "date")!.Value);
        Assert.Equal(["sample", "typography", "markdown"], meta.Elements(Dc + "subject").Select(e => e.Value));
        var modified = meta.Elements(Opf + "meta").Single(e => (string?)e.Attribute("property") == "dcterms:modified").Value;
        Assert.Matches(ModifiedFormat(), modified);
        Assert.Equal("ncx", (string)root.Element(Opf + "spine")!.Attribute("toc")!);
        Assert.Equal("application/x-dtbncx+xml", epub.Item("ncx").MediaType);
    }

    [Fact]
    public async Task Custom_identifier_and_language_from_metadata()
    {
        var doc = await FromMarkdownAsync("# One\n\nText.");
        doc.Metadata.Language = "de";
        var epub = await ExportAsync(doc, configure: o => o.Set(EpubExporter.OptIdentifier, "urn:isbn:9780000000002"));
        var meta = epub.Opf.Root!.Element(Opf + "metadata")!;

        Assert.Equal("urn:isbn:9780000000002", meta.Element(Dc + "identifier")!.Value);
        Assert.Equal("de", meta.Element(Dc + "language")!.Value);
        Assert.Contains("xml:lang=\"de\"", epub.Text("OEBPS/text/chapter-001.xhtml"));
    }

    [Theory]
    [InlineData("welcome", "h1", 1)]
    [InlineData("welcome", "h2", 5)]
    [InlineData("welcome", "none", 1)]
    [InlineData("sample-document.md", "h1", 1)]
    [InlineData("sample-document.md", "h2", 6)]
    public async Task Chapter_split_controls_the_chapter_count(string sample, string split, int expected)
    {
        var epub = await ExportAsync(await LoadAsync(sample), configure: o => o.Set(EpubExporter.OptChapterSplit, split));

        Assert.Equal(expected, epub.Chapters.Count);
        AssertValidPackage(epub);
    }

    [Fact]
    public async Task Page_breaks_split_chapters_or_become_markers()
    {
        var doc = new Document();
        doc.Blocks.Add(new HeadingBlock(1, "Part one"));
        doc.Blocks.Add(new ParagraphBlock("First."));
        doc.Blocks.Add(new PageBreakBlock());
        doc.Blocks.Add(new ParagraphBlock("Second, no heading."));
        doc.Blocks.Add(new PageBreakBlock());
        doc.Blocks.Add(new PageBreakBlock());
        doc.Blocks.Add(new ParagraphBlock("Third."));

        var split = await ExportAsync(doc, configure: o => o.Set(EpubExporter.OptChapterSplit, "page-breaks"));
        Assert.Equal(3, split.Chapters.Count);
        Assert.DoesNotContain("aio-pagebreak", string.Concat(split.Chapters.Select(c => split.Text(c.Path))));
        AssertValidPackage(split);

        var single = await ExportAsync(doc, configure: o => o.Set(EpubExporter.OptChapterSplit, "h1"));
        var chapter = Assert.Single(single.Chapters);
        Assert.Contains("<div class=\"aio-pagebreak\"></div>", single.Text(chapter.Path));
    }

    [Fact]
    public async Task Nav_nests_headings_up_to_the_theme_depth()
    {
        var theme = BuiltInThemes.Clean();
        theme.Toc.Depth = 2;
        var epub = await ExportAsync(await LoadAsync("sample-document.md"), theme, o => o.Set(EpubExporter.OptChapterSplit, "h2"));
        var nav = ParseXml(epub.Text("OEBPS/nav.xhtml"));
        var toc = nav.Descendants(Xhtml + "nav").Single(n => (string?)n.Attribute(Ops + "type") == "toc");

        var top = toc.Element(Xhtml + "ol")!.Elements(Xhtml + "li").ToList();
        Assert.Single(top);
        Assert.Equal("Field Notes on Typography", top[0].Element(Xhtml + "a")!.Value);
        var second = top[0].Element(Xhtml + "ol")!.Elements(Xhtml + "li").ToList();
        Assert.Equal(5, second.Count);
        Assert.All(second, li => Assert.Null(li.Element(Xhtml + "ol"))); // depth 2: no h3 entries
        Assert.StartsWith("text/chapter-002.xhtml#", (string)second[0].Element(Xhtml + "a")!.Attribute("href")!);

        var landmarks = nav.Descendants(Xhtml + "nav").Single(n => (string?)n.Attribute(Ops + "type") == "landmarks");
        Assert.Contains(landmarks.Descendants(Xhtml + "a"), a => (string?)a.Attribute(Ops + "type") == "bodymatter");
    }

    [Fact]
    public async Task Numbered_headings_carry_numbers_into_the_nav_and_reset_per_chapter()
    {
        var epub = await ExportAsync(await LoadAsync("welcome"), BuiltInThemes.Technical(), o => o.Set(EpubExporter.OptChapterSplit, "h2"));

        var nav = epub.Text("OEBPS/nav.xhtml");
        Assert.Contains("1.1  Typography", nav);
        Assert.Contains("counter-reset:aioh1 1 aioh2 1", epub.Text("OEBPS/text/chapter-003.xhtml"));
        Assert.Contains("counter-increment:aioh2", epub.Css);
    }

    [Fact]
    public async Task Footnotes_become_epub_asides_in_the_referencing_chapter()
    {
        var epub = await ExportAsync(await LoadAsync("sample-document.md"), configure: o => o.Set(EpubExporter.OptChapterSplit, "h2"));
        var withNote = epub.Chapters.Select(c => (c.Path, Xml: ParseXml(epub.Text(c.Path))))
            .Single(c => c.Xml.Descendants(Xhtml + "aside").Any(a => (string?)a.Attribute(Ops + "type") == "footnote"));

        var aside = withNote.Xml.Descendants(Xhtml + "aside").Single(a => (string?)a.Attribute(Ops + "type") == "footnote");
        var noteref = withNote.Xml.Descendants(Xhtml + "a").Single(a => (string?)a.Attribute(Ops + "type") == "noteref");
        Assert.Equal("#" + (string)aside.Attribute("id")!, (string)noteref.Attribute("href")!);
        Assert.Contains("Footnotes are collected", aside.Value);
        Assert.Contains("3. Callouts", withNote.Xml.Descendants(Xhtml + "h2").First().Value);
        Assert.DoesNotContain(epub.Chapters, c => c.Path != withNote.Path && epub.Text(c.Path).Contains("epub:type=\"footnote\""));
    }

    [Fact]
    public async Task Internal_links_point_at_the_chapter_holding_the_anchor()
    {
        var doc = await FromMarkdownAsync("# Start\n\nSee [the end](#the-end) and [nowhere](#missing).\n\n# The end\n\nDone, back to [start](#start).");
        var epub = await ExportAsync(doc);

        Assert.Equal(2, epub.Chapters.Count);
        var first = epub.Text("OEBPS/text/chapter-001.xhtml");
        Assert.Contains("href=\"chapter-002.xhtml#the-end\"", first);
        Assert.DoesNotContain("#missing", first);
        Assert.Contains("href=\"chapter-001.xhtml#start\"", epub.Text("OEBPS/text/chapter-002.xhtml"));
        AssertValidPackage(epub);
    }

    [Fact]
    public async Task Images_become_package_items_and_unsupported_ones_are_dropped()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var doc = new Document();
        var res = doc.AddResource("image/png", png, "photo.png");
        var bmp = doc.AddResource("image/bmp", [0x42, 0x4D, 0, 0], "old.bmp");
        doc.Blocks.Add(new ImageBlock { Source = res.Reference, AltText = "A photo" });
        doc.Blocks.Add(new ImageBlock { Source = res.Reference, AltText = "Same photo again" });
        doc.Blocks.Add(new ImageBlock { Source = "data:image/png;base64," + Convert.ToBase64String([.. png, 0]), AltText = "Inline" });
        doc.Blocks.Add(new ImageBlock { Source = "https://example.com/remote.png", AltText = "Remote" });
        doc.Blocks.Add(new ImageBlock { Source = bmp.Reference, AltText = "Bitmap" });

        var epub = await ExportAsync(doc);

        var images = epub.Manifest.Where(m => m.MediaType.StartsWith("image/", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, images.Count);
        Assert.All(images, i => Assert.Equal("image/png", i.MediaType));
        var chapter = epub.Text("OEBPS/text/chapter-001.xhtml");
        Assert.Contains("src=\"../images/", chapter);
        Assert.DoesNotContain("data:", chapter);
        Assert.DoesNotContain("example.com", chapter);
        Assert.Contains(epub.Warnings, w => w.Contains("remote", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(epub.Warnings, w => w.Contains("PNG, JPEG", StringComparison.Ordinal));
        AssertValidPackage(epub);
    }

    [Fact]
    public async Task Task_items_use_ballot_boxes_instead_of_inputs()
    {
        var epub = await ExportAsync(await LoadAsync("welcome"));
        var chapter = epub.Text("OEBPS/text/chapter-001.xhtml");

        Assert.DoesNotContain("<input", chapter);
        Assert.Contains("☑", chapter);
        Assert.Contains("☐", chapter);
        Assert.Contains("<br />", (await ExportAsync(await FromMarkdownAsync("a\\\nb"))).Text("OEBPS/text/chapter-001.xhtml"));
    }

    [Fact]
    public async Task Raw_html_is_dropped_unless_allowed_and_then_sanitised()
    {
        var doc = new Document();
        doc.Blocks.Add(new ParagraphBlock("Before"));
        doc.Blocks.Add(new HtmlBlock { Html = "<div class=\"box\" onclick=\"evil()\"><p>Kept<br>line<script>alert(1)</script></p><img src=\"https://x.test/a.png\"></div>" });
        doc.Blocks.Add(new HtmlBlock { Html = "<p>unclosed <b>bold" });
        doc.Blocks.Add(new MathBlock { Latex = "a < b & c" });

        var dropped = await ExportAsync(doc);
        var text = dropped.Text("OEBPS/text/chapter-001.xhtml");
        Assert.DoesNotContain("Kept", text);
        Assert.Contains(dropped.Warnings, w => w.Contains("raw HTML", StringComparison.Ordinal));
        Assert.Contains("<pre class=\"aio-math\"><code>a &lt; b &amp; c</code></pre>", text);

        var allowed = await ExportAsync(doc, configure: o => o.Set(EpubExporter.OptAllowRawHtml, true));
        var html = allowed.Text("OEBPS/text/chapter-001.xhtml");
        Assert.Contains("Kept", html);
        Assert.Contains("<b>bold</b>", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("x.test", html);
        AssertValidPackage(allowed);
    }

    [Fact]
    public async Task Embed_fonts_adds_font_items_face_rules_and_the_licence()
    {
        var plain = await ExportAsync(await LoadAsync("welcome"), BuiltInThemes.Manuscript());
        Assert.DoesNotContain(plain.Manifest, m => m.Path.StartsWith("OEBPS/fonts/", StringComparison.Ordinal));
        Assert.DoesNotContain("@font-face", plain.Css);
        Assert.Contains("font-family:\"Literata\",serif", plain.Css);

        var embedded = await ExportAsync(await LoadAsync("welcome"), BuiltInThemes.Manuscript(), o => o.Set(EpubExporter.OptEmbedFonts, true));
        var fonts = embedded.Manifest.Where(m => m.MediaType == "font/ttf").ToList();
        Assert.Contains(fonts, f => f.Path == "OEBPS/fonts/Literata-Regular.ttf");
        Assert.Contains(fonts, f => f.Path == "OEBPS/fonts/JetBrainsMono-Regular.ttf");
        Assert.DoesNotContain(fonts, f => f.Path.Contains("Inter", StringComparison.Ordinal)); // Manuscript does not use Inter
        Assert.Contains(embedded.Manifest, m => m.Path == "OEBPS/fonts/OFL.txt");
        Assert.Contains("SIL OPEN FONT LICENSE", embedded.Text("OEBPS/fonts/OFL.txt"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@font-face {font-family:\"Literata\";src:url(\"../fonts/Literata-Regular.ttf\")", embedded.Css);
        Assert.Contains("font-weight:700;font-style:italic", embedded.Css);
        AssertValidPackage(embedded);
    }

    [Fact]
    public async Task Respect_reader_theme_leaves_colours_to_the_reader()
    {
        var doc = await LoadAsync("welcome");
        var respect = (await ExportAsync(doc, BuiltInThemes.Night())).Css;
        var body = Rule(respect, "body");
        Assert.DoesNotContain("color", body);
        Assert.DoesNotContain("background", body);
        Assert.DoesNotContain("color", Rule(respect, ".aio-h"));
        Assert.DoesNotContain("#14161B", respect); // Night's code background
        Assert.Contains("@media (prefers-color-scheme: dark)", respect);
        Assert.Contains("font-size:1.045em", body); // 11.5 pt relative to the reader's 11 pt
        Assert.Contains(".hl-kw {color:#CF222E;}", respect); // light palette by default ...
        Assert.Contains(".hl-kw {color:#C678DD;}", respect); // ... dark palette in the dark media query

        var fixedCss = (await ExportAsync(doc, BuiltInThemes.Night(), o => o.Set(EpubExporter.OptRespectReaderTheme, false))).Css;
        Assert.DoesNotContain("prefers-color-scheme", fixedCss);
        Assert.Contains("color:#C9CDD4", Rule(fixedCss, "body"));
        Assert.Contains("background-color:#1B1E24", Rule(fixedCss, "body")); // dark text colours need their page
        Assert.Contains("background-color:#14161B", fixedCss);
        Assert.Contains(".hl-kw {color:#C678DD;}", fixedCss);

        var light = (await ExportAsync(doc, BuiltInThemes.Sepia(), o => o.Set(EpubExporter.OptRespectReaderTheme, false))).Css;
        Assert.DoesNotContain("background-color:#F7F1E3", light); // page backgrounds stay out by default
        var withPage = (await ExportAsync(doc, BuiltInThemes.Sepia(), o => o.Set(EpubExporter.OptRespectReaderTheme, false).Set(EpubExporter.OptApplyPageBackground, true))).Css;
        Assert.Contains("background-color:#F7F1E3", Rule(withPage, "body"));
    }

    [Fact]
    public async Task Stylesheet_follows_the_theme()
    {
        var css = (await ExportAsync(await LoadAsync("welcome"), BuiltInThemes.Technical())).Css;

        Assert.Contains("table.aio-table th, table.aio-table td {border:0.75pt solid", css); // Grid tables
        Assert.Contains("h1.aio-h {font-size:2.286em", css); // 24 / 10.5
        Assert.Contains("border-bottom:1.25pt solid #0B5CAD", css); // H1 rule
        Assert.Contains("aside.aio-callout-tip {", css);
        Assert.Contains("figure.aio-codeblock figcaption {display:block", css); // language label
        Assert.Contains("white-space:pre-wrap", css);
        Assert.DoesNotContain("NaN", css);
    }

    [Fact]
    public async Task Cover_option_controls_the_title_page_and_nav_can_leave_the_spine()
    {
        var doc = await LoadAsync("welcome");
        var none = await ExportAsync(doc, BuiltInThemes.Manuscript(), o => o.Set(EpubExporter.OptCover, "no").Set(EpubExporter.OptIncludeNav, false));
        Assert.DoesNotContain(none.Manifest, m => m.Path.EndsWith("cover.xhtml", StringComparison.Ordinal));
        Assert.Contains(none.Manifest, m => m.Properties == "nav");
        Assert.DoesNotContain("nav", none.Spine);
        AssertValidPackage(none);

        var themed = await ExportAsync(doc, BuiltInThemes.Manuscript()); // Manuscript has a cover
        var cover = themed.Text("OEBPS/text/cover.xhtml");
        Assert.Contains("Designing a Theme", cover);
        Assert.Contains("A short sample document for previewing PDF themes", cover);
        Assert.DoesNotContain(themed.Manifest, m => m.Properties == "cover-image"); // no renderer supplied
        Assert.Contains("<reference type=\"cover\"", themed.Text("OEBPS/content.opf"));
    }

    [Fact]
    public async Task Cover_image_is_rendered_from_the_pdf_cover_page()
    {
        var doc = await LoadAsync("welcome");
        var exporter = new EpubExporter(new SyntaxHighlighter(), new PdfRenderer(), new PdfiumRasterizer());

        var epub = await ExportAsync(doc, BuiltInThemes.Technical(), exporter: exporter);
        var image = epub.Item("cover-image");
        Assert.Equal("OEBPS/images/cover.jpg", image.Path);
        Assert.Equal("cover-image", image.Properties);
        Assert.Equal("image/jpeg", image.MediaType);
        var jpeg = epub.Entries[image.Path];
        Assert.True(jpeg.Length > 2000);
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.Contains("<meta name=\"cover\" content=\"cover-image\" />", epub.Text("OEBPS/content.opf"));
        AssertValidPackage(epub);

        var noImage = await ExportAsync(doc, BuiltInThemes.Technical(), o => o.Set(EpubExporter.OptCoverImage, false), exporter);
        Assert.DoesNotContain(noImage.Manifest, m => m.Properties == "cover-image");
        Assert.Contains(noImage.Manifest, m => m.Path == "OEBPS/text/cover.xhtml");
    }

    [Fact]
    public async Task Empty_document_still_makes_a_valid_book()
    {
        var epub = await ExportAsync(new Document());

        Assert.Single(epub.Chapters);
        Assert.Equal("book", epub.Opf.Root!.Element(Opf + "metadata")!.Element(Dc + "title")!.Value);
        AssertValidPackage(epub);
    }

    [Fact]
    public void Exporter_is_registered()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aio-epub-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var sp = new ServiceCollection().AddLogging().AddAllInOnePdfCore(dir).BuildServiceProvider();
            var exporter = Assert.Single(sp.GetServices<IExporter>(), e => e.Format == KnownFormats.Epub);
            Assert.True(exporter.SupportsTheme);
            Assert.Contains(exporter.Options, o => o.Key == EpubExporter.OptChapterSplit && o.Type == OptionType.Choice);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>The declarations of the first top-level rule whose selector is exactly <paramref name="selector"/>.</summary>
    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf("\n" + selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No rule for {selector}");
        var open = css.IndexOf('{', start);
        return css[(open + 1)..css.IndexOf('}', open)];
    }

    /// <summary>Structural checks a strict reading system (and epubcheck) would make.</summary>
    private static void AssertValidPackage(EpubPackageView epub)
    {
        // OCF
        Assert.Equal("mimetype", epub.EntryOrder[0]);
        Assert.Equal("application/epub+zip", epub.Text("mimetype"));
        var container = ParseXml(epub.Text("META-INF/container.xml"));
        var rootfile = container.Descendants(Container + "rootfile").Single();
        Assert.Equal("OEBPS/content.opf", (string)rootfile.Attribute("full-path")!);
        Assert.Equal("application/oebps-package+xml", (string)rootfile.Attribute("media-type")!);

        // Manifest ↔ zip
        Assert.Equal(epub.Manifest.Count, epub.Manifest.Select(m => m.Id).Distinct().Count());
        foreach (var item in epub.Manifest)
        {
            Assert.True(epub.Entries.ContainsKey(item.Path), $"Manifest item {item.Path} is missing from the zip");
        }

        foreach (var path in epub.Entries.Keys.Where(k => k.StartsWith("OEBPS/", StringComparison.Ordinal) && k != "OEBPS/content.opf"))
        {
            Assert.Contains(epub.Manifest, m => m.Path == path);
        }

        foreach (var idref in epub.Spine)
        {
            Assert.Contains(epub.Manifest, m => m.Id == idref && m.MediaType == "application/xhtml+xml");
        }

        Assert.Single(epub.Manifest, m => m.Properties == "nav");
        Assert.Contains(epub.Manifest, m => m.Path == "OEBPS/css/style.css" && m.MediaType == "text/css");
        Assert.Contains(epub.Manifest, m => m.Path == "OEBPS/toc.ncx");
        Assert.NotEmpty(epub.Chapters);

        // Every content document is well-formed XHTML; collect ids for link checks.
        var ids = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var docs = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        foreach (var path in epub.XhtmlPaths)
        {
            var xml = epub.Text(path);
            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
            var doc = ParseXml(xml);
            Assert.Equal(Xhtml + "html", doc.Root!.Name);
            Assert.NotNull(doc.Root.Element(Xhtml + "head")!.Element(Xhtml + "title"));
            Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName is "input" or "script");
            docs[path] = doc;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in doc.Descendants().Select(e => (string?)e.Attribute("id")).Where(i => i is not null))
            {
                Assert.True(set.Add(id!), $"Duplicate id {id} in {path}");
            }

            ids[path] = set;
        }

        void AssertTarget(string from, string href)
        {
            var (path, fragment) = Resolve(from, href);
            Assert.True(ids.ContainsKey(path), $"{from} links to missing {path}");
            if (fragment is not null)
            {
                Assert.True(ids[path].Contains(fragment), $"{from} links to missing #{fragment} in {path}");
            }
        }

        // Nav and NCX targets resolve.
        var nav = docs["OEBPS/nav.xhtml"];
        var toc = nav.Descendants(Xhtml + "nav").Single(n => (string?)n.Attribute(Ops + "type") == "toc");
        var navLinks = toc.Descendants(Xhtml + "a").ToList();
        Assert.NotEmpty(navLinks);
        foreach (var a in nav.Descendants(Xhtml + "a"))
        {
            AssertTarget("OEBPS/nav.xhtml", (string)a.Attribute("href")!);
        }

        foreach (var li in toc.Descendants(Xhtml + "li"))
        {
            Assert.NotNull(li.Element(Xhtml + "a"));
        }

        var ncx = ParseXml(epub.Text("OEBPS/toc.ncx"));
        Assert.NotEmpty(ncx.Descendants(Ncx + "navPoint"));
        foreach (var content in ncx.Descendants(Ncx + "content"))
        {
            AssertTarget("OEBPS/toc.ncx", (string)content.Attribute("src")!);
        }

        // Links and images inside content documents.
        foreach (var (path, doc) in docs)
        {
            foreach (var a in doc.Descendants(Xhtml + "a"))
            {
                var href = (string?)a.Attribute("href");
                if (href is null || href.Contains(':', StringComparison.Ordinal))
                {
                    continue;
                }

                AssertTarget(path, href);
            }

            foreach (var img in doc.Descendants(Xhtml + "img"))
            {
                var (target, _) = Resolve(path, (string)img.Attribute("src")!);
                Assert.True(epub.Entries.ContainsKey(target), $"{path} shows missing image {target}");
                Assert.Contains(epub.Manifest, m => m.Path == target);
                Assert.NotNull(img.Attribute("alt"));
            }
        }
    }
}
