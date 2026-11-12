using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AllInOnePdf.Core.Exporters.Markdown;

/// <summary>
/// Escaping helpers. Plain text is escaped only where a character could start Markdown syntax under the
/// importer's pipeline (CommonMark + GFM + Markdig extras: emphasis extras, math, footnotes, attributes, citations).
/// </summary>
internal static partial class MarkdownEscaper
{
    /// <summary>Marks a breakable space inside rendered inline text; the wrapper turns it into a space or a newline.</summary>
    public const char BreakableSpace = '\u0001';

    [Flags]
    public enum TextContext
    {
        None = 0,
        Table = 1,
        LinkText = 2,
        Breakable = 4,
        SingleLine = 8,
    }

    /// <summary>Escapes a run of literal text. <paramref name="prev"/>/<paramref name="next"/> are the neighbouring output characters, when known.</summary>
    public static string EscapeText(string text, char? prev, char? next, TextContext context)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var before = i > 0 ? text[i - 1] : prev;
            var after = i < text.Length - 1 ? text[i + 1] : next;
            switch (c)
            {
                case '\\':
                    sb.Append(after is null || IsAsciiPunctuation(after.Value) || after == '\n' ? @"\\" : @"\");
                    break;
                case '*' or '`' or '[' or '$' or '~' or '^':
                    sb.Append('\\').Append(c);
                    break;
                case '_':
                    sb.Append(IsWordChar(before) && IsWordChar(after) ? "_" : @"\_");
                    break;
                case ']':
                    sb.Append((context & TextContext.LinkText) != 0 ? @"\]" : "]");
                    break;
                case '|':
                    sb.Append((context & TextContext.Table) != 0 ? @"\|" : "|");
                    break;
                case '<':
                    sb.Append(after is { } a && (char.IsAsciiLetter(a) || a is '/' or '!' or '?') ? @"\<" : "<");
                    break;
                case '&':
                    sb.Append(EntityLike().IsMatch(text, i) ? @"\&" : "&");
                    break;
                case '=' or '+' or '"':
                    sb.Append(before == c || after == c ? "\\" + c : c.ToString());
                    break;
                case '{':
                    sb.Append(text.IndexOf('}', i) > i ? @"\{" : "{");
                    break;
                case '\r':
                    break;
                case '\n':
                    sb.Append((context & (TextContext.Table | TextContext.SingleLine)) != 0 ? ' ' : '\n');
                    break;
                case ' ' or '\t':
                    sb.Append((context & TextContext.Breakable) != 0 ? BreakableSpace : ' ');
                    break;
                case BreakableSpace:
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Escapes alt text or other text that sits inside square brackets.</summary>
    public static string EscapeBracketText(string text, bool inTable)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '[' or ']' or '`' or '*' or '_' or '$' || (inTable && c == '|'))
            {
                sb.Append('\\');
            }

            sb.Append(c is '\n' or '\r' ? ' ' : c);
        }

        return sb.ToString();
    }

    /// <summary>Escapes a character at the start of a line that would otherwise start a block (heading, list, quote, setext underline ...).</summary>
    public static string EscapeLineStart(string line)
    {
        if (line.Length == 0)
        {
            return line;
        }

        var c = line[0];
        var second = line.Length > 1 ? line[1] : '\0';
        switch (c)
        {
            case '#' when AtxStart().IsMatch(line):
            case '>':
            case '-' or '+' when second is '\0' or ' ' or '\t':
            case '-' when DashRule().IsMatch(line):
            case '=' when EqualsRule().IsMatch(line):
            case ':' when second is '\0' or ' ' or '\t' or ':':
                return "\\" + line;
        }

        if (char.IsAsciiLetterOrDigit(c))
        {
            var m = OrderedMarker().Match(line);
            if (m.Success)
            {
                var at = m.Groups["delim"].Index;
                return string.Concat(line.AsSpan(0, at), "\\", line.AsSpan(at));
            }
        }

        return line;
    }

    /// <summary>Formats a link or image destination, using the angle-bracket form when the URL contains spaces or unbalanced parentheses.</summary>
    public static string FormatDestination(string url)
    {
        if (url.Length == 0)
        {
            return "<>";
        }

        var depth = 0;
        var needsAngle = false;
        foreach (var c in url)
        {
            if (c is ' ' or '\t' or '\n' or '\r' or '<' or '>' || char.IsControl(c))
            {
                needsAngle = true;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth < 0)
            {
                needsAngle = true;
            }
        }

        needsAngle |= depth != 0;
        if (!needsAngle)
        {
            return url.Replace(@"\", @"\\", StringComparison.Ordinal);
        }

        var sb = new StringBuilder(url.Length + 2).Append('<');
        foreach (var c in url)
        {
            switch (c)
            {
                case '<' or '>' or '\\':
                    sb.Append('\\').Append(c);
                    break;
                case '\n' or '\r':
                    sb.Append("%0A");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.Append('>').ToString();
    }

    /// <summary>Formats a link title as a double-quoted string, or empty when there is no title.</summary>
    public static string FormatTitle(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        var escaped = title.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace('\n', ' ');
        return " \"" + escaped + "\"";
    }

    /// <summary>Wraps code in a backtick fence longer than any backtick run it contains.</summary>
    public static string CodeSpan(string code)
    {
        code = code.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        var fence = new string('`', LongestRun(code, '`') + 1);
        var pad = code.StartsWith('`') || code.EndsWith('`') ||
                  (code.Length > 1 && code.StartsWith(' ') && code.EndsWith(' ') && code.Trim().Length > 0);
        return pad ? fence + " " + code + " " + fence : fence + code + fence;
    }

    public static int LongestRun(string text, char c)
    {
        int best = 0, run = 0;
        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            best = Math.Max(best, run);
        }

        return best;
    }

    /// <summary>Approximate monospace width: one column per text element (grapheme).</summary>
    public static int DisplayWidth(string text) => text.Length == 0 ? 0 : new StringInfo(text).LengthInTextElements;

    public static bool IsAsciiPunctuation(char c) => c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';

    private static bool IsWordChar(char? c) => c is { } ch && char.IsLetterOrDigit(ch);

    [GeneratedRegex(@"\G&(#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex EntityLike();

    [GeneratedRegex(@"^#{1,6}(\s|$)")]
    private static partial Regex AtxStart();

    [GeneratedRegex(@"^-+\s*$")]
    private static partial Regex DashRule();

    [GeneratedRegex(@"^=+\s*$")]
    private static partial Regex EqualsRule();

    [GeneratedRegex(@"^([0-9]{1,9}|[A-Za-z]|[ivxIVX]{1,6})(?<delim>[.)])(\s|$)")]
    private static partial Regex OrderedMarker();
}
