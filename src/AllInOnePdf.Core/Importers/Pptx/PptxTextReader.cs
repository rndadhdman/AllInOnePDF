using System.Text;
using AllInOnePdf.Core.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;

namespace AllInOnePdf.Core.Importers.Pptx;

/// <summary>One DrawingML paragraph read into inlines plus its bullet/level/alignment facts.</summary>
internal sealed record PptxParagraph(
    List<Inline> Inlines,
    bool IsBullet,
    bool Ordered,
    int Start,
    int Level,
    TextAlignment? Alignment,
    bool IsEmpty);

/// <summary>
/// Reads DrawingML text (<c>a:p</c>/<c>a:r</c>/<c>a:br</c>/<c>a:fld</c>) into inlines: run styles, hyperlinks,
/// monospace runs as inline code; groups bulleted paragraphs into nested lists.
/// </summary>
internal sealed class PptxTextReader
{
    private static readonly string[] MonospaceFonts =
    [
        "Consolas", "Courier", "Courier New", "Lucida Console", "Lucida Sans Typewriter", "Menlo", "Monaco",
        "Andale Mono", "Inconsolata", "Hack", "SF Mono", "OCR A Extended",
    ];

    private readonly OpenXmlPart _part;
    private readonly bool _detectCode;

    public PptxTextReader(OpenXmlPart part, bool detectCode)
    {
        _part = part;
        _detectCode = detectCode;
    }

