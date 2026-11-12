using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>
/// Removes running headers and footers: lines in the top or bottom 12% of the page whose text (digits normalised,
/// so "Page 3 of 9" matches "Page 4 of 9") repeats at a similar size on at least 60% of the pages, plus bare page
/// numbers in those zones. A line is also dropped when one of its wide-gap segments repeats (a header with the
/// document title on the left and the current section on the right).
/// </summary>
internal static partial class HeaderFooterFilter
{
    public const double ZoneFraction = 0.12;
    public const double RepeatFraction = 0.6;

    /// <summary>Marks matching lines as <see cref="PdfLine.Removed"/> and returns how many were removed.</summary>
    public static int Apply(IReadOnlyList<PageContent> pages)
    {
        if (pages.Count == 0)
        {
            return 0;
        }

        var threshold = Math.Max(2, (int)Math.Ceiling(pages.Count * RepeatFraction));
        var keyPages = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var candidates = new List<(PdfLine Line, List<string> Keys)>();

        foreach (var page in pages)
        {
            foreach (var line in page.ActiveLines)
            {
                var zone = ZoneOf(line, page);

                // Code inside a shaded box is content even when similar lines open every page.
                if (zone == 0 || (line.IsMono && line.BoxId is not null))
                {
                    continue;
                }

                var keys = KeysFor(line, zone);
                candidates.Add((line, keys));
                foreach (var key in keys)
                {
                    if (!keyPages.TryGetValue(key, out var set))
                    {
                        keyPages[key] = set = [];
                    }

                    set.Add(page.Number);
                }
            }
        }

        var removed = 0;
        foreach (var (line, keys) in candidates)
        {
            var repeated = pages.Count >= 2 && keys.Any(k => keyPages[k].Count >= threshold);
            if (repeated || IsPageNumber(line.Text))
            {
                line.Removed = true;
                removed++;
            }
        }

        return removed;
    }

    /// <summary>1 = header zone, -1 = footer zone, 0 = body.</summary>
    internal static int ZoneOf(PdfLine line, PageContent page)
    {
        if (line.Bottom >= page.Height * (1 - ZoneFraction))
        {
            return 1;
        }

        return line.Top <= page.Height * ZoneFraction ? -1 : 0;
    }

    private static List<string> KeysFor(PdfLine line, int zone)
    {
        var size = Math.Round(line.Size).ToString(CultureInfo.InvariantCulture);
        var prefix = (zone > 0 ? "H|" : "F|") + size + "|";
        var keys = new List<string> { prefix + Normalize(line.Text) };
        if (line.Segments.Count > 1)
        {
            foreach (var segment in line.Segments)
            {
                var text = Normalize(TextJoiner.Join(segment.Words));
                if (text.Length >= 3)
                {
                    keys.Add(prefix + "S|" + text);
                }
            }
        }

        return keys;
    }

    /// <summary>Lower-case, digits replaced by '#', whitespace collapsed.</summary>
    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }

            space = false;
            sb.Append(char.IsDigit(ch) ? '#' : char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    internal static bool IsPageNumber(string text) => PageNumber().IsMatch(text.Trim());

    [GeneratedRegex(@"^(?:[-–—|\s]*)(?:page|seite|p\.|pg\.?)?\s*(?:\d{1,5}|[ivxlcdm]{1,7}|[IVXLCDM]{1,7})(?:\s*(?:of|/|von|de|sur)\s*\d{1,5})?(?:[-–—|\s]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageNumber();
}

/// <summary>
/// Drops printed tables of contents: runs of at least three lines that end in a page number after a wide gap or
/// dot leaders (or sit under internal links), plus a "Contents" title right above the run.
/// </summary>
internal static partial class TableOfContentsFilter
{
    public static int Apply(IReadOnlyList<PageContent> pages)
    {
        var removed = 0;
        foreach (var page in pages)
        {
            var lines = page.ActiveLines.ToList();
            var i = 0;
            while (i < lines.Count)
            {
                if (!IsEntry(lines[i], page))
                {
                    i++;
                    continue;
                }

                var j = i;
                while (j < lines.Count && IsEntry(lines[j], page))
                {
                    j++;
                }

                // Three entries make a contents list on their own; under a "Contents" title one is enough (a short
                // document's contents page).
                var titled = i > 0 && ContentsTitle().IsMatch(lines[i - 1].Text.Trim());
                if (j - i >= 3 || titled)
                {
                    for (var k = i; k < j; k++)
                    {
                        lines[k].Removed = true;
                        removed++;
                    }

                    if (titled)
                    {
                        lines[i - 1].Removed = true;
                        removed++;
                    }
                }

                i = j;
            }
        }

        return removed;
    }

    private static bool IsEntry(PdfLine line, PageContent page)
    {
        if (line.Words.Count < 2 || !TrailingNumber().IsMatch(line.Words[^1].Text))
        {
            return false;
        }

        if (DotLeaders().IsMatch(line.Text))
        {
            return true;
        }

        if (line.Segments.Count >= 2 && line.Segments[^1].Words.Count == 1)
        {
            return true;
        }

        var cy = (line.Top + line.Bottom) / 2;
        return page.InternalLinks.Any(a => a.Contains(line.Left + 2, cy, 2));
    }

    [GeneratedRegex(@"^(?:\d{1,4}|[ivxlcdm]{1,6})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNumber();

    [GeneratedRegex(@"(?:\.\s?){4,}|…{2,}")]
    private static partial Regex DotLeaders();

    [GeneratedRegex(@"^(?:table of contents|contents|inhalt|inhaltsverzeichnis|sommaire|table des matières|índice|indice)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContentsTitle();
}
