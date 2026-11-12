using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Exporters.Images;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using SkiaSharp;

namespace AllInOnePdf.Core.Tests;

public sealed class ImagePagesExportTests
{
    private static readonly PdfRenderer Renderer = new();
    private static readonly PdfiumRasterizer Rasterizer = new();

    private static async Task<ExportResult> ExportAsync(PageImagesExporter exporter, Document doc, Action<OptionValues>? configure = null, PdfTheme? theme = null, CancellationToken ct = default)
    {
        var options = OptionValues.FromDefaults(exporter.Options);
        configure?.Invoke(options);
        return await exporter.ExportAsync(new ExportContext { Document = doc, BaseName = "doc", Options = options, Theme = theme ?? BuiltInThemes.Clean() }, ct);
    }

    private static Document ManyPages(int pages)
    {
        var doc = new Document();
        for (var i = 1; i <= pages; i++)
        {
            if (i > 1)
            {
                doc.Blocks.Add(new PageBreakBlock());
            }

            doc.Blocks.Add(new ParagraphBlock($"Page {i}"));
        }

        return doc;
    }

    [Fact]
    public void Exporters_declare_formats_and_options()
    {
        var png = new PngPagesExporter(Renderer, Rasterizer);
        var jpeg = new JpegPagesExporter(Renderer, Rasterizer);
        var webp = new WebpPagesExporter(Renderer, Rasterizer);
        Assert.Equal(["png", "jpeg", "webp"], new[] { png.Format.Id, jpeg.Format.Id, webp.Format.Id });
        Assert.All(new IExporter[] { png, jpeg, webp }, e => Assert.True(e.SupportsTheme));

        var dpi = png.Options.Single(o => o.Key == "dpi");
        Assert.Equal((150, 36.0, 600.0), ((int)dpi.DefaultValue!, dpi.Min!.Value, dpi.Max!.Value));
        Assert.Equal(OptionType.PageRange, png.Options.Single(o => o.Key == "pages").Type);
        Assert.Equal(OptionType.Color, png.Options.Single(o => o.Key == "background").Type);
        Assert.True(png.Options.Single(o => o.Key == "quality").Advanced);
        Assert.False(jpeg.Options.Single(o => o.Key == "quality").Advanced);
        Assert.Contains(png.Options, o => o.Key == "transparent");
        Assert.DoesNotContain(jpeg.Options, o => o.Key == "transparent");
        Assert.True(png.Options.Single(o => o.Key == "maxPages").Advanced);
        Assert.Equal("{name}-p{page}", png.Options.Single(o => o.Key == "namePattern").DefaultValue);
        Assert.All(png.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
    }

    [Fact]
    public async Task Welcome_sample_becomes_one_png_per_page()
    {
        var doc = await SampleDocuments.CreateWelcomeAsync();
        var expected = Rasterizer.GetPageCount(await Renderer.RenderAsync(doc, BuiltInThemes.Clean()));

        var result = await ExportAsync(new PngPagesExporter(Renderer, Rasterizer), doc, o => o.Set("dpi", 48));

        Assert.Equal(expected, result.Files.Count);
        Assert.True(result.Files[0].IsPrimary);
        Assert.All(result.Files.Skip(1), f => Assert.False(f.IsPrimary));
        Assert.All(result.Files, f =>
        {
            Assert.Equal("image/png", f.MediaType);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, f.Content[..4]);
            Assert.EndsWith(".png", f.FileName);
        });
        var width = expected.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        Assert.Equal($"doc-p{"1".PadLeft(width, '0')}.png", result.Files[0].FileName);
    }