    public static bool IsMonospace(string? typeface)
    {
        if (string.IsNullOrWhiteSpace(typeface) || typeface.StartsWith('+'))
        {
            return false;
        }

        var name = typeface.Trim();
        return MonospaceFonts.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase))
            || name.Contains("Mono", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Code", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Typewriter", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when every run with text is set in a monospace latin font (and there is at least one).</summary>
    public static bool IsAllMonospace(IEnumerable<A.Paragraph> paragraphs)
    {
        var any = false;
        foreach (var (text, props) in TextRuns(paragraphs))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!IsMonospace(props?.GetFirstChild<A.LatinFont>()?.Typeface?.Value))
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>True when every run with text is bold (and there is at least one).</summary>
    public static bool IsAllBold(IEnumerable<A.Paragraph> paragraphs)
    {
        var any = false;
        foreach (var (text, props) in TextRuns(paragraphs))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (props?.Bold?.Value != true)
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>The paragraphs as plain text lines (breaks become new lines), trailing blank lines removed.</summary>
    public static string CodeText(IEnumerable<A.Paragraph> paragraphs)
    {
        var sb = new StringBuilder();
        foreach (var p in paragraphs)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            foreach (var child in p.ChildElements)
            {
                switch (child)
                {
                    case A.Run r:
                        sb.Append(r.Text?.Text);
                        break;
                    case A.Field f:
                        sb.Append(f.Text?.Text);
                        break;
                    case A.Break:
                        sb.Append('\n');
                        break;
                }
            }
        }

        return sb.ToString().TrimEnd('\n', ' ', '\r');
    }

    /// <summary>Plain text of the paragraphs joined with spaces (titles).</summary>
    public static string PlainText(IEnumerable<A.Paragraph> paragraphs)
    {
        var parts = new List<string>();
        foreach (var p in paragraphs)
        {
            var sb = new StringBuilder();
            foreach (var child in p.ChildElements)
            {
                switch (child)
                {
                    case A.Run r:
                        sb.Append(r.Text?.Text);
                        break;
                    case A.Field f:
                        sb.Append(f.Text?.Text);
                        break;
                    case A.Break:
                        sb.Append(' ');
                        break;
                }
            }

            var text = string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length > 0)
            {
                parts.Add(text);
            }
        }

        return string.Join(' ', parts);
    }

    public PptxParagraph Read(A.Paragraph paragraph, bool bulletsByDefault)
    {
        var segments = new List<Segment>();
        foreach (var child in paragraph.ChildElements)
        {
            switch (child)
            {
                case A.Run r:
                    AddText(segments, r.Text?.Text, r.RunProperties);
                    break;
                case A.Field f:
                    AddText(segments, f.Text?.Text, f.RunProperties);
                    break;
                case A.Break b:
                    segments.Add(new Segment(StyleOf(b.RunProperties), LinkOf(b.RunProperties), false, null));
                    break;
            }
        }

        var inlines = BuildInlines(segments);
        var isEmpty = segments.All(s => string.IsNullOrWhiteSpace(s.Text));

        var pPr = paragraph.ParagraphProperties;
        var level = Math.Clamp(pPr?.Level?.Value ?? 0, 0, 8);
        var none = pPr?.GetFirstChild<A.NoBullet>() is not null;
        var auto = pPr?.GetFirstChild<A.AutoNumberedBullet>();
        var explicitBullet = auto is not null || pPr?.GetFirstChild<A.CharacterBullet>() is not null || pPr?.GetFirstChild<A.PictureBullet>() is not null;
        var isBullet = !none && (explicitBullet || bulletsByDefault);

        TextAlignment? alignment = null;
        if (pPr?.Alignment is { HasValue: true } algn)
        {
            var v = algn.Value;
            if (v == A.TextAlignmentTypeValues.Center)
            {
                alignment = TextAlignment.Center;
            }
            else if (v == A.TextAlignmentTypeValues.Right)
            {
                alignment = TextAlignment.Right;
            }
            else if (v == A.TextAlignmentTypeValues.Justified || v == A.TextAlignmentTypeValues.Distributed)
            {
                alignment = TextAlignment.Justify;
            }
        }

        return new PptxParagraph(inlines, isBullet, auto is not null, Math.Max(1, auto?.StartAt?.Value ?? 1), level, alignment, isEmpty);
    }

    /// <summary>
    /// Paragraphs → blocks: bulleted runs become (nested) lists; empty paragraphs are dropped but end a list. With
    /// <paramref name="subheadings"/>, a short paragraph set entirely in bold at a larger size than the shape's body
    /// text becomes a heading (our exporter writes in-slide headings that way); its size step above the body is
    /// reported so the caller can rank the levels across the deck.
    /// </summary>
    public List<Block> ToBlocks(IEnumerable<A.Paragraph> paragraphs, bool bulletsByDefault, Action<HeadingBlock, int>? subheadings = null)
    {
        var blocks = new List<Block>();
        ListBuilder? list = null;
        var all = paragraphs.ToList();
        var bodySize = subheadings is null ? null : DominantSize(all);
        foreach (var p in all)
        {
            var para = Read(p, bulletsByDefault);
            if (para.IsEmpty)
            {
                list = null;
                continue;
            }

            if (para.IsBullet)
            {
                list ??= new ListBuilder(blocks);
                list.Add(para);
                continue;
            }

            list = null;
            if (subheadings is not null && bodySize is { } body && UniformBoldSize(p) is { } size && size >= body + 150
                && PlainText([p]) is { Length: > 0 and <= 200 } headingText)
            {
                var heading = new HeadingBlock(2, headingText) { Alignment = para.Alignment };
                blocks.Add(heading);
                subheadings(heading, size - body);
                continue;
            }

            var block = new ParagraphBlock { Alignment = para.Alignment };
            block.Inlines.AddRange(para.Inlines);
            blocks.Add(block);
        }

        return blocks;
    }

    /// <summary>The explicit run size (hundredths of a point) carrying most of the text, or null when runs inherit their size.</summary>
    private static int? DominantSize(IEnumerable<A.Paragraph> paragraphs)
    {
        var weights = new Dictionary<int, int>();
        foreach (var (text, props) in TextRuns(paragraphs))
        {
            if (string.IsNullOrWhiteSpace(text) || props?.FontSize?.Value is not { } size)
            {
                continue;
            }

            weights[size] = weights.GetValueOrDefault(size) + text.Length;
        }

        return weights.Count == 0 ? null : weights.MaxBy(kv => kv.Value).Key;
    }

    /// <summary>The size of a paragraph whose text runs are all bold at one explicit size, else null.</summary>
    private static int? UniformBoldSize(A.Paragraph paragraph)
    {
        int? size = null;
        foreach (var (text, props) in TextRuns([paragraph]))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (props?.Bold?.Value != true || props.FontSize?.Value is not { } s || (size is not null && size != s))
            {
                return null;
            }

            size = s;
        }

        return size;
    }

    /// <summary>Removes <paramref name="style"/> from styled runs (header cells are bold by convention, not emphasis).</summary>
    public static List<Inline> StripStyle(IEnumerable<Inline> inlines, InlineStyle style)
    {
        var result = new List<Inline>();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case StyledInline s:
                    var children = StripStyle(s.Inlines, style);
                    var remaining = s.Style & ~style;
                    if (remaining == InlineStyle.None)
                    {
                        result.AddRange(children);
                    }
                    else
                    {
                        var copy = new StyledInline { Style = remaining };
                        copy.Inlines.AddRange(children);
                        result.Add(copy);
                    }

                    break;
                case LinkInline l:
                    var link = new LinkInline { Url = l.Url, Title = l.Title };
                    link.Inlines.AddRange(StripStyle(l.Inlines, style));
                    result.Add(link);
                    break;
                default:
                    result.Add(inline);
                    break;
            }
        }

        return result;
    }

    private static IEnumerable<(string? Text, A.RunProperties? Props)> TextRuns(IEnumerable<A.Paragraph> paragraphs)
    {
        foreach (var p in paragraphs)
        {
            foreach (var child in p.ChildElements)
            {
                switch (child)
                {
                    case A.Run r:
                        yield return (r.Text?.Text, r.RunProperties);
                        break;
                    case A.Field f:
                        yield return (f.Text?.Text, f.RunProperties);
                        break;
                }
            }
        }
    }

    private void AddText(List<Segment> segments, string? text, A.RunProperties? props)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // PowerPoint stores soft line breaks inside a run as vertical tabs.
        var parts = text.Split('\v');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                segments.Add(new Segment(StyleOf(props), LinkOf(props), false, null));
            }

            if (parts[i].Length > 0)
            {
                var code = _detectCode && IsMonospace(props?.GetFirstChild<A.LatinFont>()?.Typeface?.Value);
                segments.Add(new Segment(StyleOf(props), LinkOf(props), code, parts[i]));
            }
        }
    }

    private static InlineStyle StyleOf(OpenXmlElement? props)
    {
        if (props is not A.TextCharacterPropertiesType rPr)
        {
            return InlineStyle.None;
        }

        var style = InlineStyle.None;
        if (rPr.Bold?.Value == true)
        {
            style |= InlineStyle.Bold;
        }

        if (rPr.Italic?.Value == true)
        {
            style |= InlineStyle.Italic;
        }

        if (rPr.Underline is { HasValue: true } u && u.Value != A.TextUnderlineValues.None)
        {
            style |= InlineStyle.Underline;
        }

        if (rPr.Strike is { HasValue: true } s && s.Value != A.TextStrikeValues.NoStrike)
        {
            style |= InlineStyle.Strikethrough;
        }

        if (rPr.Baseline?.Value is { } baseline)
        {
            if (baseline > 0)
            {
                style |= InlineStyle.Superscript;
            }
            else if (baseline < 0)
            {
                style |= InlineStyle.Subscript;
            }
        }

        return style;
    }

    private string? LinkOf(OpenXmlElement? props)
    {
        var id = props?.GetFirstChild<A.HyperlinkOnClick>()?.Id?.Value;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var rel = _part.HyperlinkRelationships.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        var url = rel?.Uri.OriginalString;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim();
        return url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? null
            : url;
    }

    private static List<Inline> BuildInlines(List<Segment> segments)
    {
        var result = new List<Inline>();
        var i = 0;
        while (i < segments.Count)
        {
            var first = segments[i];
            var content = new List<Inline>();
            while (i < segments.Count && segments[i].Style == first.Style && segments[i].Url == first.Url && (segments[i].Code == first.Code || segments[i].Text is null))
            {
                var s = segments[i];
                if (s.Text is null)
                {
                    content.Add(new LineBreakInline());
                }
                else if (s.Code)
                {
                    if (content.Count > 0 && content[^1] is CodeInline c)
                    {
                        c.Code += s.Text;
                    }
                    else
                    {
                        content.Add(new CodeInline(s.Text));
                    }
                }
                else if (content.Count > 0 && content[^1] is TextInline t)
                {
                    t.Text += s.Text;
                }
                else
                {
                    content.Add(new TextInline(s.Text));
                }

                i++;
            }

            // Hyperlinks are underlined by convention; that is not emphasis.
            var style = first.Url is null ? first.Style : first.Style & ~InlineStyle.Underline;
            IEnumerable<Inline> wrapped = content;
            if (style != InlineStyle.None)
            {
                wrapped = [new StyledInline(style, [.. content])];
            }

            if (first.Url is not null)
            {
                var link = new LinkInline { Url = first.Url };
                link.Inlines.AddRange(wrapped);
                result.Add(link);
            }
            else
            {
                result.AddRange(wrapped);
            }
        }

        // Leading/trailing breaks carry no content.
        while (result.Count > 0 && result[^1] is LineBreakInline)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private sealed record Segment(InlineStyle Style, string? Url, bool Code, string? Text);

    /// <summary>Builds nested lists from bulleted paragraphs; levels are relative to the first item.</summary>
    private sealed class ListBuilder
    {
        private readonly List<Block> _blocks;
        private readonly List<ListBlock> _stack = [];
        private int _baseLevel;

        public ListBuilder(List<Block> blocks)
        {
            _blocks = blocks;
        }

        public void Add(PptxParagraph para)
        {
            if (_stack.Count == 0)
            {
                _baseLevel = para.Level;
                var root = new ListBlock { Ordered = para.Ordered, Start = para.Start };
                _blocks.Add(root);
                _stack.Add(root);
            }

            var level = Math.Max(0, para.Level - _baseLevel);
            while (_stack.Count > level + 1)
            {
                _stack.RemoveAt(_stack.Count - 1);
            }

            while (_stack.Count < level + 1)
            {
                var parent = _stack[^1];
                if (parent.Items.Count == 0)
                {
                    parent.Items.Add(new ListItem());
                }

                var nested = new ListBlock { Ordered = para.Ordered, Start = para.Start };
                parent.Items[^1].Blocks.Add(nested);
                _stack.Add(nested);
            }

            var current = _stack[^1];
            if (current.Ordered != para.Ordered && current.Items.Count > 0)
            {
                var replacement = new ListBlock { Ordered = para.Ordered, Start = para.Start };
                if (_stack.Count == 1)
                {
                    _blocks.Add(replacement);
                }
                else
                {
                    var parent = _stack[^2];
                    if (parent.Items.Count == 0)
                    {
                        parent.Items.Add(new ListItem());
                    }

                    parent.Items[^1].Blocks.Add(replacement);
                }

                _stack[^1] = replacement;
                current = replacement;
            }

            var item = new ListItem();
            var paragraph = new ParagraphBlock();
            paragraph.Inlines.AddRange(para.Inlines);
            if (paragraph.Inlines.Count > 0 && paragraph.Inlines[0] is TextInline { Text.Length: >= 2 } t && t.Text[1] == ' ' && t.Text[0] is '☐' or '☑' or '☒')
            {
                item.Checked = t.Text[0] != '☐';
                t.Text = t.Text[2..];
                if (t.Text.Length == 0)
                {
                    paragraph.Inlines.RemoveAt(0);
                }
            }

            item.Blocks.Add(paragraph);
            current.Items.Add(item);
        }
    }
}
