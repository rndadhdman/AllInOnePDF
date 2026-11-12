using System.Text;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>
/// Converts words (line by line) into inline runs: bold / italic spans become <see cref="StyledInline"/>, monospace
/// spans <see cref="CodeInline"/>, raised / lowered small text super- / subscript, words under a URI annotation
/// <see cref="LinkInline"/>. Line breaks become spaces; "exam-" + "ple" is merged into "example" when enabled.
/// </summary>
internal static class InlineBuilder
{
    private readonly record struct StyleKey(FontFlags Flags, ScriptPosition Script);

    private sealed class Token(string separator, string text, StyleKey style, string? link)
    {
        public string Separator { get; set; } = separator;
        public string Text { get; set; } = text;
        public StyleKey Style { get; } = style;
        public string? Link { get; } = link;
    }

    public static List<Inline> Build(IEnumerable<IReadOnlyList<PdfWord>> lines, bool mergeHyphenation)
    {
        var tokens = Tokenize(lines, mergeHyphenation);
        var result = new List<Inline>();
        var i = 0;
        while (i < tokens.Count)
        {
            var link = tokens[i].Link;
            var j = i + 1;
            while (j < tokens.Count && string.Equals(tokens[j].Link, link, StringComparison.Ordinal))
            {
                j++;
            }

            if (i > 0 && tokens[i].Separator.Length > 0)
            {
                AppendPlain(result, tokens[i].Separator);
            }

            var run = tokens.GetRange(i, j - i);
            run[0] = new Token(string.Empty, run[0].Text, run[0].Style, run[0].Link);
            var styled = BuildStyled(run);
            if (link is null)
            {
                foreach (var inline in styled)
                {
                    if (inline is TextInline t)
                    {
                        AppendPlain(result, t.Text);
                    }
                    else
                    {
                        result.Add(inline);
                    }
                }
            }
            else
            {
                var linkInline = new LinkInline { Url = link };

                // Visible text that is itself the address (it may have wrapped, with glyph order and spacing lost at
                // the break) is rebuilt from the annotation's target.
                linkInline.Inlines.AddRange(UrlText(Inline.ToPlainText(styled), link) is { } display ? [new TextInline(display)] : styled);
                result.Add(linkInline);
            }

            i = j;
        }

        return result;
    }

    /// <summary>
    /// The text to show for a link whose visible text is a web address (or the e-mail address of a mailto link):
    /// the target itself, or null when the visible text is ordinary words.
    /// </summary>
    internal static string? UrlText(string visible, string url)
    {
        var text = visible.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            var address = url["mailto:".Length..];
            var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
            return compact.Contains('@', StringComparison.Ordinal) && !string.Equals(text, address, StringComparison.Ordinal)
                && address.Length > 0 && compact.Length >= address.Length / 2
                ? address
                : null;
        }

        var looksLikeUrl = text.StartsWith("http:/", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https:/", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("ftp:/", StringComparison.OrdinalIgnoreCase) || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeUrl || string.Equals(text, url, StringComparison.Ordinal))
        {
            return null;
        }

        // Shown without the scheme ("www.example.com/…"): keep it that way.
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            return schemeEnd > 0 ? url[(schemeEnd + 3)..] : url;
        }

