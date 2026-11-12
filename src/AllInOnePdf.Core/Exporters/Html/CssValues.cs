using System.Globalization;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Fonts;
using ThemeFontWeight = AllInOnePdf.Core.Themes.FontWeight;

namespace AllInOnePdf.Core.Exporters.Html;

/// <summary>
/// Safe conversion of theme values to CSS. Themes are imported from untrusted JSON, so colours are validated and
/// font names stripped before they reach the stylesheet. Mirrors the theme builder's helpers (UI project) in Core.
/// </summary>
internal static partial class CssValues
{
    /// <summary>True for #RGB, #RRGGBB and #AARRGGBB (the formats themes use).</summary>
    public static bool IsValidColor(string? hex) => hex is not null && HexPattern().IsMatch(hex.Trim());

    /// <summary>A CSS colour for a theme hex value (#AARRGGBB, the theme's colour format, becomes CSS #RRGGBBAA), or the fallback.</summary>
    public static string Color(string? hex, string? fallback)
    {
        if (!IsValidColor(hex))
        {
            return IsValidColor(fallback) ? Color(fallback, "#000000") : "#000000";
        }

        var s = hex!.Trim().TrimStart('#');
        if (s.Length == 8)
        {
            s = s[2..] + s[..2];
        }

        return "#" + s.ToUpperInvariant();
    }

    /// <summary>Same dark-colour test as the PDF renderer (perceived luminance below 0.45).</summary>
    public static bool IsDark(string? hex)
    {
        if (!IsValidColor(hex))
        {
            return false;
        }

        var s = hex!.Trim().TrimStart('#');
        if (s.Length == 3)
        {
            s = string.Concat(s.Select(ch => new string(ch, 2)));
        }
        else if (s.Length == 8)
        {
            s = s[2..];
        }

        var rgb = int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var luminance = (0.2126 * ((rgb >> 16) & 0xFF) + 0.7152 * ((rgb >> 8) & 0xFF) + 0.0722 * (rgb & 0xFF)) / 255.0;
        return luminance < 0.45;
    }

    /// <summary>Quoted font stack: the theme font, then a generic family matching its class (sans, serif, mono).</summary>
    public static string FontStack(string? family)
    {
        var name = SafeFontName(family);
        if (string.IsNullOrEmpty(name))
        {
            name = BundledFonts.Sans;
        }

        var generic = BundledFonts.Families.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Class switch
        {
            FontClass.Serif => "serif",
            FontClass.Mono => "ui-monospace,\"Cascadia Mono\",Consolas,monospace",
            _ => "system-ui,-apple-system,\"Segoe UI\",sans-serif",
        };
        return $"\"{name}\",{generic}";
    }

    /// <summary>Strips anything that could break out of a quoted CSS string.</summary>
    public static string SafeFontName(string? family) =>
        family is null ? string.Empty : FontNameStrip().Replace(family, string.Empty).Trim();

    public static string Weight(ThemeFontWeight w) => w switch
    {
        ThemeFontWeight.Medium => "500",
        ThemeFontWeight.SemiBold => "600",
        ThemeFontWeight.Bold => "700",
        _ => "400",
    };

    /// <summary>Invariant number with at most three decimals; non-finite values become 0.</summary>
    public static string N(double value) => Math.Round(double.IsFinite(value) ? value : 0, 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Escapes text for a CSS string literal in double quotes.</summary>
    public static string CssString(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (var ch in text)
        {
            if (ch is '"' or '\\' or '<' or '>' || char.IsControl(ch))
            {
                sb.Append('\\').Append(((int)ch).ToString("X", CultureInfo.InvariantCulture)).Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.Append('"').ToString();
    }

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"[^\p{L}\p{N} _\-\.]")]
    private static partial Regex FontNameStrip();
}
