using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Rendering;

/// <summary>
/// Semantic HTML output used by the on-screen Reader and the theme builder's fast preview.
/// Emits plain elements with <c>aio-*</c> classes so the host page controls every visual via CSS.
/// All text is HTML-encoded; raw HTML from sources is dropped unless explicitly allowed.
/// </summary>
public sealed partial class DocumentHtmlRenderer : IDocumentHtmlRenderer
{
    private readonly ISyntaxHighlighter? _highlighter;

    public DocumentHtmlRenderer()
        : this(null)
    {
    }

    /// <summary>
    /// With a highlighter, code blocks contain <c>&lt;span class="hl hl-kw"&gt;</c> runs (kw, type, str, num, cmt, attr,
    /// punct, fn, var, op, ins, del); plain text stays unwrapped. The host stylesheet colours them.
    /// </summary>
    public DocumentHtmlRenderer(ISyntaxHighlighter? highlighter)
    {
        _highlighter = highlighter;
    }

    public string RenderFragment(Document document, HtmlRenderOptions? options = null)
    {
        options ??= new HtmlRenderOptions();
        var sb = new StringBuilder(4096);
        var ctx = new Context(document, options, sb, _highlighter);
        sb.Append("<article class=\"aio-doc\">");
        var index = 0;
        foreach (var block in document.Blocks)
        {
            ctx.RenderBlock(block, index++);
        }

        if (document.Footnotes.Count > 0)
        {
            sb.Append("<section class=\"aio-footnotes\"><ol>");
            foreach (var fn in document.Footnotes)
            {
                sb.Append("<li id=\"fn-").Append(Encode(fn.Label)).Append("\">");
                foreach (var b in fn.Blocks)
                {
                    ctx.RenderBlock(b, -1);
                }

                sb.Append("</li>");
            }

            sb.Append("</ol></section>");
        }

        sb.Append("</article>");
        return sb.ToString();
    }

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static string Attr(string? text) => WebUtility.HtmlEncode(text ?? string.Empty).Replace("\"", "&quot;", StringComparison.Ordinal);

    private sealed class Context
    {
        private readonly Document _doc;
        private readonly HtmlRenderOptions _opt;
        private readonly StringBuilder _sb;
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly ISyntaxHighlighter? _highlighter;

        public Context(Document doc, HtmlRenderOptions opt, StringBuilder sb, ISyntaxHighlighter? highlighter)
        {
            _doc = doc;
            _opt = opt;
            _sb = sb;
            _highlighter = highlighter;
        }

        private void AppendCode(CodeBlock code)
        {
            IReadOnlyList<HighlightedRun>? runs = null;
            if (_highlighter is not null && !string.IsNullOrWhiteSpace(code.Language))
            {
                try
                {
                    runs = _highlighter.Highlight(code.Code, code.Language, Themes.SyntaxPalette.Light, darkBackground: false);
                }
                catch (Exception)
                {
                    runs = null;
                }
            }

            if (runs is null)
            {
                _sb.Append(Encode(code.Code));
                return;
            }

            foreach (var run in runs)
            {
                var cls = SyntaxTokenKinds.CssClass(run.Kind);
                if (cls is null)
                {
                    _sb.Append(Encode(run.Text));
                }
                else
                {
                    _sb.Append("<span class=\"hl hl-").Append(cls).Append("\">").Append(Encode(run.Text)).Append("</span>");
                }
            }
        }

