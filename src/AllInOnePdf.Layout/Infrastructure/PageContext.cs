namespace AllInOnePdf.Layout.Infrastructure;

/// <summary>Where a captured element was drawn: the page and the position on that page, in points from the page's top-left.</summary>
public readonly record struct CapturedPosition(int PageNumber, float X, float Y);

/// <summary>
/// Per-document state shared by every element: the current page while laying out, the page count and section
/// pages learnt in the first pass, and positions captured with <c>CaptureContentPosition</c>. A document is laid out
/// twice: the first pass fills this context, the second pass draws with the final numbers.
/// </summary>
public sealed class PageContext
{
    private readonly Dictionary<string, int> _sectionPages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PageSpan> _tracked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CapturedPosition>> _captures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TrackedBox>> _boxes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _overflowing = new(StringComparer.Ordinal);

    /// <summary>1-based number of the page being laid out.</summary>
    public int CurrentPage { get; private set; }

    /// <summary>Total pages, known after the first pass (0 before).</summary>
    public int PageCount { get; private set; }

    /// <summary>True while the engine measures the document to learn page numbers; drawing calls are ignored then.</summary>
    public bool IsFirstPass { get; private set; } = true;

    public void BeginFirstPass()
    {
        IsFirstPass = true;
        CurrentPage = 0;
        _sectionPages.Clear();
        _tracked.Clear();
        _captures.Clear();
        _boxes.Clear();
        _overflowing.Clear();
    }

    public void BeginSecondPass(int pageCount)
    {
        IsFirstPass = false;
        PageCount = pageCount;
        CurrentPage = 0;
    }

    public void BeginPage() => CurrentPage++;

    /// <summary>Records the page a section starts on (first page wins). Called by the Section element when it first draws.</summary>
    public void SetSectionPage(string name, int page)
    {
        ArgumentNullException.ThrowIfNull(name);
        _sectionPages.TryAdd(name, page);
    }

    /// <summary>The page a section starts on, or null when the section was not laid out (yet).</summary>
    public int? GetSectionPage(string name) => _sectionPages.TryGetValue(name, out var page) ? page : null;

    /// <summary>Records that the element tracked under <paramref name="key"/> drew on <paramref name="page"/> (called by the page tracker element on every draw).</summary>
    public void TrackPage(string key, int page)
    {
        ArgumentNullException.ThrowIfNull(key);
        _tracked[key] = _tracked.TryGetValue(key, out var span)
            ? new PageSpan(Math.Min(span.First, page), Math.Max(span.Last, page))
            : new PageSpan(page, page);
    }

    /// <summary>The pages every tracked element has drawn on so far.</summary>
    public IReadOnlyDictionary<string, PageSpan> TrackedPages => _tracked;

    /// <summary>Records one draw of the element tracked under <paramref name="key"/> (the page tracker calls it on every draw of the drawing pass).</summary>
    /// <param name="key">The tracking key.</param>
    /// <param name="box">The page and the rectangle, in page coordinates, the element was drawn into.</param>
    public void TrackBox(string key, TrackedBox box)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_boxes.TryGetValue(key, out var list))
        {
            _boxes[key] = list = [];
        }

        list.Add(box);
    }

    /// <summary>A copy of every tracked element's boxes so far, per key, in draw order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TrackedBox>> TrackedBoxes =>
        _boxes.ToDictionary(static p => p.Key, static p => (IReadOnlyList<TrackedBox>)p.Value.ToArray(), StringComparer.Ordinal);

    /// <summary>Records that the freely placed item under <paramref name="key"/> did not fit its box.</summary>
    /// <param name="key">The item's key.</param>
    public void MarkOverflowing(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _overflowing.Add(key);
    }

    /// <summary>The keys of freely placed items whose content did not fit their box.</summary>
    public IReadOnlySet<string> Overflowing => _overflowing;

    public void RegisterCapture(string id, CapturedPosition position)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_captures.TryGetValue(id, out var list))
        {
            _captures[id] = list = [];
        }

        list.Add(position);
    }

    /// <summary>Every position captured under <paramref name="id"/> so far (empty in the first pass before the element was reached).</summary>
    public IReadOnlyList<CapturedPosition> GetCaptures(string id) =>
        _captures.TryGetValue(id, out var list) ? list : [];
}
