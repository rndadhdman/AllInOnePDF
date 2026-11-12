using System.Globalization;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>
/// Turns superscript numbers plus the matching numbered notes at the end of a page (or of the document, after the
/// separator rule) back into footnotes: the notes move to <see cref="Document.Footnotes"/> and the superscripts
/// become <see cref="FootnoteReferenceInline"/>s. Labels are matched in order (1, 2, 3 …), so a superscript that is
/// not a note reference (x², a later "2" before note 2's reference) stays as it is.
/// </summary>
internal static partial class FootnoteRecovery
{
    public static int Apply(Document document, LayoutBuilder builder, double bodySize)
    {
        var blocks = document.Blocks;
        var references = SuperscriptNumbers(blocks);
        if (references.Count == 0)
        {
            return 0;
        }

        var labels = references.Select(r => r.Label).ToHashSet(StringComparer.Ordinal);
        var notes = new Dictionary<string, ParagraphBlock>(StringComparer.Ordinal);
        var remove = new HashSet<Block>(ReferenceEqualityComparer.Instance);
        var i = 0;
        while (i < blocks.Count)
        {
            if (NoteNumber(blocks[i]) is not { } first || !labels.Contains(first))
            {
                i++;
                continue;
            }

            var j = i;
            while (j < blocks.Count && NoteNumber(blocks[j]) is { } n && labels.Contains(n))
            {
                j++;
            }

            // Notes close a page (the next block is on a later page) or the document.
            var atEnd = j == blocks.Count || (blocks[j].SourcePage is { } nextPage && blocks[j - 1].SourcePage is { } lastPage && nextPage > lastPage);
            var afterRule = i > 0 && blocks[i - 1] is ThematicBreakBlock;
            var small = Enumerable.Range(i, j - i).All(k => blocks[k] is ParagraphBlock p && builder.ParagraphSources.TryGetValue(p, out var src) && src.All(l => l.Size < bodySize - 0.4));
            if (atEnd && (afterRule || small))
            {
                for (var k = i; k < j; k++)
                {
                    var p = (ParagraphBlock)blocks[k];
                    notes.TryAdd(NoteNumber(p)!, p);
                    remove.Add(p);
                }

                if (afterRule)
                {
                    remove.Add(blocks[i - 1]);
                }
            }

            i = j;
        }

        if (notes.Count == 0)
        {
            return 0;
        }

        // Pair each note with the first matching superscript after the previous pairing.
        var linked = new List<(StyledInline Mark, List<Inline> Owner, string Label)>();
        var next = 0;
        foreach (var label in notes.Keys.OrderBy(k => int.Parse(k, CultureInfo.InvariantCulture)))
        {
            for (var r = next; r < references.Count; r++)
            {
                if (references[r].Label == label && !remove.Contains(references[r].Block))
                {
                    linked.Add((references[r].Mark, references[r].Owner, label));
                    next = r + 1;
                    break;
                }
            }
        }

        if (linked.Count == 0)
        {
            return 0;
        }

        foreach (var (mark, owner, label) in linked)
        {
            var index = owner.IndexOf(mark);
            owner[index] = new FootnoteReferenceInline { Label = label };
            var paragraph = notes[label];
            StripNumber(paragraph);
            var footnote = new Footnote { Label = label };
            footnote.Blocks.Add(paragraph);
            document.Footnotes.Add(footnote);
        }

        // Notes whose reference was not found stay as text.
        var orphanNotes = notes.Where(n => !linked.Any(l => l.Label == n.Key)).Select(n => (Block)n.Value).ToHashSet(ReferenceEqualityComparer.Instance);
        blocks.RemoveAll(b => remove.Contains(b) && !orphanNotes.Contains(b));
        return linked.Count;
    }

    private sealed record Reference(StyledInline Mark, List<Inline> Owner, Block Block, string Label);

    /// <summary>Superscript runs holding only a 1-3 digit number, in document order.</summary>
    private static List<Reference> SuperscriptNumbers(IEnumerable<Block> blocks)
    {
        var result = new List<Reference>();

        void Walk(List<Inline> inlines, Block block)
        {
            foreach (var inline in inlines)
            {
                if (inline is StyledInline { Style: InlineStyle.Superscript } s && Number().IsMatch(Inline.ToPlainText(s.Inlines)))
                {
                    result.Add(new Reference(s, inlines, block, Inline.ToPlainText(s.Inlines).TrimStart('0') is { Length: > 0 } t ? t : "0"));
                }
                else if (inline is StyledInline or LinkInline)
                {
                    Walk(inline is StyledInline st ? st.Inlines : ((LinkInline)inline).Inlines, block);
                }
            }
        }

        foreach (var block in Block.EnumerateRecursive(blocks.ToList()))
        {
            if (block is InlineContainerBlock icb)
            {
                Walk(icb.Inlines, block);
            }
        }

        return result;
    }

    /// <summary>The note number a paragraph starts with ("1 Text", "1. Text", a superscript "1"), or null.</summary>
    private static string? NoteNumber(Block block)
    {
        if (block is not ParagraphBlock p || p.Inlines.Count == 0)
        {
            return null;
        }

        if (p.Inlines[0] is StyledInline { Style: InlineStyle.Superscript } s && Number().IsMatch(Inline.ToPlainText(s.Inlines)) && p.Inlines.Count > 1)
        {
            return Inline.ToPlainText(s.Inlines).TrimStart('0');
        }

        var m = Leading().Match(p.ToPlainText());
        return m.Success ? m.Groups[1].Value.TrimStart('0') : null;
    }

    private static void StripNumber(ParagraphBlock paragraph)
    {
        if (paragraph.Inlines[0] is StyledInline { Style: InlineStyle.Superscript })
        {
            paragraph.Inlines.RemoveAt(0);
            if (paragraph.Inlines.Count > 0 && paragraph.Inlines[0] is TextInline lead)
            {
                lead.Text = lead.Text.TrimStart();
            }

            return;
        }

        if (paragraph.Inlines[0] is TextInline t)
        {
            var m = Leading().Match(t.Text);
            if (m.Success)
            {
                t.Text = t.Text[m.Length..];
                if (t.Text.Length == 0)
                {
                    paragraph.Inlines.RemoveAt(0);
                }
            }
        }
    }

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^(\d{1,3})[.)]?\s+(?=\S)")]
    private static partial Regex Leading();
}
