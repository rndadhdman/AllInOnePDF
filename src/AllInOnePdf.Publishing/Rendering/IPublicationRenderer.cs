using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Rendering;

/// <summary>Lays out a publication as a PDF with the layout engine and the publication's theme snapshot.</summary>
public interface IPublicationRenderer
{
    /// <summary>Renders <paramref name="publication"/> and returns the PDF bytes.</summary>
    Task<byte[]> RenderAsync(Publication publication, PublicationRenderOptions? options = null, CancellationToken ct = default);

    /// <summary>Renders <paramref name="publication"/> and also reports where the PDF put every section, row, column and module.</summary>
    Task<PublicationRenderResult> RenderWithPagesAsync(Publication publication, PublicationRenderOptions? options = null, CancellationToken ct = default);
}

/// <summary>The first and last page (1-based) a section, row, column or module was drawn on.</summary>
/// <param name="First">The page it starts on.</param>
/// <param name="Last">The page it ends on (the same page unless the PDF split it).</param>
public readonly record struct PublicationPageSpan(int First, int Last);

/// <summary>One draw of a section, row, column or module: the page and the rectangle it was drawn into.</summary>
/// <param name="Page">The 1-based page.</param>
/// <param name="XPt">Left edge in points from the page's left edge (page margins included).</param>
/// <param name="YPt">Top edge in points from the page's top edge (page margins included).</param>
/// <param name="WidthPt">Width of the space it was drawn into, in points.</param>
/// <param name="HeightPt">Height of the space it was drawn into, in points.</param>
public readonly record struct PublicationBox(int Page, double XPt, double YPt, double WidthPt, double HeightPt);

/// <summary>
/// A rendered publication: the PDF, its page count and the pages of every section, row, column and module by id.
/// Hidden modules, empty rows and page-break modules have no entry; a module that spans pages reports both ends.
/// </summary>
/// <param name="Pdf">The PDF bytes.</param>
/// <param name="PageCount">Pages in the PDF.</param>
/// <param name="Pages">Pages by element id.</param>
public sealed record PublicationRenderResult(byte[] Pdf, int PageCount, IReadOnlyDictionary<string, PublicationPageSpan> Pages)
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PublicationBox>> NoBoxes = new Dictionary<string, IReadOnlyList<PublicationBox>>(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Where every section, row, column and module painted, by id: one box per page it touches, in draw order, in page
    /// coordinates (points from the page's top-left, margins included). The Creator uses it to convert a Structure
    /// section into Free flow without moving anything visibly.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PublicationBox>> Boxes { get; init; } = NoBoxes;

    /// <summary>Ids of free-flow modules whose content did not fit their box (drawn cut off).</summary>
    public IReadOnlySet<string> Overflowing { get; init; } = NoKeys;
}

/// <summary>Switches for <see cref="IPublicationRenderer.RenderAsync"/>.</summary>
public sealed class PublicationRenderOptions
{
    /// <summary>Faster, lower-quality output for live previews (lower image quality, like <c>PdfRenderOptions.Draft</c>).</summary>
    public bool Draft { get; set; }

    /// <summary>Draw grey boxes for image modules without an asset (template thumbnails, the builder preview).</summary>
    public bool ShowPlaceholders { get; set; }

    /// <summary>Write title, authors, subject, keywords and language into the PDF.</summary>
    public bool EmbedMetadata { get; set; } = true;

    /// <summary>JPEG quality for re-encoded images (1 to 100).</summary>
    public int ImageQuality { get; set; } = 85;
}
