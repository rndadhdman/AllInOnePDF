using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;

namespace AllInOnePdf.Core.Importers.Rtf;

/// <summary>Paragraph → block decisions, lists, tables, page breaks and the final clean-up passes.</summary>
internal sealed partial class RtfDocumentBuilder
{
    private readonly Dictionary<int, long> _sizeChars = [];
    private readonly Dictionary<ParagraphBlock, RtfParagraphBuffer> _paragraphStats = [];
    private readonly Dictionary<string, Block> _bookmarkTargets = new(StringComparer.Ordinal);
    private int _explicitHeadings;
    private bool _sectionPending;
    private bool _sectionBreaksPage = true;

    private readonly record struct ListPlacement(int Level, string Key, bool Ordered, int Start);

    // ----------------------------------------------------------------- paragraph end

    /// <summary>Ends the paragraph being read (<c>\par</c>, <c>\cell</c>, end of a footnote or of the document).</summary>
    private void EndParagraph(RtfParaState para, RtfCharState chars, bool forceCell = false)
    {
        var ctx = Ctx;
        var buffer = ctx.Paragraph;
        ctx.Paragraph = new RtfParagraphBuffer();

        if (forceCell || para.IsInTable)
        {
            var table = ctx.Table ??= new RtfTableBuilder();
            if (AddParagraph(table.Cell, buffer, para, topLevel: false) is null)
            {
                ctx.Paragraph.Bookmarks.AddRange(buffer.Bookmarks);
            }

            return;
        }

        if (ctx.Table is { IsEmpty: false })
        {
            FinishTable(ctx);
        }

        if (buffer.VisibleChars == 0)
        {
            ctx.Paragraph.Bookmarks.AddRange(buffer.Bookmarks);
            var open = ctx.Sink.OpenCode;
            if (open is not null && ctx.Sink.Blocks.Count > 0 && ctx.Sink.Blocks[^1] == open && IsMonospace(chars.Font))
            {
                open.Code += "\n";
            }
            else
            {
                ctx.Sink.OpenCode = null;
            }

            return;
        }

        if (ctx.IsBody)
        {
            if (_sectionPending)
            {
                _sectionPending = false;
                if (_sectionBreaksPage)
                {
                    AddPageBreak();
                }
            }

            if (para.PageBreakBefore)
            {
                AddPageBreak();
            }
        }

        if (AddParagraph(ctx.Sink, buffer, para, topLevel: ctx.IsBody) is null)
        {
            ctx.Paragraph.Bookmarks.AddRange(buffer.Bookmarks);
        }
    }

