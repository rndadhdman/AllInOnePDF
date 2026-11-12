using System.Globalization;
using System.Text;
using AllInOnePdf.Core.Documents;
using static AllInOnePdf.Core.Exporters.Markdown.MarkdownEscaper;
using static AllInOnePdf.Core.Exporters.Markdown.MarkdownTextWrapper;

namespace AllInOnePdf.Core.Exporters.Markdown;

/// <summary>
/// Serialises a <see cref="Document"/> to GitHub-flavoured Markdown (plus the Markdig extras the importer reads:
/// footnotes, math, alerts, highlight, super/subscript). Blocks are rendered to strings with "\n" line endings and
/// joined with blank lines; containers prefix or indent their children's lines.
/// </summary>
internal sealed class MarkdownWriter
{
    private const string PageBreakDiv = "<div style=\"page-break-after: always;\"></div>";

    private readonly Document _document;
    private readonly MarkdownExportSettings _settings;
    private readonly MarkdownInlineWriter _inline;
    private int _listDepth;

    public MarkdownWriter(Document document, MarkdownExportSettings settings, MarkdownImageResolver images)
    {
        _document = document;
        _settings = settings;
        _inline = new MarkdownInlineWriter(settings, images);
    }

    /// <summary>The whole document as Markdown with "\n" line endings (empty for an empty document).</summary>
    public string Write(Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var parts = new List<string>();
        var frontMatter = _settings.FrontMatter ? MarkdownFrontMatter.Write(_document.Metadata) : null;
        if (frontMatter is not null)
        {
            parts.Add(frontMatter);
        }

        var title = _document.Metadata.Title;
        if (_settings.TitleHeading && !string.IsNullOrWhiteSpace(title) && !_document.Headings.Any(h => h.Level == 1))
        {
            parts.Add(WriteHeading(new HeadingBlock(1, title.Trim())));
        }

        var bodyStart = parts.Count;
        var blocks = _document.Blocks;
        var sequence = new BlockSequence();
        for (var i = 0; i < blocks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteBlock(blocks[i], _settings.WrapWidth, sequence) is { Length: > 0 } text)
            {
                parts.Add(text);
            }

            progress?.Invoke((double)(i + 1) / Math.Max(1, blocks.Count));
        }

        // A leading "---" would be mistaken for a YAML front-matter fence.
        if (frontMatter is null && parts.Count > bodyStart && parts[bodyStart].StartsWith("---", StringComparison.Ordinal) && bodyStart == 0)
        {
            parts[0] = "***" + parts[0][3..];
        }

        WriteFootnotes(parts);

        if (_inline.References.Count > 0)
        {
            var refs = new StringBuilder();
            for (var i = 0; i < _inline.References.Count; i++)
            {
                var (url, linkTitle) = _inline.References[i];
                if (i > 0)
                {
                    refs.Append('\n');
                }

                refs.Append('[').Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("]: ")
                    .Append(FormatDestination(url)).Append(FormatTitle(linkTitle));
            }

            parts.Add(refs.ToString());
        }

