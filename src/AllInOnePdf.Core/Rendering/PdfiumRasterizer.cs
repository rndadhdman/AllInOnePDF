using System.Runtime.CompilerServices;
using PDFtoImage;
using SkiaSharp;
using PdfConversion = PDFtoImage.Conversion;

namespace AllInOnePdf.Core.Rendering;

/// <summary>Rasterises PDF pages with the bundled pdfium (via PDFtoImage). Runs in-process, no external tools.</summary>
public sealed class PdfiumRasterizer : IPdfRasterizer
{
    public int GetPageCount(byte[] pdf) => PdfConversion.GetPageCount(pdf);

    public (double WidthPt, double HeightPt) GetPageSize(byte[] pdf, int pageIndex)
    {
        var size = PdfConversion.GetPageSize(pdf, page: pageIndex);
        return (size.Width, size.Height);
    }

    public Task<PageImage> RasterizePageAsync(byte[] pdf, int pageIndex, RasterizeOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => RasterizePage(pdf, pageIndex, options ?? new RasterizeOptions()), cancellationToken);

    public async IAsyncEnumerable<PageImage> RasterizeAsync(byte[] pdf, RasterizeOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new RasterizeOptions();
        var count = GetPageCount(pdf);
        var first = Math.Clamp(options.FirstPage ?? 0, 0, Math.Max(0, count - 1));
        var last = Math.Clamp(options.LastPage ?? count - 1, first, Math.Max(0, count - 1));
        for (var i = first; i <= last; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await Task.Run(() => RasterizePage(pdf, i, options), cancellationToken);
        }
    }

    private static PageImage RasterizePage(byte[] pdf, int pageIndex, RasterizeOptions options)
    {
        var background = options.Background is { } hex && SKColor.TryParse(hex, out var c) ? c : SKColors.White;
        var renderOptions = new RenderOptions(
            Dpi: options.Dpi,
            WithAnnotations: options.WithAnnotations,
            WithFormFill: true,
            WithAspectRatio: true,
            AntiAliasing: PdfAntiAliasing.All,
            BackgroundColor: background);

        using var bitmap = PdfConversion.ToImage(pdf, page: pageIndex, options: renderOptions);
        var (format, quality, mediaType) = options.Encoding switch
        {
            ImageEncoding.Jpeg => (SKEncodedImageFormat.Jpeg, options.JpegQuality, "image/jpeg"),
            ImageEncoding.WebP => (SKEncodedImageFormat.Webp, options.JpegQuality, "image/webp"),
            _ => (SKEncodedImageFormat.Png, 100, "image/png"),
        };

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return new PageImage(pageIndex, bitmap.Width, bitmap.Height, data.ToArray(), mediaType);
    }
}
