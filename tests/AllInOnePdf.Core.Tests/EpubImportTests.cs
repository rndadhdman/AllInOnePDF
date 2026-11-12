using System.IO.Compression;
using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;
using Microsoft.Extensions.DependencyInjection;
using static AllInOnePdf.Core.Tests.HtmlTestHelpers;

namespace AllInOnePdf.Core.Tests;

public class EpubImportTests
{
    private const string Opf = """
        <?xml version="1.0" encoding="UTF-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:identifier id="uid">urn:uuid:1234</dc:identifier>
            <dc:title>Test Book</dc:title>
            <dc:creator>Jane Doe</dc:creator>
            <dc:creator>John Roe</dc:creator>
            <dc:language>en</dc:language>
            <dc:description>&lt;p&gt;A &lt;b&gt;tiny&lt;/b&gt; book.&lt;/p&gt;</dc:description>
            <dc:date>2023-05-01</dc:date>
            <dc:subject>Fiction</dc:subject>
            <dc:subject>Tests</dc:subject>
          </metadata>
          <manifest>
            <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
            <item id="cover" href="Text/cover.xhtml" media-type="application/xhtml+xml"/>
            <item id="ch1" href="Text/ch1.xhtml" media-type="application/xhtml+xml"/>
            <item id="ch2" href="Text/ch%202.xhtml" media-type="application/xhtml+xml"/>
            <item id="img" href="Images/dot.png" media-type="image/png" properties="cover-image"/>
          </manifest>
          <spine>
            <itemref idref="cover"/>
            <itemref idref="ch1"/>
            <itemref idref="ch2"/>
          </spine>
        </package>
        """;

    private const string Nav = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
        <head><title>Contents</title></head>
        <body><nav epub:type="toc"><ol>
          <li><a href="Text/ch1.xhtml">Chapter One</a></li>
          <li><a href="Text/ch%202.xhtml">Chapter Two</a></li>
        </ol></nav></body></html>
        """;

    private const string Cover = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Cover</title></head>
        <body><h1>Test Book</h1><div><img src="../Images/dot.png" alt="Cover"/></div></body></html>
        """;

    private const string Chapter1 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml"><head><title>One</title><link rel="stylesheet" href="../style.css"/></head>
        <body>
          <h1>Chapter One</h1>
          <p>First chapter, <a href="ch%202.xhtml#s2">see two</a> or <a href="ch%202.xhtml">chapter two</a>.<a id="end"/></p>
          <p><img src="../Images/dot.png" alt="dot"/></p>
        </body></html>
        """;

    private const string Chapter2 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Two</title></head>
        <body><section><p id="s2">Second chapter text.</p><p><a href="ch1.xhtml">back</a> <a href="https://example.com">web</a></p></section></body></html>
        """;

