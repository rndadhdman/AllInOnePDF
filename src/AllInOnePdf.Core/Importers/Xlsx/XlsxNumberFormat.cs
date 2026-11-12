using System.Globalization;
using System.Text;

namespace AllInOnePdf.Core.Importers.Xlsx;

/// <summary>
/// Renders a cell value the way Excel displays it for a number format code, culture-invariant. Covers General
/// (up to 11 significant characters), fixed decimals, thousands separators and scaling commas, percent, scientific,
/// literal text and currency symbols, up to four sections (positive;negative;zero;text), colours and locale tags
/// (ignored) and dates/times (y, m, d, h, s, AM/PM, elapsed [h]/[m]/[s], fractional seconds) in the 1900 or 1904
/// date system. Fractions, conditional sections and anything else unusual fall back to General.
/// Built-in short-date ids (14, 22 and the East Asian date ids) render as ISO yyyy-mm-dd because their real look
/// depends on the reader's locale.
/// </summary>
internal static class XlsxNumberFormat
{
    public const string GeneralCode = "General";

    /// <summary>Largest serial Excel accepts as a date (9999-12-31).</summary>
    private const double MaxDateSerial = 2958465.99999999;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Format code of a built-in number format id, or null when the id is not built in (or unknown).</summary>
    public static string? BuiltInCode(int id) => id switch
    {
        0 => GeneralCode,
        1 => "0",
        2 => "0.00",
        3 => "#,##0",
        4 => "#,##0.00",
        5 => "\"$\"#,##0_);(\"$\"#,##0)",
        6 => "\"$\"#,##0_);[Red](\"$\"#,##0)",
        7 => "\"$\"#,##0.00_);(\"$\"#,##0.00)",
        8 => "\"$\"#,##0.00_);[Red](\"$\"#,##0.00)",
        9 => "0%",
        10 => "0.00%",
        11 => "0.00E+00",
        12 => "# ?/?",
        13 => "# ??/??",
        14 => "yyyy-mm-dd",
        15 => "d-mmm-yy",
        16 => "d-mmm",
        17 => "mmm-yy",
        18 => "h:mm AM/PM",
        19 => "h:mm:ss AM/PM",
        20 => "h:mm",
        21 => "h:mm:ss",
        22 => "yyyy-mm-dd h:mm",
        37 => "#,##0 ;(#,##0)",
        38 => "#,##0 ;[Red](#,##0)",
        39 => "#,##0.00;(#,##0.00)",
        40 => "#,##0.00;[Red](#,##0.00)",
        45 => "mm:ss",
        46 => "[h]:mm:ss",
        47 => "mm:ss.0",
        48 => "##0.0E+0",
        49 => "@",
        >= 27 and <= 36 or >= 50 and <= 58 => "yyyy-mm-dd",
        _ => null,
    };

    /// <summary>Renders <paramref name="value"/> with a built-in id, or with <paramref name="customCode"/> when the workbook defines one.</summary>
    public static string Format(double value, int numberFormatId, string? customCode, bool date1904 = false) =>
        Format(value, customCode ?? BuiltInCode(numberFormatId) ?? GeneralCode, date1904);

    /// <summary>True when the code (its first section, outside quotes and brackets) shows a date or time.</summary>
    public static bool IsDateFormat(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var sections = SplitSections(code);
        return Tokenize(sections[0], out _).Any(t => t.Kind == TokenKind.Date);
    }

    public static bool IsDateFormat(int numberFormatId, string? customCode) =>
        IsDateFormat(customCode ?? BuiltInCode(numberFormatId));

