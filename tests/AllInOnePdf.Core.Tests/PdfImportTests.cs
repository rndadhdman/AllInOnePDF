using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Importers.Pdf;
using AllInOnePdf.Core.Themes;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Outline.Destinations;

namespace AllInOnePdf.Core.Tests;

public sealed class PdfImportTests
{
    [Fact]
    public void Importer_is_ready_and_declares_its_options()
    {
        var importer = new PdfImporter();
        Assert.Equal(ConverterStatus.Ready, ((IImporter)importer).Status);
        Assert.Equal("pdf", importer.Format.Id);
        var keys = importer.Options.Select(o => o.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        foreach (var key in new[] { "pages", "useBookmarks", "detectHeadings", "headingSensitivity", "detectLists", "detectCode", "detectTables", "extractImages", "minImageSizePx", "removeHeadersFooters", "mergeHyphenation", "pageBreaks", "layout", "password" })
        {
            Assert.Contains(key, keys);
        }

        Assert.Equal(OptionType.PageRange, importer.Options.Single(o => o.Key == "pages").Type);
        Assert.Equal(48, importer.Options.Single(o => o.Key == "minImageSizePx").DefaultValue);
        // Help points users to the password field, so it is not hidden under "Show advanced" (QA C10).
        Assert.False(importer.Options.Single(o => o.Key == "password").Advanced);
        var keep = importer.Options.Single(o => o.Key == PdfImporter.OptKeepSourcePdf).Description;
        Assert.DoesNotContain("PDF → PDF", keep);
        Assert.EndsWith(".", keep);
        Assert.All(importer.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
        Assert.All(importer.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Group)));
    }

    [Fact]
    public async Task Images_are_extracted_in_reading_order()
    {
        var png = PdfImportTestData.CreatePng(120, 80);
        var markdown = $"# Pictures\n\nBefore the image.\n\n![A test image](data:image/png;base64,{Convert.ToBase64String(png)})\n\nAfter the image.\n";
        var pdf = await PdfImportTestData.RenderMarkdownAsync(markdown, BuiltInThemes.Clean());

        var doc = await PdfImportTestData.ImportPdfAsync(pdf, o => o.Set(PdfImporter.OptKeepSourcePdf, false));
        var resource = Assert.Single(doc.Resources);
        Assert.Contains(resource.MediaType, new[] { "image/png", "image/jpeg" });
        Assert.True(resource.Data.Length > 100);

        var kinds = doc.Blocks.Select(b => b switch
        {
            HeadingBlock => "H",
            ImageBlock => "I",
            ParagraphBlock p => "P:" + p.ToPlainText(),
            _ => b.GetType().Name,
        }).ToList();
        Assert.Equal(["H", "P:Before the image.", "I", "P:After the image."], kinds);

        var image = Assert.Single(doc.Blocks.OfType<ImageBlock>());
        Assert.Equal(resource.Reference, image.Source);
        Assert.Same(resource, doc.FindResource(image.Source));
        Assert.Equal(120, image.PixelWidth);
        Assert.Equal(80, image.PixelHeight);
        Assert.Equal("A test image", image.AltText);

        var none = await PdfImportTestData.ImportPdfAsync(pdf, o => o.Set(PdfImporter.OptMinImageSize, 200).Set(PdfImporter.OptKeepSourcePdf, false));
        Assert.Empty(none.Resources);
        Assert.Empty(none.Blocks.OfType<ImageBlock>());

        var off = await PdfImportTestData.ImportPdfAsync(pdf, o => o.Set(PdfImporter.OptExtractImages, false).Set(PdfImporter.OptKeepSourcePdf, false));
        Assert.Empty(off.Resources);
    }

