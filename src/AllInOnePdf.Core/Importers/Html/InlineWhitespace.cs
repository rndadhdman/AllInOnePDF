using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers.Html;

/// <summary>
/// HTML white-space processing across inline boundaries: text runs were already collapsed to single spaces;
/// this drops a space that follows another space (even inside a different styled run), trims the start and end
/// of the block and around hard line breaks, and removes runs that end up empty.
/// </summary>
internal static class InlineWhitespace
{
    private sealed class State
    {
        public bool SkipLeading = true;
        public TextInline? LastText;
    }

    public static void Normalize(List<Inline> inlines)
    {
        var state = new State();
        Walk(inlines, state);
        state.LastText?.Text = state.LastText.Text.TrimEnd(' ');
        Prune(inlines);

        while (inlines.Count > 0 && inlines[0] is LineBreakInline)
        {
            inlines.RemoveAt(0);
        }

        while (inlines.Count > 0 && inlines[^1] is LineBreakInline)
        {
            inlines.RemoveAt(inlines.Count - 1);
        }
    }

    /// <summary>True when the inlines carry something visible (text, code, images, math, footnote references).</summary>
    public static bool HasContent(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline t when t.Text.Trim().Length > 0:
                case CodeInline:
                case ImageInline:
                case MathInline:
                case FootnoteReferenceInline:
                case HtmlInline:
                    return true;
                case LineBreakInline:
                case TextInline:
                    break;
                default:
                    if (HasContent(inline.Children))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static void Walk(List<Inline> inlines, State state)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline t:
                    if (state.SkipLeading)
                    {
                        t.Text = t.Text.TrimStart(' ');
                    }

                    if (t.Text.Length > 0)
                    {
                        state.SkipLeading = t.Text[^1] == ' ';
                        state.LastText = t;
                    }

                    break;
                case LineBreakInline { IsHard: true }:
                    state.LastText?.Text = state.LastText.Text.TrimEnd(' ');
                    state.LastText = null;
                    state.SkipLeading = true;
                    break;
                case StyledInline s:
                    Walk(s.Inlines, state);
                    break;
                case LinkInline l:
                    Walk(l.Inlines, state);
                    break;
                default:
                    state.SkipLeading = false;
                    state.LastText = null;
                    break;
            }
        }
    }

    private static void Prune(List<Inline> inlines)
    {
        for (var i = inlines.Count - 1; i >= 0; i--)
        {
            switch (inlines[i])
            {
                case TextInline { Text.Length: 0 }:
                    inlines.RemoveAt(i);
                    break;
                case StyledInline s:
                    Prune(s.Inlines);
                    if (s.Inlines.Count == 0)
                    {
                        inlines.RemoveAt(i);
                    }

                    break;
                case LinkInline l:
                    Prune(l.Inlines);
                    break;
            }
        }
    }
}