    [Fact]
    public async Task Dpi_changes_the_pixel_size()
    {
        var doc = ManyPages(1);
        var exporter = new PngPagesExporter(Renderer, Rasterizer);
        var low = await ExportAsync(exporter, doc, o => o.Set("dpi", 72));
        var high = await ExportAsync(exporter, doc, o => o.Set("dpi", 144));

        var (lw, lh) = ImageTestData.Decode(low.Files[0].Content);
        var (hw, hh) = ImageTestData.Decode(high.Files[0].Content);
        Assert.InRange(lw, 590, 600); // A4 at 72 dpi = 595 px
        Assert.InRange(hw, 2 * lw - 2, 2 * lw + 2);
        Assert.InRange(hh, 2 * lh - 2, 2 * lh + 2);
    }

    [Fact]
    public async Task Page_range_first_page_only_and_name_padding()
    {
        var doc = ManyPages(12);
        var exporter = new PngPagesExporter(Renderer, Rasterizer);

        var some = await ExportAsync(exporter, doc, o => o.Set("dpi", 36).Set("pages", "2,11-12"));
        Assert.Equal(["doc-p02.png", "doc-p11.png", "doc-p12.png"], some.Files.Select(f => f.FileName));

        var first = await ExportAsync(exporter, doc, o => o.Set("dpi", 36).Set("firstPageOnly", true).Set("namePattern", "cover"));
        Assert.Equal("cover.png", Assert.Single(first.Files).FileName);

        var pattern = await ExportAsync(exporter, doc, o => o.Set("dpi", 36).Set("pages", "1-2").Set("namePattern", "{name}/{count}"));
        Assert.Equal(["doc_12-01.png", "doc_12-02.png"], pattern.Files.Select(f => f.FileName));

        await Assert.ThrowsAsync<ArgumentException>(() => ExportAsync(exporter, doc, o => o.Set("pages", "40-50")));
    }

