using System.Globalization;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using AngleSharp.Dom;
using Document = AllInOnePdf.Core.Documents.Document;

namespace AllInOnePdf.Core.Importers.Html;

/// <summary>Footnotes (our own output, Pandoc, markdown-it, EPUB 3 notes) and page metadata.</summary>
internal sealed partial class HtmlDocumentConverter
{
    // ------------------------------------------------------------------ metadata

    /// <summary>Reads title, authors, description, keywords, date and language from the page head.</summary>
    public void ReadMetadata(IDocument dom)
    {
        var meta = _doc.Metadata;
        var title = NullIfEmpty(CollapseText(dom.Head?.QuerySelector("title")?.TextContent ?? string.Empty));
        var metas = dom.Head?.QuerySelectorAll("meta").ToList() ?? [];

        string? Content(params string[] names) => metas
            .Where(m => names.Any(n => string.Equals(m.GetAttribute("name") ?? m.GetAttribute("property"), n, StringComparison.OrdinalIgnoreCase)))
            .Select(m => NullIfEmpty(m.GetAttribute("content")))
            .FirstOrDefault(v => v is not null);

        meta.Title ??= title ?? Content("og:title", "twitter:title", "dc.title", "dcterms.title");
        meta.Subtitle ??= Content("subtitle");
        meta.Description ??= Content("description", "og:description", "dc.description", "dcterms.description");

        if (meta.Authors.Count == 0)
        {
            foreach (var m in metas.Where(m => (m.GetAttribute("name") ?? m.GetAttribute("property")) is { } n
                && (string.Equals(n, "author", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "dc.creator", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "article:author", StringComparison.OrdinalIgnoreCase))))
            {
                var author = NullIfEmpty(m.GetAttribute("content"));
                if (author is not null && !meta.Authors.Contains(author) && !author.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    meta.Authors.Add(author);
                }
            }
        }

        if (meta.Keywords.Count == 0 && Content("keywords") is { } keywords)
        {
            meta.Keywords.AddRange(keywords.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        if (meta.Date is null && ParseDate(Content("date", "dcterms.date", "dc.date", "article:published_time", "dcterms.created")) is { } date)
        {
            meta.Date = date;
        }

        meta.Language ??= NullIfEmpty(dom.DocumentElement?.GetAttribute("lang") ?? dom.DocumentElement?.GetAttribute("xml:lang"));
    }

    internal static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }

    // ------------------------------------------------------------------ footnotes

    private static bool IsFootnoteContainer(IElement el)
    {
        if (el.LocalName is not ("section" or "div" or "ol" or "aside" or "nav"))
        {
            return false;
        }

        if (el.ClassList.Contains("aio-footnotes") || el.ClassList.Contains("footnotes") || el.ClassList.Contains("footnote-list"))
        {
            return true;
        }

        var role = el.GetAttribute("role");
        if (string.Equals(role, "doc-endnotes", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "doc-footnotes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return EpubType(el) is { } type && (type.Contains("footnotes", StringComparison.Ordinal) || type.Contains("endnotes", StringComparison.Ordinal) || type.Contains("rearnotes", StringComparison.Ordinal));
    }

    private static bool IsSingleFootnote(IElement el)
    {
        if (el.LocalName is not ("aside" or "div" or "section" or "p" or "li"))
        {
            return false;
        }

        var role = el.GetAttribute("role");
        if (string.Equals(role, "doc-footnote", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "doc-endnote", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return EpubType(el) is { } type && (type.Contains("footnote", StringComparison.Ordinal) || type.Contains("endnote", StringComparison.Ordinal) || type.Contains("rearnote", StringComparison.Ordinal) || type == "note");
    }

    private static string? EpubType(IElement el) =>
        (el.GetAttribute("epub:type") ?? el.GetAttribute("http://www.idpf.org/2007/ops", "type"))?.ToLowerInvariant();

    private void ReadFootnotes(IElement container)
    {
        if (IsSingleFootnote(container) && !IsFootnoteContainer(container))
        {
            ReadSingleFootnote(container);
            return;
        }

        var items = container.QuerySelectorAll("li").Where(li => li.ParentElement?.Closest("li") is null).ToList();
        if (items.Count == 0)
        {
            items = container.Children.Where(IsSingleFootnote).ToList();
        }

        var index = 0;
        foreach (var li in items)
        {
            index++;
            var id = NullIfEmpty(li.Id);
            var label = id is null ? index.ToString(CultureInfo.InvariantCulture) : FootnoteLabel(id);
            AddFootnote(li, label);
        }
    }

    private void ReadSingleFootnote(IElement el)
    {
        var id = NullIfEmpty(el.Id);
        var label = id is null ? (_doc.Footnotes.Count + 1).ToString(CultureInfo.InvariantCulture) : FootnoteLabel(id);
        AddFootnote(el, label);
    }

    private void AddFootnote(IElement el, string label)
    {
        foreach (var back in el.QuerySelectorAll("a").ToArray())
        {
            var href = back.GetAttribute("href") ?? string.Empty;
            var role = back.GetAttribute("role") ?? string.Empty;
            // Our own HTML and EPUB write <a class="aio-fn-back" href="#ref-fn-1">1</a> in front of the note.
            if (back.ClassList.Contains("footnote-backref") || back.ClassList.Contains("aio-fnback") || back.ClassList.Contains("aio-fn-back")
                || role.Equals("doc-backlink", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("#fnref", StringComparison.OrdinalIgnoreCase) || href.StartsWith("#ref-fn", StringComparison.OrdinalIgnoreCase)
                || (EpubType(back)?.Contains("backlink", StringComparison.Ordinal) ?? false))
            {
                back.Remove();
            }
        }

        var footnote = new Footnote { Label = label };
        ConvertChildren(el, footnote.Blocks);
        _doc.Footnotes.RemoveAll(f => string.Equals(f.Label, label, StringComparison.Ordinal));
        _doc.Footnotes.Add(footnote);
    }

    private FootnoteReferenceInline? FootnoteRefFrom(IElement el)
    {
        IElement? anchor = null;
        if (el.LocalName == "a")
        {
            var role = el.GetAttribute("role");
            var isRef = el.ClassList.Contains("footnote-ref") || string.Equals(role, "doc-noteref", StringComparison.OrdinalIgnoreCase)
                || (EpubType(el)?.Contains("noteref", StringComparison.Ordinal) ?? false)
                || el.ParentElement is { LocalName: "sup" } sup && (sup.ClassList.Contains("aio-fnref") || sup.ClassList.Contains("footnote-ref"));
            anchor = isRef ? el : null;
        }
        else if (el.LocalName == "sup")
        {
            var links = el.Children.Where(c => c.LocalName == "a").ToList();
            if (links.Count == 1 && el.Children.Length == 1)
            {
                var href = links[0].GetAttribute("href") ?? string.Empty;
                var isRef = el.ClassList.Contains("aio-fnref") || el.ClassList.Contains("footnote-ref") || links[0].ClassList.Contains("footnote-ref")
                    || FootnoteHref().IsMatch(href) || (EpubType(links[0])?.Contains("noteref", StringComparison.Ordinal) ?? false);
                anchor = isRef ? links[0] : null;
            }
        }

        var target = anchor?.GetAttribute("href");
        if (target is null)
        {
            return null;
        }

        var hash = target.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0 || hash == target.Length - 1)
        {
            return null;
        }

        return new FootnoteReferenceInline { Label = FootnoteLabel(Uri.UnescapeDataString(target[(hash + 1)..])) };
    }

    /// <summary>"fn-3", "fn:3" and "fn3" become "3"; other note ids are numbered in order of first appearance.</summary>
    private string FootnoteLabel(string id)
    {
        var m = FootnoteId().Match(id);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        if (!_footnoteLabels.TryGetValue(id, out var label))
        {
            do
            {
                label = (++_footnoteCounter).ToString(CultureInfo.InvariantCulture);
            }
            while (_footnoteLabels.ContainsValue(label) || _doc.Footnotes.Any(f => f.Label == label));

            _footnoteLabels[id] = label;
        }

        return label;
    }

    [GeneratedRegex(@"^fn[-:_]?([\w.\-]+)$")]
    private static partial Regex FootnoteId();

    [GeneratedRegex(@"^#fn[-:_]?[\w.\-]+$")]
    private static partial Regex FootnoteHref();
}