    [Fact]
    public async Task Encrypted_pdf_asks_for_the_password()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => PdfImportTestData.ImportPdfAsync(PdfImportTestData.CreateEncryptedPdf()));
        Assert.Equal("The PDF is encrypted; supply the password in options.", ex.Message);

        var wrong = await Assert.ThrowsAsync<InvalidDataException>(() => PdfImportTestData.ImportPdfAsync(PdfImportTestData.CreateEncryptedPdf(), o => o.Set(PdfImporter.OptPassword, "guess")));
        Assert.Contains("password", wrong.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Garbage_input_gives_a_clear_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => PdfImportTestData.ImportPdfAsync("this is not a pdf"u8.ToArray()));
        Assert.Contains("PDF", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_with_only_an_image_is_reported_as_scanned()
    {
        var png = PdfImportTestData.CreatePng(400, 500);
        var pdf = PdfImportTestData.BuildPdf([], (_, page) => page.AddPng(png, new PdfRectangle(50, 100, 450, 700)));
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => PdfImportTestData.ImportPdfAsync(pdf));
        Assert.Equal("Scanned PDF with no text layer: OCR is not available yet.", ex.Message);
    }

    [Fact]
    public async Task Bookmarks_decide_headings_when_present()
    {
        var lines = new List<(string, double, double, double, bool)>
        {
            ("Introduction", 72, 760, 11, false),
            ("This paragraph introduces the topic and is long enough to be body text.", 72, 740, 11, false),
            ("Details", 72, 710, 11, false),
            ("More body text follows the details heading in the same font and size.", 72, 690, 11, false),
        };
        var pdf = PdfImportTestData.BuildPdf(lines, (builder, _) =>
        {
            var destination = new ExplicitDestination(1, ExplicitDestinationType.FitPage, new ExplicitDestinationCoordinates(null));
            var details = new DocumentBookmarkNode("Details", 1, destination, []);
            builder.Bookmarks = new Bookmarks([new DocumentBookmarkNode("Introduction", 0, destination, [details])]);
        });

        var doc = await PdfImportTestData.ImportPdfAsync(pdf);
        Assert.Equal([(1, "Introduction"), (2, "Details")], doc.Headings.Select(h => (h.Level, h.ToPlainText())));
        Assert.Equal(["introduction", "details"], doc.Headings.Select(h => h.Id!));
        Assert.Equal(2, doc.Blocks.OfType<ParagraphBlock>().Count());

        var ignored = await PdfImportTestData.ImportPdfAsync(pdf, o => o.Set(PdfImporter.OptUseBookmarks, false));
        Assert.Empty(ignored.Headings);
    }

    [Fact]
    public async Task Bold_body_size_lines_become_headings_below_the_size_levels()
    {
        var lines = new List<(string, double, double, double, bool)>
        {
            ("Big Title", 72, 770, 22, true),
            ("Body text paragraph that explains something in a normal font.", 72, 740, 11, false),
            ("Bold section", 72, 710, 11, true),
            ("Another body paragraph with normal weight text in it.", 72, 690, 11, false),
        };
        var doc = await PdfImportTestData.ImportPdfAsync(PdfImportTestData.BuildPdf(lines));
        Assert.Equal([(1, "Big Title"), (2, "Bold section")], doc.Headings.Select(h => (h.Level, h.ToPlainText())));

        var low = await PdfImportTestData.ImportPdfAsync(PdfImportTestData.BuildPdf(lines), o => o.Set(PdfImporter.OptHeadingSensitivity, "low"));
        Assert.Equal(["Big Title"], low.Headings.Select(h => h.ToPlainText()));
    }

    [Fact]
    public async Task Uri_link_annotations_become_links()
    {
        var lines = new List<(string, double, double, double, bool)> { ("Visit example site today", 72, 700, 12, false) };
        var pdf = PdfImportTestData.BuildPdf(lines, (_, page) =>
        {
            // At 12 pt Helvetica "Visit " is 26 pt wide, so "example site" spans x = 98 … 165.
            page.AddLink("https://example.org/docs", new PdfRectangle(96, 696, 167, 712));
        });

        var doc = await PdfImportTestData.ImportPdfAsync(pdf);
        var paragraph = Assert.Single(doc.Blocks.OfType<ParagraphBlock>());
        var link = Assert.Single(paragraph.Inlines.OfType<LinkInline>());
        Assert.Equal("https://example.org/docs", link.Url);
        Assert.Equal("example site", Inline.ToPlainText(link.Inlines));
        Assert.Equal("Visit example site today", paragraph.ToPlainText());
    }

    [Fact]
    public async Task Two_column_page_is_read_column_by_column()
    {
        var lines = new List<(string, double, double, double, bool)>();
        for (var i = 0; i < 12; i++)
        {
            lines.Add(($"Left column line {i + 1} with several words", 50, 760 - (i * 14), 10, false));
            lines.Add(($"Right column line {i + 1} with several words", 320, 760 - (i * 14), 10, false));
        }

        var doc = await PdfImportTestData.ImportPdfAsync(PdfImportTestData.BuildPdf(lines));
        var text = PdfImportTestData.AllText(doc);
        var lastLeft = text.IndexOf("Left column line 12", StringComparison.Ordinal);
        var firstRight = text.IndexOf("Right column line 1 ", StringComparison.Ordinal);
        Assert.True(lastLeft >= 0 && firstRight >= 0, text);
        Assert.True(lastLeft < firstRight, "the left column should be read completely before the right one:\n" + text);
        Assert.Empty(doc.EnumerateAllBlocks().OfType<TableBlock>());
    }

    [Fact]
    public async Task Hyphenated_line_ends_are_joined_when_enabled()
    {
        var lines = new List<(string, double, double, double, bool)>
        {
            ("A paragraph that ends with a hyphen- ated word and con-", 72, 700, 11, false),
            ("tinues on the next line of the same paragraph.", 72, 686, 11, false),
        };
        var pdf = PdfImportTestData.BuildPdf(lines);
        var joined = await PdfImportTestData.ImportPdfAsync(pdf);
        Assert.Equal("A paragraph that ends with a hyphen- ated word and continues on the next line of the same paragraph.",
            Assert.Single(joined.Blocks.OfType<ParagraphBlock>()).ToPlainText());

        var kept = await PdfImportTestData.ImportPdfAsync(pdf, o => o.Set(PdfImporter.OptMergeHyphenation, false));
        Assert.Contains("con- tinues", Assert.Single(kept.Blocks.OfType<ParagraphBlock>()).ToPlainText());
    }

    [Fact]
    public async Task Progress_is_reported_per_page_and_cancellation_is_honoured()
    {
        var pdf = await PdfImportTestData.RenderMarkdownAsync("# One\n\nText.\n\nMore text.", BuiltInThemes.Clean());
        var reports = new List<ConversionProgress>();
        await PdfImportTestData.ImportPdfAsync(pdf, progress: new SyncProgress(reports.Add));
        Assert.Contains(reports, r => r.Message == "Page 1 of 1");
        Assert.Equal(1, reports[^1].Fraction);
        Assert.All(reports, r => Assert.Equal(ConversionStage.Importing, r.Stage));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfImportTestData.ImportPdfAsync(pdf, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Bullet_glyph_lists_nest_by_indent()
    {
        var doc = await PdfImportTestData.ImportPdfAsync(await PdfImportTestData.RenderMarkdownAsync(SampleDocuments.WelcomeMarkdown, BuiltInThemes.Clean()));
        var bullets = doc.Blocks.OfType<ListBlock>().First(l => !l.Ordered && l.Items[0].Checked is null);
        Assert.Equal(4, bullets.Items.Count);
        var nested = Assert.Single(bullets.Items[3].Blocks.OfType<ListBlock>());
        Assert.Equal(["with sub-items", "and more"], nested.Items.Select(i => ((ParagraphBlock)i.Blocks[0]).ToPlainText()));
        var styles = bullets.Items[2].Blocks.OfType<ParagraphBlock>().Single();
        Assert.Contains(styles.Inlines.OfType<LinkInline>(), l => l.Url == "https://example.com" && Inline.ToPlainText(l.Inlines) == "links");
        Assert.Contains(styles.Inlines.OfType<StyledInline>(), s => s.Style.HasFlag(InlineStyle.Italic) && Inline.ToPlainText(s.Inlines) == "italic");
    }

    /// <summary>Progress&lt;T&gt; posts asynchronously; this one records synchronously.</summary>
    private sealed class SyncProgress(Action<ConversionProgress> report) : IProgress<ConversionProgress>
    {
        public void Report(ConversionProgress value) => report(value);
    }
}

public sealed class PdfImportHeuristicsTests
{
    [Theory]
    [InlineData("", 5, new[] { 1, 2, 3, 4, 5 })]
    [InlineData("1-3,7", 9, new[] { 1, 2, 3, 7 })]
    [InlineData(" 5- ", 6, new[] { 5, 6 })]
    [InlineData("-2, 4", 6, new[] { 1, 2, 4 })]
    [InlineData("3-1", 6, new[] { 1, 2, 3 })]
    [InlineData("2,2,8", 4, new[] { 2 })]
    public void Page_ranges_parse(string text, int pages, int[] expected) =>
        Assert.Equal(expected, PageRangeParser.Parse(text, pages));

    [Theory]
    [InlineData("1-x")]
    [InlineData("0")]
    [InlineData("abc")]
    public void Bad_page_ranges_throw(string text) =>
        Assert.Throws<ArgumentException>(() => PageRangeParser.Parse(text, 10));

    [Theory]
    [InlineData("12", true)]
    [InlineData("Page 3 of 9", true)]
    [InlineData("- 4 -", true)]
    [InlineData("3 / 12", true)]
    [InlineData("xiv", true)]
    [InlineData("Introduction", false)]
    [InlineData("Chapter 3", false)]
    public void Page_numbers_are_recognised(string text, bool expected) =>
        Assert.Equal(expected, HeaderFooterFilter.IsPageNumber(text));

    [Fact]
    public void Header_keys_normalise_digits_and_case() =>
        Assert.Equal(HeaderFooterFilter.Normalize("Page  3 of 12"), HeaderFooterFilter.Normalize("page 4 of 12 "));

    [Theory]
    [InlineData("12", 12)]
    [InlineData("c", 3)]
    [InlineData("iv", 4)]
    [InlineData("IX", 9)]
    public void Ordinals_parse(string marker, int expected) => Assert.Equal(expected, ListDetector.ParseOrdinal(marker));

    [Theory]
    [InlineData("ABCDEF+Inter-SemiBold", true, false, false)]
    [InlineData("Courier-Oblique", false, true, true)]
    [InlineData("EAAAAA+JetBrainsMono-Regular", false, false, true)]
    [InlineData("Literata-Italic", false, true, false)]
    [InlineData("Helvetica", false, false, false)]
    public void Font_names_hint_styles(string name, bool bold, bool italic, bool mono)
    {
        var flags = FontClassifier.FromName(name);
        Assert.Equal(bold, flags.HasFlag(FontFlags.Bold));
        Assert.Equal(italic, flags.HasFlag(FontFlags.Italic));
        Assert.Equal(mono, flags.HasFlag(FontFlags.Mono));
    }

    [Theory]
    [InlineData("Note", CalloutKind.Note)]
    [InlineData("WARNING:", CalloutKind.Warning)]
    [InlineData("Tip", CalloutKind.Tip)]
    [InlineData("Danger", CalloutKind.Caution)]
    public void Callout_titles_map_to_kinds(string title, CalloutKind kind) => Assert.Equal(kind, LayoutBuilder.KindFromTitle(title));

    [Theory]
    [InlineData("1 Introduction", "Introduction")]
    [InlineData("1.2 Getting started", "Getting started")]
    [InlineData("1.2.3) Deep", "Deep")]
    [InlineData("2. Results", "Results")]
    [InlineData("1.1 1. Measure", "1. Measure")]
    [InlineData("2024", "2024")]
    [InlineData("3.5", "3.5")]
    [InlineData("Version 2 notes", "Version 2 notes")]
    public void Heading_numbers_strip_once_and_never_empty(string input, string expected) =>
        Assert.Equal(expected, LayoutBuilder.StripHeadingNumber(input));

    [Fact]
    public void Outline_titles_normalise_punctuation() =>
        Assert.Equal("1 2 getting started", HeadingDetector.NormalizeTitle("1.2  Getting started!"));
}