    public static string Format(double value, string? code, bool date1904 = false)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return value.ToString(Inv);
        }

        if (string.IsNullOrWhiteSpace(code) || code.Equals(GeneralCode, StringComparison.OrdinalIgnoreCase))
        {
            return General(value);
        }

        try
        {
            return FormatCore(value, code, date1904) ?? General(value);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or FormatException)
        {
            return General(value);
        }
    }

    /// <summary>Excel's General format: up to 11 characters, scientific notation for very large or tiny values.</summary>
    public static string General(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return value.ToString(Inv);
        }

        if (value == 0)
        {
            return "0";
        }

        var abs = Math.Abs(value);
        if (abs >= 1e11 || abs < 1e-9)
        {
            return value.ToString("0.#####E+00", Inv);
        }

        var intDigits = abs < 1 ? 1 : (int)Math.Floor(Math.Log10(abs)) + 1;
        var decimals = Math.Clamp(10 - intDigits, 0, 10);
        var rounded = Math.Round((decimal)value, decimals, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.##########", Inv);
    }

    private static string? FormatCore(double value, string code, bool date1904)
    {
        var sections = SplitSections(code);
        var parsed = sections.Select(s => (Tokens: Tokenize(s, out var condition), Condition: condition)).ToList();
        if (parsed.Any(p => p.Condition))
        {
            return null;
        }

        // A text-only fourth section does not count for number selection.
        var numeric = parsed.Count > 3 ? parsed.Take(3).ToList() : parsed;
        List<Token> tokens;
        var autoMinus = true;
        var v = value;
        if (numeric.Count == 1 || value > 0 || (value == 0 && numeric.Count < 3))
        {
            tokens = numeric[0].Tokens;
        }
        else if (value < 0)
        {
            tokens = numeric[1].Tokens;
            autoMinus = false;
            v = -value;
        }
        else
        {
            tokens = numeric[2].Tokens;
        }

        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        if (tokens.Any(t => t.Kind == TokenKind.Date))
        {
            return FormatDate(v, tokens, date1904);
        }

        return FormatNumber(v, tokens, autoMinus);
    }

    // ---- Tokens ----------------------------------------------------------------------------------------------

    private enum TokenKind { Literal, Digit, Point, Comma, Percent, Exponent, Slash, At, General, Date }

    private readonly record struct Token(TokenKind Kind, string Text);

    internal static List<string> SplitSections(string code)
    {
        var sections = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        var bracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (inQuote)
            {
                sb.Append(c);
                inQuote = c != '"';
                continue;
            }

            switch (c)
            {
                case '\\' when i + 1 < code.Length:
                    sb.Append(c).Append(code[++i]);
                    continue;
                case '"':
                    inQuote = true;
                    break;
                case '[':
                    bracket = true;
                    break;
                case ']':
                    bracket = false;
                    break;
                case ';' when !bracket:
                    sections.Add(sb.ToString());
                    sb.Clear();
                    continue;
            }

            sb.Append(c);
        }

        sections.Add(sb.ToString());
        return sections;
    }

    private static List<Token> Tokenize(string section, out bool condition)
    {
        condition = false;
        var tokens = new List<Token>();
        var s = section;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"':
                {
                    var end = s.IndexOf('"', i + 1);
                    if (end < 0)
                    {
                        end = s.Length;
                    }

                    tokens.Add(new Token(TokenKind.Literal, s[(i + 1)..end]));
                    i = end;
                    break;
                }

                case '\\':
                    if (i + 1 < s.Length)
                    {
                        tokens.Add(new Token(TokenKind.Literal, s[++i].ToString()));
                    }

                    break;
                case '_':
                    // Padding the width of the next character: a space is the closest culture-free rendering.
                    i++;
                    tokens.Add(new Token(TokenKind.Literal, " "));
                    break;
                case '*':
                    // Repeat-to-fill: depends on column width; drop it.
                    i++;
                    break;
                case '[':
                {
                    var end = s.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        end = s.Length;
                    }

                    var inner = s[(i + 1)..end];
                    i = end;
                    Bracket(inner, tokens, ref condition);
                    break;
                }

                case '0' or '#' or '?':
                    tokens.Add(new Token(TokenKind.Digit, c.ToString()));
                    break;
                case '.':
                    tokens.Add(new Token(TokenKind.Point, "."));
                    break;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ","));
                    break;
                case '%':
                    tokens.Add(new Token(TokenKind.Percent, "%"));
                    break;
                case '/':
                    tokens.Add(new Token(TokenKind.Slash, "/"));
                    break;
                case '@':
                    tokens.Add(new Token(TokenKind.At, "@"));
                    break;
                case 'E' or 'e' when i + 1 < s.Length && s[i + 1] is '+' or '-':
                    tokens.Add(new Token(TokenKind.Exponent, "E" + s[i + 1]));
                    i++;
                    break;
                case 'G' or 'g' when string.Compare(s, i, GeneralCode, 0, GeneralCode.Length, StringComparison.OrdinalIgnoreCase) == 0:
                    tokens.Add(new Token(TokenKind.General, GeneralCode));
                    i += GeneralCode.Length - 1;
                    break;
                case 'A' or 'a' when string.Compare(s, i, "AM/PM", 0, 5, StringComparison.OrdinalIgnoreCase) == 0:
                    tokens.Add(new Token(TokenKind.Date, "AM/PM"));
                    i += 4;
                    break;
                case 'A' or 'a' when string.Compare(s, i, "A/P", 0, 3, StringComparison.OrdinalIgnoreCase) == 0:
                    tokens.Add(new Token(TokenKind.Date, s.Substring(i, 3)));
                    i += 2;
                    break;
                case 'y' or 'Y' or 'm' or 'M' or 'd' or 'D' or 'h' or 'H' or 's' or 'S':
                {
                    var lower = char.ToLowerInvariant(c);
                    var j = i;
                    while (j < s.Length && char.ToLowerInvariant(s[j]) == lower)
                    {
                        j++;
                    }

                    tokens.Add(new Token(TokenKind.Date, new string(lower, j - i)));
                    i = j - 1;
                    break;
                }

                case 'B' or 'b' when i + 1 < s.Length && s[i + 1] is '1' or '2':
                    // Buddhist / Gregorian calendar switch.
                    i++;
                    break;
                default:
                    tokens.Add(new Token(TokenKind.Literal, c.ToString()));
                    break;
            }
        }

        return tokens;
    }

    private static void Bracket(string inner, List<Token> tokens, ref bool condition)
    {
        if (inner.Length == 0)
        {
            return;
        }

        if (inner[0] is '<' or '>' or '=')
        {
            condition = true;
            return;
        }

        if (inner[0] == '$')
        {
            // [$€-407] currency symbol with locale; [$-409] locale only.
            var dash = inner.IndexOf('-', StringComparison.Ordinal);
            var symbol = dash < 0 ? inner[1..] : inner[1..dash];
            if (symbol.Length > 0)
            {
                tokens.Add(new Token(TokenKind.Literal, symbol));
            }

            return;
        }

        var lower = inner.ToLowerInvariant();
        if (lower.Length > 0 && lower.All(ch => ch == lower[0]) && lower[0] is 'h' or 'm' or 's')
        {
            tokens.Add(new Token(TokenKind.Date, "[" + lower + "]"));
        }

        // Anything else is a colour ([Red], [Color10]) or a locale tag: no visible output.
    }

    // ---- Numbers ---------------------------------------------------------------------------------------------

    private static string? FormatNumber(double value, List<Token> tokens, bool autoMinus)
    {
        if (tokens.Any(t => t.Kind == TokenKind.Slash))
        {
            return null;
        }

        var first = tokens.FindIndex(t => t.Kind == TokenKind.Digit);
        if (first < 0)
        {
            // No digit placeholders: General inside literals, or literal text only.
            var sb0 = new StringBuilder();
            if (value < 0 && autoMinus && tokens.Any(t => t.Kind == TokenKind.General))
            {
                sb0.Append('-');
            }

            foreach (var t in tokens)
            {
                sb0.Append(t.Kind switch
                {
                    TokenKind.General or TokenKind.At => General(Math.Abs(value)),
                    TokenKind.Percent => "%",
                    _ => t.Text,
                });
            }

            return sb0.ToString();
        }

        var last = tokens.FindLastIndex(t => t.Kind == TokenKind.Digit);
        for (var k = first; k <= last; k++)
        {
            if (tokens[k].Kind is TokenKind.Literal or TokenKind.General or TokenKind.At)
            {
                return null;
            }
        }

        var percent = tokens.Count(t => t.Kind == TokenKind.Percent);
        var abs = Math.Abs(value) * Math.Pow(100, percent);

        var exponentIndex = tokens.FindIndex(first, last - first + 1, t => t.Kind == TokenKind.Exponent);
        var mantissaEnd = exponentIndex < 0 ? last : exponentIndex - 1;
        var pointIndex = tokens.FindIndex(first, mantissaEnd - first + 1, t => t.Kind == TokenKind.Point);
        var intEnd = pointIndex < 0 ? mantissaEnd : pointIndex - 1;

        var intDigits = new List<string>();
        var thousands = false;
        var scaleCommas = 0;
        for (var k = first; k <= intEnd; k++)
        {
            if (tokens[k].Kind == TokenKind.Digit)
            {
                intDigits.Add(tokens[k].Text);
                scaleCommas = 0;
            }
            else if (tokens[k].Kind == TokenKind.Comma)
            {
                scaleCommas++;
                if (k + 1 <= intEnd && tokens[k + 1].Kind == TokenKind.Digit)
                {
                    thousands = true;
                    scaleCommas = 0;
                }
            }
        }

        // Commas straight after the integer digits (and at the end of the section) divide by 1000 each.
        for (var k = last + 1; k < tokens.Count && tokens[k].Kind == TokenKind.Comma && exponentIndex < 0; k++)
        {
            scaleCommas++;
        }

        if (scaleCommas > 0)
        {
            abs /= Math.Pow(1000, scaleCommas);
        }

        var decDigits = new List<string>();
        if (pointIndex >= 0)
        {
            for (var k = pointIndex + 1; k <= mantissaEnd; k++)
            {
                if (tokens[k].Kind == TokenKind.Digit)
                {
                    decDigits.Add(tokens[k].Text);
                }
            }
        }

        var minInt = intDigits.Count(d => d == "0");
        var maxDec = decDigits.Count;
        var minDec = decDigits.Count(d => d == "0");

        string number;
        if (exponentIndex >= 0)
        {
            var expDigits = 0;
            for (var k = exponentIndex + 1; k <= last; k++)
            {
                if (tokens[k].Kind == TokenKind.Digit)
                {
                    expDigits++;
                }
            }

            number = Scientific(abs, Math.Max(1, intDigits.Count), minInt, minDec, maxDec, tokens[exponentIndex].Text[1] == '+', Math.Max(1, expDigits), thousands);
        }
        else
        {
            number = Fixed(abs, minInt, minDec, maxDec, thousands, pointIndex >= 0);
        }

        var sb = new StringBuilder();
        if (value < 0 && autoMinus && number.Any(ch => ch is >= '1' and <= '9'))
        {
            sb.Append('-');
        }

        for (var k = 0; k < tokens.Count; k++)
        {
            if (k == first)
            {
                sb.Append(number);
                k = last;
                continue;
            }

            var t = tokens[k];
            switch (t.Kind)
            {
                case TokenKind.Comma when k > last && scaleCommas > 0:
                    break;
                case TokenKind.Date:
                    break;
                default:
                    sb.Append(t.Text);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string Fixed(double abs, int minInt, int minDec, int maxDec, bool thousands, bool hasPoint)
    {
        string digits;
        if (abs < 7.9e27)
        {
            var rounded = Math.Round((decimal)abs, maxDec, MidpointRounding.AwayFromZero);
            digits = rounded.ToString("F" + maxDec.ToString(Inv), Inv);
        }
        else
        {
            digits = abs.ToString("F" + maxDec.ToString(Inv), Inv);
        }

        var dot = digits.IndexOf('.', StringComparison.Ordinal);
        var intPart = dot < 0 ? digits : digits[..dot];
        var frac = dot < 0 ? string.Empty : digits[(dot + 1)..];
        while (frac.Length > minDec && frac.EndsWith('0'))
        {
            frac = frac[..^1];
        }

        intPart = intPart.TrimStart('0');
        if (intPart.Length < minInt)
        {
            intPart = new string('0', minInt - intPart.Length) + intPart;
        }

        if (thousands && intPart.Length > 3)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < intPart.Length; i++)
            {
                if (i > 0 && (intPart.Length - i) % 3 == 0)
                {
                    sb.Append(',');
                }

                sb.Append(intPart[i]);
            }

            intPart = sb.ToString();
        }

        return frac.Length > 0 || (hasPoint && maxDec == 0) ? intPart + "." + frac : intPart;
    }

    private static string Scientific(double abs, int intPlaces, int minInt, int minDec, int maxDec, bool plus, int expDigits, bool thousands)
    {
        var exponent = 0;
        if (abs > 0)
        {
            exponent = (int)Math.Floor(Math.Log10(abs));
            if (intPlaces > 1)
            {
                // Engineering style (##0.0E+0): exponent in steps of the integer placeholder count.
                exponent = (int)Math.Floor((double)exponent / intPlaces) * intPlaces;
            }
            else
            {
                exponent -= Math.Max(0, minInt - 1);
            }
        }

        var mantissa = abs / Math.Pow(10, exponent);
        var limit = Math.Pow(10, intPlaces);
        if (Math.Round(mantissa, maxDec, MidpointRounding.AwayFromZero) >= limit)
        {
            exponent += intPlaces;
            mantissa = abs / Math.Pow(10, exponent);
        }

        var m = Fixed(mantissa, Math.Max(1, minInt), minDec, maxDec, thousands, false);
        var sign = exponent < 0 ? "-" : plus ? "+" : string.Empty;
        return m + "E" + sign + Math.Abs(exponent).ToString(Inv).PadLeft(expDigits, '0');
    }

    // ---- Dates -----------------------------------------------------------------------------------------------

    private static string? FormatDate(double serial, List<Token> tokens, bool date1904)
    {
        if (serial < 0 || serial > MaxDateSerial)
        {
            return null;
        }

        // Fractional seconds: "ss.0", "ss.000" (a point and zeros after a seconds token).
        var fractionDigits = 0;
        var fractionStart = -1;
        for (var k = 0; k < tokens.Count; k++)
        {
            if (tokens[k].Kind == TokenKind.Point && k > 0 && PreviousDate(tokens, k) is { } prev && prev.StartsWith('s'))
            {
                var j = k + 1;
                while (j < tokens.Count && tokens[j] is { Kind: TokenKind.Digit, Text: "0" })
                {
                    j++;
                }

                if (j > k + 1)
                {
                    fractionStart = k;
                    fractionDigits = j - k - 1;
                }

                break;
            }
        }

        var scale = Math.Pow(10, fractionDigits);
        var units = Math.Round(serial * 86400 * scale, MidpointRounding.AwayFromZero);
        var totalSeconds = Math.Floor(units / scale);
        var fraction = (long)(units - totalSeconds * scale);
        var wholeDays = (long)Math.Floor(totalSeconds / 86400);
        var secondsOfDay = (long)(totalSeconds - wholeDays * 86400);

        DateTime date;
        if (date1904)
        {
            date = new DateTime(1904, 1, 1).AddDays(wholeDays);
        }
        else
        {
            // Excel's 1900 system counts the non-existent 1900-02-29 (serial 60).
            date = wholeDays < 61 ? new DateTime(1899, 12, 31).AddDays(Math.Max(0, wholeDays)) : new DateTime(1899, 12, 30).AddDays(wholeDays);
            if (wholeDays == 0)
            {
                date = new DateTime(1900, 1, 1);
            }
        }

        var hour = (int)(secondsOfDay / 3600);
        var minute = (int)(secondsOfDay % 3600 / 60);
        var second = (int)(secondsOfDay % 60);
        var hasAmPm = tokens.Any(t => t.Kind == TokenKind.Date && t.Text.Contains('/', StringComparison.Ordinal));

        var sb = new StringBuilder();
        for (var k = 0; k < tokens.Count; k++)
        {
            var t = tokens[k];
            if (k == fractionStart)
            {
                sb.Append('.').Append(fraction.ToString(Inv).PadLeft(fractionDigits, '0'));
                k += fractionDigits;
                continue;
            }

            if (t.Kind != TokenKind.Date)
            {
                sb.Append(t.Kind == TokenKind.General ? General(serial) : t.Text);
                continue;
            }

            var text = t.Text;
            switch (text[0])
            {
                case 'y':
                    sb.Append(text.Length <= 2 ? (date.Year % 100).ToString("00", Inv) : date.Year.ToString("0000", Inv));
                    break;
                case 'm' when IsMinute(tokens, k):
                    sb.Append(text.Length >= 2 ? minute.ToString("00", Inv) : minute.ToString(Inv));
                    break;
                case 'm':
                    sb.Append(text.Length switch
                    {
                        1 => date.Month.ToString(Inv),
                        2 => date.Month.ToString("00", Inv),
                        3 => Inv.DateTimeFormat.GetAbbreviatedMonthName(date.Month),
                        5 => Inv.DateTimeFormat.GetMonthName(date.Month)[..1],
                        _ => Inv.DateTimeFormat.GetMonthName(date.Month),
                    });
                    break;
                case 'd':
                    sb.Append(text.Length switch
                    {
                        1 => date.Day.ToString(Inv),
                        2 => date.Day.ToString("00", Inv),
                        3 => Inv.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek),
                        _ => Inv.DateTimeFormat.GetDayName(date.DayOfWeek),
                    });
                    break;
                case 'h':
                {
                    var h = hasAmPm ? (hour % 12 == 0 ? 12 : hour % 12) : hour;
                    sb.Append(text.Length >= 2 ? h.ToString("00", Inv) : h.ToString(Inv));
                    break;
                }

                case 's':
                    sb.Append(text.Length >= 2 ? second.ToString("00", Inv) : second.ToString(Inv));
                    break;
                case '[':
                {
                    var elapsed = text[1] switch
                    {
                        'h' => (long)Math.Floor(totalSeconds / 3600),
                        'm' => (long)Math.Floor(totalSeconds / 60),
                        _ => (long)totalSeconds,
                    };
                    sb.Append(elapsed.ToString(Inv).PadLeft(text.Length - 2, '0'));
                    break;
                }

                case 'A' or 'a':
                    sb.Append(text.Length == 5
                        ? (hour < 12 ? "AM" : "PM")
                        : hour < 12 ? text[..1] : text[2..3]);
                    break;
                default:
                    sb.Append(text);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>"m" means minutes right after an hour token or right before a seconds token.</summary>
    private static bool IsMinute(List<Token> tokens, int index)
    {
        if (PreviousDate(tokens, index) is { } prev && (prev[0] == 'h' || prev.StartsWith("[h", StringComparison.Ordinal)))
        {
            return true;
        }

        for (var k = index + 1; k < tokens.Count; k++)
        {
            if (tokens[k].Kind == TokenKind.Date)
            {
                return tokens[k].Text[0] == 's' || tokens[k].Text.StartsWith("[s", StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static string? PreviousDate(List<Token> tokens, int index)
    {
        for (var k = index - 1; k >= 0; k--)
        {
            if (tokens[k].Kind == TokenKind.Date)
            {
                return tokens[k].Text;
            }
        }

        return null;
    }
}