        public void RenderBlock(Block block, int index)
        {
            var data = _opt.BlockIndexes && index >= 0 ? $" data-block=\"{index.ToString(CultureInfo.InvariantCulture)}\"" : string.Empty;
            switch (block)
            {
                case HeadingBlock h:
                    {
                        var id = _opt.HeadingAnchors ? $" id=\"{Attr(ResolveHeadingId(h))}\"" : string.Empty;
                        var align = h.Alignment is { } ha && ha != TextAlignment.Justify ? $" style=\"text-align:{ha.ToString().ToLowerInvariant()}\"" : string.Empty;
                        _sb.Append($"<h{h.Level} class=\"aio-h aio-h{h.Level}\"{id}{align}{data}>");
                        RenderInlines(h.Inlines);
                        _sb.Append($"</h{h.Level}>");
                        break;
                    }

                case ParagraphBlock p:
                    {
                        var align = p.Alignment is { } a ? $" style=\"text-align:{a.ToString().ToLowerInvariant()}\"" : string.Empty;
                        _sb.Append($"<p class=\"aio-p\"{align}{data}>");
                        RenderInlines(p.Inlines);
                        _sb.Append("</p>");
                        break;
                    }

                case ListBlock list:
                    {
                        var tag = list.Ordered ? "ol" : "ul";
                        var start = list.Ordered && list.Start != 1 ? $" start=\"{list.Start.ToString(CultureInfo.InvariantCulture)}\"" : string.Empty;
                        var cls = list.Tight ? "aio-list aio-list-tight" : "aio-list";
                        if (list.Items.Any(i => i.Checked is not null))
                        {
                            cls += " aio-tasklist";
                        }

                        _sb.Append($"<{tag} class=\"{cls}\"{start}{data}>");
                        foreach (var item in list.Items)
                        {
                            var itemCls = item.Checked switch { true => " class=\"aio-task aio-task-done\"", false => " class=\"aio-task\"", _ => string.Empty };
                            _sb.Append($"<li{itemCls}>");
                            if (item.Checked is { } checkedState)
                            {
                                _sb.Append("<input type=\"checkbox\" disabled").Append(checkedState ? " checked" : string.Empty).Append("> ");
                            }

                            RenderNested(item.Blocks);
                            _sb.Append("</li>");
                        }

                        _sb.Append($"</{tag}>");
                        break;
                    }

                case CodeBlock code:
                    {
                        var lang = string.IsNullOrWhiteSpace(code.Language) ? string.Empty : $" data-lang=\"{Attr(code.Language)}\"";
                        _sb.Append($"<figure class=\"aio-codeblock\"{lang}{data}>");
                        if (!string.IsNullOrWhiteSpace(code.Caption))
                        {
                            _sb.Append("<figcaption>").Append(Encode(code.Caption)).Append("</figcaption>");
                        }

                        _sb.Append("<pre><code");
                        if (!string.IsNullOrWhiteSpace(code.Language))
                        {
                            _sb.Append(" class=\"language-").Append(Attr(code.Language)).Append('"');
                        }

                        _sb.Append('>');
                        AppendCode(code);
                        _sb.Append("</code></pre></figure>");
                        break;
                    }

                case QuoteBlock quote:
                    _sb.Append($"<blockquote class=\"aio-quote\"{data}>");
                    RenderNested(quote.Blocks);
                    if (!string.IsNullOrWhiteSpace(quote.Attribution))
                    {
                        _sb.Append("<footer class=\"aio-attribution\">").Append(Encode(quote.Attribution)).Append("</footer>");
                    }

                    _sb.Append("</blockquote>");
                    break;

                case CalloutBlock callout:
                    {
                        var kind = callout.Kind.ToString().ToLowerInvariant();
                        _sb.Append($"<aside class=\"aio-callout aio-callout-{kind}\"{data}>");
                        _sb.Append("<div class=\"aio-callout-title\">").Append(Encode(callout.Title ?? callout.Kind.ToString())).Append("</div>");
                        RenderNested(callout.Blocks);
                        _sb.Append("</aside>");
                        break;
                    }

                case TableBlock table:
                    {
                        _sb.Append($"<figure class=\"aio-tablewrap\"{data}><table class=\"aio-table\">");
                        if (!string.IsNullOrWhiteSpace(table.Caption))
                        {
                            _sb.Append("<caption>").Append(Encode(table.Caption)).Append("</caption>");
                        }

                        var headerRows = table.Rows.Where(r => r.IsHeader).ToList();
                        var bodyRows = table.Rows.Where(r => !r.IsHeader).ToList();
                        if (headerRows.Count > 0)
                        {
                            _sb.Append("<thead>");
                            foreach (var row in headerRows)
                            {
                                RenderRow(table, row, "th");
                            }

                            _sb.Append("</thead>");
                        }

                        _sb.Append("<tbody>");
                        foreach (var row in bodyRows)
                        {
                            RenderRow(table, row, "td");
                        }

                        _sb.Append("</tbody></table></figure>");
                        break;
                    }

                case ImageBlock img:
                    {
                        var src = ResolveImage(img.Source);
                        _sb.Append($"<figure class=\"aio-figure\"{data}>");
                        if (src is not null)
                        {
                            var style = img.WidthPercent is { } w ? $" style=\"width:{w.ToString(CultureInfo.InvariantCulture)}%\"" : string.Empty;
                            _sb.Append($"<img src=\"{Attr(src)}\" alt=\"{Attr(img.AltText)}\" loading=\"lazy\"{style}>");
                        }
                        else
                        {
                            _sb.Append("<div class=\"aio-image-missing\">").Append(Encode(img.AltText ?? img.Source)).Append("</div>");
                        }

                        var caption = img.ResolveCaption();
                        if (!string.IsNullOrWhiteSpace(caption))
                        {
                            _sb.Append("<figcaption>").Append(Encode(caption)).Append("</figcaption>");
                        }

                        _sb.Append("</figure>");
                        break;
                    }

                case ThematicBreakBlock:
                    _sb.Append($"<hr class=\"aio-hr\"{data}>");
                    break;

                case PageBreakBlock:
                    _sb.Append($"<div class=\"aio-pagebreak\"{data}></div>");
                    break;

                case HtmlBlock html:
                    if (_opt.AllowRawHtml)
                    {
                        _sb.Append(html.Html);
                    }
                    else
                    {
                        var text = StripTags().Replace(html.Html, string.Empty).Trim();
                        if (text.Length > 0)
                        {
                            _sb.Append($"<p class=\"aio-p aio-rawhtml\"{data}>").Append(Encode(text)).Append("</p>");
                        }
                    }

                    break;

                case MathBlock math:
                    _sb.Append($"<pre class=\"aio-math\"{data}><code>").Append(Encode(math.Latex)).Append("</code></pre>");
                    break;
            }
        }