    /// <summary>Turns a finished paragraph into a block in <paramref name="sink"/>; returns the block that carries its bookmarks.</summary>
    private Block? AddParagraph(RtfBlockSink sink, RtfParagraphBuffer buffer, RtfParaState para, bool topLevel)
    {
        if (buffer.VisibleChars == 0)
        {
            return null;
        }

        var (role, styleLevel) = RoleOf(para.StyleId);
        var headingLevel = para.OutlineLevel is >= 0 and <= 8
            ? Math.Min(para.OutlineLevel + 1, 6)
            : role switch { RtfStyleRole.Heading => styleLevel, RtfStyleRole.Title => 1, _ => 0 };

        Block? produced;
        if (headingLevel > 0 && !HasOnlyImages(buffer))
        {
            var heading = new HeadingBlock { Level = headingLevel };
            heading.Inlines.AddRange(BuildInlines(buffer.Pieces, InlineStyle.Bold));
            if (heading.Inlines.Count == 0)
            {
                return null;
            }

            sink.Add(heading);
            _explicitHeadings++;
            produced = heading;
        }
        else if (_settings.DetectCode && (role == RtfStyleRole.Code || IsAllMonospace(buffer)) && !HasImages(buffer))
        {
            var code = CodeText(buffer);
            if (sink.OpenCode is { } open && sink.Blocks.Count > 0 && sink.Blocks[^1] == open)
            {
                open.Code += "\n" + code;
                produced = open;
            }
            else
            {
                var block = new CodeBlock { Code = code };
                sink.Add(block);
                sink.OpenCode = block;
                produced = block;
            }
        }
        else if (role == RtfStyleRole.Quote)
        {
            var p = NewParagraph(buffer, para, InlineStyle.None);
            if (p.Inlines.Count == 0)
            {
                return null;
            }

            if (sink.OpenQuote is { } quote && sink.Blocks.Count > 0 && sink.Blocks[^1] == quote)
            {
                quote.Blocks.Add(p);
            }
            else
            {
                quote = new QuoteBlock();
                quote.Blocks.Add(p);
                sink.Add(quote);
                sink.OpenQuote = quote;
            }

            produced = p;
        }
        else if (ListPlacementOf(buffer, para) is { } placement)
        {
            var p = NewParagraph(buffer, para, InlineStyle.None);
            p.Alignment = null;
            if (p.Inlines.Count == 0)
            {
                return null;
            }

            var item = AddListItem(sink, placement);
            item.Blocks.Add(p);
            produced = p;
        }
        else if (HasOnlyImages(buffer))
        {
            produced = null;
            foreach (var piece in buffer.Pieces.Where(x => x.Kind == RtfPieceKind.Image))
            {
                sink.Add(piece.Image!);
                produced ??= piece.Image;
            }
        }
        else
        {
            var p = NewParagraph(buffer, para, InlineStyle.None);
            if (p.Inlines.Count == 0)
            {
                return null;
            }

            if (role == RtfStyleRole.Subtitle)
            {
                var italic = new StyledInline { Style = InlineStyle.Italic };
                italic.Inlines.AddRange(p.Inlines);
                p.Inlines.Clear();
                p.Inlines.Add(italic);
            }
            else if (topLevel)
            {
                _paragraphStats[p] = buffer;
            }

            sink.Add(p);
            produced = p;
        }

        sink.Alignments.Add(para.Alignment);
        if (produced is not null)
        {
            foreach (var name in buffer.Bookmarks)
            {
                var slug = DocumentHtmlRenderer.Slugify(name);
                produced.Id ??= slug;
                _bookmarkTargets.TryAdd(slug, produced);
            }
        }

        return produced;
    }

    private ParagraphBlock NewParagraph(RtfParagraphBuffer buffer, RtfParaState para, InlineStyle drop)
    {
        var p = new ParagraphBlock();
        p.Inlines.AddRange(BuildInlines(buffer.Pieces, drop));
        if (para.Alignment is TextAlignment.Center or TextAlignment.Right)
        {
            p.Alignment = para.Alignment;
        }

        return p;
    }