    [Fact]
    public async Task Max_pages_stops_with_a_warning()
    {
        var result = await ExportAsync(new PngPagesExporter(Renderer, Rasterizer), ManyPages(4), o => o.Set("dpi", 36).Set("maxPages", 2));
        Assert.Equal(2, result.Files.Count);
        Assert.Contains(result.Warnings, w => w.Contains("2 of 4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Jpeg_quality_affects_size_and_webp_has_its_media_type()
    {
        var doc = await SampleDocuments.CreateWelcomeAsync();
        var jpeg = new JpegPagesExporter(Renderer, Rasterizer);
        var low = await ExportAsync(jpeg, doc, o => o.Set("firstPageOnly", true).Set("dpi", 96).Set("quality", 10));
        var high = await ExportAsync(jpeg, doc, o => o.Set("firstPageOnly", true).Set("dpi", 96).Set("quality", 95));
        Assert.Equal("image/jpeg", low.Files[0].MediaType);
        Assert.Equal(0xFF, low.Files[0].Content[0]);
        Assert.EndsWith(".jpg", low.Files[0].FileName);
        Assert.True(low.Files[0].Length * 2 < high.Files[0].Length, $"{low.Files[0].Length} vs {high.Files[0].Length}");

        var webp = await ExportAsync(new WebpPagesExporter(Renderer, Rasterizer), doc, o => o.Set("firstPageOnly", true).Set("dpi", 48));
        var file = Assert.Single(webp.Files);
        Assert.Equal("image/webp", file.MediaType);
        Assert.EndsWith(".webp", file.FileName);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(file.Content, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(file.Content, 8, 4));
    }

    [Fact]
    public async Task Background_colour_and_transparency()
    {
        var exporter = new PngPagesExporter(Renderer, Rasterizer);

        // A PDF page with no fill of its own shows the background colour.
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A6);
        var bare = new Document();
        bare.AddResource("application/pdf", builder.Build(), "bare.pdf", Document.SourceFileResourceId);
        var red = await ExportAsync(exporter, bare, o => o.Set("dpi", 36).Set("background", "FF0000"));
        using (var bitmap = SKBitmap.Decode(red.Files[0].Content))
        {
            Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(1, bitmap.Height - 2));
        }

        // Themed pages are painted by the theme.
        var themed = await ExportAsync(exporter, ManyPages(1), o => o.Set("dpi", 36).Set("background", "#FF0000"), BuiltInThemes.Night());
        using (var bitmap = SKBitmap.Decode(themed.Files[0].Content))
        {
            Assert.NotEqual(new SKColor(255, 0, 0), bitmap.GetPixel(1, bitmap.Height - 2));
        }

        var clear = await ExportAsync(exporter, ManyPages(1), o => o.Set("dpi", 36).Set("transparent", true));
        using (var bitmap = SKBitmap.Decode(clear.Files[0].Content))
        {
            Assert.Equal(0, bitmap.GetPixel(1, bitmap.Height - 2).Alpha);
        }

        var invalid = await ExportAsync(exporter, ManyPages(1), o => o.Set("dpi", 36).Set("background", "teal-ish"));
        Assert.Single(invalid.Files);
        Assert.Contains(invalid.Warnings, w => w.Contains("not a valid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Source_pdf_is_rasterised_unchanged_when_present()
    {
        var original = await Renderer.RenderAsync(ManyPages(3), BuiltInThemes.Clean());
        var importer = new PdfImporter();
        Document imported;
        using (var stream = new MemoryStream(original))
        {
            imported = await importer.ImportAsync(new ImportContext { Input = stream, FileName = "report.pdf", Options = OptionValues.FromDefaults(importer.Options) });
        }

        Assert.NotNull(imported.SourceFile);
        Assert.Equal("application/pdf", imported.SourceFile!.MediaType);
        Assert.Equal(original, imported.SourceFile.Data);

        // A renderer that fails proves the original bytes are used rather than a re-layout of the model.
        var exporter = new PngPagesExporter(new ThrowingRenderer(), Rasterizer);
        var result = await ExportAsync(exporter, imported, o => o.Set("dpi", 36), BuiltInThemes.Compact());
        Assert.Equal(Rasterizer.GetPageCount(original), result.Files.Count);

        // Turning the option off drops the source copy.
        using var again = new MemoryStream(original);
        var lean = await importer.ImportAsync(new ImportContext { Input = again, FileName = "report.pdf", Options = OptionValues.FromDefaults(importer.Options).Set(PdfImporter.OptKeepSourcePdf, false) });
        Assert.Null(lean.SourceFile);
    }

    [Fact]
    public async Task Cancellation_and_progress()
    {
        var exporter = new PngPagesExporter(Renderer, Rasterizer);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportAsync(exporter, ManyPages(2), ct: cts.Token));

        var reports = new List<ConversionProgress>();
        var options = OptionValues.FromDefaults(exporter.Options).Set("dpi", 36);
        await exporter.ExportAsync(new ExportContext { Document = ManyPages(3), BaseName = "doc", Options = options, Progress = new SyncProgress(reports.Add) });
        Assert.Equal(3, reports.Count(r => r.Stage == ConversionStage.Exporting && r.Message?.StartsWith("Page", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void Naming_pads_to_the_page_count_width()
    {
        Assert.Equal("doc-p007.png", PageImageNaming.Build(null, "doc", 7, 120, true, ".png"));
        Assert.Equal("doc-p7.png", PageImageNaming.Build("{name}-p{page}", "doc", 7, 9, true, ".png"));
        Assert.Equal("x-3.png", PageImageNaming.Build("x", "doc", 3, 9, true, ".png"));
        Assert.Equal("x.png", PageImageNaming.Build("x", "doc", 3, 9, false, ".png"));
        Assert.Equal("a_b-1.jpg", PageImageNaming.Build("a:b-{PAGE}", "doc", 1, 5, true, ".jpg"));
    }

    private sealed class ThrowingRenderer : IPdfRenderer
    {
        public Task<byte[]> RenderAsync(Document document, PdfTheme theme, PdfRenderOptions? options = null, IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The renderer must not be used when the source PDF is kept.");
    }

    private sealed class SyncProgress(Action<ConversionProgress> report) : IProgress<ConversionProgress>
    {
        public void Report(ConversionProgress value) => report(value);
    }
}
