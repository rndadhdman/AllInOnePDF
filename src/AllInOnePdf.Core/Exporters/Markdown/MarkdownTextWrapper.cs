using System.Text;
using static AllInOnePdf.Core.Exporters.Markdown.MarkdownEscaper;

namespace AllInOnePdf.Core.Exporters.Markdown;

/// <summary>Greedy word wrapping over rendered inline text. Only <see cref="MarkdownEscaper.BreakableSpace"/> positions may break, so code spans, links and HTML stay intact.</summary>
internal static class MarkdownTextWrapper
{
    public static string Wrap(string text, int width)
    {
        if (width <= 0)
        {
            return text.Replace(BreakableSpace, ' ');
        }

        var sb = new StringBuilder(text.Length + 16);
        var firstLine = true;
        foreach (var rawLine in text.Split('\n'))
        {
            if (!firstLine)
            {
                sb.Append('\n');
            }

            firstLine = false;
            var lineWidth = 0;
            var lineStart = true;
            foreach (var word in rawLine.Split(BreakableSpace, StringSplitOptions.RemoveEmptyEntries))
            {
                var w = DisplayWidth(word);
                if (!lineStart && lineWidth + 1 + w > width)
                {
                    sb.Append('\n');
                    lineWidth = 0;
                    lineStart = true;
                }

                if (!lineStart)
                {
                    sb.Append(' ');
                    lineWidth++;
                }

                sb.Append(word);
                lineWidth += w;
                lineStart = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>Trims leading whitespace, drops blank lines (which would end the paragraph) and escapes block markers at each line start.</summary>
    public static string FinishLines(string text)
    {
        var lines = text.Split('\n')
            .Select(l => l.TrimStart(' ', '\t'))
            .Where(l => l.Length > 0)
            .Select(EscapeLineStart);
        return string.Join('\n', lines);
    }

    /// <summary>Prefixes every line (e.g. "&gt; " for quotes); empty lines get <paramref name="blankPrefix"/>.</summary>
    public static string Prefix(string text, string prefix, string blankPrefix)
    {
        if (text.Length == 0)
        {
            return blankPrefix;
        }

        return string.Join('\n', text.Split('\n').Select(l => l.Length == 0 ? blankPrefix : prefix + l));
    }

    /// <summary>Prefixes the first line with <paramref name="first"/> and indents the rest (list items, footnote definitions).</summary>
    public static string Hang(string text, string first, int indent)
    {
        if (text.Length == 0)
        {
            return first.TrimEnd();
        }

        var pad = new string(' ', indent);
        var lines = text.Split('\n');
        var sb = new StringBuilder(first).Append(lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            sb.Append('\n');
            if (lines[i].Length > 0)
            {
                sb.Append(pad).Append(lines[i]);
            }
        }

        return sb.ToString();
    }
}
