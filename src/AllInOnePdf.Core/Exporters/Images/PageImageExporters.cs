using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Rendering;

namespace AllInOnePdf.Core.Exporters.Images;

/// <summary>Document → one PNG per page (lossless, optional transparent background).</summary>
public sealed class PngPagesExporter(IPdfRenderer renderer, IPdfRasterizer rasterizer)
    : PageImagesExporter(renderer, rasterizer, KnownFormats.Png, ImageEncoding.Png);

/// <summary>Document → one JPEG per page.</summary>
public sealed class JpegPagesExporter(IPdfRenderer renderer, IPdfRasterizer rasterizer)
    : PageImagesExporter(renderer, rasterizer, KnownFormats.Jpeg, ImageEncoding.Jpeg);

/// <summary>Document → one WebP per page.</summary>
public sealed class WebpPagesExporter(IPdfRenderer renderer, IPdfRasterizer rasterizer)
    : PageImagesExporter(renderer, rasterizer, KnownFormats.WebP, ImageEncoding.WebP);
