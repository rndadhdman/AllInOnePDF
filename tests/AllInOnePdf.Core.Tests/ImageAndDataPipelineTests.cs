using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters.Images;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace AllInOnePdf.Core.Tests;

/// <summary>End-to-end runs of the image, CSV, JSON and source-code converters through <see cref="IConversionService"/>.</summary>
public sealed class ImageAndDataPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aio-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public ImageAndDataPipelineTests()
    {
        Directory.CreateDirectory(_dir);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddAllInOnePdfCore(Path.Combine(_dir, "data"));
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("table.csv", typeof(CsvImporter))]
    [InlineData("data.json", typeof(JsonImporter))]
    [InlineData("photo.JPG", typeof(ImageImporter))]
    [InlineData("scan.webp", typeof(ImageImporter))]
    [InlineData("Program.cs", typeof(SourceCodeImporter))]
    [InlineData("main.rs", typeof(SourceCodeImporter))]
    [InlineData("notes.txt", typeof(PlainTextImporter))]
    [InlineData("notes.md", typeof(MarkdownImporter))]
    [InlineData("report.pdf", typeof(PdfImporter))]
    public void Registry_picks_the_right_importer(string fileName, Type expected)
    {
        var registry = _services.GetRequiredService<FormatRegistry>();
        Assert.IsType(expected, registry.FindImporter(fileName));
    }

    [Fact]
    public void Page_image_exporters_are_registered()
    {
        var registry = _services.GetRequiredService<FormatRegistry>();
        Assert.IsType<PngPagesExporter>(registry.FindExporter("png"));
        Assert.IsType<JpegPagesExporter>(registry.FindExporter("jpeg"));
        Assert.IsType<WebpPagesExporter>(registry.FindExporter("webp"));
        var targets = registry.GetTargetsFor("report.pdf").Select(t => t.Format.Id).ToList();
        Assert.Contains("png", targets);
        Assert.Contains(".csv", registry.ImportableExtensions);
        Assert.Contains(".webp", registry.ImportableExtensions);
    }

    [Fact]
    public async Task Csv_converts_to_pdf()
    {
        var service = _services.GetRequiredService<IConversionService>();
        var csv = "Product;Units;Price\nWidget;12;3,50\nGadget;7;12,00\n\"Thing; with semicolon\";1;0,99\n";
        var result = await service.ConvertAsync(new ConversionRequest
        {
            SourceContent = Encoding.UTF8.GetBytes(csv),
            SourceFileName = "inventory.csv",
            TargetFormatId = "pdf",
            OutputDirectory = Path.Combine(_dir, "csv"),
        });

        Assert.True(result.Success, result.Error);
        Assert.EndsWith("inventory.pdf", Assert.Single(result.WrittenPaths));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(result.Primary!.Content, 0, 4));
        var table = result.Document!.Blocks.OfType<TableBlock>().Single();
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal("Thing; with semicolon", table.Rows[3].Cells[0].ToPlainText());
        Assert.Equal(ColumnAlignment.Right, table.Alignments[1]);
        Assert.Equal("inventory", result.Document.Metadata.Title);
    }

    [Fact]
    public async Task Image_converts_to_pdf_with_one_page()
    {
        var service = _services.GetRequiredService<IConversionService>();
        var source = Path.Combine(_dir, "chart.png");
        await File.WriteAllBytesAsync(source, ImageTestData.Encode(800, 500, SKEncodedImageFormat.Png));

        var result = await service.ConvertAsync(new ConversionRequest
        {
            SourcePath = source,
            TargetFormatId = "pdf",
            ExportOptions = new OptionValues().Set("cover", "no").Set("toc", "no"),
        });

        Assert.True(result.Success, result.Error);
        var pdf = result.Primary!.Content;
        Assert.Equal(1, new PdfiumRasterizer().GetPageCount(pdf));
        Assert.Equal("chart", result.Document!.Metadata.Title);
        Assert.Equal(source, result.Document.Metadata.SourcePath);
    }

    [Fact]
    public async Task Pdf_converts_to_page_images_from_the_original()
    {
        var original = await new PdfRenderer().RenderAsync(await SampleDocuments.CreateWelcomeAsync(), BuiltInThemes.Manuscript());
        var pages = new PdfiumRasterizer().GetPageCount(original);
        var service = _services.GetRequiredService<IConversionService>();

        var result = await service.ConvertAsync(new ConversionRequest
        {
            SourceContent = original,
            SourceFileName = "guide.pdf",
            TargetFormatId = "png",
            ThemeId = "compact",
            ExportOptions = new OptionValues().Set("dpi", 40),
            OutputDirectory = Path.Combine(_dir, "pages"),
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(pages, result.Outputs.Count);
        Assert.Equal(pages, result.WrittenPaths.Count);
        Assert.All(result.WrittenPaths, p => Assert.Matches(@"guide-p\d+\.png$", p));
        Assert.True(result.Outputs[0].IsPrimary);
    }

    [Fact]
    public async Task Json_and_source_code_convert_to_pdf()
    {
        var service = _services.GetRequiredService<IConversionService>();
        foreach (var (name, content) in new[] { ("settings.json", "{\"a\":[1,2,{\"b\":null}]}"), ("hello.py", "def hi():\n\tprint('hi')\n") })
        {
            var result = await service.ConvertAsync(new ConversionRequest
            {
                SourceContent = Encoding.UTF8.GetBytes(content),
                SourceFileName = name,
                TargetFormatId = "pdf",
            });

            Assert.True(result.Success, result.Error);
            Assert.Equal("%PDF", Encoding.ASCII.GetString(result.Primary!.Content, 0, 4));
            Assert.IsType<CodeBlock>(Assert.Single(result.Document!.Blocks));
        }
    }
}
