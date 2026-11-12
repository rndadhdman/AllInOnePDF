namespace AllInOnePdf.Layout.Infrastructure;

/// <summary>The first and last page (1-based) an element tracked with <c>TrackPages</c> was drawn on.</summary>
/// <param name="First">The page the element starts on.</param>
/// <param name="Last">The page the element ends on (the same page unless it was split across pages).</param>
public readonly record struct PageSpan(int First, int Last);

/// <summary>
/// What the layout learnt while generating a PDF: the number of pages and, for every element wrapped with
/// <c>TrackPages(key)</c>, the pages it was drawn on. A page builder uses it to show where its content lands.
/// </summary>
/// <param name="PageCount">Pages in the PDF (at least 1).</param>
/// <param name="TrackedPages">Pages per tracking key; keys whose element never drew are absent.</param>
public sealed record DocumentLayoutInfo(int PageCount, IReadOnlyDictionary<string, PageSpan> TrackedPages)
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<TrackedBox>> NoBoxes = new Dictionary<string, IReadOnlyList<TrackedBox>>(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Where every element tracked with <c>TrackPages(key)</c> painted: one box per draw (one per page for content
    /// split across pages), in draw order, in page coordinates. Translated, deferred (<c>ZIndex</c>) and freely placed
    /// (<c>Freeform</c>) content reports where it really painted. Keys whose element never drew are absent.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TrackedBox>> TrackedBoxes { get; init; } = NoBoxes;

    /// <summary>The keys of <c>Freeform</c> items whose content did not fit their box (drawn cut off).</summary>
    public IReadOnlySet<string> Overflowing { get; init; } = NoKeys;
}

/// <summary>One draw of a tracked element: the page and the rectangle it was drawn into.</summary>
/// <param name="Page">The 1-based page.</param>
/// <param name="X">Left edge in points from the page's left edge (page margins included).</param>
/// <param name="Y">Top edge in points from the page's top edge (page margins included).</param>
/// <param name="Width">Width of the space the element was drawn into, in points.</param>
/// <param name="Height">Height of the space the element was drawn into, in points.</param>
public readonly record struct TrackedBox(int Page, float X, float Y, float Width, float Height);
