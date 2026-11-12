using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;

namespace AllInOnePdf.UI.Services;

/// <summary>A file opened in the Reader during this session.</summary>
/// <param name="DisplayName">File name (or document title for in-memory documents).</param>
/// <param name="Path">Absolute path on desktop; null for uploads and samples.</param>
/// <param name="Document">Kept for documents without a path so they can be reopened (uploads, samples).</param>
public sealed record ReaderRecentItem(string DisplayName, string? Path, DateTimeOffset OpenedAt, Document? Document)
{
    /// <summary>The folder that holds the file, shown under the name as Office's Recent list does; null without a path.</summary>
    public string? Folder
    {
        get
        {
            if (string.IsNullOrEmpty(Path))
            {
                return null;
            }

            try
            {
                var folder = System.IO.Path.GetDirectoryName(Path);
                return string.IsNullOrEmpty(folder) ? null : folder;
            }
            catch (Exception ex) when (ex is ArgumentException or System.IO.PathTooLongException)
            {
                return null;
            }
        }
    }

    /// <summary>When it was opened: the time for today (the usual case in one session), else the short date and time.</summary>
    public string OpenedLabel(DateTimeOffset now, System.Globalization.CultureInfo culture)
    {
        var local = OpenedAt.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("t", culture)
            : local.ToString("g", culture);
    }
}

/// <summary>
/// Per-session (per circuit / per window) Reader state: recently opened files, the remembered scroll position of
/// each document, the last mode, and the print-preview cache. Nothing here is persisted to disk.
/// </summary>
public sealed class ReaderSession
{
    public const int MaxRecent = 8;

    private readonly List<ReaderRecentItem> _recent = [];
    private readonly Dictionary<string, double> _scroll = new(Shared.FilePaths.Comparer);

    public event Action? Changed;

    public IReadOnlyList<ReaderRecentItem> Recent => _recent;

    public ReaderMode Mode { get; set; } = ReaderMode.Reflow;

    /// <summary>Print-preview PDF and page images of the most recent document shown in Pages mode.</summary>
    public ReaderPageCache Pages { get; } = new();

    /// <summary>HTML, outline and word count of the last document rendered in Reflow mode.</summary>
    public ReaderRenderResult? LastRender { get; set; }

    /// <summary>
    /// Adjustments made in the Reader (palette, size, font, outline…) for this session. They are never written to the
    /// preferences: Settings holds the defaults, and the Reader starts from them again in a new session or when the
    /// defaults change. Null while the Reader shows the defaults unchanged.
    /// </summary>
    public ReaderViewSettings? Adjusted { get; private set; }

    /// <summary>Palette picked in the Reader this session (null: as in Settings).</summary>
    public bool? AdjustedPaletteChosen { get; private set; }

    /// <summary>What the Reader shows: this session's adjustments, else the Settings defaults.</summary>
    public ReaderViewSettings ResolveSettings(UserPreferences preferences) =>
        Adjusted ?? ReaderViewSettings.FromPreferences(preferences);

    /// <summary>Whether the shown palette is an explicit choice (see <see cref="ReaderViewSettings.EffectivePalette"/>).</summary>
    public bool ResolvePaletteChosen(UserPreferences preferences) =>
        AdjustedPaletteChosen ?? preferences.ReaderPaletteChosen;

