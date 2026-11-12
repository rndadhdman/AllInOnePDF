using System.Text;

namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>An outline (bookmark) entry flattened for matching against text lines.</summary>
internal sealed record OutlineEntry(string Title, int Level, int? Page);

/// <summary>
/// Decides heading levels. Sources, in order: the PDF outline (bookmark title matched to a line on the same page,
/// bookmark depth = level), then font size (distinct sizes larger than the body cluster into levels, largest = H1),
/// then bold body-size short lines (one level below the smallest size level, capped at H4).
/// Sizes that only occur on a sparse first page (a cover) are not levels of their own: the largest becomes H1.
/// </summary>
internal sealed class HeadingDetector
{
    private readonly List<(double Size, int Level)> _levels = [];
    private readonly HashSet<double> _coverSizes = [];
    private readonly double _coverTitleSize = -1;
    private readonly int _coverPage;
    private readonly double _ratio;
    private readonly bool _enabled;
    private readonly bool _boldHeadings;
    private readonly int _boldMaxLength;
    private readonly List<OutlineMatch> _outline = [];

    private sealed class OutlineMatch(OutlineEntry entry)
    {
        public OutlineEntry Entry { get; } = entry;
        public string Key { get; } = NormalizeTitle(entry.Title);
        public bool Used { get; set; }
    }

    public FontStatistics Statistics { get; }

    /// <summary>Level used for bold body-size headings.</summary>
    public int BoldLevel { get; }

    public IReadOnlyList<(double Size, int Level)> SizeLevels => _levels;

    public HeadingDetector(FontStatistics statistics, IReadOnlyList<PageContent> pages, PdfImportSettings settings, IReadOnlyList<OutlineEntry>? outline)
    {
        Statistics = statistics;
        _enabled = settings.DetectHeadings;
        _ratio = settings.HeadingSensitivity switch
        {
            HeadingSensitivity.Low => 1.3,
            HeadingSensitivity.High => 1.06,
            _ => 1.12,
        };
        _boldHeadings = settings.HeadingSensitivity != HeadingSensitivity.Low;
        _boldMaxLength = settings.HeadingSensitivity == HeadingSensitivity.High ? 100 : 70;

        if (settings.UseBookmarks && outline is { Count: > 0 })
        {
            var minLevel = outline.Min(o => o.Level);
            foreach (var entry in outline)
            {
                _outline.Add(new OutlineMatch(entry with { Level = entry.Level - minLevel + 1 }));
            }
        }

        // Candidate sizes with the pages they appear on.
        var sizePages = new Dictionary<double, HashSet<int>>();
        foreach (var page in pages)
        {
            foreach (var line in page.ActiveLines)
            {
                if (IsSizeCandidate(line))
                {
                    var key = FontStatistics.Round(line.Size);
                    if (!sizePages.TryGetValue(key, out var set))
                    {
                        sizePages[key] = set = [];
                    }

                    set.Add(line.Page);
                }
            }
        }

        // A sparse first page whose large sizes appear nowhere else is a cover: keep only its largest size as H1.
        if (pages.Count >= 2 && pages[0].Number == 1 && pages[0].ActiveLines.Count() <= 12)
        {
            _coverPage = pages[0].Number;
            foreach (var (size, set) in sizePages)
            {
                if (set.Count == 1 && set.Contains(_coverPage))
                {
                    _coverSizes.Add(size);
                }
            }

            if (_coverSizes.Count > 0 && _coverSizes.Count < sizePages.Count)
            {
                _coverTitleSize = _coverSizes.Max();
                foreach (var size in _coverSizes)
                {
                    sizePages.Remove(size);
                }
            }
            else
            {
                _coverSizes.Clear();
            }
        }

        var sorted = sizePages.Keys.OrderByDescending(s => s).ToList();
        var level = 0;
        double? clusterTop = null;
        foreach (var size in sorted)
        {
            if (clusterTop is null || clusterTop.Value - size > Math.Max(0.6, clusterTop.Value * 0.04))
            {
                level++;
                clusterTop = size;
            }

            _levels.Add((size, Math.Min(level, 6)));
        }

        BoldLevel = Math.Clamp(level + 1, 1, 4);
    }

    private bool IsSizeCandidate(PdfLine line) =>
        line.LetterCount >= 1 && !line.IsMono && line.Size >= Statistics.BodySize * _ratio && line.Text.Length <= 200;

    /// <summary>Heading level from font size alone (0 = not a heading).</summary>
    public int LevelForSize(PdfLine line)
    {
        if (!_enabled || line.LetterCount == 0 || line.IsMono)
        {
            return 0;
        }

        var key = FontStatistics.Round(line.Size);
        if (_coverSizes.Contains(key) && line.Page == _coverPage)
        {
            return key == _coverTitleSize ? 1 : 0;
        }

        if (!IsSizeCandidate(line))
        {
            return 0;
        }

        foreach (var (size, level) in _levels)
        {
            if (Math.Abs(size - key) < 0.01)
            {
                return level;
            }
        }

        return 0;
    }

    /// <summary>True when a fully bold body-size line may stand as a heading of its own.</summary>
    public bool IsBoldHeadingCandidate(PdfLine line)
    {
        if (!_enabled || !_boldHeadings || !line.IsBold || line.IsMono)
        {
            return false;
        }

        var text = line.Text.Trim();
        if (text.Length is < 2 or > 100 || text.Length > _boldMaxLength)
        {
            return false;
        }

        if (text.EndsWith('.') || text.EndsWith(',') || text.EndsWith(';'))
        {
            return false;
        }

        return Math.Abs(line.Size - Statistics.BodySize) <= Math.Max(0.6, Statistics.BodySize * 0.06) && text.Any(char.IsLetter);
    }

    /// <summary>Matches the text against an unused outline entry on the same (or adjacent) page.</summary>
    public int? MatchOutline(string text, int page)
    {
        if (_outline.Count == 0)
        {
            return null;
        }

        var key = NormalizeTitle(text);
        if (key.Length == 0)
        {
            return null;
        }

        foreach (var candidate in _outline)
        {
            if (candidate.Used || candidate.Key.Length == 0)
            {
                continue;
            }

            if (candidate.Entry.Page is { } p && Math.Abs(p - page) > 1)
            {
                continue;
            }

            var match = string.Equals(candidate.Key, key, StringComparison.Ordinal)
                || (candidate.Key.Length >= 4 && key.EndsWith(candidate.Key, StringComparison.Ordinal) && key.Length - candidate.Key.Length <= 12);
            if (match)
            {
                candidate.Used = true;
                return Math.Clamp(candidate.Entry.Level, 1, 6);
            }
        }

        return null;
    }

    /// <summary>True when the text could still become an outline match (prefix of an unused title on that page).</summary>
    public bool IsOutlinePrefix(string text, int page)
    {
        var key = NormalizeTitle(text);
        return key.Length > 0 && _outline.Any(c => !c.Used && c.Key.Length > key.Length
            && (c.Entry.Page is null || Math.Abs(c.Entry.Page.Value - page) <= 1)
            && (c.Key.StartsWith(key, StringComparison.Ordinal) || c.Key.Contains(key, StringComparison.Ordinal)));
    }

    public bool HasOutline => _outline.Count > 0;

    /// <summary>Letters and digits only, lower case, single spaces.</summary>
    internal static string NormalizeTitle(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (space && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                space = false;
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                space = true;
            }
        }

        return sb.ToString();
    }
}