    private static byte[] BuildEpub(Action<ZipArchive>? extra = null, bool includeStandard = true)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }

            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            if (includeStandard)
            {
                Add("OEBPS/content.opf", Opf);
                Add("OEBPS/nav.xhtml", Nav);
                Add("OEBPS/Text/cover.xhtml", Cover);
                Add("OEBPS/Text/ch1.xhtml", Chapter1);
                Add("OEBPS/Text/ch 2.xhtml", Chapter2);
                using var img = zip.CreateEntry("OEBPS/Images/dot.png").Open();
                img.Write(TinyPng);
            }

            extra?.Invoke(zip);
        }

        return ms.ToArray();
    }

    private static async Task<(Document Document, List<string> Warnings)> ImportEpubAsync(byte[] epub, params (string Key, object Value)[] options)
    {
        var importer = new EpubImporter();
        var values = new OptionValues();
        foreach (var (key, value) in options)
        {
            values.Set(key, value);
        }

        using var stream = new MemoryStream(epub);
        var context = new ImportContext { Input = stream, FileName = "book.epub", Options = values.WithDefaults(importer.Options) };
        var document = await importer.ImportAsync(context);
        return (document, context.Warnings);
    }

    [Fact]
    public async Task Imports_metadata_chapters_images_and_breaks()
    {
        var (doc, warnings) = await ImportEpubAsync(BuildEpub());

        Assert.Equal("Test Book", doc.Metadata.Title);
        Assert.Equal(["Jane Doe", "John Roe"], doc.Metadata.Authors);
        Assert.Equal("en", doc.Metadata.Language);
        Assert.Equal("A tiny book.", doc.Metadata.Description);
        Assert.Equal(new DateTimeOffset(2023, 5, 1, 0, 0, 0, TimeSpan.Zero), doc.Metadata.Date);
        Assert.Equal(["Fiction", "Tests"], doc.Metadata.Keywords);
        Assert.Equal("epub", doc.Metadata.SourceFormat);

        // Cover skipped by default; chapter 1, a page break, chapter 2.
        var h1 = Assert.IsType<HeadingBlock>(doc.Blocks[0]);
        Assert.Equal("Chapter One", h1.ToPlainText());
        var image = Assert.IsType<ImageBlock>(doc.Blocks[2]);
        Assert.Equal("image/png", doc.FindResource(image.Source)!.MediaType);
        Assert.Single(doc.Resources);
        Assert.IsType<PageBreakBlock>(doc.Blocks[3]);
        Assert.Equal("Second chapter text.", Assert.IsType<ParagraphBlock>(doc.Blocks[4]).ToPlainText());
        Assert.Single(doc.Blocks.OfType<PageBreakBlock>());
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Rewrites_links_between_chapters()
    {
        var (doc, _) = await ImportEpubAsync(BuildEpub());
        var links = doc.EnumerateAllBlocks().OfType<InlineContainerBlock>().SelectMany(b => b.Inlines).OfType<LinkInline>().ToList();

        Assert.Equal("#s2", links[0].Url);
        Assert.Equal("#", links[1].Url);
        Assert.Equal("#" + doc.Blocks.OfType<HeadingBlock>().First().Id, links[2].Url);
        Assert.Equal("https://example.com", links[3].Url);
    }

    [Fact]
    public async Task Heading_breaks_add_titles_from_the_navigation()
    {
        var (doc, _) = await ImportEpubAsync(BuildEpub(), (EpubImporter.OptChapterBreaks, "headings"));

        var headings = doc.Blocks.OfType<HeadingBlock>().Select(h => h.ToPlainText()).ToList();
        Assert.Equal(["Chapter One", "Chapter Two"], headings);
        Assert.DoesNotContain(doc.Blocks, b => b is PageBreakBlock);
    }

    [Fact]
    public async Task No_breaks_runs_chapters_together()
    {
        var (doc, _) = await ImportEpubAsync(BuildEpub(), (EpubImporter.OptChapterBreaks, "none"));

        Assert.DoesNotContain(doc.Blocks, b => b is PageBreakBlock);
        Assert.Single(doc.Blocks.OfType<HeadingBlock>());
    }

    [Fact]
    public async Task Cover_is_kept_without_its_duplicate_title_when_requested()
    {
        var (doc, _) = await ImportEpubAsync(BuildEpub(), (EpubImporter.OptIncludeCover, true));

        var cover = Assert.IsType<ImageBlock>(doc.Blocks[0]);
        Assert.Equal("Cover", cover.AltText);
        Assert.IsType<PageBreakBlock>(doc.Blocks[1]);
        Assert.Equal("Chapter One", Assert.IsType<HeadingBlock>(doc.Blocks[2]).ToPlainText());
        Assert.DoesNotContain(doc.Blocks.OfType<HeadingBlock>(), h => h.ToPlainText() == "Test Book");
        Assert.Single(doc.Resources);
    }

    [Fact]
    public async Task Images_can_be_left_out()
    {
        var (doc, _) = await ImportEpubAsync(BuildEpub(), (EpubImporter.OptEmbedImages, false));

        Assert.Empty(doc.Resources);
        Assert.Equal("../Images/dot.png", doc.Blocks.OfType<ImageBlock>().Single().Source);
    }

    [Fact]
    public async Task Drm_protected_epub_is_rejected()
    {
        var epub = BuildEpub(zip =>
        {
            using var writer = new StreamWriter(zip.CreateEntry("META-INF/encryption.xml").Open());
            writer.Write("""
                <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
                  <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/>
                  <enc:CipherData><enc:CipherReference URI="OEBPS/Text/ch1.xhtml"/></enc:CipherData></enc:EncryptedData>
                </encryption>
                """);
        });

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => ImportEpubAsync(epub));
        Assert.Contains("DRM-protected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Font_obfuscation_is_not_treated_as_drm()
    {
        var epub = BuildEpub(zip =>
        {
            using var writer = new StreamWriter(zip.CreateEntry("META-INF/encryption.xml").Open());
            writer.Write("""
                <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
                  <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.idpf.org/2008/embedding"/>
                  <enc:CipherData><enc:CipherReference URI="OEBPS/Fonts/a.otf"/></enc:CipherData></enc:EncryptedData>
                </encryption>
                """);
        });

        var (doc, _) = await ImportEpubAsync(epub);
        Assert.Equal("Test Book", doc.Metadata.Title);
    }

    [Theory]
    [InlineData("../evil.xhtml")]
    [InlineData("OEBPS/../../evil.xhtml")]
    [InlineData("/abs/evil.xhtml")]
    [InlineData("C:/evil.xhtml")]
    public async Task Unsafe_entry_paths_are_rejected(string entryName)
    {
        var epub = BuildEpub(zip =>
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write("<html/>");
        });

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportEpubAsync(epub));
        Assert.Contains("unsafe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_a_zip_gives_a_clear_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportEpubAsync(Encoding.UTF8.GetBytes("plain text")));
        Assert.Contains("not a valid EPUB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_package_gives_a_clear_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportEpubAsync(BuildEpub(includeStandard: false)));
        Assert.Contains("package document", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registered_in_core_services()
    {
        var services = new ServiceCollection();
        services.AddAllInOnePdfCore(Path.Combine(Path.GetTempPath(), "aio-epub-di-" + Guid.NewGuid().ToString("N")));
        await using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<FormatRegistry>();

        Assert.IsType<EpubImporter>(registry.FindImporter("book.epub"));
        Assert.IsType<HtmlImporter>(registry.FindImporter("page.html"));
        Assert.NotNull(registry.FindExporter("html"));
        Assert.True(registry.Resolve("book.epub", "html")!.IsAvailable);
    }
}