    /// <summary>Records an adjustment made while reading (does not touch the preferences).</summary>
    public void Adjust(ReaderViewSettings settings, bool paletteChosen)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Adjusted = settings.Normalized();
        AdjustedPaletteChosen = paletteChosen;
    }

    /// <summary>Drops the adjustments: the Reader shows the Settings defaults again.</summary>
    public void ClearAdjustments()
    {
        Adjusted = null;
        AdjustedPaletteChosen = null;
    }

    /// <summary>Stable key for remembering a position: the source path, else the document title.</summary>
    public static string KeyFor(Document document, string? path) =>
        !string.IsNullOrEmpty(path) ? "path:" + path : "mem:" + (document.ResolveTitle() ?? "untitled");

    public void AddRecent(Document document, string? path, string? displayName = null)
    {
        var name = displayName
                   ?? (string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileName(path))
                   ?? document.ResolveTitle()
                   ?? "Untitled";

        _recent.RemoveAll(r => path is not null
            ? Shared.FilePaths.AreEqual(r.Path, path)
            : r.Path is null && (ReferenceEquals(r.Document, document) || string.Equals(r.DisplayName, name, StringComparison.Ordinal)));
        _recent.Insert(0, new ReaderRecentItem(name, path, DateTimeOffset.Now, path is null ? document : null));
        if (_recent.Count > MaxRecent)
        {
            _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
        }

        Changed?.Invoke();
    }

    public void RemoveRecent(ReaderRecentItem item)
    {
        if (_recent.Remove(item))
        {
            Changed?.Invoke();
        }
    }

    public double? GetScrollFraction(string key) => _scroll.TryGetValue(key, out var f) ? f : null;

    public void SetScrollFraction(string key, double fraction)
    {
        if (double.IsFinite(fraction))
        {
            _scroll[key] = Math.Clamp(fraction, 0, 1);
        }
    }
}

/// <summary>
/// Cache for Pages mode: the PDF rendered for one document with one theme, its page sizes, and rasterised page
/// images (data URLs only) per page at the DPI they were rendered at.
/// </summary>
public sealed class ReaderPageCache
{
    /// <summary>Keep at most this many rasterised pages in memory.</summary>
    public const int MaxImages = 48;

    private readonly Dictionary<int, CachedPage> _images = [];

    public Document? Document { get; private set; }
    public string? ThemeId { get; private set; }
    public string? ThemeName { get; private set; }
    public byte[]? Pdf { get; private set; }
    public IReadOnlyList<(double WidthPt, double HeightPt)> PageSizes { get; private set; } = [];

    public bool Matches(Document document, string themeId) =>
        ReferenceEquals(Document, document) && string.Equals(ThemeId, themeId, StringComparison.Ordinal) && Pdf is not null;

    public void Set(Document document, string themeId, string themeName, byte[] pdf, IReadOnlyList<(double WidthPt, double HeightPt)> sizes)
    {
        Document = document;
        ThemeId = themeId;
        ThemeName = themeName;
        Pdf = pdf;
        PageSizes = sizes;
        _images.Clear();
    }

    public void Clear()
    {
        Document = null;
        ThemeId = null;
        ThemeName = null;
        Pdf = null;
        PageSizes = [];
        _images.Clear();
    }

    /// <summary>Returns the cached image for a page if one exists (at any DPI).</summary>
    public CachedPage? Get(int pageIndex) => _images.GetValueOrDefault(pageIndex);

    public void Put(PageImage image, int dpi, IReadOnlyCollection<int> keepNear)
    {
        // Keep only the data URL the page renders: holding the encoded bytes as well would double the memory.
        _images[image.PageIndex] = new CachedPage(image.PageIndex, dpi, image.ToDataUrl());
        if (_images.Count <= MaxImages)
        {
            return;
        }

        // Evict the pages furthest from what is on screen.
        var anchor = keepNear.Count > 0 ? keepNear.Average() : image.PageIndex;
        foreach (var victim in _images.Keys.OrderByDescending(k => Math.Abs(k - anchor)).Take(_images.Count - MaxImages).ToList())
        {
            _images.Remove(victim);
        }
    }
}

/// <summary>A rasterised preview page: its index, the DPI it was rendered at and the image as a data URL.</summary>
public sealed record CachedPage(int PageIndex, int Dpi, string DataUrl);

public sealed record ReaderRenderResult(Document Document, string Html, IReadOnlyList<ReaderOutlineNode> Outline, int WordCount);