    private (RtfStyleRole Role, int Level) RoleOf(int styleId, int depth = 0)
    {
        if (_roles.TryGetValue(styleId, out var cached))
        {
            return cached;
        }

        if (!_styles.TryGetValue(styleId, out var style) || depth > 10)
        {
            return (RtfStyleRole.None, 0);
        }

        var name = style.Name.Split(',')[0].Trim().ToLowerInvariant();
        (RtfStyleRole, int) role;
        if (style.OutlineLevel is >= 0 and <= 8)
        {
            role = (RtfStyleRole.Heading, Math.Min(style.OutlineLevel + 1, 6));
        }
        else if (name.StartsWith("heading ", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 9)
        {
            role = (RtfStyleRole.Heading, Math.Min(n, 6));
        }
        else if (name == "title")
        {
            role = (RtfStyleRole.Title, 1);
        }
        else if (name == "subtitle")
        {
            role = (RtfStyleRole.Subtitle, 0);
        }
        else if (name is "quote" or "intense quote" or "block text")
        {
            role = (RtfStyleRole.Quote, 0);
        }
        else if (name.Contains("code", StringComparison.Ordinal)
            || (name is "html preformatted" or "plain text" && IsMonospace(style.Font)))
        {
            role = (RtfStyleRole.Code, 0);
        }
        else if (style.BasedOn >= 0 && style.BasedOn != styleId)
        {
            role = RoleOf(style.BasedOn, depth + 1);
        }
        else
        {
            role = (RtfStyleRole.None, 0);
        }

        _roles[styleId] = role;
        return role;
    }

    // ----------------------------------------------------------------- lists

    private ListPlacement? ListPlacementOf(RtfParagraphBuffer buffer, RtfParaState para)
    {
        if (para.ListOverride > 0)
        {
            var key = "ls" + para.ListOverride.ToString(CultureInfo.InvariantCulture);
            var level = para.ListLevel;
            if (_overrides.TryGetValue(para.ListOverride, out var listId) && _lists.TryGetValue(listId, out var list) && level < list.Levels.Count)
            {
                var info = list.Levels[level];
                return new ListPlacement(level, key, info.Ordered, info.StartAt);
            }

            return new ListPlacement(level, key, false, 1);
        }

        if (para.Legacy != LegacyListKind.None || buffer.LegacyMarker)
        {
            var marker = buffer.MarkerText.ToString().Trim();
            var numbered = para.Legacy == LegacyListKind.Numbered
                || (para.Legacy == LegacyListKind.None && marker.Length > 0 && char.IsLetterOrDigit(marker[0]));
            var start = para.Legacy != LegacyListKind.None ? para.LegacyStart : LeadingNumber(marker) ?? 1;
            return new ListPlacement(para.LegacyLevel, "pn", numbered, start);
        }

        if (_settings.DetectLists && para.FirstIndent < 0 && StripTypedMarker(buffer) is { } typed)
        {
            return new ListPlacement(0, "typed", typed.Ordered, typed.Start);
        }

        return null;
    }

    private static int? LeadingNumber(string marker)
    {
        var digits = 0;
        while (digits < marker.Length && char.IsAsciiDigit(marker[digits]))
        {
            digits++;
        }

        return digits > 0 && int.TryParse(marker.AsSpan(0, digits), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>
    /// A paragraph that begins with a typed bullet ("•", "-") or number ("1.", "2)", "(3)", "a.") followed by a tab
    /// or space: removes the marker and says what kind of list it starts.
    /// </summary>
    private static (bool Ordered, int Start)? StripTypedMarker(RtfParagraphBuffer buffer)
    {
        var prefix = new StringBuilder();
        foreach (var piece in buffer.Pieces)
        {
            if (piece.Kind != RtfPieceKind.Text || prefix.Length >= 16)
            {
                break;
            }

            prefix.Append(piece.Text);
        }

        var match = TypedMarker().Match(prefix.ToString());
        if (!match.Success)
        {
            return null;
        }

        var ordered = !match.Groups["bullet"].Success;
        var start = 1;
        if (match.Groups["num"].Success)
        {
            start = int.Parse(match.Groups["num"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
        else if (match.Groups["letter"].Success)
        {
            start = char.ToLowerInvariant(match.Groups["letter"].Value[0]) - 'a' + 1;
        }

        var remove = match.Length;
        foreach (var piece in buffer.Pieces)
        {
            if (remove == 0 || piece.Kind != RtfPieceKind.Text)
            {
                break;
            }

            var take = Math.Min(remove, piece.Text.Length);
            piece.Text.Remove(0, take);
            remove -= take;
        }

        buffer.Pieces.RemoveAll(p => p.Kind == RtfPieceKind.Text && p.Text.Length == 0);
        return (ordered, start);
    }

    [GeneratedRegex(@"^[ \t]*(?:(?<bullet>[•·‣▪◦■●\-–])|(?<num>\d{1,3})[.)]|\((?<num>\d{1,3})\)|(?<letter>[a-zA-Z])[.)])[\t ]+")]
    private static partial Regex TypedMarker();

    private static ListItem AddListItem(RtfBlockSink sink, ListPlacement placement)
    {
        sink.OpenCode = null;
        sink.OpenQuote = null;
        var level = Math.Min(placement.Level, sink.Lists.Count);
        while (sink.Lists.Count > level + 1)
        {
            sink.Lists.RemoveAt(sink.Lists.Count - 1);
        }

        if (sink.Lists.Count == level + 1
            && (!string.Equals(sink.Lists[level].Key, placement.Key, StringComparison.Ordinal) || sink.Lists[level].List.Ordered != placement.Ordered))
        {
            sink.Lists.RemoveAt(level);
        }

        if (sink.Lists.Count == level)
        {
            var list = new ListBlock { Ordered = placement.Ordered, Start = placement.Ordered ? placement.Start : 1 };
            if (level == 0)
            {
                sink.Blocks.Add(list);
            }
            else
            {
                var parent = sink.Lists[level - 1];
                parent.Item ??= NewItem(parent.List);
                parent.Item.Blocks.Add(list);
            }

            sink.Lists.Add(new RtfListFrame { List = list, Key = placement.Key });
        }

        var frame = sink.Lists[level];
        var item = NewItem(frame.List);
        frame.Item = item;
        return item;
    }

    private static ListItem NewItem(ListBlock list)
    {
        var item = new ListItem();
        list.Items.Add(item);
        return item;
    }

    // ----------------------------------------------------------------- inlines

    private static bool HasImages(RtfParagraphBuffer buffer) => buffer.Pieces.Exists(p => p.Kind == RtfPieceKind.Image);

    private static bool HasOnlyImages(RtfParagraphBuffer buffer) =>
        HasImages(buffer) && buffer.Pieces.TrueForAll(p => p.Kind is RtfPieceKind.Image or RtfPieceKind.Break
            || (p.Kind == RtfPieceKind.Text && string.IsNullOrWhiteSpace(p.Text.ToString())));

    private static bool IsAllMonospace(RtfParagraphBuffer buffer)
    {
        var any = false;
        foreach (var piece in buffer.Pieces)
        {
            if (piece.Kind == RtfPieceKind.Text)
            {
                if (string.IsNullOrWhiteSpace(piece.Text.ToString()))
                {
                    continue;
                }

                if (!piece.Monospace)
                {
                    return false;
                }

                any = true;
            }
        }

        return any;
    }

    private static string CodeText(RtfParagraphBuffer buffer)
    {
        var sb = new StringBuilder();
        foreach (var piece in buffer.Pieces)
        {
            switch (piece.Kind)
            {
                case RtfPieceKind.Text:
                    sb.Append(piece.Text);
                    break;
                case RtfPieceKind.Break:
                    sb.Append('\n');
                    break;
                case RtfPieceKind.FootnoteReference:
                    sb.Append('[').Append(piece.Label).Append(']');
                    break;
            }
        }

        return sb.ToString().Replace(' ', ' ');
    }

    private List<Inline> BuildInlines(List<RtfPiece> pieces, InlineStyle drop)
    {
        var result = new List<Inline>();
        LinkInline? link = null;
        var linkId = 0;
        foreach (var piece in pieces)
        {
            List<Inline> target;
            if (piece.LinkId > 0 && piece.LinkId <= _links.Count)
            {
                if (link is null || linkId != piece.LinkId)
                {
                    link = new LinkInline { Url = _links[piece.LinkId - 1] };
                    linkId = piece.LinkId;
                    result.Add(link);
                }

                target = link.Inlines;
            }
            else
            {
                link = null;
                linkId = 0;
                target = result;
            }

            switch (piece.Kind)
            {
                case RtfPieceKind.Text:
                    AddText(target, piece, drop);
                    break;
                case RtfPieceKind.Break:
                    target.Add(new LineBreakInline());
                    break;
                case RtfPieceKind.FootnoteReference:
                    target.Add(new FootnoteReferenceInline { Label = piece.Label! });
                    break;
                case RtfPieceKind.Image:
                    target.Add(new ImageInline { Source = piece.Image!.Source });
                    break;
            }
        }

        TrimEdges(result);
        return result;
    }

    private void AddText(List<Inline> target, RtfPiece piece, InlineStyle drop)
    {
        var text = piece.Text.ToString();
        if (text.Contains('\t', StringComparison.Ordinal))
        {
            text = TabRun().Replace(text, " ");
        }

        var style = piece.Style & ~drop;
        if (_settings.DetectCode && piece.Monospace && !string.IsNullOrWhiteSpace(text))
        {
            var core = text.Trim();
            var lead = text[..(text.Length - text.TrimStart().Length)];
            var trail = text[(lead.Length + core.Length)..];
            if (lead.Length > 0)
            {
                target.Add(Styled(style, lead));
            }

            Inline code = new CodeInline(core);
            target.Add(style == InlineStyle.None ? code : new StyledInline(style, code));
            if (trail.Length > 0)
            {
                target.Add(Styled(style, trail));
            }

            return;
        }

        target.Add(Styled(style, text));
    }

    private static Inline Styled(InlineStyle style, string text) =>
        style == InlineStyle.None ? new TextInline(text) : new StyledInline(style, text);

    [GeneratedRegex(@"[ ]*\t[\t ]*")]
    private static partial Regex TabRun();

    /// <summary>Removes leading/trailing white space and trailing line breaks of a paragraph.</summary>
    private static void TrimEdges(List<Inline> inlines)
    {
        while (inlines.Count > 0)
        {
            var first = inlines[0];
            if (EdgeText(first, fromStart: true) is { } t)
            {
                t.Text = t.Text.TrimStart(' ', '\t');
                if (t.Text.Length > 0)
                {
                    break;
                }

                RemoveEdge(inlines, 0);
                continue;
            }

            break;
        }

        while (inlines.Count > 0)
        {
            var last = inlines[^1];
            if (last is LineBreakInline)
            {
                inlines.RemoveAt(inlines.Count - 1);
                continue;
            }

            if (EdgeText(last, fromStart: false) is { } t)
            {
                t.Text = t.Text.TrimEnd(' ', '\t');
                if (t.Text.Length > 0)
                {
                    break;
                }

                RemoveEdge(inlines, inlines.Count - 1);
                continue;
            }

            break;
        }
    }

    private static TextInline? EdgeText(Inline inline, bool fromStart) => inline switch
    {
        TextInline t => t,
        StyledInline { Inlines.Count: > 0 } s => EdgeText(fromStart ? s.Inlines[0] : s.Inlines[^1], fromStart),
        _ => null,
    };

    private static void RemoveEdge(List<Inline> inlines, int index)
    {
        if (inlines[index] is StyledInline { Inlines.Count: > 1 } s)
        {
            // Only the edge child is empty; the trim loop then sees the next child.
            var childIndex = index == 0 ? 0 : s.Inlines.Count - 1;
            s.Inlines.RemoveAt(childIndex);
            return;
        }

        inlines.RemoveAt(index);
    }

    // ----------------------------------------------------------------- tables and breaks

    private void EndCell()
    {
        var table = Ctx.Table ??= new RtfTableBuilder();
        EndParagraph(_state.Para, _state.Char, forceCell: true);
        table.EndCell();
    }

    private void EndRow()
    {
        var table = Ctx.Table ??= new RtfTableBuilder();
        if (Ctx.Paragraph.VisibleChars > 0)
        {
            EndParagraph(_state.Para, _state.Char, forceCell: true);
        }

        table.EndRow();
    }

    private static void FinishTable(RtfContext ctx)
    {
        var builder = ctx.Table;
        ctx.Table = null;
        if (builder?.Build() is { } table)
        {
            ctx.Sink.Add(table);
        }
    }

    private void PageBreak()
    {
        if (Ctx.Paragraph.VisibleChars > 0)
        {
            EndParagraph(_state.Para, _state.Char);
        }

        if (Ctx.IsBody && !_state.Para.IsInTable)
        {
            AddPageBreak();
        }
    }

    private void AddPageBreak()
    {
        if (!_settings.PageBreakMarkers)
        {
            return;
        }

        var ctx = _contexts[0];
        if (ctx.Table is { IsEmpty: false })
        {
            FinishTable(ctx);
        }

        var blocks = ctx.Sink.Blocks;
        if (blocks.Count > 0 && blocks[^1] is not PageBreakBlock)
        {
            ctx.Sink.Add(new PageBreakBlock());
        }
    }

    // ----------------------------------------------------------------- finish

    private void Finish()
    {
        var body = _contexts[0];
        if (body.Table is not null)
        {
            FinishTable(body);
        }

        DetectHeadings(body.Sink.Blocks);

        var blocks = body.Sink.Blocks;
        while (blocks.Count > 0 && blocks[^1] is PageBreakBlock)
        {
            blocks.RemoveAt(blocks.Count - 1);
        }

        _doc.Blocks.AddRange(blocks);

        var all = _doc.EnumerateAllBlocks().Concat(_doc.Footnotes.SelectMany(f => Block.EnumerateRecursive(f.Blocks))).ToList();
        foreach (var code in all.OfType<CodeBlock>())
        {
            code.Code = code.Code.TrimEnd('\n');
        }

        foreach (var container in all.OfType<InlineContainerBlock>())
        {
            foreach (var link in Links(container.Inlines))
            {
                if (link.IsInternal && _bookmarkTargets.TryGetValue(link.Url[1..], out var target) && target.Id is { } id && id != link.Url[1..])
                {
                    link.Url = "#" + id;
                }
            }
        }

        AddWarnings();
    }

    private static IEnumerable<LinkInline> Links(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is LinkInline link)
            {
                yield return link;
            }

            foreach (var nested in Links(inline.Children))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Without heading styles or outline levels, short top-level paragraphs that are all bold or clearly larger
    /// than the body text become headings, ranked by size (at most three levels).
    /// </summary>
    private void DetectHeadings(List<Block> blocks)
    {
        if (!_settings.DetectHeadings || _explicitHeadings > 0 || _paragraphStats.Count == 0)
        {
            return;
        }

        var bodySize = _sizeChars.Count == 0 ? 24 : _sizeChars.MaxBy(kv => kv.Value).Key;
        var paragraphs = blocks.OfType<ParagraphBlock>().Where(_paragraphStats.ContainsKey).ToList();
        var candidates = new List<(ParagraphBlock Paragraph, int Size, bool BySize)>();
        foreach (var p in paragraphs)
        {
            var stats = _paragraphStats[p];
            var text = p.ToPlainText().Trim();
            if (text.Length is 0 or > 120 || text[^1] is '.' or ',' or ';' or ':' || !text.Any(char.IsLetter))
            {
                continue;
            }

            if (p.Inlines.Exists(i => i is ImageInline or FootnoteReferenceInline or LineBreakInline))
            {
                continue;
            }

            var bySize = stats.MinFontSize >= bodySize * 1.25;
            if (bySize || (stats.AllBold && stats.MinFontSize >= bodySize))
            {
                candidates.Add((p, stats.MinFontSize, bySize));
            }
        }

        // A document that is mostly bold paragraphs uses bold for emphasis, not for headings.
        if (paragraphs.Count > 2 && candidates.Count(c => !c.BySize) * 2 > paragraphs.Count)
        {
            candidates.RemoveAll(c => !c.BySize);
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var sizes = candidates.Select(c => c.Size).Distinct().OrderDescending().ToList();
        foreach (var (paragraph, size, _) in candidates)
        {
            var index = blocks.IndexOf(paragraph);
            if (index < 0)
            {
                continue;
            }

            var heading = new HeadingBlock { Level = Math.Min(sizes.IndexOf(size) + 1, 3), Id = paragraph.Id };
            heading.Inlines.AddRange(WithoutBold(paragraph.Inlines));
            blocks[index] = heading;
            foreach (var key in _bookmarkTargets.Where(kv => kv.Value == paragraph).Select(kv => kv.Key).ToList())
            {
                _bookmarkTargets[key] = heading;
            }
        }
    }

    private static List<Inline> WithoutBold(IEnumerable<Inline> inlines)
    {
        var result = new List<Inline>();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case StyledInline s:
                    var children = WithoutBold(s.Inlines);
                    var style = s.Style & ~InlineStyle.Bold;
                    if (style == InlineStyle.None)
                    {
                        result.AddRange(children);
                    }
                    else
                    {
                        var copy = new StyledInline { Style = style };
                        copy.Inlines.AddRange(children);
                        result.Add(copy);
                    }

                    break;
                case LinkInline l:
                    var link = new LinkInline { Url = l.Url, Title = l.Title };
                    link.Inlines.AddRange(WithoutBold(l.Inlines));
                    result.Add(link);
                    break;
                default:
                    result.Add(inline);
                    break;
            }
        }

        return result;
    }

    private void AddWarnings()
    {
        var w = _doc.ImportWarnings;
        if (_tokenizer.Unbalanced)
        {
            w.Add("The RTF file is truncated or its braces do not match; the text that could be read was kept.");
        }

        if (_skippedDrawings > 0)
        {
            w.Add(_skippedDrawings == 1
                ? "1 drawing in a format that cannot be converted (WMF/EMF) was left out."
                : string.Create(CultureInfo.InvariantCulture, $"{_skippedDrawings} drawings in a format that cannot be converted (WMF/EMF) were left out."));
        }

        if (_skippedObjects > 0)
        {
            w.Add(_skippedObjects == 1
                ? "1 embedded object (a chart, equation or other OLE content) was left out."
                : string.Create(CultureInfo.InvariantCulture, $"{_skippedObjects} embedded objects (charts, equations or other OLE content) were left out."));
        }

        if (_tooLargeImages > 0)
        {
            w.Add(string.Create(CultureInfo.InvariantCulture,
                $"{_tooLargeImages} {(_tooLargeImages == 1 ? "picture" : "pictures")} larger than {_settings.MaxImageMb} MB {(_tooLargeImages == 1 ? "was" : "were")} left out."));
        }

        foreach (var codePage in _missingCodePages)
        {
            w.Add(string.Create(CultureInfo.InvariantCulture,
                $"The document uses code page {codePage}, which is not available; it was read as Western (Windows-1252), so some characters may look wrong."));
        }
    }
}
