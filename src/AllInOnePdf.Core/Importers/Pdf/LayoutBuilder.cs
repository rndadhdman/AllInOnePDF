using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>
/// Turns the analysed lines of a page into document blocks: tables, quote / callout groups (lines next to an accent
/// bar), code blocks, headings, nested lists and paragraphs, with images and horizontal rules slotted in at their
/// vertical position.
/// </summary>
internal sealed partial class LayoutBuilder
{
    private readonly PdfImportSettings _settings;
    private readonly FontStatistics _stats;
    private readonly HeadingDetector _headings;
    private readonly PdfMarkers _markers;
    private readonly Dictionary<ImageBlock, PageImage> _pendingImages = new(ReferenceEqualityComparer.Instance);

    public LayoutBuilder(PdfImportSettings settings, FontStatistics stats, HeadingDetector headings, PdfMarkers? markers = null)
    {
        _settings = settings;
        _stats = stats;
        _headings = headings;
        _markers = markers ?? PdfMarkers.Empty;
    }

    /// <summary>Source lines of every top-level paragraph, so paragraphs split by a page break can be re-joined.</summary>
    public Dictionary<ParagraphBlock, List<PdfLine>> ParagraphSources { get; } = new(ReferenceEqualityComparer.Instance);

    private bool Structure => _settings.StructureEnabled;
    private bool Code => Structure && _settings.DetectCode;
    private bool Lists => Structure && _settings.DetectLists;

    public List<Block> BuildPage(PageContent page)
    {
        var captions = TakeImageCaptions(page);
        var lines = page.ActiveLines.ToList();
        var tables = Structure && _settings.DetectTables ? TableDetector.Detect(page, lines, _stats) : [];
        var tableOf = new Dictionary<PdfLine, DetectedTable>(ReferenceEqualityComparer.Instance);
        foreach (var table in tables)
        {
            foreach (var line in table.Lines)
            {
                tableOf[line] = table;
            }
        }

        var items = new List<(double Top, Block Block)>();
        var emitted = new HashSet<DetectedTable>(ReferenceEqualityComparer.Instance);
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (tableOf.TryGetValue(line, out var table))
            {
                if (emitted.Add(table))
                {
                    items.Add((table.Top, ToTableBlock(table)));
                }

                i++;
                continue;
            }

            int j;
            if (Structure && line.BarId is { } bar)
            {
                j = i + 1;
                while (j < lines.Count && lines[j].BarId == bar && !tableOf.ContainsKey(lines[j]))
                {
                    j++;
                }

                items.Add((line.Top, BuildBarGroup(lines.GetRange(i, j - i), page)));
                i = j;
                continue;
            }

            // A shaded box (without accent bar) whose first line is a label such as "Note" or "Warning".
            if (Structure && line.BoxId is { } boxId && !line.IsMono && KindFromTitle(line.Text) is not null)
            {
                j = i + 1;
                while (j < lines.Count && lines[j].BoxId == boxId && !tableOf.ContainsKey(lines[j]))
                {
                    j++;
                }

                items.Add((line.Top, BuildBarGroup(lines.GetRange(i, j - i), page)));
                i = j;
                continue;
            }

            j = i + 1;
            while (j < lines.Count && !tableOf.ContainsKey(lines[j]) && (!Structure || lines[j].BarId is null)
                && !(Structure && lines[j].BoxId is not null && lines[j].BoxId != lines[j - 1].BoxId && KindFromTitle(lines[j].Text) is not null))
            {
                j++;
            }

            items.AddRange(BuildFlow(lines.GetRange(i, j - i), page, nested: false));
            i = j;
        }

        foreach (var image in page.Images.OrderByDescending(im => im.Bounds.Top))
        {
            var block = new ImageBlock
            {
                Source = string.Empty,
                PixelWidth = image.PixelWidth,
                PixelHeight = image.PixelHeight,
                AltText = captions.GetValueOrDefault(image),
            };
            Insert(items, image.Bounds.Top, block);
            _pendingImages[block] = image;
        }

        if (Structure)
        {
            foreach (var y in ThematicBreaks(page, lines, tables))
            {
                Insert(items, y, new ThematicBreakBlock());
            }
        }

