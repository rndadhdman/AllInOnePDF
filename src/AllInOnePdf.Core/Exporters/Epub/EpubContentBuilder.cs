using System.Globalization;
using System.Text;
using System.Xml;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Xhtml;
using Document = AllInOnePdf.Core.Documents.Document;

namespace AllInOnePdf.Core.Exporters.Epub;

/// <summary>A heading the navigation document links to.</summary>
internal sealed record EpubHeading(int Level, string Id, string Label);

/// <summary>One spine item made from a run of top-level blocks.</summary>
internal sealed class EpubChapter
{
    public required int Number { get; init; }
    public required string FileName { get; init; }
    public string Href => "text/" + FileName;
    public string Id => "chapter-" + Number.ToString("D3", CultureInfo.InvariantCulture);
    public required string Title { get; set; }
    public required IElement Body { get; init; }
    public List<EpubHeading> Headings { get; } = [];
    public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
    public string Xhtml { get; set; } = string.Empty;
}

/// <summary>
/// Renders the whole document once with <see cref="DocumentHtmlRenderer"/> (so heading ids are unique across the
/// book), parses the HTML5 fragment with AngleSharp, splits its top-level blocks into chapters, fixes everything EPUB
/// needs (task boxes, footnotes as <c>epub:type</c> asides, cross-chapter links, image items) and re-serialises each
/// chapter as well-formed XHTML.
/// </summary>
internal sealed class EpubContentBuilder
{
    private static readonly HashSet<string> UnsafeElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "form", "input", "button", "select",
        "option", "textarea", "link", "meta", "base", "noscript", "template", "svg", "math", "audio", "video", "source", "canvas",
        "html", "head", "body", "title",
    };

    private readonly ISyntaxHighlighter? _highlighter;
    private readonly XhtmlMarkupFormatter _formatter = new(false);

    public EpubContentBuilder(ISyntaxHighlighter? highlighter)
    {
        _highlighter = highlighter;
    }

    public List<EpubChapter> Build(Document document, PdfTheme theme, EpubSettings settings, EpubImageStore images, List<string> warnings, CancellationToken cancellationToken)
    {
        var renderer = new DocumentHtmlRenderer(_highlighter);
        var html = renderer.RenderFragment(document, new HtmlRenderOptions
        {
            HeadingAnchors = true,
            BlockIndexes = true,
            ExternalLinksBlank = false,
            AllowRawHtml = false,
            ResolveImage = images.Resolve,
        });

        cancellationToken.ThrowIfCancellationRequested();
        var parser = new HtmlParser();
        var dom = parser.ParseDocument("<!DOCTYPE html><html><head><title></title></head><body>" + html + "</body></html>");
        var article = dom.QuerySelector("article.aio-doc") ?? dom.Body!;
        Clean(article);
        NormalizeIds(article);

        // Top-level elements carry the index of the block that produced them.
        var byIndex = new Dictionary<int, IElement>();
        IElement? footnoteSection = null;
        foreach (var child in article.Children.ToList())
        {
            if (child.LocalName == "section" && child.ClassList.Contains("aio-footnotes"))
            {
                footnoteSection = child;
                continue;
            }

            if (int.TryParse(child.GetAttribute("data-block"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                byIndex[index] = child;
            }
        }

        var footnotes = new Dictionary<string, IElement>(StringComparer.Ordinal);
        foreach (var li in footnoteSection?.QuerySelector("ol")?.Children ?? Enumerable.Empty<IElement>())
        {
            if (li.LocalName == "li" && !string.IsNullOrEmpty(li.Id))
            {
                footnotes.TryAdd(li.Id, li);
            }
        }

        var chapters = new List<EpubChapter>();
        var droppedRaw = 0;
        var brokenRaw = 0;
        foreach (var group in Group(document, settings.Split))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = dom.CreateElement("section");
            section.ClassName = "aio-doc";
            section.SetAttribute("epub:type", "chapter");
            foreach (var index in group)
            {
                if (document.Blocks[index] is HtmlBlock raw)
                {
                    if (!settings.AllowRawHtml)
                    {
                        droppedRaw++;
                    }
                    else if (!AppendRawHtml(dom, section, raw.Html, document, images))
                    {
                        brokenRaw++;
                    }

                    continue;
                }

                if (byIndex.TryGetValue(index, out var element))
                {
                    section.AppendChild(element);
                }
            }

            if (!HasContent(section) && chapters.Count > 0)
            {
                continue;
            }

            var number = chapters.Count + 1;
            chapters.Add(new EpubChapter
            {
                Number = number,
                FileName = $"chapter-{number.ToString("D3", CultureInfo.InvariantCulture)}.xhtml",
                Title = string.Empty,
                Body = section,
            });
        }

        // A run of empty leading groups can still leave an empty first chapter; drop it when real content follows.
        if (chapters.Count > 1 && !HasContent(chapters[0].Body))
        {
            chapters.RemoveAt(0);
            for (var i = 0; i < chapters.Count; i++)
            {
                var c = chapters[i];
                chapters[i] = new EpubChapter { Number = i + 1, FileName = $"chapter-{(i + 1).ToString("D3", CultureInfo.InvariantCulture)}.xhtml", Title = c.Title, Body = c.Body };
            }
        }

        if (droppedRaw > 0)
        {
            warnings.Add($"{droppedRaw} raw HTML block(s) were left out. Turn on \"Allow raw HTML\" to keep them.");
        }

        if (brokenRaw > 0)
        {
            warnings.Add($"{brokenRaw} raw HTML block(s) could not be turned into valid XHTML and were left out.");
        }

        var usedFootnotes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chapter in chapters)
        {
            FixElements(dom, chapter.Body, theme, settings);
            AttachFootnotes(dom, chapter.Body, footnotes, usedFootnotes);
        }

        var unreferenced = footnotes.Keys.Where(k => !usedFootnotes.Contains(k)).ToList();
        if (unreferenced.Count > 0)
        {
            AppendFootnotes(dom, chapters[^1].Body, unreferenced, footnotes, backLinks: null);
        }

        var fallbackTitle = document.ResolveTitle();
        foreach (var chapter in chapters)
        {
            CollectIds(chapter);
            chapter.Title = chapter.Headings.FirstOrDefault()?.Label is { Length: > 0 } label
                ? label
                : !string.IsNullOrWhiteSpace(fallbackTitle) ? fallbackTitle! : $"Chapter {chapter.Number.ToString(CultureInfo.InvariantCulture)}";
        }

        RewriteInternalLinks(chapters);
        if (theme.Headings.NumberHeadings)
        {
            NumberHeadings(chapters);
        }

        foreach (var chapter in chapters)
        {
            var body = chapter.Body.ToHtml(_formatter);
            chapter.Xhtml = EpubXml.XhtmlDocument(settings.Language, chapter.Title, "../css/style.css", body);
        }

        return chapters;
    }

    /// <summary>Splits top-level block indexes into chapters.</summary>
    private static List<List<int>> Group(Document document, EpubChapterSplit split)
    {
        var groups = new List<List<int>>();
        var current = new List<int>();
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            var block = document.Blocks[i];
            if (split == EpubChapterSplit.PageBreaks && block is PageBreakBlock)
            {
                if (current.Count > 0)
                {
                    groups.Add(current);
                    current = [];
                }

                continue;
            }

            var starts = split switch
            {
                EpubChapterSplit.H1 => block is HeadingBlock { Level: 1 },
                EpubChapterSplit.H2 => block is HeadingBlock { Level: <= 2 },
                _ => false,
            };
            if (starts && current.Count > 0)
            {
                groups.Add(current);
                current = [];
            }

            current.Add(i);
        }

        groups.Add(current);
        return groups;
    }

    private static bool HasContent(IElement section) =>
        section.Children.Length > 0 || !string.IsNullOrWhiteSpace(section.TextContent);

    /// <summary>Removes comments and characters XML forbids; drops attributes XHTML readers do not need.</summary>
    private static void Clean(INode root)
    {
        foreach (var node in root.ChildNodes.ToList())
        {
            switch (node)
            {
                case IComment:
                    node.Parent?.RemoveChild(node);
                    continue;
                case IText text:
                    var clean = EpubXml.StripInvalid(text.Data);
                    if (!string.Equals(clean, text.Data, StringComparison.Ordinal))
                    {
                        text.Data = clean;
                    }

                    continue;
                case IElement element:
                    foreach (var attr in element.Attributes.ToList())
                    {
                        var value = EpubXml.StripInvalid(attr.Value);
                        if (!string.Equals(value, attr.Value, StringComparison.Ordinal))
                        {
                            element.SetAttribute(attr.Name, value);
                        }
                    }

                    element.RemoveAttribute("loading");
                    element.RemoveAttribute("target");
                    Clean(element);
                    break;
            }
        }
    }

    /// <summary>XHTML ids cannot contain whitespace; rewrites them (and the links pointing at them).</summary>
    private static void NormalizeIds(IElement root)
    {
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in root.QuerySelectorAll("[id]"))
        {
            var id = element.Id ?? string.Empty;
            var fixedId = new string(id.Select(ch => char.IsWhiteSpace(ch) ? '-' : ch).ToArray());
            if (fixedId.Length == 0)
            {
                element.RemoveAttribute("id");
                continue;
            }

            var unique = fixedId;
            var n = 2;
            while (!seen.Add(unique))
            {
                unique = fixedId + "-" + n++.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.Equals(unique, id, StringComparison.Ordinal))
            {
                renamed.TryAdd(id, unique);
                element.Id = unique;
            }
        }

        if (renamed.Count == 0)
        {
            return;
        }

        foreach (var a in root.QuerySelectorAll("a[href^='#']"))
        {
            var target = Uri.UnescapeDataString(a.GetAttribute("href")![1..]);
            if (renamed.TryGetValue(target, out var newId))
            {
                a.SetAttribute("href", "#" + newId);
            }
        }
    }

    private void FixElements(IDocument dom, IElement section, PdfTheme theme, EpubSettings settings)
    {
        foreach (var element in section.QuerySelectorAll("[data-block]"))
        {
            element.RemoveAttribute("data-block");
        }

        if (!settings.AllowRawHtml)
        {
            foreach (var raw in section.QuerySelectorAll(".aio-rawhtml").ToList())
            {
                raw.Remove();
            }
        }

        // Task-list checkboxes: <input> is not allowed in reading systems' content, so use ballot-box characters.
        foreach (var input in section.QuerySelectorAll("input").ToList())
        {
            if (input.ParentElement is { LocalName: "li" } li && li.ClassList.Contains("aio-task"))
            {
                var box = dom.CreateElement("span");
                box.ClassName = "aio-task-box";
                var done = input.HasAttribute("checked");
                box.SetAttribute("role", "img");
                box.SetAttribute("aria-label", done ? "Done" : "Not done");
                box.TextContent = done ? "☑" : "☐";
                input.Replace(box);
            }
            else
            {
                input.Remove();
            }
        }

        if (theme.Code.ShowLanguageLabel)
        {
            foreach (var figure in section.QuerySelectorAll("figure.aio-codeblock[data-lang]"))
            {
                if (figure.QuerySelector("figcaption") is null)
                {
                    var caption = dom.CreateElement("figcaption");
                    caption.TextContent = figure.GetAttribute("data-lang") ?? string.Empty;
                    figure.Prepend(caption);
                }
            }
        }

        foreach (var callout in section.QuerySelectorAll("aside.aio-callout"))
        {
            var kindClass = callout.ClassList.FirstOrDefault(c => c.StartsWith("aio-callout-", StringComparison.Ordinal));
            if (kindClass is null || !Enum.TryParse<CalloutKind>(kindClass["aio-callout-".Length..], ignoreCase: true, out var kind))
            {
                continue;
            }

            var label = theme.Callouts.Kind(kind).Label;
            if (!string.IsNullOrWhiteSpace(label) && callout.QuerySelector(".aio-callout-title") is { } title
                && string.Equals(title.TextContent, kind.ToString(), StringComparison.Ordinal))
            {
                title.TextContent = label;
            }
        }

        // Links neutralised by the renderer ("#") or empty ones carry no target.
        foreach (var a in section.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href")!.Trim();
            if (href.Length == 0 || href == "#")
            {
                a.RemoveAttribute("href");
            }
        }
    }

    private static void AttachFootnotes(IDocument dom, IElement section, Dictionary<string, IElement> footnotes, HashSet<string> used)
    {
        var order = new List<string>();
        var backLinks = new Dictionary<string, (string RefId, string Label)>(StringComparer.Ordinal);
        foreach (var a in section.QuerySelectorAll("sup.aio-fnref > a[href]"))
        {
            var id = Uri.UnescapeDataString(a.GetAttribute("href")!.TrimStart('#'));
            if (!footnotes.ContainsKey(id))
            {
                continue;
            }

            a.SetAttribute("epub:type", "noteref");
            a.SetAttribute("role", "doc-noteref");
            if (!backLinks.ContainsKey(id))
            {
                var refId = "ref-" + id;
                a.Id = refId;
                backLinks[id] = (refId, a.TextContent);
                order.Add(id);
            }

            used.Add(id);
        }

        if (order.Count > 0)
        {
            AppendFootnotes(dom, section, order, footnotes, backLinks);
        }
    }

    private static void AppendFootnotes(IDocument dom, IElement section, List<string> ids, Dictionary<string, IElement> footnotes, Dictionary<string, (string RefId, string Label)>? backLinks)
    {
        var container = section.QuerySelector("section.aio-footnotes");
        if (container is null)
        {
            container = dom.CreateElement("section");
            container.ClassName = "aio-footnotes";
            container.SetAttribute("epub:type", "footnotes");
            section.AppendChild(container);
        }

        foreach (var id in ids)
        {
            var aside = dom.CreateElement("aside");
            aside.ClassName = "aio-footnote";
            aside.Id = id;
            aside.SetAttribute("epub:type", "footnote");
            aside.SetAttribute("role", "doc-footnote");
            foreach (var child in footnotes[id].ChildNodes)
            {
                aside.AppendChild(child.Clone(deep: true));
            }

            if (backLinks is not null && backLinks.TryGetValue(id, out var back))
            {
                var link = dom.CreateElement("a");
                link.ClassName = "aio-fn-back";
                link.SetAttribute("href", "#" + back.RefId);
                link.TextContent = back.Label;
                var host = aside.FirstElementChild is { LocalName: "p" } p ? p : null;
                if (host is null)
                {
                    host = dom.CreateElement("p");
                    host.ClassName = "aio-p";
                    aside.Prepend(host);
                }

                host.Prepend(link, dom.CreateTextNode(" "));
            }

            container.AppendChild(aside);
        }
    }

    private static void CollectIds(EpubChapter chapter)
    {
        foreach (var element in chapter.Body.QuerySelectorAll("[id]"))
        {
            chapter.Ids.Add(element.Id!);
            if (element.ClassList.Contains("aio-h") && element.LocalName.Length == 2 && element.LocalName[0] == 'h'
                && char.IsDigit(element.LocalName[1]))
            {
                chapter.Headings.Add(new EpubHeading(element.LocalName[1] - '0', element.Id!, Collapse(element.TextContent)));
            }
        }
    }

    /// <summary>Links to <c>#id</c> point into whichever chapter now holds the id; dangling ones lose their href.</summary>
    private static void RewriteInternalLinks(List<EpubChapter> chapters)
    {
        var owner = new Dictionary<string, EpubChapter>(StringComparer.Ordinal);
        foreach (var chapter in chapters)
        {
            foreach (var id in chapter.Ids)
            {
                owner.TryAdd(id, chapter);
            }
        }

        foreach (var chapter in chapters)
        {
            foreach (var a in chapter.Body.QuerySelectorAll("a[href^='#']"))
            {
                var id = Uri.UnescapeDataString(a.GetAttribute("href")![1..]);
                if (chapter.Ids.Contains(id))
                {
                    continue;
                }

                if (owner.TryGetValue(id, out var target))
                {
                    a.SetAttribute("href", target.FileName + "#" + id);
                }
                else
                {
                    a.RemoveAttribute("href");
                }
            }
        }
    }

    /// <summary>
    /// CSS counters restart in every file, so each chapter's wrapper resets them to the running totals; the nav labels
    /// get the same numbers.
    /// </summary>
    private static void NumberHeadings(List<EpubChapter> chapters)
    {
        var counters = new int[6];
        foreach (var chapter in chapters)
        {
            var reset = string.Join(' ', Enumerable.Range(0, 6).Select(i => $"aioh{(i + 1).ToString(CultureInfo.InvariantCulture)} {counters[i].ToString(CultureInfo.InvariantCulture)}"));
            chapter.Body.SetAttribute("style", "counter-reset:" + reset);
            for (var i = 0; i < chapter.Headings.Count; i++)
            {
                var h = chapter.Headings[i];
                counters[h.Level - 1]++;
                for (var deeper = h.Level; deeper < 6; deeper++)
                {
                    counters[deeper] = 0;
                }

                var number = string.Join('.', counters.Take(h.Level).Select(c => c.ToString(CultureInfo.InvariantCulture)));
                chapter.Headings[i] = h with { Label = number + "  " + h.Label };
            }
        }
    }

    private bool AppendRawHtml(IDocument dom, IElement target, string html, Document document, EpubImageStore images)
    {
        var holder = dom.CreateElement("div");
        try
        {
            holder.InnerHtml = html;
        }
        catch (Exception)
        {
            return false;
        }

        Sanitize(holder, document, images);
        Clean(holder);
        var xml = "<div xmlns=\"" + EpubXml.XhtmlNs + "\" xmlns:epub=\"" + EpubXml.OpsNs + "\">" + string.Concat(holder.ChildNodes.Select(n => n.ToHtml(_formatter))) + "</div>";
        if (!EpubXml.IsWellFormed(xml))
        {
            return false;
        }

        foreach (var node in holder.ChildNodes.ToList())
        {
            target.AppendChild(node);
        }

        return true;
    }

    private static void Sanitize(IElement root, Document document, EpubImageStore images)
    {
        foreach (var element in root.QuerySelectorAll("*").ToList())
        {
            if (UnsafeElements.Contains(element.LocalName) || element.NamespaceUri != AngleSharp.Dom.NamespaceNames.HtmlUri)
            {
                element.Remove();
                continue;
            }

            foreach (var attr in element.Attributes.ToList())
            {
                var name = attr.Name;
                var drop = name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || name.Contains(':', StringComparison.Ordinal) || !IsXmlName(name)
                           || name.Equals("xmlns", StringComparison.OrdinalIgnoreCase) || name.Equals("srcset", StringComparison.OrdinalIgnoreCase);
                if (!drop && name is "href" or "src")
                {
                    var value = attr.Value.Trim();
                    drop = value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase);
                }

                if (drop)
                {
                    element.RemoveAttribute(name);
                }
            }

            if (element.LocalName == "img")
            {
                var resolved = images.Resolve(document, element.GetAttribute("src") ?? string.Empty);
                if (resolved is null)
                {
                    element.Remove();
                }
                else
                {
                    element.SetAttribute("src", resolved);
                    if (!element.HasAttribute("alt"))
                    {
                        element.SetAttribute("alt", string.Empty);
                    }
                }
            }
        }
    }

    private static bool IsXmlName(string name)
    {
        try
        {
            XmlConvert.VerifyNCName(name);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>Collapses runs of whitespace to single spaces (labels for the nav and titles).</summary>
    internal static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var part in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(part);
        }

        return sb.ToString();
    }
}
