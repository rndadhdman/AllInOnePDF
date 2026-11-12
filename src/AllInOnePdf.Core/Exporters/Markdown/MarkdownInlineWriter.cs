using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using static AllInOnePdf.Core.Exporters.Markdown.MarkdownEscaper;

namespace AllInOnePdf.Core.Exporters.Markdown;

/// <summary>Serialises runs of <see cref="Inline"/>s. Spaces that may become line breaks are emitted as <see cref="BreakableSpace"/>.</summary>
internal sealed partial class MarkdownInlineWriter
{
    private readonly MarkdownExportSettings _settings;
    private readonly MarkdownImageResolver _images;
    private readonly List<(string Url, string? Title)> _references = [];

    public MarkdownInlineWriter(MarkdownExportSettings settings, MarkdownImageResolver images)
    {
        _settings = settings;
        _images = images;
    }

    /// <summary>Link reference definitions collected in reference-link mode, in label order (label = index + 1).</summary>
    public IReadOnlyList<(string Url, string? Title)> References => _references;

    public string Render(IReadOnlyList<Inline> inlines, TextContext context)
    {
        var count = inlines.Count;
        while (count > 0 && inlines[count - 1] is LineBreakInline)
        {
            count--;
        }

        var sb = new StringBuilder();
        RenderInto(sb, count == inlines.Count ? inlines : inlines.Take(count).ToList(), context, null, null);
        return sb.ToString().Trim(' ', '\t', '\n', BreakableSpace);
    }

    /// <summary>Markdown for an image (block or inline), or its escaped alt text when images are omitted or missing.</summary>
    public string RenderImage(string source, string? alt, string? title, TextContext context, char? prev = null, char? next = null)
    {
        var destination = _images.Resolve(source);
        if (destination is null)
        {
            return EscapeText(alt ?? string.Empty, prev, next, context);
        }

        return "![" + EscapeBracketText(alt ?? string.Empty, (context & TextContext.Table) != 0) + "](" +
               FormatDestination(destination) + FormatTitle(title) + ")";
    }

    public static string FootnoteLabel(string label)
    {
        var cleaned = FootnoteLabelInvalid().Replace(label.Trim(), "-");
        return cleaned.Length == 0 ? "note" : cleaned;
    }

    private void RenderInto(StringBuilder sb, IReadOnlyList<Inline> inlines, TextContext context, char? outerPrev, char? outerNext)
    {
        var start = sb.Length;
        for (var i = 0; i < inlines.Count; i++)
        {
            var prev = sb.Length > 0 ? sb[^1] : outerPrev;
            var next = FirstChar(inlines, i + 1) ?? outerNext;
            RenderInline(sb, inlines[i], context, prev, next, afterSibling: sb.Length > start);
        }
    }

    private void RenderInline(StringBuilder sb, Inline inline, TextContext context, char? prev, char? next, bool afterSibling)
    {
        switch (inline)
        {
            case TextInline text:
                sb.Append(EscapeText(text.Text, prev, next, context));
                break;

            case CodeInline code when code.Code.Length > 0:
                sb.Append(CodeSpan(code.Code));
                break;

            case CodeInline:
                break;

            case StyledInline styled:
                RenderStyled(sb, styled, context, prev, next, afterSibling);
                break;

            case LinkInline link:
                RenderLink(sb, link, context);
                break;

            case ImageInline image:
                sb.Append(RenderImage(image.Source, image.AltText, image.Title, context, prev, next));
                break;

            case LineBreakInline br:
                RenderBreak(sb, br, context);
                break;

            case FootnoteReferenceInline fn:
                sb.Append(_settings.Footnotes ? "[^" + FootnoteLabel(fn.Label) + "]" : "^" + EscapeText(fn.Label, '^', '^', TextContext.SingleLine) + "^");
                break;

            case MathInline math:
                {
                    var latex = math.Latex.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ').Trim();
                    if (latex.Length == 0)
                    {
                        break;
                    }

                    sb.Append(_settings.Math == MathStyle.Dollar && !latex.Contains('$', StringComparison.Ordinal) ? "$" + latex + "$" : CodeSpan(latex));
                    break;
                }

            case HtmlInline html:
                {
                    var raw = html.Html;
                    if ((context & (TextContext.Table | TextContext.SingleLine)) != 0)
                    {
                        raw = raw.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ');
                    }

                    if ((context & TextContext.Table) != 0)
                    {
                        raw = raw.Replace("|", @"\|", StringComparison.Ordinal);
                    }

                    sb.Append(raw);
                    break;
                }

            default:
                RenderInto(sb, inline.Children.ToList(), context, prev, next);
                break;
        }
    }

    private void RenderBreak(StringBuilder sb, LineBreakInline br, TextContext context)
    {
        if ((context & TextContext.Table) != 0)
        {
            TrimEndSpaces(sb);
            sb.Append(br.IsHard ? "<br>" : " ");
            return;
        }

        if ((context & TextContext.SingleLine) != 0)
        {
            TrimEndSpaces(sb);
            sb.Append(' ');
            return;
        }

        if (!br.IsHard && (context & TextContext.Breakable) != 0 && _settings.WrapWidth > 0)
        {
            TrimEndSpaces(sb);
            sb.Append(BreakableSpace);
            return;
        }

        TrimEndSpaces(sb);
        if (br.IsHard)
        {
            sb.Append(_settings.HardBreak == HardBreakStyle.Backslash ? "\\\n" : "  \n");
        }
        else
        {
            sb.Append('\n');
        }
    }