        private void RenderRow(TableBlock table, TableRow row, string cellTag)
        {
            _sb.Append("<tr>");
            var col = 0;
            foreach (var cell in row.Cells)
            {
                var align = table.GetAlignment(col) switch
                {
                    ColumnAlignment.Left => " style=\"text-align:left\"",
                    ColumnAlignment.Center => " style=\"text-align:center\"",
                    ColumnAlignment.Right => " style=\"text-align:right\"",
                    _ => string.Empty,
                };
                var span = (cell.ColumnSpan > 1 ? $" colspan=\"{cell.ColumnSpan}\"" : string.Empty)
                           + (cell.RowSpan > 1 ? $" rowspan=\"{cell.RowSpan}\"" : string.Empty);
                _sb.Append($"<{cellTag}{align}{span}>");
                if (cell.Blocks.Count == 1 && cell.Blocks[0] is ParagraphBlock p)
                {
                    RenderInlines(p.Inlines);
                }
                else
                {
                    RenderNested(cell.Blocks);
                }

                _sb.Append($"</{cellTag}>");
                col += cell.ColumnSpan;
            }

            _sb.Append("</tr>");
        }

        private void RenderNested(List<Block> blocks)
        {
            foreach (var b in blocks)
            {
                RenderBlock(b, -1);
            }
        }

