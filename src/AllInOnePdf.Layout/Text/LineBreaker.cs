namespace AllInOnePdf.Layout.Text;

/// <summary>What a break unit is, for the line breaker.</summary>
internal enum BreakUnitKind
{
    /// <summary>A visible cluster (or an inline element).</summary>
    Content,

    /// <summary>A breaking space: hangs at the end of a line and does not count for alignment.</summary>
    Space,

    /// <summary>A hard line break ("\n"): ends the line.</summary>
    HardBreak,
}

/// <summary>One unbreakable piece of a paragraph (a glyph cluster, a space, an inline element, a hard break).</summary>
internal struct BreakUnit
{
    /// <summary>Advance in points, letter spacing included.</summary>
    public float Width;

    public BreakUnitKind Kind;

    /// <summary>A line may end after this unit.</summary>
    public bool CanBreakAfter;

    /// <summary>The break after this unit is a soft hyphen: a hyphen of <see cref="HyphenWidth"/> is shown when the line ends here.</summary>
    public bool SoftHyphenAfter;

    public float HyphenWidth;

    /// <summary>Letter spacing included in <see cref="Width"/> that does not count when the unit ends a line.</summary>
    public float TrailingSpacing;
}

/// <summary>A line found by the breaker: units <c>[Start, End)</c>.</summary>
/// <param name="Start">First unit.</param>
/// <param name="End">One past the last unit (hard breaks and trailing spaces included).</param>
/// <param name="EndsWithHardBreak">The line ends with a "\n" (not justified; the next line starts a paragraph).</param>
/// <param name="Hyphenated">The line ends at a soft hyphen, which is drawn.</param>
/// <param name="StartsParagraph">First line of the text or first line after a hard break (gets the first-line indent).</param>
/// <param name="Width">Visible content width: trailing spaces and trailing letter spacing excluded, the hyphen included, the indent excluded.</param>
internal readonly record struct LineSpan(int Start, int End, bool EndsWithHardBreak, bool Hyphenated, bool StartsParagraph, float Width);

/// <summary>
/// Greedy line breaking over <see cref="BreakUnit"/>s: a line takes units while they fit, ends at the last break
/// opportunity when one overflows, and breaks inside a word only when the word alone is wider than the line.
/// Spaces never overflow (they hang). Character classification for break opportunities lives here too.
/// </summary>
internal static class LineBreaker
{
    /// <summary>Tolerance for floating-point width comparisons.</summary>
    public const float Epsilon = 0.001f;

    /// <summary>Breaks <paramref name="units"/> into lines no wider than <paramref name="maxWidth"/> (minus <paramref name="indent"/> on paragraph-first lines).</summary>
    public static List<LineSpan> Break(IReadOnlyList<BreakUnit> units, float maxWidth, float indent = 0)
    {
        ArgumentNullException.ThrowIfNull(units);
        var lines = new List<LineSpan>();
        var count = units.Count;
        var i = 0;
        var paragraphStart = true;
        while (i < count)
        {
            var lineStart = i;
            var limit = maxWidth - (paragraphStart ? indent : 0) + Epsilon;
            var width = 0f;
            var lastBreak = -1;
            var end = -1;
            var hard = false;
            var atOpportunity = false;
            var j = i;
            for (; j < count; j++)
            {
                var unit = units[j];
                if (unit.Kind == BreakUnitKind.HardBreak)
                {
                    end = j + 1;
                    hard = true;
                    break;
                }

                if (unit.Kind == BreakUnitKind.Space)
                {
                    // Spaces hang, break-anywhere text included (Skia's paragraph, which QuestPDF uses, ends the
                    // line after the space: "… ct = default)" | "=> await …").
                    width += unit.Width;
                    if (unit.CanBreakAfter)
                    {
                        lastBreak = j;
                    }

                    continue;
                }

                var next = width + unit.Width;
                if (next - unit.TrailingSpacing > limit && j > lineStart)
                {
                    atOpportunity = lastBreak >= lineStart;
                    end = atOpportunity ? lastBreak + 1 : j;
                    break;
                }

                width = next;
                if (unit.CanBreakAfter && (!unit.SoftHyphenAfter || width - unit.TrailingSpacing + unit.HyphenWidth <= limit))
                {
                    lastBreak = j;
                }
            }

            if (end < 0)
            {
                end = count;
            }

            var hyphenated = atOpportunity && end < count && units[end - 1] is { SoftHyphenAfter: true, Kind: BreakUnitKind.Content };
            lines.Add(new LineSpan(lineStart, end, hard, hyphenated, paragraphStart, VisibleWidth(units, lineStart, end, hyphenated)));
            paragraphStart = hard;
            i = end;
        }

        // A trailing "\n" opens one more (empty) line, as in browsers and Skia's paragraph layout.
        if (count > 0 && units[count - 1].Kind == BreakUnitKind.HardBreak)
        {
            lines.Add(new LineSpan(count, count, false, false, true, 0));
        }

        return lines;
    }