        var result = items.Select(t => t.Block).ToList();
        foreach (var block in Block.EnumerateRecursive(result))
        {
            block.SourcePage ??= page.Number;
        }

        return result;
    }

    /// <summary>Image blocks created so far with their extracted data (the importer turns them into resources).</summary>
    public IReadOnlyDictionary<ImageBlock, PageImage> Images => _pendingImages;

    /// <summary>
    /// A short italic line centred just below an image is its caption: it becomes the alt text and leaves the flow.
    /// </summary>
    private static Dictionary<PageImage, string> TakeImageCaptions(PageContent page)
    {
        var result = new Dictionary<PageImage, string>(ReferenceEqualityComparer.Instance);
        foreach (var image in page.Images)
        {
            var center = (image.Bounds.Left + image.Bounds.Right) / 2;
            var caption = page.ActiveLines.FirstOrDefault(l =>
                l.Top <= image.Bounds.Bottom + 2 && l.Top >= image.Bounds.Bottom - l.Size * 3
                && l.ItalicFraction >= 0.9 && l.Text.Length <= 200
                && (Math.Abs((l.Left + l.Right) / 2 - center) <= 6 || Math.Abs(l.Left - image.Bounds.Left) <= 2));
            if (caption is not null)
            {
                caption.Removed = true;
                result[image] = caption.Text.Trim();
            }
        }

        return result;
    }

    private static void Insert(List<(double Top, Block Block)> items, double top, Block block)
    {
        var index = items.FindIndex(t => t.Top < top - 1);
        if (index < 0)
        {
            items.Add((top, block));
        }
        else
        {
            items.Insert(index, (top, block));
        }
    }

    // ------------------------------------------------------------------- flow

    private List<(double Top, Block Block)> BuildFlow(List<PdfLine> run, PageContent page, bool nested)
    {
        var result = new List<(double, Block)>();
        var i = 0;
        while (i < run.Count)
        {
            var line = run[i];
            var next = i + 1 < run.Count ? run[i + 1] : null;

            if (Code && CodeBlockBuilder.IsCodeLine(line))
            {
                var j = i + 1;
                while (j < run.Count && CodeBlockBuilder.IsCodeLine(run[j]) && SameCodeBlock(run, i, j))
                {
                    j++;
                }

                var code = CodeBlockBuilder.Build(run.GetRange(i, j - i), page);

                // The app's renderer marks code blocks with their language even when no label is printed.
                if (_markers.TakeCodeLanguage(page.Number, line.Top, line.Size) is { } language)
                {
                    code.Language = language;
                }

                result.Add((line.Top, code));
                i = j;
                continue;
            }

            if (TryHeading(run, i, nested, out var heading, out var count))
            {
                result.Add((line.Top, heading));
                i += count;
                continue;
            }

            if (Lists && ListDetector.TryParse(line, next, page) is not null)
            {
                var j = BuildList(run, i, page, out var list);
                if (list is not null && j > i)
                {
                    result.Add((line.Top, list));
                    i = j;
                    continue;
                }
            }

            var end = i + 1;
            while (end < run.Count && ContinuesParagraph(run, i, end, page))
            {
                end++;
            }

            var lines = run.GetRange(i, end - i);
            var paragraph = new ParagraphBlock();
            paragraph.Inlines.AddRange(InlineBuilder.Build(lines.Select(l => (IReadOnlyList<PdfWord>)l.Words), _settings.MergeHyphenation));
            ParagraphSources[paragraph] = lines;

            result.Add((line.Top, paragraph));
            i = end;
        }

        return result;
    }

    private double ParagraphLimit(PdfLine a, PdfLine b) => Math.Max(a.Size, b.Size) * _stats.ParagraphGapFactor;

    private static bool HasScripts(PdfLine line) => line.Words.Any(w => w.Script != ScriptPosition.Normal);

    private bool SameCodeBlock(List<PdfLine> run, int start, int j)
    {
        var prev = run[j - 1];
        var cur = run[j];
        if (prev.BoxId != cur.BoxId)
        {
            // Allow a language label (outside the box) directly above the boxed code.
            var isLabel = j - 1 == start && prev.BoxId is null && prev.Words.Count == 1;
            if (!isLabel)
            {
                return false;
            }
        }

        var delta = prev.Baseline - cur.Baseline;
        return delta > 0 && delta <= Math.Max(prev.Size, cur.Size) * 4.2;
    }

    private bool ContinuesParagraph(List<PdfLine> run, int start, int j, PageContent page)
    {
        var prev = run[j - 1];
        var cur = run[j];
        if (cur.BoxId != prev.BoxId)
        {
            return false;
        }

        if (Code && CodeBlockBuilder.IsCodeLine(cur) != CodeBlockBuilder.IsCodeLine(prev))
        {
            return false;
        }

        if (Math.Abs(cur.Size - prev.Size) > Math.Max(1.0, prev.Size * 0.1))
        {
            return false;
        }

        var delta = prev.Baseline - cur.Baseline;
        var limit = ParagraphLimit(prev, cur);
        if (HasScripts(prev) || HasScripts(cur))
        {
            // Superscripts and subscripts push lines apart.
            limit += Math.Max(prev.Size, cur.Size) * 0.45;
        }

        // Inside a paragraph the pitch is regular: a clearly larger gap than the paragraph's own first gap starts a new one.
        if (j - 1 > start)
        {
            var firstGap = run[start].Baseline - run[start + 1].Baseline;
            if (firstGap > 0 && !HasScripts(prev) && !HasScripts(cur) && !HasScripts(run[start]) && !HasScripts(run[start + 1]))
            {
                limit = Math.Min(limit, firstGap * 1.3);
            }
        }

        if (delta <= 0 || delta > limit)
        {
            return false;
        }

        if (_headings.LevelForSize(cur) > 0 && _headings.LevelForSize(prev) == 0)
        {
            return false;
        }

        var centred = Math.Abs((cur.Left + cur.Right) / 2 - (prev.Left + prev.Right) / 2) <= 2 && Math.Abs(cur.Left - prev.Left) > 4;
        if (cur.Left > prev.Left + 4 && !centred)
        {
            // A hanging indent (footnote, bibliography entry): later lines align with the first line's second word.
            var first = run[start];
            var hanging = first.Words.Count > 1 && Math.Abs(cur.Left - first.Words[1].Left) <= 2 && first.Words[1].Left - first.Words[0].Right > first.Size * 0.5;
            if (!hanging)
            {
                return false;
            }
        }

        if (j - 1 > start && cur.Left < prev.Left - 4 && !centred)
        {
            return false;
        }

        var next = j + 1 < run.Count ? run[j + 1] : null;
        return !(Lists && ListDetector.TryParse(cur, next, page) is not null);
    }

    // --------------------------------------------------------------- headings

    private bool TryHeading(List<PdfLine> run, int i, bool nested, out HeadingBlock heading, out int count)
    {
        heading = null!;
        count = 0;
        var line = run[i];
        var level = 0;

        if (_headings.HasOutline)
        {
            var take = 1;
            var lines = new List<PdfLine> { line };
            while (true)
            {
                var text = InlineBuilder.PlainText(lines.Select(l => (IReadOnlyList<PdfWord>)l.Words), _settings.MergeHyphenation);
                if (_headings.MatchOutline(text, line.Page) is { } outlineLevel)
                {
                    level = outlineLevel;
                    count = take;
                    break;
                }

                if (take >= 3 || i + take >= run.Count || !_headings.IsOutlinePrefix(text, line.Page))
                {
                    break;
                }

                var nextLine = run[i + take];
                if (Math.Abs(nextLine.Size - line.Size) > 0.5 || lines[^1].Baseline - nextLine.Baseline > line.Size * 1.8)
                {
                    break;
                }

                lines.Add(nextLine);
                take++;
            }
        }

        if (level == 0)
        {
            var sizeLevel = _headings.LevelForSize(line);
            if (sizeLevel > 0)
            {
                level = sizeLevel;
                count = 1;
                while (i + count < run.Count && count < 4)
                {
                    var prev = run[i + count - 1];
                    var cur = run[i + count];
                    if (_headings.LevelForSize(cur) != sizeLevel || Math.Abs(cur.Size - line.Size) > 0.5
                        || prev.Baseline - cur.Baseline > line.Size * 1.8 || prev.Baseline <= cur.Baseline || cur.BoxId != line.BoxId)
                    {
                        break;
                    }

                    count++;
                }
            }
            else if (!nested && line.BoxId is null && _headings.IsBoldHeadingCandidate(line) && Standalone(run, i))
            {
                level = _headings.BoldLevel;
                count = 1;
            }
        }

        if (level == 0)
        {
            return false;
        }

        var headingText = InlineBuilder.PlainText(run.GetRange(i, count).Select(l => (IReadOnlyList<PdfWord>)l.Words), _settings.MergeHyphenation);
        if (headingText.Length == 0 || headingText.Length > 300)
        {
            return false;
        }

        if (_settings.StripHeadingNumbers)
        {
            headingText = StripHeadingNumber(headingText);
        }

        heading = new HeadingBlock(level, headingText);
        return true;
    }

    private bool Standalone(List<PdfLine> run, int i)
    {
        var line = run[i];
        if (i + 1 < run.Count)
        {
            var next = run[i + 1];
            var delta = line.Baseline - next.Baseline;
            if (next.IsBold && delta > 0 && delta <= ParagraphLimit(line, next))
            {
                return false;
            }

            if (Lists && ListDetector.TryParse(line, next, null) is not null)
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ lists

    private sealed class ListLevel
    {
        public required ListBlock List { get; init; }
        public double MarkerLeft { get; init; }
        public double TextLeft { get; set; }
        public ListItem? Item { get; set; }
        public List<IReadOnlyList<PdfWord>>? Paragraph { get; set; }
        public PdfLine? ParagraphLast { get; set; }
    }

    private int BuildList(List<PdfLine> run, int start, PageContent page, out ListBlock? root)
    {
        root = null;
        var levels = new List<ListLevel>();
        PdfLine? prevLine = null;
        var j = start;

        void Flush(ListLevel level)
        {
            if (level.Paragraph is { Count: > 0 } && level.Item is not null)
            {
                var p = new ParagraphBlock();
                p.Inlines.AddRange(InlineBuilder.Build(level.Paragraph, _settings.MergeHyphenation));
                level.Item.Blocks.Add(p);
            }

            level.Paragraph = null;
            level.ParagraphLast = null;
        }

        while (j < run.Count)
        {
            var line = run[j];
            var next = j + 1 < run.Count ? run[j + 1] : null;
            if (prevLine is not null)
            {
                var delta = prevLine.Baseline - line.Baseline;
                if (line.BoxId != prevLine.BoxId || delta <= 0 || delta > Math.Max(line.Size, prevLine.Size) * _stats.PitchFactor * 2.2)
                {
                    break;
                }

                if ((Code && CodeBlockBuilder.IsCodeLine(line)) || _headings.LevelForSize(line) > 0)
                {
                    break;
                }
            }

            var marker = ListDetector.TryParse(line, next, page);
            if (marker is not null)
            {
                if (levels.Count == 0)
                {
                    root = new ListBlock { Ordered = marker.Ordered, Start = marker.Ordered ? marker.Number : 1 };
                    levels.Add(new ListLevel { List = root, MarkerLeft = marker.MarkerLeft, TextLeft = marker.TextLeft });
                }
                else
                {
                    while (levels.Count > 0 && marker.MarkerLeft < levels[^1].MarkerLeft - 4)
                    {
                        Flush(levels[^1]);
                        levels.RemoveAt(levels.Count - 1);
                    }

                    if (levels.Count == 0)
                    {
                        break;
                    }

                    var top = levels[^1];
                    if (marker.MarkerLeft > top.MarkerLeft + 4 && top.Item is not null)
                    {
                        Flush(top);
                        var nestedList = new ListBlock { Ordered = marker.Ordered, Start = marker.Ordered ? marker.Number : 1 };
                        top.Item.Blocks.Add(nestedList);
                        levels.Add(new ListLevel { List = nestedList, MarkerLeft = marker.MarkerLeft, TextLeft = marker.TextLeft });
                    }
                    else if (top.List.Ordered != marker.Ordered)
                    {
                        if (levels.Count == 1)
                        {
                            break;
                        }

                        Flush(top);
                        levels.RemoveAt(levels.Count - 1);
                        var parent = levels[^1];
                        var sibling = new ListBlock { Ordered = marker.Ordered, Start = marker.Ordered ? marker.Number : 1 };
                        parent.Item!.Blocks.Add(sibling);
                        levels.Add(new ListLevel { List = sibling, MarkerLeft = marker.MarkerLeft, TextLeft = marker.TextLeft });
                    }
                }

                var level = levels[^1];
                Flush(level);
                var item = new ListItem { Checked = marker.Checked };
                level.List.Items.Add(item);
                level.Item = item;
                level.TextLeft = marker.TextLeft;
                level.Paragraph = [line.Words.Skip(marker.SkipWords).ToList()];
                level.ParagraphLast = line;
                prevLine = line;
                j++;
                continue;
            }

            // Continuation line: aligned with the text of an open item.
            var matchIndex = -1;
            for (var k = levels.Count - 1; k >= 0; k--)
            {
                if (Math.Abs(line.Left - levels[k].TextLeft) <= 3)
                {
                    matchIndex = k;
                    break;
                }
            }

            if (matchIndex < 0)
            {
                break;
            }

            while (levels.Count - 1 > matchIndex)
            {
                Flush(levels[^1]);
                levels.RemoveAt(levels.Count - 1);
            }

            var target = levels[matchIndex];
            if (target.Item is null)
            {
                break;
            }

            if (target.Paragraph is not null && target.ParagraphLast is not null
                && target.ParagraphLast.Baseline - line.Baseline <= ParagraphLimit(target.ParagraphLast, line))
            {
                target.Paragraph.Add(line.Words);
            }
            else
            {
                Flush(target);
                target.Paragraph = [line.Words];
            }

            target.ParagraphLast = line;
            prevLine = line;
            j++;
        }

        foreach (var level in levels)
        {
            Flush(level);
        }

        return root is null ? start : j;
    }

    // ---------------------------------------------------------- bar groups

    private Block BuildBarGroup(List<PdfLine> lines, PageContent page)
    {
        var first = lines[0];
        var title = first.Text.Trim();
        var kind = KindFromTitle(title);
        if (kind is not null && (first.BoldFraction >= 0.5 || first.Words.Count == 1))
        {
            var callout = new CalloutBlock { Kind = kind.Value };
            var bare = title.TrimEnd(':').Trim();
            if (!string.Equals(bare, kind.Value.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                callout.Title = bare;
            }

            callout.Blocks.AddRange(BuildFlow(lines.Skip(1).ToList(), page, nested: true).Select(t => t.Block));
            return callout;
        }

        var boxed = lines.Any(l => l.BoxId is not null);
        if (boxed && first.IsBold && title.Length <= 60 && lines.Count >= 2)
        {
            var callout = new CalloutBlock { Kind = CalloutKind.Note, Title = title };
            callout.Blocks.AddRange(BuildFlow(lines.Skip(1).ToList(), page, nested: true).Select(t => t.Block));
            return callout;
        }

        var quote = new QuoteBlock();
        quote.Blocks.AddRange(BuildFlow(lines, page, nested: true).Select(t => t.Block));
        return quote;
    }

    internal static CalloutKind? KindFromTitle(string title)
    {
        var t = title.TrimEnd(':', '.').Trim().ToLowerInvariant();
        return t switch
        {
            "note" or "info" or "information" => CalloutKind.Note,
            "tip" or "hint" => CalloutKind.Tip,
            "important" => CalloutKind.Important,
            "warning" or "attention" => CalloutKind.Warning,
            "caution" or "danger" => CalloutKind.Caution,
            _ => null,
        };
    }

    // ----------------------------------------------------------------- tables

    private TableBlock ToTableBlock(DetectedTable table)
    {
        var block = new TableBlock();
        foreach (var row in table.Rows)
        {
            var tr = new TableRow { IsHeader = row.IsHeader };
            foreach (var cellWords in row.Cells)
            {
                var cell = new TableCell();
                if (cellWords.Count > 0)
                {
                    var p = new ParagraphBlock();
                    var inlines = InlineBuilder.Build(SplitIntoLines(cellWords), _settings.MergeHyphenation);

                    // Header cells are bold by the table style, not by emphasis in the text.
                    p.Inlines.AddRange(row.IsHeader ? InlineBuilder.StripStyle(inlines, InlineStyle.Bold) : inlines);
                    cell.Blocks.Add(p);
                }

                tr.Cells.Add(cell);
            }

            block.Rows.Add(tr);
        }

        block.Alignments.AddRange(table.Alignments);
        return block;
    }

    private static List<IReadOnlyList<PdfWord>> SplitIntoLines(List<PdfWord> words)
    {
        var result = new List<IReadOnlyList<PdfWord>>();
        List<PdfWord>? current = null;
        foreach (var word in words)
        {
            if (current is null || Math.Abs(current[^1].Baseline - word.Baseline) > word.Size * 0.5)
            {
                current = [];
                result.Add(current);
            }

            current.Add(word);
        }

        foreach (var line in result)
        {
            ((List<PdfWord>)line).Sort((a, b) => a.Left.CompareTo(b.Left));
        }

        return result;
    }

    /// <summary>
    /// Removes one leading section number ("1", "1.2", "1.2.3", "1.2." optionally followed by ")" or ".") that a
    /// PDF generator put in front of a heading. The heading is never emptied.
    /// </summary>
    internal static string StripHeadingNumber(string text)
    {
        var m = HeadingNumber().Match(text);
        if (!m.Success)
        {
            return text;
        }

        var rest = text[m.Length..].Trim();
        return rest.Length == 0 ? text : rest;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\d{1,3}(?:\.\d{1,3})*\.?\)?\s+")]
    private static partial System.Text.RegularExpressions.Regex HeadingNumber();

    // ------------------------------------------------------- thematic breaks

    private static List<double> ThematicBreaks(PageContent page, List<PdfLine> lines, List<DetectedTable> tables)
    {
        var result = new List<double>();
        var zone = page.Height * HeaderFooterFilter.ZoneFraction;
        foreach (var rule in page.HorizontalRules.OrderByDescending(r => r.Y))
        {
            if (rule.Right - rule.Left < page.Width * 0.3 || rule.Y < zone || rule.Y > page.Height - zone)
            {
                continue;
            }

            if (result.Any(y => Math.Abs(y - rule.Y) < 3))
            {
                continue;
            }

            // Part of a table, a box border, an underline, or crossing text?
            var inTable = tables.Any(t => rule.Y <= t.Top + 4 && rule.Y >= t.Lines.Min(l => l.Bottom) - 8);
            var inBox = page.Boxes.Any(b => rule.Y >= b.Bottom - 1 && rule.Y <= b.Top + 1 && rule.Left >= b.Left - 1 && rule.Right <= b.Right + 1);
            var nearText = lines.Any(l => l.Right > rule.Left && l.Left < rule.Right
                && rule.Y >= l.Bottom - l.Size * 0.6 && rule.Y <= l.Top + 1);
            var otherRules = page.HorizontalRules.Count(r => Math.Abs(r.Left - rule.Left) < 4 && Math.Abs(r.Right - rule.Right) < 4 && Math.Abs(r.Y - rule.Y) > 3 && Math.Abs(r.Y - rule.Y) < 60);
            if (inTable || inBox || nearText || otherRules >= 2)
            {
                continue;
            }

            // A break separates content: there must be kept text on this page both above and below the rule (rules
            // left over from filtered headers, footers, contents pages or cover decorations do not qualify), and it
            // must not hug a shaded shape such as a cover banner.
            var textAbove = lines.Any(l => !l.Removed && l.Bottom > rule.Y + 1);
            var textBelow = lines.Any(l => !l.Removed && l.Top < rule.Y - 1);
            var nearShape = page.Boxes.Any(b => rule.Right > b.Left && rule.Left < b.Right
                && (Math.Abs(rule.Y - b.Bottom) <= 4 || Math.Abs(rule.Y - b.Top) <= 4));
            if (!textAbove || !textBelow || nearShape)
            {
                continue;
            }

            result.Add(rule.Y);
        }

        return result;
    }
}
