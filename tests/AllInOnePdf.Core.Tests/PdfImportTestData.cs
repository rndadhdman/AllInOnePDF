using System.Globalization;
using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AllInOnePdf.Core.Tests;

/// <summary>Builds test PDFs (rendered with PdfRenderer or written with PdfPig) and runs the PDF importer on them.</summary>
internal static class PdfImportTestData
{
    public static string SampleMarkdownPath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "samples", "sample-document.md")))
            {
                dir = dir.Parent;
            }

            return dir is null
                ? throw new FileNotFoundException("samples/sample-document.md not found above the test output folder.")
                : Path.Combine(dir.FullName, "samples", "sample-document.md");
        }
    }

    public static async Task<Document> ImportMarkdownAsync(string markdown)
    {
        var importer = new MarkdownImporter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
        return await importer.ImportAsync(new ImportContext { Input = stream, FileName = "test.md", Options = OptionValues.FromDefaults(importer.Options) });
    }

    public static async Task<byte[]> RenderMarkdownAsync(string markdown, PdfTheme theme) =>
        await new PdfRenderer().RenderAsync(await ImportMarkdownAsync(markdown), theme);

    public static async Task<Document> ImportPdfAsync(byte[] pdf, Action<OptionValues>? configure = null, IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var importer = new PdfImporter();
        var options = OptionValues.FromDefaults(importer.Options);
        configure?.Invoke(options);
        using var stream = new MemoryStream(pdf);
        return await importer.ImportAsync(new ImportContext { Input = stream, FileName = "test.pdf", Options = options, Progress = progress }, cancellationToken);
    }

    public static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
            using var paint = new SKPaint { Color = SKColors.Orange, IsAntialias = true };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A PDF written with PdfPig's builder: each entry is (text, x, y, size, bold).</summary>
    public static byte[] BuildPdf(IEnumerable<(string Text, double X, double Y, double Size, bool Bold)> lines, Action<PdfDocumentBuilder, PdfPageBuilder>? extra = null)
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var page = builder.AddPage(PageSize.A4);
        foreach (var (text, x, y, size, isBold) in lines)
        {
            page.AddText(text, size, new PdfPoint(x, y), isBold ? bold : regular);
        }

        extra?.Invoke(builder, page);
        return builder.Build();
    }

    /// <summary>A minimal PDF whose trailer references a standard security handler with a non-empty user password.</summary>
    public static byte[] CreateEncryptedPdf()
    {
        static string Hex(int n, byte seed)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < n; i++)
            {
                sb.Append(((byte)(seed + i * 37)).ToString("X2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /Filter /Standard /V 1 /R 2 /Length 40 /O <{Hex(32, 7)}> /U <{Hex(32, 91)}> /P -44 >>",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        var id = Hex(16, 3);
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Encrypt 4 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public static string PlainText(Block block) => block switch
    {
        InlineContainerBlock icb => icb.ToPlainText(),
        CodeBlock code => code.Code,
        _ => string.Empty,
    };

    /// <summary>All text in the document (blocks and nested blocks, table cells included), one block per line.</summary>
    public static string AllText(Document document) =>
        string.Join("\n", document.EnumerateAllBlocks().Select(PlainText).Where(t => t.Length > 0));
}

/// <summary>Renders the repository sample with three themes once and imports each PDF with default options.</summary>
public sealed class PdfSampleFixture : IAsyncLifetime
{
    public Dictionary<string, byte[]> Pdfs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Document> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task InitializeAsync()
    {
        var markdown = await File.ReadAllTextAsync(PdfImportTestData.SampleMarkdownPath);
        foreach (var theme in new[] { BuiltInThemes.Clean(), BuiltInThemes.Technical(), BuiltInThemes.Manuscript() })
        {
            var pdf = await PdfImportTestData.RenderMarkdownAsync(markdown, theme);
            Pdfs[theme.Id] = pdf;
            Documents[theme.Id] = await PdfImportTestData.ImportPdfAsync(pdf);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
