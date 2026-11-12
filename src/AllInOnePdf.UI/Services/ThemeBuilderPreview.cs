using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.UI.Services;

/// <summary>
/// Builds the fast-preview markup: the document HTML from <c>IDocumentHtmlRenderer</c> laid out on simulated
/// sheets (cover, table of contents, content split at explicit page breaks and, when the theme asks for it,
/// before every level-1 heading) with header and footer strips. Styling comes from <see cref="ThemeBuilderCssGenerator"/>.
/// </summary>
public static partial class ThemeBuilderPreview
{
    /// <param name="document">The sample document.</param>
    /// <param name="fragment">Output of <c>RenderFragment(PrepareDocument(document), BlockIndexes = true, HeadingAnchors = false)</c>.</param>
    /// <param name="theme">Theme to lay out with.</param>
    /// <param name="totalPages">Page count from the last exact render, used for the {pages} token.</param>
    public static string Compose(Document document, string fragment, PdfTheme theme, int? totalPages)
    {
        var inner = ApplyThemeMarkers(StripArticle(fragment), theme);
        if (theme.Code.ShowLineNumbers)
        {
            inner = CodeBlock().Replace(inner, m =>
            {
                var lines = m.Groups["body"].Value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
                var sb = new StringBuilder(m.Length + lines.Length * 24);
                sb.Append("<pre><code").Append(m.Groups["attrs"].Value).Append('>');
                foreach (var line in lines)
                {
                    sb.Append("<span class=\"tb-ln\">").Append(line.Length == 0 ? "&#8203;" : line).Append("</span>");
                }

                sb.Append("</code></pre>");
                return sb.ToString();
            });
        }

        var (chunks, chunkStarts) = SplitIntoChunks(inner, theme.Headings.PageBreakBeforeH1);
        var title = document.ResolveTitle() ?? "Document";
        // Before the first exact render the count is the number of simulated sheets, filled in once they are all laid out.
        var pagesText = totalPages is > 0 ? totalPages.Value.ToString(CultureInfo.InvariantCulture) : PagesPending;
        var lang = LangTag().IsMatch(document.Metadata.Language ?? string.Empty) ? document.Metadata.Language! : "en";

        var tocEntries = theme.Toc.Enabled ? document.Headings.Where(h => h.Level <= theme.Toc.Depth).ToList() : [];
        var includeToc = theme.Toc.Enabled && tocEntries.Count > 0;

        var html = new StringBuilder(inner.Length + 4096);
        html.Append("<div class=\"tb-pages\">");
        var page = 1;

        if (theme.Cover.Enabled)
        {
            // Header/Footer.ShowOnCover: the renderer prints them on the cover only when the theme opts in.
            html.Append("<section class=\"tb-sheet tb-cover-sheet\" data-page=\"1\" aria-label=\"Cover page\">");
            if (theme.Header.HasContent && theme.Header.ShowOnCover)
            {
                html.Append("<div class=\"tb-header-slot\">");
                AppendStrip(html, "tb-header", theme.Header, document, title, 1, pagesText);
                html.Append("</div>");
            }

            AppendCover(html, document, theme, title);
            if (theme.Footer.HasContent && theme.Footer.ShowOnCover)
            {
                html.Append("<div class=\"tb-footer-slot\">");
                AppendStrip(html, "tb-footer", theme.Footer, document, title, 1, pagesText);
                html.Append("</div>");
            }

            html.Append("</section>");
            page++;
        }

        var firstContentPage = page;
        var contentPage = 0;

        void OpenSheet(int number)
        {
            html.Append("<section class=\"tb-sheet\" data-page=\"").Append(number.ToString(CultureInfo.InvariantCulture))
                .Append("\" aria-label=\"Page ").Append(number.ToString(CultureInfo.InvariantCulture)).Append("\">");
            var showHeader = theme.Header.HasContent && (theme.Header.ShowOnFirstPage || contentPage > 0);
            if (showHeader)
            {
                html.Append("<div class=\"tb-header-slot\">");
                AppendStrip(html, "tb-header", theme.Header, document, title, number, pagesText);
                html.Append("</div>");
            }

            html.Append("<div class=\"tb-body\">");
        }

        void CloseSheet(int number)
        {
            html.Append("</div>");
            var showFooter = theme.Footer.HasContent && (theme.Footer.ShowOnFirstPage || contentPage > 0);
            if (showFooter)
            {
                html.Append("<div class=\"tb-footer-slot\">");
                AppendStrip(html, "tb-footer", theme.Footer, document, title, number, pagesText);
                html.Append("</div>");
            }

            html.Append("</section>");
            contentPage++;
        }

        var tocOwnSheet = includeToc && theme.Toc.PageBreakAfter;
        var chunkPageOffset = tocOwnSheet ? 1 : 0;
        string? tocHtml = includeToc ? BuildToc(document, theme, tocEntries, chunkStarts, firstContentPage + chunkPageOffset) : null;

        if (tocOwnSheet)
        {
            OpenSheet(page);
            html.Append(tocHtml);
            CloseSheet(page);
            page++;
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            OpenSheet(page);
            if (i == 0 && includeToc && !tocOwnSheet)
            {
                html.Append(tocHtml).Append("<div class=\"tb-toc-spacer\"></div>");
            }

            // lang lets the browser hyphenate (CSS hyphens:auto) like the renderer's Typography.Hyphenate.
            html.Append("<article class=\"aio-doc\" lang=\"").Append(lang).Append("\">").Append(chunks[i]).Append("</article>");
            CloseSheet(page);
            page++;
        }

        html.Append("</div>");
        var result = html.ToString();
        return totalPages is > 0 ? result : result.Replace(PagesPending, (page - 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>Stands in for {pages} until the sheet count is known (control characters pass HTML encoding unchanged).</summary>
    private const string PagesPending = "\u0001pages\u0001";

    /// <summary>Prefix the prepared document puts on generated callout titles (no title of their own).</summary>
    private const char CalloutDefaultMarker = '';

    /// <summary>Prefix the prepared document puts on a quote's attribution paragraph.</summary>
    private const char AttributionMarker = '';

    /// <summary>
    /// Returns a shallow copy of <paramref name="source"/> for the fast preview's HTML render (the source is never
    /// mutated). Quotes whose last line starts with an em dash or "--" get a separate, marked attribution paragraph,
    /// as <c>PdfRenderer</c> does, and callouts without a title get a marked one so <see cref="Compose"/> can
    /// substitute the theme's label. Top-level block indexes are preserved.
    /// </summary>
    public static Document PrepareDocument(Document source)
    {
        var doc = new Document { Metadata = source.Metadata };
        doc.Resources.AddRange(source.Resources);
        doc.Footnotes.AddRange(source.Footnotes);
        foreach (var block in source.Blocks)
        {
            doc.Blocks.Add(PrepareBlock(block));
        }

        return doc;
    }

    private static Block PrepareBlock(Block block)
    {
        switch (block)
        {
            case QuoteBlock quote:
                {
                    var (blocks, attribution) = SplitAttribution(quote);
                    var copy = new QuoteBlock { Id = quote.Id, SourcePage = quote.SourcePage, Attribution = quote.Attribution };
                    copy.Blocks.AddRange(blocks.Select(PrepareBlock));
                    if (attribution is not null)
                    {
                        var p = new ParagraphBlock();
                        p.Inlines.Add(new TextInline(AttributionMarker + "— "));
                        p.Inlines.AddRange(attribution);
                        copy.Blocks.Add(p);
                    }

                    return copy;
                }

            case CalloutBlock callout:
                {
                    var title = string.IsNullOrWhiteSpace(callout.Title) ? CalloutDefaultMarker + callout.Kind.ToString() : callout.Title;
                    var copy = new CalloutBlock { Id = callout.Id, SourcePage = callout.SourcePage, Kind = callout.Kind, Title = title };
                    copy.Blocks.AddRange(callout.Blocks.Select(PrepareBlock));
                    return copy;
                }

            default:
                return block;
        }
    }

    /// <summary>Mirror of <c>PdfRenderer.SplitAttribution</c>: the attribution inlines without the dash, or null.</summary>
    private static (IReadOnlyList<Block> Blocks, List<Inline>? Attribution) SplitAttribution(QuoteBlock quote)
    {
        if (!string.IsNullOrWhiteSpace(quote.Attribution) || quote.Blocks.Count == 0 || quote.Blocks[^1] is not ParagraphBlock last)
        {
            return (quote.Blocks, null);
        }

        var blocks = quote.Blocks.Take(quote.Blocks.Count - 1).ToList();
        if (StripDash(last.Inlines) is { } whole)
        {
            return blocks.Count == 0 ? (quote.Blocks, null) : (blocks, whole);
        }

        var br = last.Inlines.FindLastIndex(i => i is LineBreakInline);
        if (br <= 0 || StripDash(last.Inlines.Skip(br + 1).ToList()) is not { } tail)
        {
            return (quote.Blocks, null);
        }

        var head = new ParagraphBlock { Alignment = last.Alignment };
        head.Inlines.AddRange(last.Inlines.Take(br));
        blocks.Add(head);
        return (blocks, tail);
    }

    private static List<Inline>? StripDash(List<Inline> inlines)
    {
        if (inlines.Count == 0 || inlines[0] is not TextInline first)
        {
            return null;
        }

        var text = first.Text.TrimStart();
        string rest;
        if (text.StartsWith('—') || text.StartsWith('―') || text.StartsWith('–'))
        {
            rest = text[1..];
        }
        else if (text.StartsWith("--", StringComparison.Ordinal))
        {
            rest = text[2..];
        }
        else
        {
            return null;
        }

        rest = rest.TrimStart();
        var result = new List<Inline>(inlines.Count);
        if (rest.Length > 0)
        {
            result.Add(new TextInline(rest));
        }

        result.AddRange(inlines.Skip(1));
        return Inline.ToPlainText(result).Trim().Length == 0 ? null : result;
    }

    /// <summary>Applies the theme's callout labels and marks attribution lines in the prepared fragment.</summary>
    private static string ApplyThemeMarkers(string inner, PdfTheme theme)
    {
        inner = CalloutDefaultTitle().Replace(inner, m =>
        {
            var kind = Enum.TryParse<CalloutKind>(m.Groups["kind"].Value, out var k) ? k : CalloutKind.Note;
            var label = theme.Callouts.Kind(kind).Label;
            return "<div class=\"aio-callout-title tb-callout-default\">" + Encode(string.IsNullOrWhiteSpace(label) ? kind.ToString() : label) + "</div>";
        });
        return AttributionParagraph().Replace(inner, "<p class=\"aio-p tb-attribution\"${rest}>");
    }

    /// <summary>Replaces template tokens the same way the PDF renderer does; {page} and {pages} use the given values.</summary>
    public static string ExpandTemplate(string? template, Document document, string title, string page, string pages)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return Token().Replace(template, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "page" => page,
            "pages" => pages,
            "title" or "section" => title,
            "subtitle" => document.Metadata.Subtitle ?? string.Empty,
            "author" => string.Join(", ", document.Metadata.Authors),
            "date" => DocumentDates.Short(document.Metadata.Date ?? DateTimeOffset.Now, document.Metadata.Language),
            "filename" => Path.GetFileName(document.Metadata.SourcePath ?? string.Empty),
            _ => m.Value,
        });
    }

    public static IReadOnlyList<(string Token, string Description)> Tokens { get; } =
    [
        ("{title}", "Document title"),
        ("{subtitle}", "Subtitle"),
        ("{author}", "Author"),
        ("{date}", "Date"),
        ("{page}", "Page number"),
        ("{pages}", "Number of pages"),
        ("{section}", "Current section heading"),
        ("{filename}", "Source file name"),
    ];

    private static void AppendStrip(StringBuilder html, string cls, HeaderFooterSettings hf, Document document, string title, int page, string pages)
    {
        var p = page.ToString(CultureInfo.InvariantCulture);
        html.Append("<div class=\"tb-hf ").Append(cls).Append("\">");
        foreach (var (part, value) in new[] { ("l", hf.Left), ("c", hf.Center), ("r", hf.Right) })
        {
            html.Append("<div class=\"tb-hf-").Append(part).Append("\">")
                .Append(Encode(ExpandTemplate(value, document, title, p, pages)))
                .Append("</div>");
        }

        html.Append("</div>");
    }

    private static void AppendCover(StringBuilder html, Document document, PdfTheme theme, string title)
    {
        var cover = theme.Cover;
        var coverTitle = cover.TitleOverride ?? title;
        var subtitle = cover.ShowSubtitle && !string.IsNullOrWhiteSpace(document.Metadata.Subtitle) ? document.Metadata.Subtitle : null;
        var meta = new List<string>();
        if (cover.ShowAuthor && document.Metadata.Authors.Count > 0)
        {
            meta.Add(string.Join(", ", document.Metadata.Authors));
        }

        if (cover.ShowDate)
        {
            meta.Add(DocumentDates.Long(document.Metadata.Date ?? DateTimeOffset.Now, document.Metadata.Language));
        }

        var style = cover.Style.ToString().ToLowerInvariant();
        html.Append("<div class=\"tb-cover tb-cover-").Append(style).Append("\">");

        if (cover.Style == CoverStyle.Banner)
        {
            html.Append("<div class=\"tb-cover-band\">");
            if (cover.ShowTitle)
            {
                html.Append("<div class=\"tb-cover-title\">").Append(Encode(coverTitle)).Append("</div>");
            }

            if (subtitle is not null)
            {
                html.Append("<div class=\"tb-cover-subtitle\">").Append(Encode(subtitle)).Append("</div>");
            }

            html.Append("</div><div class=\"tb-cover-after\">");
            if (meta.Count > 0)
            {
                html.Append("<div class=\"tb-cover-meta\">").Append(Encode(string.Join("  ·  ", meta))).Append("</div>");
            }

            if (!string.IsNullOrWhiteSpace(cover.Note))
            {
                html.Append("<div class=\"tb-cover-note\">").Append(Encode(cover.Note)).Append("</div>");
            }

            html.Append("</div></div>");
            return;
        }

        if (cover.Style == CoverStyle.Split)
        {
            html.Append("<div class=\"tb-cover-bar\"></div><div class=\"tb-cover-block\">");
        }

        if (cover.AccentBar)
        {
            html.Append("<div class=\"tb-cover-accent\"></div>");
        }

        if (cover.ShowTitle)
        {
            html.Append("<div class=\"tb-cover-title\">").Append(Encode(coverTitle)).Append("</div>");
        }

        if (subtitle is not null)
        {
            html.Append("<div class=\"tb-cover-subtitle\">").Append(Encode(subtitle)).Append("</div>");
        }

        if (meta.Count > 0)
        {
            html.Append("<div class=\"tb-cover-meta\">").Append(Encode(string.Join("  ·  ", meta))).Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(cover.Note))
        {
            html.Append("<div class=\"tb-cover-note\">").Append(Encode(cover.Note)).Append("</div>");
        }

        if (cover.Style == CoverStyle.Split)
        {
            html.Append("</div>");
        }

        html.Append("</div>");
    }

    private static string BuildToc(Document document, PdfTheme theme, List<HeadingBlock> entries, List<int> chunkStarts, int firstChunkPage)
    {
        // Numbering mirrors PdfRenderer.AssignSectionIds (counted over every heading, not only TOC entries).
        var numbers = new Dictionary<HeadingBlock, string>();
        if (theme.Headings.NumberHeadings)
        {
            var counters = new int[7];
            foreach (var h in document.Headings)
            {
                var level = Math.Clamp(h.Level, 1, 6);
                counters[level]++;
                for (var l = level + 1; l < counters.Length; l++)
                {
                    counters[l] = 0;
                }

                numbers[h] = string.Join('.', counters.Skip(1).Take(level).Select(c => c.ToString(CultureInfo.InvariantCulture)));
            }
        }

        // Estimated page: the sheet whose chunk contains the heading's top-level block.
        var blockOf = new Dictionary<HeadingBlock, int>();
        var lastTop = 0;
        var topIndex = document.Blocks.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
        foreach (var block in document.EnumerateAllBlocks())
        {
            if (topIndex.TryGetValue(block, out var idx))
            {
                lastTop = idx;
            }

            if (block is HeadingBlock h)
            {
                blockOf[h] = lastTop;
            }
        }

        var minLevel = entries.Min(h => h.Level);
        var sb = new StringBuilder();
        sb.Append("<nav class=\"tb-toc\" aria-label=\"Table of contents\"><div class=\"tb-toc-title\">")
            .Append(Encode(theme.Toc.Title)).Append("</div><ol class=\"tb-toc-list\">");
        foreach (var h in entries)
        {
            var chunk = 0;
            var bi = blockOf.GetValueOrDefault(h);
            for (var i = 0; i < chunkStarts.Count; i++)
            {
                if (chunkStarts[i] <= bi)
                {
                    chunk = i;
                }
            }

            var indent = theme.Toc.IndentLevels ? (h.Level - minLevel) * 14 : 0;
            var label = (numbers.TryGetValue(h, out var num) ? num + "  " : string.Empty) + h.ToPlainText();
            sb.Append("<li class=\"tb-toc-entry").Append(h.Level == minLevel ? " tb-toc-top" : string.Empty)
                .Append("\" style=\"padding-left:").Append(indent.ToString(CultureInfo.InvariantCulture)).Append("pt\">")
                .Append("<span class=\"tb-toc-label\">").Append(Encode(label)).Append("</span>");
            if (theme.Toc.ShowPageNumbers)
            {
                if (theme.Toc.DotLeaders)
                {
                    sb.Append("<span class=\"tb-toc-dots\" aria-hidden=\"true\"></span>");
                }

                sb.Append("<span class=\"tb-toc-page\">").Append((firstChunkPage + chunk).ToString(CultureInfo.InvariantCulture)).Append("</span>");
            }

            sb.Append("</li>");
        }

        sb.Append("</ol></nav>");
        return sb.ToString();
    }

    /// <summary>Splits top-level content at explicit page breaks and (optionally) before level-1 headings.</summary>
    private static (List<string> Chunks, List<int> Starts) SplitIntoChunks(string inner, bool breakBeforeH1)
    {
        var chunks = new List<string>();
        var starts = new List<int> { 0 };
        var last = 0;
        foreach (Match m in BreakPoint().Matches(inner))
        {
            var block = int.Parse(m.Groups["idx"].Value, CultureInfo.InvariantCulture);
            var isPageBreak = m.Groups["pb"].Success;
            if (!isPageBreak && (!breakBeforeH1 || block == 0))
            {
                continue;
            }

            var before = inner[last..m.Index];
            if (before.Trim().Length > 0)
            {
                chunks.Add(before);
                starts.Add(block);
            }

            last = isPageBreak ? m.Index + m.Length : m.Index;
        }

        chunks.Add(inner[last..]);
        if (starts.Count > chunks.Count)
        {
            starts.RemoveAt(starts.Count - 1);
        }

        return (chunks, starts);
    }

    private static string StripArticle(string fragment)
    {
        const string open = "<article class=\"aio-doc\">";
        const string close = "</article>";
        var s = fragment.StartsWith(open, StringComparison.Ordinal) ? fragment[open.Length..] : fragment;
        return s.EndsWith(close, StringComparison.Ordinal) ? s[..^close.Length] : s;
    }

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    [GeneratedRegex("<div class=\"aio-callout-title\">(?<kind>[A-Za-z]+)</div>")]
    private static partial Regex CalloutDefaultTitle();

    [GeneratedRegex("<p class=\"aio-p\"(?<rest>[^>]*)>")]
    private static partial Regex AttributionParagraph();

    [GeneratedRegex(@"^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$")]
    private static partial Regex LangTag();

    [GeneratedRegex(@"\{([a-zA-Z]+)\}")]
    private static partial Regex Token();

    [GeneratedRegex(@"<pre><code(?<attrs>[^>]*)>(?<body>.*?)</code></pre>", RegexOptions.Singleline)]
    private static partial Regex CodeBlock();

    [GeneratedRegex(@"(?<pb><div class=""aio-pagebreak"" data-block=""(?<idx>\d+)""></div>)|<h1 class=""aio-h aio-h1"" data-block=""(?<idx>\d+)"">")]
    private static partial Regex BreakPoint();
}
