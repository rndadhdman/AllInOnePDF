using System.Globalization;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>
/// Recognises a cover page (the PDF's first page holding only a title block: large title, optional subtitle, an
/// "author · date" line and a note, no body paragraphs) and folds it into the metadata instead of the content. The
/// app's own covers carry an invisible <c>aio-cover</c> marker; other PDFs qualify when the title matches the PDF
/// title or is repeated as the first line of the next page.
/// </summary>
internal static class CoverPageFilter
{
    private const int MaxLines = 10;
    private const int MaxLetters = 600;

    /// <summary>What the cover said.</summary>
    internal sealed record CoverInfo(string Title, string? Subtitle, IReadOnlyList<string> Authors, DateTimeOffset? Date);

    /// <summary>
    /// Removes the cover's lines (marks them <see cref="PdfLine.Removed"/>) and fills <paramref name="metadata"/>:
    /// title and authors when the PDF has none, the subtitle, and the cover date as the document date.
    /// </summary>
    public static CoverInfo? Apply(IReadOnlyList<PageContent> pages, PdfMarkers markers, DocumentMetadata metadata)
    {
        if (pages.Count < 2 || pages[0].Number != 1)
        {
            return null;
        }

        var cover = pages[0];
        var lines = cover.ActiveLines.OrderByDescending(l => l.Top).ToList();
        var marked = markers.IsCover(cover.Number);
        if (lines.Count == 0 || (!marked && !LooksLikeCover(lines, pages, metadata.Title)))
        {
            return null;
        }

        var info = Read(lines, metadata.Language);
        foreach (var line in lines)
        {
            line.Removed = true;
        }

        if (info is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(metadata.Title))
        {
            metadata.Title = info.Title;
        }

        if (info.Subtitle is { } subtitle)
        {
            metadata.Subtitle = subtitle;

            // The renderer writes the subtitle into the PDF subject when there is no description.
            if (string.Equals(metadata.Description, subtitle, StringComparison.Ordinal))
            {
                metadata.Description = null;
            }
        }

        if (metadata.Authors.Count == 0)
        {
            metadata.Authors.AddRange(info.Authors);
        }

        if (info.Date is { } date)
        {
            metadata.Date = date;
        }

        return info;
    }

    /// <summary>Few short lines, one clearly larger than the body text of the following pages, no code, and a title that is confirmed elsewhere.</summary>
    private static bool LooksLikeCover(List<PdfLine> lines, IReadOnlyList<PageContent> pages, string? pdfTitle)
    {
        if (lines.Count > MaxLines || lines.Sum(l => l.LetterCount) > MaxLetters || lines.Any(l => l.IsMono))
        {
            return false;
        }

        var body = BodySize(pages.Skip(1));
        var largest = lines.Max(l => l.Size);
        if (body <= 0 || largest < body * 1.6)
        {
            return false;
        }

        // No paragraph: at most three lines at body size.
        if (lines.Count(l => Math.Abs(l.Size - body) <= 1) > 3)
        {
            return false;
        }

        var title = Normalize(TitleText(lines));
        if (title.Length == 0)
        {
            return false;
        }

        if (pdfTitle is not null && Normalize(pdfTitle) == title)
        {
            return true;
        }

        // The title repeated as the first line(s) of the next page (a cover followed by the document's own title).
        var next = pages[1].ActiveLines.OrderByDescending(l => l.Top).Take(3).Select(l => l.Text);
        return Normalize(string.Join(" ", next)).StartsWith(title, StringComparison.Ordinal);
    }

    private static CoverInfo? Read(List<PdfLine> lines, string? language)
    {
        var titleSize = lines.Max(l => l.Size);
        var title = TitleText(lines);
        if (title.Length == 0)
        {
            return null;
        }

        // Lines of one wrapped item (a long subtitle) are read together.
        var rest = new List<(string Text, double Size, double Italic)>();
        PdfLine? previous = null;
        foreach (var line in lines.Where(l => Math.Abs(l.Size - titleSize) > 0.5))
        {
            if (previous is not null && rest.Count > 0 && Math.Abs(line.Size - previous.Size) <= 0.5
                && previous.Baseline - line.Baseline <= line.Size * 1.8 && !line.Text.Contains('·', StringComparison.Ordinal))
            {
                rest[^1] = (rest[^1].Text + " " + line.Text.Trim(), rest[^1].Size, rest[^1].Italic);
            }
            else
            {
                rest.Add((line.Text.Trim(), line.Size, line.ItalicFraction));
            }

            previous = line;
        }

        var body = rest.Count == 0 ? 0 : rest.Min(l => l.Size);
        string? subtitle = null;
        var authors = new List<string>();
        DateTimeOffset? date = null;
        foreach (var line in rest)
        {
            var text = line.Text;
            if (text.Length == 0)
            {
                continue;
            }

            if (subtitle is null && authors.Count == 0 && date is null && line.Size > body + 1)
            {
                subtitle = text;
                continue;
            }

            // "Author · Date" (the renderer joins the cover's meta items with a middle dot).
            var parts = text.Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var recognised = new List<string>();
            DateTimeOffset? lineDate = null;
            foreach (var part in parts)
            {
                if (lineDate is null && ParseDate(part, language) is { } d)
                {
                    lineDate = d;
                }
                else
                {
                    recognised.Add(part);
                }
            }

            if (lineDate is not null || parts.Length > 1)
            {
                date ??= lineDate;
                authors.AddRange(recognised.SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }
            else if (subtitle is null && line.Italic < 0.5 && authors.Count == 0 && date is null)
            {
                subtitle = text;
            }
        }

        return new CoverInfo(title, subtitle, authors, date);
    }

    /// <summary>The lines at the largest size, top to bottom, joined.</summary>
    private static string TitleText(List<PdfLine> lines)
    {
        var size = lines.Max(l => l.Size);
        return string.Join(" ", lines.Where(l => Math.Abs(l.Size - size) <= 0.5).Select(l => l.Text.Trim())).Trim();
    }

    /// <summary>A date written as the cover writes it (long date of the document's or the current culture), or ISO.</summary>
    internal static DateTimeOffset? ParseDate(string text, string? language)
    {
        var cultures = new List<CultureInfo> { DocumentDates.CultureFor(language), CultureInfo.CurrentCulture, CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-GB") };
        foreach (var culture in cultures.Distinct())
        {
            if (DateTime.TryParseExact(text, culture.DateTimeFormat.LongDatePattern, culture, DateTimeStyles.AllowWhiteSpaces, out var exact))
            {
                return new DateTimeOffset(exact.Date, TimeZoneInfo.Local.GetUtcOffset(exact.Date));
            }
        }

        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return new DateTimeOffset(iso, TimeZoneInfo.Local.GetUtcOffset(iso));
        }

        return null;
    }

    private static double BodySize(IEnumerable<PageContent> pages)
    {
        var weights = new Dictionary<double, int>();
        foreach (var line in pages.SelectMany(p => p.ActiveLines))
        {
            var key = Math.Round(line.Size * 2) / 2;
            weights[key] = weights.GetValueOrDefault(key) + line.LetterCount;
        }

        return weights.Count == 0 ? 0 : weights.MaxBy(kv => kv.Value).Key;
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
