using System.Globalization;
using System.Text;

namespace AllInOnePdf.Core.Importers.Data;

/// <summary>
/// RFC 4180 CSV reader: quoted fields, doubled quotes inside quotes, line breaks inside quoted fields, CRLF / LF / CR
/// record separators. Lenient where the RFC is strict (a stray quote inside an unquoted field is kept as text).
/// </summary>
internal static class CsvParser
{
    /// <summary>Delimiters tried by <see cref="DetectDelimiter"/>, in tie-break order.</summary>
    public static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>Yields records lazily so callers can stop early or count without keeping every row.</summary>
    public static IEnumerable<List<string>> Parse(string text, char delimiter)
    {
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        var fieldStarted = false;
        var i = 0;
        if (text.Length > 0 && text[0] == '﻿')
        {
            i = 1;
        }

        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && !fieldStarted)
            {
                inQuotes = true;
                fieldStarted = true;
            }
            else if (c == delimiter)
            {
                record.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                record.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                yield return record;
                record = [];
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }

        if (fieldStarted || field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }

    /// <summary>
    /// Picks the candidate delimiter that splits the first records into the most consistent number (&gt; 1) of
    /// fields. Falls back to a comma.
    /// </summary>
    public static char DetectDelimiter(string text)
    {
        var sample = text.Length > 64 * 1024 ? text[..(64 * 1024)] : text;
        var best = ',';
        var bestScore = 0.0;
        foreach (var candidate in Candidates)
        {
            var counts = Parse(sample, candidate)
                .Where(r => !IsBlank(r))
                .Take(50)
                .Select(r => r.Count)
                .ToList();

            // The sample may cut the last record short.
            if (text.Length > sample.Length && counts.Count > 1)
            {
                counts.RemoveAt(counts.Count - 1);
            }

            if (counts.Count == 0)
            {
                continue;
            }

            var mode = counts.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            if (mode.Key < 2)
            {
                continue;
            }

            // Consistency matters most; more columns breaks ties between equally consistent candidates.
            var score = (double)mode.Count() / counts.Count * 1000 + Math.Min(mode.Key, 100);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    public static bool IsBlank(List<string> record) => record.Count == 1 && string.IsNullOrWhiteSpace(record[0]);

    /// <summary>True when the cell reads as a number in the invariant or the current culture (currency and % allowed).</summary>
    public static bool IsNumeric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var s = value.Trim();
        if (s.EndsWith('%'))
        {
            s = s[..^1].TrimEnd();
        }

        if (s.Length == 0)
        {
            return false;
        }

        const NumberStyles styles = NumberStyles.Float | NumberStyles.AllowThousands | NumberStyles.AllowCurrencySymbol | NumberStyles.AllowParentheses;
        return double.TryParse(s, styles, CultureInfo.InvariantCulture, out _)
            || double.TryParse(s, styles, CultureInfo.CurrentCulture, out _);
    }
}