        return parts.Count == 0 ? string.Empty : string.Join("\n\n", parts) + "\n";
    }

    private void WriteFootnotes(List<string> parts)
    {
        foreach (var footnote in _document.Footnotes)
        {
            if (_settings.Footnotes)
            {
                var content = WriteBlocks(footnote.Blocks, Inner(_settings.WrapWidth, 4));
                parts.Add(Hang(content, "[^" + MarkdownInlineWriter.FootnoteLabel(footnote.Label) + "]: ", 4));
            }
            else
            {
                var content = WriteBlocks(footnote.Blocks, _settings.WrapWidth);
                var marker = "^" + EscapeText(footnote.Label, '^', '^', TextContext.SingleLine) + "^ ";
                parts.Add(content.Length == 0 ? marker.TrimEnd() : marker + content);
            }
        }
    }

    /// <summary>Tracks the previous sibling so adjacent lists do not merge into one.</summary>
    private sealed class BlockSequence
    {
        public Block? Previous { get; set; }
        public char PreviousMarker { get; set; }
    }

    private string WriteBlocks(IReadOnlyList<Block> blocks, int width, bool tight = false)
    {
        var sb = new StringBuilder();
        var sequence = new BlockSequence();
        foreach (var block in blocks)
        {
            if (WriteBlock(block, width, sequence) is not { Length: > 0 } text)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(tight ? "\n" : "\n\n");
            }

            sb.Append(text);
        }

        return sb.ToString();
    }

    private string? WriteBlock(Block block, int width, BlockSequence sequence)
    {
        string? text = block switch
        {
            HeadingBlock h => WriteHeading(h),
            ParagraphBlock p => WriteParagraph(p, width),
            ListBlock l => WriteList(l, width, sequence),
            CodeBlock c => WriteCode(c),
            QuoteBlock q => WriteQuote(q, width),
            CalloutBlock c => WriteCallout(c, width),
            TableBlock t => WriteTable(t),
            ImageBlock i => WriteImage(i),
            ThematicBreakBlock => Rule(),
            PageBreakBlock => _settings.PageBreaks switch
            {
                PageBreakStyle.Omit => null,
                PageBreakStyle.HorizontalRule => Rule(),
                _ => PageBreakDiv,
            },
            HtmlBlock html => NormalizeNewlines(html.Html).Trim('\n') is { Length: > 0 } raw && !string.IsNullOrWhiteSpace(raw) ? raw : null,
            MathBlock math => WriteMath(math),
            _ => null,
        };

        if (!string.IsNullOrEmpty(text))
        {
            sequence.Previous = block;
        }

        return text;
    }

    private string Rule() => _listDepth > 0 ? "___" : "---";

    private string? WriteParagraph(ParagraphBlock paragraph, int width)
    {
        var text = _inline.Render(paragraph.Inlines, TextContext.Breakable);
        return string.IsNullOrWhiteSpace(text.Replace(BreakableSpace, ' ')) ? null : FinishLines(Wrap(text, width));
    }

    private string WriteHeading(HeadingBlock heading)
    {
        var text = _inline.Render(heading.Inlines, TextContext.SingleLine).Replace(BreakableSpace, ' ').Trim();
        if (_settings.SetextHeadings && heading.Level <= 2 && text.Length > 0)
        {
            var line = EscapeLineStart(text);
            return line + "\n" + new string(heading.Level == 1 ? '=' : '-', Math.Max(3, DisplayWidth(line)));
        }

        // A trailing run of '#' after a space would be read as the optional ATX closing sequence.
        if (text.EndsWith('#'))
        {
            var run = text.Length;
            while (run > 0 && text[run - 1] == '#')
            {
                run--;
            }

            if (run == 0 || text[run - 1] == ' ')
            {
                text = text[..run] + "\\" + text[run..];
            }
        }

        var hashes = new string('#', heading.Level);
        return text.Length == 0 ? hashes : hashes + " " + text;
    }

    private string WriteCode(CodeBlock code)
    {
        var body = NormalizeNewlines(code.Code);
        if (body.EndsWith('\n'))
        {
            body = body[..^1];
        }

        var info = (code.Language ?? string.Empty).Trim().Replace(' ', '-').Replace('\t', '-');
        if (info.Length > 0 && !string.IsNullOrWhiteSpace(code.Caption))
        {
            info += " " + code.Caption.Replace('\n', ' ').Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        }

        return Fence(body, info);
    }

    private string Fence(string body, string info)
    {
        var fenceChar = _settings.CodeFence;
        if (fenceChar == '`' && info.Contains('`', StringComparison.Ordinal))
        {
            fenceChar = '~';
        }

        var fence = new string(fenceChar, Math.Max(3, LongestRun(body, fenceChar) + 1));
        return body.Length == 0 ? fence + info + "\n" + fence : fence + info + "\n" + body + "\n" + fence;
    }

    private string WriteMath(MathBlock math)
    {
        var latex = NormalizeNewlines(math.Latex).Trim('\n');
        if (string.IsNullOrWhiteSpace(latex))
        {
            return string.Empty;
        }

        return _settings.Math == MathStyle.CodeBlock ? Fence(latex, "math") : "$$\n" + latex + "\n$$";
    }

    private string WriteQuote(QuoteBlock quote, int width)
    {
        var inner = WriteBlocks(quote.Blocks, Inner(width, 2));
        if (!string.IsNullOrWhiteSpace(quote.Attribution))
        {
            var attribution = FinishLines(EscapeText("— " + quote.Attribution.Trim(), null, null, TextContext.SingleLine));
            inner = inner.Length == 0 ? attribution : inner + "\n\n" + attribution;
        }

        return Prefix(inner, "> ", ">");
    }

    private string WriteCallout(CalloutBlock callout, int width)
    {
        var inner = WriteBlocks(callout.Blocks, Inner(width, 2));
        string Bold(string text) => new string(_settings.EmphasisMarker, 2) + EscapeText(text.Trim(), null, null, TextContext.SingleLine) + new string(_settings.EmphasisMarker, 2);
        string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "\n\n" + b;
        var title = string.IsNullOrWhiteSpace(callout.Title) ? null : callout.Title;

        switch (_settings.Callouts)
        {
            case CalloutStyle.BoldTitle:
                return Prefix(Join(Bold(title ?? KindLabel(callout.Kind)), inner), "> ", ">");

            case CalloutStyle.PlainQuote:
                return Prefix(title is null ? inner : Join(Bold(title), inner), "> ", ">");

            default:
                {
                    var body = title is null ? inner : Join(Bold(title), inner);
                    var marker = "[!" + callout.Kind.ToString().ToUpperInvariant() + "]";
                    return Prefix(body.Length == 0 ? marker : marker + "\n" + body, "> ", ">");
                }
        }
    }

    private static string KindLabel(CalloutKind kind) => kind switch
    {
        CalloutKind.Tip => "Tip",
        CalloutKind.Important => "Important",
        CalloutKind.Warning => "Warning",
        CalloutKind.Caution => "Caution",
        _ => "Note",
    };

    private string? WriteTable(TableBlock table)
    {
        var text = MarkdownTableWriter.Write(table, RenderCell, _settings.AlignTables);
        if (text is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(table.Caption))
        {
            text += "\n\n" + Emphasis(table.Caption);
        }

        return text;
    }

    private string RenderCell(TableCell cell) => string.Join("<br>", cell.Blocks.Select(RenderCellBlock).Where(s => s.Length > 0));

    private string RenderCellBlock(Block block)
    {
        switch (block)
        {
            case InlineContainerBlock icb:
                return _inline.Render(icb.Inlines, TextContext.Table);
            case CodeBlock code:
                return string.Join("<br>", NormalizeNewlines(code.Code).TrimEnd('\n').Split('\n').Where(l => l.Length > 0).Select(l => CodeSpan(l).Replace("|", @"\|", StringComparison.Ordinal)));
            case ImageBlock image:
                return _inline.RenderImage(image.Source, image.AltText, image.Title, TextContext.Table);
            case ListBlock list:
                {
                    var n = list.Start;
                    return string.Join("<br>", list.Items.Select(item =>
                    {
                        var marker = list.Ordered ? (n++).ToString(CultureInfo.InvariantCulture) + ". " : "• ";
                        var task = item.Checked switch { true => "☑ ", false => "☐ ", _ => string.Empty };
                        return marker + task + string.Join(" ", item.Blocks.Select(RenderCellBlock).Where(s => s.Length > 0));
                    }));
                }

            case MathBlock math:
                {
                    var latex = NormalizeNewlines(math.Latex).Replace('\n', ' ').Trim();
                    return _settings.Math == MathStyle.Dollar && !latex.Contains('$', StringComparison.Ordinal) ? "$" + latex + "$" : CodeSpan(latex);
                }

            case HtmlBlock html:
                return NormalizeNewlines(html.Html).Replace('\n', ' ').Trim().Replace("|", @"\|", StringComparison.Ordinal);
            case TableBlock nested:
                return string.Join("<br>", nested.Rows.Select(r => string.Join(" · ", r.Cells.Select(RenderCell))));
            default:
                return string.Join("<br>", block.Children.Select(RenderCellBlock).Where(s => s.Length > 0));
        }
    }

    private string? WriteImage(ImageBlock image)
    {
        var markup = _inline.RenderImage(image.Source, image.AltText, image.Title, TextContext.SingleLine);
        if (string.IsNullOrWhiteSpace(markup))
        {
            return null;
        }

        markup = FinishLines(markup);
        if (HasCaption(image) && markup.StartsWith("![", StringComparison.Ordinal))
        {
            markup += "\n\n" + Emphasis(image.Title!);
        }

        return markup;
    }

    private static bool HasCaption(Block block) =>
        block is ImageBlock { Title: { } title } image && !string.IsNullOrWhiteSpace(title) && !string.Equals(title.Trim(), image.AltText?.Trim(), StringComparison.Ordinal);

    private string Emphasis(string text) =>
        _settings.EmphasisMarker + EscapeText(text.Trim(), _settings.EmphasisMarker, _settings.EmphasisMarker, TextContext.SingleLine) + _settings.EmphasisMarker;

    private string WriteList(ListBlock list, int width, BlockSequence sequence)
    {
        // Two adjacent lists of the same kind would merge; switching the marker character starts a new list.
        char marker;
        if (list.Ordered)
        {
            marker = sequence.Previous is ListBlock { Ordered: true } && sequence.PreviousMarker == '.' ? ')' : '.';
        }
        else
        {
            var primary = _settings.BulletMarker;
            var alternate = primary == '-' ? '*' : '-';
            marker = sequence.Previous is ListBlock { Ordered: false } && sequence.PreviousMarker == primary ? alternate : primary;
        }

        sequence.PreviousMarker = marker;
        var tight = list.Tight && list.Items.All(i => CanBeTight(i.Blocks));
        var number = Math.Clamp(list.Start, 0, 999_999_999);
        var sb = new StringBuilder();
        _listDepth++;
        try
        {
            foreach (var item in list.Items)
            {
                var bullet = list.Ordered ? number.ToString(CultureInfo.InvariantCulture) + marker + " " : marker + " ";
                if (list.Ordered && !_settings.OrderedAllOnes)
                {
                    number = Math.Min(number + 1, 999_999_999);
                }

                var content = WriteBlocks(item.Blocks, Inner(width, bullet.Length), tight);
                if (item.Checked is { } done)
                {
                    var box = done ? "[x]" : "[ ]";
                    content = content.Length == 0 ? box : item.Blocks.FirstOrDefault() is ParagraphBlock ? box + " " + content : box + "\n" + content;
                }

                if (sb.Length > 0)
                {
                    sb.Append(tight ? "\n" : "\n\n");
                }

                sb.Append(Hang(content, bullet, bullet.Length));
            }
        }
        finally
        {
            _listDepth--;
        }

        return sb.ToString();
    }

    /// <summary>True when the item's blocks can be written without blank lines between them and still parse back as the same blocks.</summary>
    private bool CanBeTight(List<Block> blocks)
    {
        var visible = blocks.Where(b => b is not ParagraphBlock p || !string.IsNullOrWhiteSpace(p.ToPlainText()) || p.Inlines.Any(i => i is not TextInline)).ToList();
        if (visible.Any(HasCaption))
        {
            return false;
        }

        for (var i = 1; i < visible.Count; i++)
        {
            if (!CanFollowTight(visible[i - 1], visible[i]))
            {
                return false;
            }
        }

        return true;
    }

    private bool CanFollowTight(Block a, Block b)
    {
        if (a is ListBlock or QuoteBlock or CalloutBlock or TableBlock or HtmlBlock or MathBlock or PageBreakBlock)
        {
            return false;
        }

        return b switch
        {
            ListBlock l => !l.Ordered || l.Start == 1,
            CodeBlock or QuoteBlock or CalloutBlock => true,
            HeadingBlock h => !(_settings.SetextHeadings && h.Level <= 2),
            ParagraphBlock or ImageBlock => a is CodeBlock or HeadingBlock or ThematicBreakBlock,
            _ => false,
        };
    }

    private static int Inner(int width, int indent) => width <= 0 ? 0 : Math.Max(20, width - indent);

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
