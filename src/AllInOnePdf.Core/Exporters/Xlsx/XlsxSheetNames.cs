using System.Globalization;
using System.Text;

namespace AllInOnePdf.Core.Exporters.Xlsx;

/// <summary>Excel sheet-name rules: 1–31 characters, none of <c>[]:*?/\</c>, no leading/trailing apostrophe, unique (case-insensitive).</summary>
internal sealed class XlsxSheetNames
{
    public const int MaxLength = 31;

    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public string Unique(string? wanted, string fallback)
    {
        var name = Sanitize(wanted);
        if (name.Length == 0)
        {
            name = Sanitize(fallback);
        }

        if (name.Length == 0)
        {
            name = "Sheet";
        }

        var candidate = name;
        for (var n = 2; IsReserved(candidate) || _used.Contains(candidate); n++)
        {
            var suffix = string.Create(CultureInfo.InvariantCulture, $" ({n})");
            candidate = name[..Math.Min(name.Length, MaxLength - suffix.Length)].TrimEnd() + suffix;
        }

        _used.Add(candidate);
        return candidate;
    }

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '[' or ']' or ':' or '*' or '?' or '/' or '\\')
            {
                sb.Append(' ');
            }
            else if (!char.IsControl(ch))
            {
                sb.Append(ch);
            }
        }

        var s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('\'').Trim();
        if (s.Length > MaxLength)
        {
            s = s[..MaxLength].TrimEnd().TrimEnd('\'');
            if (s.Length > 0 && char.IsHighSurrogate(s[^1]))
            {
                s = s[..^1];
            }
        }

        return s;
    }

    /// <summary>Excel reserves "History" for its change-tracking sheet.</summary>
    private static bool IsReserved(string name) => string.Equals(name, "History", StringComparison.OrdinalIgnoreCase);
}