    private void RenderStyled(StringBuilder sb, StyledInline styled, TextContext context, char? prev, char? next, bool afterSibling)
    {
        // Collapse single-child nesting (Italic(Bold(x)) == Bold|Italic) so equivalent trees serialise identically.
        var style = styled.Style;
        IReadOnlyList<Inline> children = styled.Inlines;
        while (children is [StyledInline only])
        {
            style |= only.Style;
            children = only.Inlines;
        }

        style &= ~InlineStyle.SmallCaps;
        if (style == InlineStyle.None)
        {
            RenderInto(sb, children, context, prev, next);
            return;
        }

        var em = _settings.EmphasisMarker;
        if (em == '_' && (IsWord(prev) || IsWord(next)))
        {
            em = '*';
        }

        if (afterSibling && prev == em)
        {
            // "*a**b*" is ambiguous; switch marker when the preceding sibling already ends with ours.
            em = em == '*' && !IsWord(next) ? '_' : '*';
        }

        var open = new StringBuilder();
        var closeParts = new List<string>();
        void Add(string o, string c)
        {
            open.Append(o);
            closeParts.Insert(0, c);
        }

        if (style.HasFlag(InlineStyle.Bold))
        {
            Add(new string(em, 2), new string(em, 2));
        }

        if (style.HasFlag(InlineStyle.Italic))
        {
            Add(em.ToString(), em.ToString());
        }

        if (style.HasFlag(InlineStyle.Strikethrough))
        {
            Add("~~", "~~");
        }

        if (style.HasFlag(InlineStyle.Highlight))
        {
            Add("==", "==");
        }

        if (style.HasFlag(InlineStyle.Underline))
        {
            Add("<u>", "</u>");
        }

        if (style.HasFlag(InlineStyle.Superscript))
        {
            Add("^", "^");
        }

        if (style.HasFlag(InlineStyle.Subscript))
        {
            // "~~~x~~~" would read as a code fence or be ambiguous; fall back to HTML next to strikethrough.
            if (style.HasFlag(InlineStyle.Strikethrough))
            {
                Add("<sub>", "</sub>");
            }
            else
            {
                Add("~", "~");
            }
        }

        var openText = open.ToString();
        var closeText = string.Concat(closeParts);
        var inner = new StringBuilder();
        RenderInto(inner, children, context, openText[^1], closeText[0]);
        var text = inner.ToString();

        var lead = 0;
        while (lead < text.Length && IsSpace(text[lead]))
        {
            lead++;
        }

        var trail = 0;
        while (trail < text.Length - lead && IsSpace(text[text.Length - 1 - trail]))
        {
            trail++;
        }

        if (lead + trail >= text.Length)
        {
            sb.Append(text);
            return;
        }

        sb.Append(text, 0, lead)
          .Append(openText)
          .Append(text, lead, text.Length - lead - trail)
          .Append(closeText)
          .Append(text, text.Length - trail, trail);
    }

    private void RenderLink(StringBuilder sb, LinkInline link, TextContext context)
    {
        var linkContext = (context | TextContext.LinkText) & ~TextContext.Breakable;
        var inner = new StringBuilder();
        RenderInto(inner, link.Inlines, linkContext, '[', ']');
        var text = inner.ToString().Trim(' ', BreakableSpace);
        var url = link.Url;

        if (string.IsNullOrEmpty(link.Title) && link.Inlines.All(i => i is TextInline))
        {
            var plain = Inline.ToPlainText(link.Inlines);
            if (AutolinkSafe().IsMatch(url) && (plain == url || (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && url[7..] == plain && plain.Contains('@', StringComparison.Ordinal))))
            {
                sb.Append('<').Append(plain).Append('>');
                return;
            }
        }

        if (_settings.ReferenceLinks && text.Length > 0)
        {
            var index = _references.FindIndex(r => r.Url == url && r.Title == link.Title);
            if (index < 0)
            {
                _references.Add((url, link.Title));
                index = _references.Count - 1;
            }

            sb.Append('[').Append(text).Append("][").Append(index + 1).Append(']');
            return;
        }

        sb.Append('[').Append(text).Append("](").Append(FormatDestination(url)).Append(FormatTitle(link.Title)).Append(')');
    }

    /// <summary>The first output character the following inlines will produce (approximate; markup is punctuation).</summary>
    private static char? FirstChar(IReadOnlyList<Inline> inlines, int start)
    {
        for (var j = start; j < inlines.Count; j++)
        {
            switch (inlines[j])
            {
                case TextInline { Text.Length: 0 }:
                    continue;
                case TextInline t:
                    return t.Text[0];
                case LineBreakInline:
                    return ' ';
                case StyledInline s when (s.Style & ~InlineStyle.SmallCaps) == InlineStyle.None:
                    if (FirstChar(s.Inlines, 0) is { } c)
                    {
                        return c;
                    }

                    continue;
                case CodeInline { Code.Length: 0 }:
                    continue;
                default:
                    return '*';
            }
        }

        return null;
    }

    private static void TrimEndSpaces(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] is ' ' or '\t' or BreakableSpace)
        {
            sb.Length--;
        }
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or BreakableSpace;

    private static bool IsWord(char? c) => c is { } ch && char.IsLetterOrDigit(ch);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]{1,31}:[^\s<>\x00-\x1f\\]*$")]
    private static partial Regex AutolinkSafe();

    [GeneratedRegex(@"[\s\[\]\^]+")]
    private static partial Regex FootnoteLabelInvalid();
}
