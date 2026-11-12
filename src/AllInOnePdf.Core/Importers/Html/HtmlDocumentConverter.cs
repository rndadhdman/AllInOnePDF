using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using AngleSharp.Dom;
using Document = AllInOnePdf.Core.Documents.Document;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace AllInOnePdf.Core.Importers.Html;

/// <summary>
/// Maps an HTML DOM (AngleSharp) onto the document model. Shared by the HTML importer (one page, images from disk)
/// and the EPUB importer (one instance across all chapters, images from the archive). Never fetches anything:
/// remote images keep their URL and are counted so the caller can warn once.
/// </summary>
internal sealed partial class HtmlDocumentConverter
{
    private const int MaxDepth = 200;

    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "body", "caption", "center", "details", "dialog", "dd", "dir", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "html",
        "legend", "li", "listing", "main", "menu", "nav", "ol", "p", "plaintext", "pre", "search", "section", "summary", "table",
        "tbody", "td", "tfoot", "th", "thead", "tr", "ul", "xmp", "frameset", "noframes",
    };

    private static readonly HashSet<string> AlwaysDropped = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "template", "head", "title", "meta", "link", "base", "colgroup", "col", "source", "track",
        "param", "area", "map", "datalist", "option", "optgroup", "slot", "frame",
    };

    /// <summary>Embedded or interactive content: kept as raw HTML with <c>keepRawHtml</c>, otherwise dropped.</summary>
    private static readonly HashSet<string> RawElements = new(StringComparer.Ordinal)
    {
        "iframe", "video", "audio", "canvas", "object", "embed", "svg", "button", "select", "textarea", "input", "progress", "meter", "portal",
    };

    private static readonly HashSet<string> KnownInline = new(StringComparer.Ordinal)
    {
        "a", "abbr", "b", "bdi", "bdo", "big", "br", "cite", "code", "data", "del", "dfn", "em", "font", "i", "img", "ins", "kbd",
        "label", "mark", "math", "nobr", "output", "picture", "q", "rp", "rt", "ruby", "s", "samp", "small", "span", "strike",
        "strong", "sub", "sup", "time", "tt", "u", "var", "wbr", "acronym", "image",
    };

    private readonly Document _doc;
    private readonly HtmlConversionSettings _settings;
    private readonly Dictionary<string, string> _imageCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _remoteImages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingImages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _oversizedImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _footnoteLabels = new(StringComparer.Ordinal);
    private HtmlImageResolver? _resolver;
    private Func<string, string>? _rewriteLink;
    private int _footnoteCounter;
    private int _depth;
    private int _rawDropped;

    public HtmlDocumentConverter(Document document, HtmlConversionSettings settings)
    {
        _doc = document;
        _settings = settings;
    }

    public int RemoteImageCount => _remoteImages.Count;

    /// <summary>A parser configured for untrusted input: no scripting, comments skipped. It never loads resources.</summary>
    public static HtmlParser CreateParser() => new(new HtmlParserOptions
    {
        IsScripting = false,
        SkipComments = true,
        IsNotSupportingFrames = true,
    });

    /// <summary>
    /// XHTML written for XML parsers uses self-closing non-void elements (<c>&lt;a id="x"/&gt;</c>) that the HTML parser
    /// would leave open; this expands them to start/end pairs first.
    /// </summary>
    public static string FixSelfClosingTags(string xhtml) => SelfClosing().Replace(xhtml, "<$1$2></$1>");

    /// <summary>Converts the document body (or its main content) into blocks. Footnotes go to <see cref="Document.Footnotes"/>.</summary>
    public List<Block> Convert(IDocument dom, HtmlImageResolver? resolver = null, Func<string, string>? rewriteLink = null)
    {
        _resolver = resolver;
        _rewriteLink = rewriteLink;
        var body = (IElement?)dom.Body ?? dom.DocumentElement;
        var blocks = new List<Block>();
        if (body is null)
        {
            return blocks;
        }

        var root = _settings.MainContentOnly ? FindMainContent(body) ?? body : body;
        ConvertChildren(root, blocks);
        return blocks;
    }

    /// <summary>Summary warnings for images that were not embedded (one line per kind, not per image).</summary>
    public void AddWarnings(List<string> warnings)
    {
        if (_remoteImages.Count > 0)
        {
            warnings.Add(_remoteImages.Count == 1
                ? "1 remote image was not downloaded (conversion works offline); it keeps its web address."
                : $"{_remoteImages.Count} remote images were not downloaded (conversion works offline); they keep their web addresses.");
        }

        if (_missingImages.Count > 0)
        {
            warnings.Add(_missingImages.Count == 1
                ? $"1 image could not be found ({Shorten(_missingImages.First())}); it is left as a reference."
                : $"{_missingImages.Count} images could not be found; they are left as references.");
        }

        if (_oversizedImages.Count > 0)
        {
            var mb = (_settings.MaxImageBytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture);
            warnings.Add($"{_oversizedImages.Count} image(s) exceed the {mb} MB limit and were left as references.");
        }

        if (_rawDropped > 0)
        {
            warnings.Add($"{_rawDropped} embedded element(s) (video, frames, forms, drawings) were left out. Turn on \"Keep raw HTML\" to keep them.");
        }
    }

    private static string Shorten(string value) => value.Length > 80 ? value[..77] + "..." : value;

    // ------------------------------------------------------------------ main content

    private IElement? FindMainContent(IElement body)
    {
        var main = body.QuerySelector("main") ?? body.QuerySelector("[role=main]");
        if (main is not null)
        {
            return main;
        }

        var articles = body.QuerySelectorAll("article").Where(a => a.ParentElement?.Closest("article") is null).ToList();
        if (articles.Count == 1)
        {
            return articles[0];
        }

        if (articles.Count > 1)
        {
            return null;
        }

        // Largest content block among the usual CMS containers.
        IElement? best = null;
        var bestLength = 0;
        foreach (var candidate in body.QuerySelectorAll("#content, #main, #main-content, .content, .main-content, .post-content, .entry-content, .article-body, .post, .entry"))
        {
            var length = candidate.TextContent.Length;
            if (length > bestLength)
            {
                best = candidate;
                bestLength = length;
            }
        }

        return best is not null && bestLength >= body.TextContent.Length / 3 ? best : null;
    }

    // ------------------------------------------------------------------ block level

    private void ConvertChildren(INode parent, List<Block> blocks)
    {
        if (++_depth > MaxDepth)
        {
            var flat = new List<Inline>();
            AppendText(flat, parent.TextContent);
            FlushParagraph(flat, blocks);
            _depth--;
            return;
        }

        var pending = new List<Inline>();
        foreach (var node in parent.ChildNodes.ToArray())
        {
            ConvertNode(node, blocks, pending);
        }

        FlushParagraph(pending, blocks);
        _depth--;
    }

    private void ConvertNode(INode node, List<Block> blocks, List<Inline> pending)
    {
        if (node is IText text)
        {
            AppendText(pending, text.Data);
            return;
        }

        if (node is not IElement el || IsDropped(el))
        {
            return;
        }

        if (RawElements.Contains(el.LocalName) && !IsTaskCheckbox(el))
        {
            if (_settings.KeepRawHtml)
            {
                FlushParagraph(pending, blocks);
                blocks.Add(new HtmlBlock { Html = el.OuterHtml });
            }
            else if (el.LocalName == "svg" && SvgImage(el) is { } svgImage)
            {
                pending.Add(svgImage);
            }
            else if (el.LocalName is not ("input" or "button" or "select" or "textarea"))
            {
                _rawDropped++;
            }

            return;
        }

        if (IsBlockLevel(el))
        {
            FlushParagraph(pending, blocks);
            ConvertBlockElement(el, blocks);
            return;
        }

        if (_settings.KeepRawHtml && !KnownInline.Contains(el.LocalName))
        {
            pending.Add(new HtmlInline { Html = el.OuterHtml });
            return;
        }

        ConvertInline(el, pending);
    }

    private static bool IsBlockLevel(IElement el)
    {
        var name = el.LocalName;
        if (BlockElements.Contains(name))
        {
            return true;
        }

        if (name == "math")
        {
            return string.Equals(el.GetAttribute("display"), "block", StringComparison.OrdinalIgnoreCase);
        }

        return name == "span" && (el.ClassList.Contains("katex-display") || (el.ClassList.Contains("math") && el.ClassList.Contains("display")));
    }

    private bool IsDropped(IElement el)
    {
        var name = el.LocalName;
        if (AlwaysDropped.Contains(name) || el.HasAttribute("hidden") || string.Equals(el.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (el.ClassList.Contains("aio-cover") || el.ClassList.Contains("aio-toc"))
        {
            return true;
        }

        var style = el.GetAttribute("style");
        if (style is not null && DisplayNone().IsMatch(style))
        {
            return true;
        }

        if (!_settings.MainContentOnly)
        {
            return false;
        }

        var role = el.GetAttribute("role");
        if (name == "nav" || string.Equals(role, "navigation", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "complementary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "banner", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "contentinfo", StringComparison.OrdinalIgnoreCase))
        {
            return !IsFootnoteContainer(el);
        }

        // Page-level header/footer only: an article's own header (with its title) is content.
        return name is "header" or "footer" && el.ParentElement?.Closest("article, section, main, blockquote, figure, aside, details") is null;
    }

    private void ConvertBlockElement(IElement el, List<Block> blocks)
    {
        var name = el.LocalName;
        if (IsFootnoteContainer(el))
        {
            ReadFootnotes(el);
            return;
        }

        if (IsSingleFootnote(el))
        {
            ReadSingleFootnote(el);
            return;
        }

        if (name is "div" or "aside" or "section" or "blockquote" or "p" && CalloutKindFromClasses(el) is { } kind)
        {
            blocks.Add(ConvertCallout(el, kind));
            return;
        }

        switch (name)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                {
                    var heading = new HeadingBlock { Level = name[1] - '0', Id = NullIfEmpty(el.Id) };
                    ConvertInlineChildren(el, heading.Inlines);
                    InlineWhitespace.Normalize(heading.Inlines);
                    if (InlineWhitespace.HasContent(heading.Inlines))
                    {
                        blocks.Add(heading);
                    }

                    return;
                }

            case "p":
                {
                    var inlines = new List<Inline>();
                    ConvertInlineChildren(el, inlines);
                    FlushParagraph(inlines, blocks, ParseAlignment(el));
                    return;
                }

            case "ul" or "ol" or "menu" or "dir":
                blocks.Add(ConvertList(el));
                return;

            case "pre" or "listing" or "xmp" or "plaintext":
                blocks.Add(ConvertPre(el, null, null));
                return;

            case "blockquote":
                blocks.Add(ConvertQuote(el));
                return;

            case "table":
                blocks.Add(ConvertTable(el, null));
                return;

            case "figure":
                ConvertFigure(el, blocks);
                return;

            case "hr":
                blocks.Add(new ThematicBreakBlock());
                return;

            case "dl":
                ConvertDefinitionList(el, blocks);
                return;

            case "details":
                blocks.Add(ConvertDetails(el));
                return;

            case "math":
                blocks.Add(new MathBlock { Latex = MathText(el) });
                return;

            case "span":
                blocks.Add(new MathBlock { Latex = MathText(el) });
                return;
        }

        if (IsPageBreak(el))
        {
            blocks.Add(new PageBreakBlock());
            return;
        }

        if (name is "div" or "section" or "article" or "aside" or "main" or "header" or "footer" or "nav" or "hgroup" or "body" or "html"
            || !_settings.KeepRawHtml)
        {
            // Transparent container (or an unsupported block element flattened to paragraphs).
            ConvertChildren(el, blocks);
            return;
        }

        if (name is "li" or "dt" or "dd" or "td" or "th" or "tr" or "thead" or "tbody" or "tfoot" or "caption" or "figcaption" or "summary" or "center" or "address")
        {
            ConvertChildren(el, blocks);
            return;
        }

        blocks.Add(new HtmlBlock { Html = el.OuterHtml });
    }

    private void FlushParagraph(List<Inline> pending, List<Block> blocks, TextAlignment? alignment = null)
    {
        if (pending.Count == 0)
        {
            return;
        }

        InlineWhitespace.Normalize(pending);
        if (!InlineWhitespace.HasContent(pending))
        {
            pending.Clear();
            return;
        }

        if (pending.Count == 1 && pending[0] is ImageInline img)
        {
            blocks.Add(ImageBlockFrom(img));
        }
        else if (pending.Count == 1 && pending[0] is HtmlInline html && _settings.KeepRawHtml)
        {
            blocks.Add(new HtmlBlock { Html = html.Html });
        }
        else
        {
            var p = new ParagraphBlock { Alignment = alignment };
            p.Inlines.AddRange(pending);
            blocks.Add(p);
        }

        pending.Clear();
    }

    private static TextAlignment? ParseAlignment(IElement el)
    {
        var value = el.GetAttribute("align");
        var style = el.GetAttribute("style");
        if (style is not null && TextAlign().Match(style) is { Success: true } m)
        {
            value = m.Groups[1].Value;
        }

        return value?.Trim().ToLowerInvariant() switch
        {
            "left" or "start" => TextAlignment.Left,
            "center" => TextAlignment.Center,
            "right" or "end" => TextAlignment.Right,
            "justify" => TextAlignment.Justify,
            _ => null,
        };
    }

    private static bool IsPageBreak(IElement el)
    {
        if (el.ClassList.Contains("aio-pagebreak") || el.ClassList.Contains("page-break") || el.ClassList.Contains("pagebreak"))
        {
            return el.TextContent.Trim().Length == 0;
        }

        var style = el.GetAttribute("style");
        return style is not null && PageBreakStyle().IsMatch(style) && el.TextContent.Trim().Length == 0;
    }

    // ------------------------------------------------------------------ lists

    private ListBlock ConvertList(IElement el)
    {
        var list = new ListBlock { Ordered = el.LocalName == "ol" };
        if (list.Ordered && int.TryParse(el.GetAttribute("start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start))
        {
            list.Start = start;
        }

        foreach (var node in el.ChildNodes.ToArray())
        {
            if (node is IElement child && !IsDropped(child))
            {
                if (child.LocalName == "li")
                {
                    list.Items.Add(ConvertListItem(child));
                }
                else if (child.LocalName is "ul" or "ol" && list.Items.Count > 0)
                {
                    // Malformed but common: a nested list directly inside the parent list.
                    list.Items[^1].Blocks.Add(ConvertList(child));
                }
                else
                {
                    var item = new ListItem();
                    var pending = new List<Inline>();
                    ConvertNode(child, item.Blocks, pending);
                    FlushParagraph(pending, item.Blocks);
                    if (item.Blocks.Count > 0)
                    {
                        list.Items.Add(item);
                    }
                }
            }
            else if (node is IText text && text.Data.Trim().Length > 0)
            {
                list.Items.Add(new ListItem(text.Data.Trim()));
            }
        }

        list.Tight = el.ClassList.Contains("aio-list")
            ? el.ClassList.Contains("aio-list-tight")
            : !el.Children.Where(c => c.LocalName == "li").Any(li => li.Children.Any(c => c.LocalName == "p"));
        return list;
    }

    private ListItem ConvertListItem(IElement li)
    {
        var item = new ListItem();
        var checkbox = FindTaskCheckbox(li);
        if (checkbox is not null)
        {
            item.Checked = checkbox.HasAttribute("checked");
            checkbox.Remove();
        }
        else if (li.ClassList.Contains("aio-task") || li.ClassList.Contains("task-list-item"))
        {
            item.Checked = li.ClassList.Contains("aio-task-done") || li.ClassList.Contains("checked") || li.ClassList.Contains("done");

            // The drawn box (our HTML and EPUB write <span class="aio-task-box">☑</span>) is the checked state
            // itself, not text; leaving it in would also push the item's paragraph onto a second line.
            foreach (var box in li.Children.Where(IsTaskBoxGlyph).ToList())
            {
                box.Remove();
            }
        }

        ConvertChildren(li, item.Blocks);
        return item;
    }

    private static IElement? FindTaskCheckbox(IElement li)
    {
        foreach (var child in li.Children)
        {
            if (IsTaskCheckbox(child))
            {
                return child;
            }

            if (child.LocalName is "p" or "label" or "span" or "div")
            {
                var nested = child.Children.FirstOrDefault(IsTaskCheckbox)
                    ?? child.Children.FirstOrDefault(c => c.LocalName == "label")?.Children.FirstOrDefault(IsTaskCheckbox);
                if (nested is not null)
                {
                    return nested;
                }
            }

            if (child.LocalName is "ul" or "ol" || child.TextContent.Trim().Length > 0)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>A box glyph standing for a task's state: <c>span.aio-task-box</c> or a span holding only ☐, ☑ or ☒.</summary>
    private static bool IsTaskBoxGlyph(IElement el) =>
        el.LocalName == "span" && (el.ClassList.Contains("aio-task-box") || el.TextContent.Trim() is "☐" or "☑" or "☒");

    private static bool IsTaskCheckbox(IElement el) =>
        el.LocalName == "input" && string.Equals(el.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ code

    private Block ConvertPre(IElement pre, string? caption, string? languageHint)
    {
        if (pre.ClassList.Contains("aio-math") || pre.ClassList.Contains("math"))
        {
            return new MathBlock { Latex = pre.TextContent.Trim('\n', '\r') };
        }

        var elementChildren = pre.Children.Where(c => !IsDropped(c)).ToList();
        var onlyCode = elementChildren.Count == 1 && elementChildren[0].LocalName is "code" or "samp" or "kbd"
            && pre.ChildNodes.OfType<IText>().All(t => t.Data.Trim().Length == 0)
            ? elementChildren[0]
            : null;

        var sb = new StringBuilder();
        AppendPreText(onlyCode ?? pre, sb);
        var code = sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');

        var language = LanguageFrom(onlyCode) ?? LanguageFrom(pre) ?? languageHint;
        return new CodeBlock { Code = code, Language = language, Caption = caption };
    }

    private void AppendPreText(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText t:
                    sb.Append(t.Data);
                    break;
                case IElement { LocalName: "br" }:
                    sb.Append('\n');
                    break;
                case IElement e when !IsDropped(e):
                    AppendPreText(e, sb);
                    break;
            }
        }
    }

    private static string? LanguageFrom(IElement? el)
    {
        if (el is null)
        {
            return null;
        }

        foreach (var cls in el.ClassList)
        {
            if (cls.StartsWith("language-", StringComparison.OrdinalIgnoreCase) && cls.Length > 9)
            {
                return cls[9..];
            }

            if (cls.StartsWith("lang-", StringComparison.OrdinalIgnoreCase) && cls.Length > 5)
            {
                return cls[5..];
            }

            if (cls.StartsWith("highlight-source-", StringComparison.OrdinalIgnoreCase) && cls.Length > 17)
            {
                return cls[17..];
            }
        }

        // Pandoc: class="sourceCode csharp"
        if (el.ClassList.Contains("sourceCode"))
        {
            var other = el.ClassList.FirstOrDefault(c => !string.Equals(c, "sourceCode", StringComparison.Ordinal) && c.Length > 0);
            if (other is not null)
            {
                return other;
            }
        }

        return NullIfEmpty(el.GetAttribute("data-lang") ?? el.GetAttribute("data-language"));
    }

    // ------------------------------------------------------------------ quotes, callouts, details, definitions

    private QuoteBlock ConvertQuote(IElement el)
    {
        var quote = new QuoteBlock();
        foreach (var child in el.Children.ToArray())
        {
            if (child.LocalName is "footer" || (child.LocalName == "cite" && child == el.Children[^1]) || child.ClassList.Contains("aio-attribution"))
            {
                var text = CollapseText(child.TextContent).Trim().TrimStart('—', '–', '-', ' ').Trim();
                if (text.Length > 0)
                {
                    quote.Attribution = text;
                }

                child.Remove();
            }
        }

        ConvertChildren(el, quote.Blocks);
        return quote;
    }

    private static CalloutKind? CalloutKindFromClasses(IElement el)
    {
        var isCallout = false;
        foreach (var cls in el.ClassList)
        {
            var c = cls.ToLowerInvariant();
            if (c is "note" or "tip" or "warning" or "caution" or "important" or "admonition" or "callout" or "alert"
                || c.StartsWith("markdown-alert", StringComparison.Ordinal) || c.StartsWith("aio-callout", StringComparison.Ordinal)
                || c.StartsWith("admonition-", StringComparison.Ordinal) || c.StartsWith("callout-", StringComparison.Ordinal) || c.StartsWith("alert-", StringComparison.Ordinal))
            {
                isCallout = true;
            }
        }

        if (!isCallout || el.ClassList.Contains("aio-callout-title"))
        {
            return null;
        }

        var all = string.Join(' ', el.ClassList).ToLowerInvariant();
        if (all.Contains("caution", StringComparison.Ordinal) || all.Contains("danger", StringComparison.Ordinal) || all.Contains("error", StringComparison.Ordinal))
        {
            return CalloutKind.Caution;
        }

        if (all.Contains("warning", StringComparison.Ordinal) || all.Contains("attention", StringComparison.Ordinal))
        {
            return CalloutKind.Warning;
        }

        if (all.Contains("important", StringComparison.Ordinal))
        {
            return CalloutKind.Important;
        }

        if (all.Contains("tip", StringComparison.Ordinal) || all.Contains("hint", StringComparison.Ordinal) || all.Contains("success", StringComparison.Ordinal))
        {
            return CalloutKind.Tip;
        }

        return CalloutKind.Note;
    }

    private CalloutBlock ConvertCallout(IElement el, CalloutKind kind)
    {
        var callout = new CalloutBlock { Kind = kind };
        var titleElement = el.Children.FirstOrDefault(c => c.ClassList.Any(cls => cls.Contains("title", StringComparison.OrdinalIgnoreCase) || cls.Contains("heading", StringComparison.OrdinalIgnoreCase)));
        if (titleElement is not null)
        {
            var title = CollapseText(titleElement.TextContent).Trim();
            callout.Title = title.Length == 0 || string.Equals(title, kind.ToString(), StringComparison.OrdinalIgnoreCase) ? null : title;
            titleElement.Remove();
        }

        if (el.LocalName == "p")
        {
            var inlines = new List<Inline>();
            ConvertInlineChildren(el, inlines);
            FlushParagraph(inlines, callout.Blocks);
        }
        else
        {
            ConvertChildren(el, callout.Blocks);
        }

        return callout;
    }

    private CalloutBlock ConvertDetails(IElement el)
    {
        var callout = new CalloutBlock { Kind = CalloutKindFromClasses(el) ?? CalloutKind.Note };
        var summary = el.Children.FirstOrDefault(c => c.LocalName == "summary");
        if (summary is not null)
        {
            var title = CollapseText(summary.TextContent).Trim();
            callout.Title = title.Length == 0 ? null : title;
            summary.Remove();
        }

        ConvertChildren(el, callout.Blocks);
        return callout;
    }

    private void ConvertDefinitionList(IElement el, List<Block> blocks)
    {
        // Same mapping as the Markdown importer: bold term paragraph, definition as an indented quote.
        foreach (var child in el.Children)
        {
            switch (child.LocalName)
            {
                case "dt":
                    {
                        var inner = new List<Inline>();
                        ConvertInlineChildren(child, inner);
                        InlineWhitespace.Normalize(inner);
                        if (InlineWhitespace.HasContent(inner))
                        {
                            var p = new ParagraphBlock();
                            p.Inlines.Add(new StyledInline(InlineStyle.Bold, [.. inner]));
                            blocks.Add(p);
                        }

                        break;
                    }

                case "dd":
                    {
                        var quote = new QuoteBlock();
                        ConvertChildren(child, quote.Blocks);
                        if (quote.Blocks.Count > 0)
                        {
                            blocks.Add(quote);
                        }

                        break;
                    }

                case "div":
                    ConvertDefinitionList(child, blocks);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ figures and tables

    private void ConvertFigure(IElement el, List<Block> blocks)
    {
        var figcaption = el.Children.FirstOrDefault(c => c.LocalName == "figcaption");
        var caption = figcaption is null ? null : NullIfEmpty(CollapseText(figcaption.TextContent).Trim());
        figcaption?.Remove();

        var content = el.Children.Where(c => !IsDropped(c)).ToList();
        var pre = content.FirstOrDefault(c => c.LocalName == "pre");
        if (pre is not null && content.Count == 1)
        {
            blocks.Add(ConvertPre(pre, caption, NullIfEmpty(el.GetAttribute("data-lang"))));
            return;
        }

        var table = content.FirstOrDefault(c => c.LocalName == "table");
        if (table is not null && content.Count == 1)
        {
            blocks.Add(ConvertTable(table, caption));
            return;
        }

        var inner = new List<Block>();
        ConvertChildren(el, inner);
        if (inner.Count == 1 && inner[0] is ImageBlock image)
        {
            if (caption is not null && !string.Equals(caption, image.AltText, StringComparison.Ordinal))
            {
                image.Title = caption;
            }

            image.AltText ??= caption;
            blocks.Add(image);
            return;
        }

        blocks.AddRange(inner);
        if (caption is not null)
        {
            blocks.Add(new ParagraphBlock(caption));
        }
    }

    private TableBlock ConvertTable(IElement el, string? figureCaption)
    {
        var table = new TableBlock { Caption = figureCaption };
        var hasHead = el.Children.Any(c => c.LocalName == "thead");
        var explicitAlign = new SortedDictionary<int, ColumnAlignment>();

        void AddRow(IElement tr, bool header)
        {
            var row = new TableRow { IsHeader = header };
            var col = 0;
            foreach (var cellEl in tr.Children.Where(c => c.LocalName is "td" or "th"))
            {
                var cell = new TableCell
                {
                    ColumnSpan = ParseSpan(cellEl.GetAttribute("colspan")),
                    RowSpan = ParseSpan(cellEl.GetAttribute("rowspan")),
                };
                ConvertChildren(cellEl, cell.Blocks);
                row.Cells.Add(cell);
                if (ParseAlignment(cellEl) is { } a && !explicitAlign.ContainsKey(col))
                {
                    explicitAlign[col] = a switch
                    {
                        TextAlignment.Center => ColumnAlignment.Center,
                        TextAlignment.Right => ColumnAlignment.Right,
                        _ => ColumnAlignment.Left,
                    };
                }

                col += cell.ColumnSpan;
            }

            if (row.Cells.Count > 0)
            {
                table.Rows.Add(row);
            }
        }

        foreach (var child in el.Children)
        {
            switch (child.LocalName)
            {
                case "caption":
                    table.Caption ??= NullIfEmpty(CollapseText(child.TextContent).Trim());
                    break;
                case "thead":
                    foreach (var tr in child.Children.Where(c => c.LocalName == "tr"))
                    {
                        AddRow(tr, true);
                    }

                    break;
                case "tbody" or "tfoot":
                    foreach (var tr in child.Children.Where(c => c.LocalName == "tr"))
                    {
                        AddRow(tr, !hasHead && table.Rows.Count == 0 && IsAllHeaderCells(tr));
                    }

                    break;
                case "tr":
                    AddRow(child, !hasHead && table.Rows.Count == 0 && IsAllHeaderCells(child));
                    break;
            }
        }

        if (explicitAlign.Count > 0)
        {
            var last = explicitAlign.Keys.Max();
            for (var i = 0; i <= last; i++)
            {
                table.Alignments.Add(explicitAlign.GetValueOrDefault(i, ColumnAlignment.Default));
            }
        }

        return table;
    }

    private static bool IsAllHeaderCells(IElement tr)
    {
        var cells = tr.Children.Where(c => c.LocalName is "td" or "th").ToList();
        return cells.Count > 0 && cells.All(c => c.LocalName == "th");
    }

    private static int ParseSpan(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, 1000) : 1;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string CollapseText(string text) => Whitespace().Replace(text, " ");

    [GeneratedRegex("[ \t\n\r\f]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"text-align\s*:\s*(left|center|right|justify|start|end)", RegexOptions.IgnoreCase)]
    private static partial Regex TextAlign();

    [GeneratedRegex(@"display\s*:\s*none", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayNone();

    [GeneratedRegex(@"(page-break-(after|before)\s*:\s*always)|(break-(after|before)\s*:\s*page)", RegexOptions.IgnoreCase)]
    private static partial Regex PageBreakStyle();

    [GeneratedRegex(@"<(?!(?:area|base|br|col|embed|hr|img|input|link|meta|param|source|track|wbr)\b)([a-zA-Z][\w:.\-]*)(\s[^<>]*?)?\s*/>")]
    private static partial Regex SelfClosing();
}