        private void RenderInlines(IEnumerable<Inline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case TextInline t:
                        _sb.Append(Encode(t.Text));
                        break;
                    case StyledInline s:
                        {
                            var open = new StringBuilder();
                            var close = new StringBuilder();
                            void Wrap(string tag, string? cls = null)
                            {
                                open.Append('<').Append(tag);
                                if (cls is not null)
                                {
                                    open.Append(" class=\"").Append(cls).Append('"');
                                }

                                open.Append('>');
                                close.Insert(0, $"</{tag}>");
                            }

                            if (s.Style.HasFlag(InlineStyle.Bold)) Wrap("strong");
                            if (s.Style.HasFlag(InlineStyle.Italic)) Wrap("em");
                            if (s.Style.HasFlag(InlineStyle.Strikethrough)) Wrap("del");
                            if (s.Style.HasFlag(InlineStyle.Underline)) Wrap("u");
                            if (s.Style.HasFlag(InlineStyle.Highlight)) Wrap("mark");
                            if (s.Style.HasFlag(InlineStyle.Superscript)) Wrap("sup");
                            if (s.Style.HasFlag(InlineStyle.Subscript)) Wrap("sub");
                            if (s.Style.HasFlag(InlineStyle.SmallCaps)) Wrap("span", "aio-smallcaps");
                            _sb.Append(open);
                            RenderInlines(s.Inlines);
                            _sb.Append(close);
                            break;
                        }

                    case CodeInline c:
                        _sb.Append("<code class=\"aio-code\">").Append(Encode(c.Code)).Append("</code>");
                        break;
                    case LinkInline link:
                        {
                            var href = SafeHref(link.Url);
                            var target = !link.IsInternal && _opt.ExternalLinksBlank ? " target=\"_blank\" rel=\"noopener noreferrer\"" : string.Empty;
                            var title = link.Title is null ? string.Empty : $" title=\"{Attr(link.Title)}\"";
                            _sb.Append($"<a class=\"aio-link\" href=\"{Attr(href)}\"{title}{target}>");
                            RenderInlines(link.Inlines);
                            _sb.Append("</a>");
                            break;
                        }

                    case ImageInline img:
                        {
                            var src = ResolveImage(img.Source);
                            if (src is not null)
                            {
                                _sb.Append($"<img class=\"aio-inline-img\" src=\"{Attr(src)}\" alt=\"{Attr(img.AltText)}\" loading=\"lazy\">");
                            }
                            else
                            {
                                _sb.Append(Encode(img.AltText));
                            }

                            break;
                        }

                    case LineBreakInline br:
                        _sb.Append(br.IsHard ? "<br>" : " ");
                        break;
                    case FootnoteReferenceInline fr:
                        _sb.Append($"<sup class=\"aio-fnref\"><a href=\"#fn-{Attr(fr.Label)}\">").Append(Encode(fr.Label)).Append("</a></sup>");
                        break;
                    case HtmlInline html:
                        if (_opt.AllowRawHtml)
                        {
                            _sb.Append(html.Html);
                        }

                        break;
                    case MathInline math:
                        _sb.Append("<code class=\"aio-math-inline\">").Append(Encode(math.Latex)).Append("</code>");
                        break;
                }
            }
        }

        private string? ResolveImage(string source)
        {
            if (_opt.ResolveImage is not null)
            {
                return _opt.ResolveImage(_doc, source);
            }

            var res = _doc.FindResource(source);
            if (res is not null)
            {
                return $"data:{res.MediaType};base64,{Convert.ToBase64String(res.Data)}";
            }

            if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }

            // Offline by design: remote images are never fetched, and local paths are not reachable from the web view.
            // Hosts that can serve local files supply ResolveImage.
            return null;
        }

        private static string SafeHref(string url)
        {
            var trimmed = url.Trim();
            if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase) || (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)))
            {
                return "#";
            }

            return trimmed;
        }

        private string ResolveHeadingId(HeadingBlock h)
        {
            var id = h.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                id = Slugify(h.ToPlainText());
                h.Id = id;
            }

            var unique = id;
            var n = 2;
            while (!_ids.Add(unique))
            {
                unique = $"{id}-{n++}";
            }

            if (unique != id)
            {
                h.Id = unique;
            }

            return unique;
        }
    }

    /// <summary>GitHub-style slug: lower-case, spaces to dashes, punctuation removed.</summary>
    public static string Slugify(string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch is ' ' or '-' or '_')
            {
                sb.Append('-');
            }
        }

        var slug = MultiDash().Replace(sb.ToString(), "-").Trim('-');
        return slug.Length == 0 ? "section" : slug;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex StripTags();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultiDash();
}
