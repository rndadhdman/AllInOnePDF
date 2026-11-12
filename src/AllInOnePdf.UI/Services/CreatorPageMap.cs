using AllInOnePdf.Publishing.Rendering;

namespace AllInOnePdf.UI.Services;

/// <summary>
/// Where the PDF puts the working publication: the result of the latest background render that
/// <see cref="CreatorState"/> runs after each edit. The design canvas splits its sheets and the structure tree
/// draws its page markers from it; the preview reuses the PDF when the content has not changed since.
/// </summary>
/// <param name="Json">The content snapshot (<see cref="CreatorState.CurrentJson"/>) the render was made from.</param>
/// <param name="PageCount">Pages in the PDF.</param>
/// <param name="Pages">First and last page of every section, row, column and module, by id.</param>
/// <param name="Pdf">The draft-quality PDF itself.</param>
public sealed record CreatorPageMap(string Json, int PageCount, IReadOnlyDictionary<string, PublicationPageSpan> Pages, byte[] Pdf)
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PublicationBox>> NoBoxes = new Dictionary<string, IReadOnlyList<PublicationBox>>(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Where every section, row, column and module painted (<see cref="PublicationRenderResult.Boxes"/>): one box per
    /// page it touches, in page coordinates. Empty when the renderer does not report boxes.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PublicationBox>> Boxes { get; init; } = NoBoxes;

    /// <summary>Ids of free-flow modules whose content did not fit their box (<see cref="PublicationRenderResult.Overflowing"/>).</summary>
    public IReadOnlySet<string> Overflowing { get; init; } = NoKeys;

    /// <summary>The pages of the element with that id, or null when the render did not place it (hidden, empty, or added since).</summary>
    public PublicationPageSpan? Of(string id) => Pages.TryGetValue(id, out var span) ? span : null;

    /// <summary>The page the element starts on, or null.</summary>
    public int? FirstPageOf(string id) => Of(id)?.First;

    /// <summary>The first box the element painted, or null when the render reported none.</summary>
    public PublicationBox? FirstBoxOf(string id) => Boxes.TryGetValue(id, out var boxes) && boxes.Count > 0 ? boxes[0] : null;

    /// <summary>True when the render cut off the content of the free-flow module with that id.</summary>
    public bool IsOverflowing(string id) => Overflowing.Contains(id);

    /// <summary>Builds the map from a render result.</summary>
    public static CreatorPageMap From(string json, PublicationRenderResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new CreatorPageMap(json, result.PageCount, result.Pages, result.Pdf)
        {
            Boxes = result.Boxes ?? NoBoxes,
            Overflowing = result.Overflowing ?? NoKeys,
        };
    }
}