        return url;
    }

    /// <summary>Removes <paramref name="style"/> from styled runs, unwrapping runs left without a style (adjacent text merges).</summary>
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
                        result.Add(new StyledInline(remaining, [.. children]));
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

        return Merge(result);
    }

    /// <summary>Plain text of the words with the same joining rules (used for headings).</summary>
    public static string PlainText(IEnumerable<IReadOnlyList<PdfWord>> lines, bool mergeHyphenation)
    {
        var sb = new StringBuilder();
        foreach (var token in Tokenize(lines, mergeHyphenation))
        {
            sb.Append(token.Separator).Append(token.Text);
        }

        return sb.ToString().Trim();
    }

    private static List<Token> Tokenize(IEnumerable<IReadOnlyList<PdfWord>> lines, bool mergeHyphenation)
    {
        var tokens = new List<Token>();
        var firstLine = true;
        foreach (var line in lines)
        {
            if (line.Count == 0)
            {
                continue;
            }

            for (var w = 0; w < line.Count; w++)
            {
                var word = line[w];
                string separator;
                if (w > 0)
                {
                    separator = TextJoiner.NeedsSpace(line[w - 1], word) ? " " : string.Empty;
                }
                else if (firstLine)
                {
                    separator = string.Empty;
                }
                else
                {
                    separator = " ";
                    var last = tokens.Count > 0 ? tokens[^1] : null;
                    if (last is not null && JoinsHyphenated(last.Text, word.Text, mergeHyphenation, out var trimmed))
                    {
                        last.Text = trimmed;
                        separator = string.Empty;
                    }
                    else if (last?.Link is { } url && string.Equals(url, word.LinkUrl, StringComparison.Ordinal)
                        && url.Contains(last.Text + word.Text, StringComparison.Ordinal))
                    {
                        // A URL wrapped over two lines.
                        separator = string.Empty;
                    }
                }

                AddWordTokens(tokens, word, separator);
            }

            firstLine = false;
        }

        return tokens;
    }

    private static bool JoinsHyphenated(string previous, string next, bool merge, out string trimmed)
    {
        trimmed = previous;
        if (previous.Length < 2 || next.Length == 0)
        {
            return false;
        }

        var last = previous[^1];
        if (last == '\u00AD')
        {
            trimmed = previous[..^1];
            return true;
        }

        if (!merge || last is not ('-' or '\u2010') || !char.IsLetter(previous[^2]) || !char.IsLower(next[0]))
        {
            return false;
        }

        trimmed = previous[..^1];
        return true;
    }

    private static void AddWordTokens(List<Token> tokens, PdfWord word, string separator)
    {
        var sb = new StringBuilder();
        FontFlags? current = null;
        var sep = separator;
        foreach (var glyph in word.Glyphs)
        {
            var flags = glyph.Flags;
            if (current is not null && flags != current.Value)
            {
                tokens.Add(new Token(sep, sb.ToString(), new StyleKey(current.Value, word.Script), word.LinkUrl));
                sb.Clear();
                sep = string.Empty;
            }

            current = flags;
            sb.Append(glyph.Value);
        }

        if (sb.Length > 0 && current is not null)
        {
            tokens.Add(new Token(sep, sb.ToString(), new StyleKey(current.Value, word.Script), word.LinkUrl));
        }
    }

    private static List<Inline> BuildStyled(List<Token> run)
    {
        var result = new List<Inline>();
        var i = 0;
        while (i < run.Count)
        {
            var style = run[i].Style;
            var sb = new StringBuilder(run[i].Text);
            var j = i + 1;
            while (j < run.Count && run[j].Style == style)
            {
                sb.Append(run[j].Separator).Append(run[j].Text);
                j++;
            }

            if (i > 0 && run[i].Separator.Length > 0)
            {
                AppendPlain(result, run[i].Separator);
            }

            result.Add(Wrap(sb.ToString(), style));
            i = j;
        }

        return Merge(result);
    }

    private static Inline Wrap(string text, StyleKey key)
    {
        Inline inner = (key.Flags & FontFlags.Mono) != 0 ? new CodeInline(text) : new TextInline(text);
        var style = InlineStyle.None;
        if ((key.Flags & FontFlags.Bold) != 0)
        {
            style |= InlineStyle.Bold;
        }

        if ((key.Flags & FontFlags.Italic) != 0)
        {
            style |= InlineStyle.Italic;
        }

        if ((key.Flags & FontFlags.Strike) != 0)
        {
            style |= InlineStyle.Strikethrough;
        }

        if ((key.Flags & FontFlags.Highlight) != 0)
        {
            style |= InlineStyle.Highlight;
        }

        if (key.Script == ScriptPosition.Super)
        {
            style |= InlineStyle.Superscript;
        }
        else if (key.Script == ScriptPosition.Sub)
        {
            style |= InlineStyle.Subscript;
        }

        return style == InlineStyle.None ? inner : new StyledInline(style, inner);
    }

    private static void AppendPlain(List<Inline> list, string text)
    {
        if (list.Count > 0 && list[^1] is TextInline last)
        {
            last.Text += text;
        }
        else
        {
            list.Add(new TextInline(text));
        }
    }

    private static List<Inline> Merge(List<Inline> inlines)
    {
        var result = new List<Inline>(inlines.Count);
        foreach (var inline in inlines)
        {
            if (inline is TextInline t && result.Count > 0 && result[^1] is TextInline last)
            {
                last.Text += t.Text;
            }
            else
            {
                result.Add(inline);
            }
        }

        return result;
    }
}