    /// <summary>Width of units [start, end) without trailing spaces/breaks and trailing letter spacing, plus the hyphen when shown.</summary>
    public static float VisibleWidth(IReadOnlyList<BreakUnit> units, int start, int end, bool hyphenated)
    {
        ArgumentNullException.ThrowIfNull(units);
        var last = end - 1;
        while (last >= start && units[last].Kind != BreakUnitKind.Content)
        {
            last--;
        }

        var width = 0f;
        for (var k = start; k <= last; k++)
        {
            width += units[k].Width;
        }

        if (last >= start)
        {
            width -= units[last].TrailingSpacing;
            if (hyphenated)
            {
                width += units[last].HyphenWidth;
            }
        }

        return width;
    }

    // ------------------------------------------------------------------ character classes

    /// <summary>A space that offers a break and hangs at line ends (not the no-break spaces U+00A0, U+2007, U+202F).</summary>
    public static bool IsBreakingSpace(char c) => c switch
    {
        ' ' or ' ' or ' ' or '　' => true,
        >= ' ' and <= ' ' => c != ' ',
        _ => false,
    };

    /// <summary>Hyphens and dashes after which a line may break.</summary>
    public static bool IsBreakingHyphen(char c) => c is '-' or '‐' or '‒' or '–';

    /// <summary>Ideographs, kana, Hangul and CJK punctuation: a line may break before and after them.</summary>
    public static bool IsCjk(char c) => c switch
    {
        >= '⺀' and <= '⿿' => true,
        >= '、' and <= '〿' => true,
        >= '぀' and <= 'ヿ' => true,
        >= '㄀' and <= 'ㇿ' => true,
        >= '㐀' and <= '䶿' => true,
        >= '一' and <= '鿿' => true,
        >= '가' and <= '힯' => true,
        >= '豈' and <= '﫿' => true,
        >= '＀' and <= '￯' => true,
        _ => false,
    };

    /// <summary>Characters a line must not start with (closing punctuation, small kana marks).</summary>
    public static bool IsNoBreakBefore(char c) =>
        c is ',' or '.' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '…' or '、' or '。' or '，' or '．'
            or '：' or '；' or '？' or '！' or '）' or '」' or '』' or '】' or '〕' or '〉'
            or '》' or 'ー' or '々' or 'ゝ' or 'ゞ';

    /// <summary>
    /// Characters a line must not start with even after spaces (UAX #14 rule 13: closing brackets, exclamation,
    /// infix separators and the solidus), so a space before them offers no break.
    /// </summary>
    public static bool IsNoBreakAfterSpace(char c) => c is ')' or ']' or '}' or '!' or '?' or ',' or '.' or ';' or ':' or '/';

    /// <summary>Characters a line must not end with (opening brackets).</summary>
    public static bool IsNoBreakAfter(char c) =>
        c is '(' or '[' or '{' or '（' or '「' or '『' or '【' or '〔' or '〈' or '《';

    /// <summary>
    /// Whether a line may break between two content units: <paramref name="before"/> is the last character of the
    /// first unit (<paramref name="beforePrevious"/> the one before it, if any), <paramref name="after"/> the first
    /// character of the second.
    /// </summary>
    public static bool CanBreakBetween(char beforePrevious, char before, char after, bool wrapAnywhere)
    {
        if (IsBreakingSpace(after))
        {
            return false;
        }

        // Break-anywhere text breaks at every character boundary, punctuation included (QuestPDF's code blocks
        // wrap "…(path, ct)" | "; // comment").
        if (wrapAnywhere)
        {
            return true;
        }

        if (IsNoBreakBefore(after) || IsNoBreakAfter(before))
        {
            return false;
        }

        if (IsBreakingHyphen(before))
        {
            return beforePrevious != '\0' && !IsBreakingSpace(beforePrevious) && !IsBreakingHyphen(beforePrevious) && !char.IsDigit(after) && !IsBreakingHyphen(after);
        }

        // A solidus offers a break after it (URLs and paths wrap after "/"), except before another solidus or a
        // digit (UAX #14 rules 13 and 25, as ICU applies them in Skia's paragraph).
        if (before == '/')
        {
            return after != '/' && !char.IsDigit(after);
        }

        return IsCjk(before) || IsCjk(after);
    }
}
