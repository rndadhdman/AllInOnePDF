using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Rendering;

/// <summary>Lays out a document as PDF bytes according to a theme.</summary>
public interface IPdfRenderer
{
    Task<byte[]> RenderAsync(Document document, PdfTheme theme, PdfRenderOptions? options = null, IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class PdfRenderOptions
{
    /// <summary>Overrides the theme's cover setting (null = use theme).</summary>
    public bool? IncludeCover { get; init; }
    public bool? IncludeToc { get; init; }
    /// <summary>Title used when the document has none.</summary>
    public string? FallbackTitle { get; init; }
    /// <summary>Embed document metadata (title, author, keywords) in the PDF info dictionary.</summary>
    public bool EmbedMetadata { get; init; } = true;
    /// <summary>Lower quality / faster for live previews.</summary>
    public bool Draft { get; init; }
    /// <summary>Image quality 1-100 for re-encoded raster images.</summary>
    public int ImageQuality { get; init; } = 85;
}

/// <summary>Turns PDF pages into images for on-screen preview and for the PDF → image exporter.</summary>
public interface IPdfRasterizer
{
    int GetPageCount(byte[] pdf);

    /// <summary>Page size in points for page <paramref name="pageIndex"/> (0-based).</summary>
    (double WidthPt, double HeightPt) GetPageSize(byte[] pdf, int pageIndex);

    Task<PageImage> RasterizePageAsync(byte[] pdf, int pageIndex, RasterizeOptions? options = null, CancellationToken cancellationToken = default);

    IAsyncEnumerable<PageImage> RasterizeAsync(byte[] pdf, RasterizeOptions? options = null, CancellationToken cancellationToken = default);
}

public sealed class RasterizeOptions
{
    /// <summary>Dots per inch. 96 for thumbnails, 144-192 for crisp on-screen preview, 300 for export.</summary>
    public int Dpi { get; init; } = 144;
    /// <summary>Hex background colour painted under transparent pages; null keeps PDF white.</summary>
    public string? Background { get; init; }
    /// <summary>Inclusive 0-based page range; null = all.</summary>
    public int? FirstPage { get; init; }
    public int? LastPage { get; init; }
    public bool WithAnnotations { get; init; } = true;
    public ImageEncoding Encoding { get; init; } = ImageEncoding.Png;
    public int JpegQuality { get; init; } = 90;
}

public enum ImageEncoding { Png, Jpeg, WebP }

public sealed record PageImage(int PageIndex, int Width, int Height, byte[] Data, string MediaType)
{
    public int PageNumber => PageIndex + 1;

    public string ToDataUrl() => $"data:{MediaType};base64,{Convert.ToBase64String(Data)}";
}

/// <summary>Renders the document model to semantic HTML for the on-screen reader and fast theme previews.</summary>
public interface IDocumentHtmlRenderer
{
    /// <summary>Returns an HTML fragment (no html/body wrapper) rooted in a single <c>&lt;article class="aio-doc"&gt;</c>.</summary>
    string RenderFragment(Document document, HtmlRenderOptions? options = null);
}

public sealed class HtmlRenderOptions
{
    /// <summary>Add id attributes to headings for in-page navigation.</summary>
    public bool HeadingAnchors { get; init; } = true;
    /// <summary>Emit <c>data-block</c> indexes so a UI can map clicks back to model blocks (theme builder).</summary>
    public bool BlockIndexes { get; init; }
    /// <summary>How to turn image sources into URLs. Default inlines embedded resources as data URLs and leaves other sources as-is.</summary>
    public Func<Document, string, string?>? ResolveImage { get; init; }
    /// <summary>Open external links in a new window/handler.</summary>
    public bool ExternalLinksBlank { get; init; } = true;
    /// <summary>Render raw HTML blocks from the source (off by default: untrusted input).</summary>
    public bool AllowRawHtml { get; init; }
}
