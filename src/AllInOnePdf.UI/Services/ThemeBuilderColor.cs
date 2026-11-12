using System.Globalization;
using System.Text.RegularExpressions;

namespace AllInOnePdf.UI.Services;

/// <summary>Colour helpers for the theme builder: validation, CSS conversion and WCAG contrast.</summary>
public static partial class ThemeBuilderColor
{
    /// <summary>True for #RGB, #RRGGBB and #AARRGGBB (the theme's colour formats).</summary>
    public static bool IsValid(string? hex) => hex is not null && HexPattern().IsMatch(hex.Trim());

    /// <summary>Returns a CSS colour for a theme hex value, or <paramref name="fallback"/> when invalid. Never returns untrusted text.</summary>
    public static string ToCss(string? hex, string fallback)
    {
        if (!IsValid(hex))
        {
            return IsValid(fallback) ? ToCss(fallback, "#000000") : "#000000";
        }

        var s = hex!.Trim().TrimStart('#');
        if (s.Length == 8)
        {
            // #AARRGGBB, the theme's colour format; CSS wants #RRGGBBAA.
            s = s[2..] + s[..2];
        }

        return "#" + s.ToUpperInvariant();
    }

    /// <summary>#RRGGBB for an &lt;input type="color"&gt; (alpha dropped).</summary>
    public static string ToRgb6(string? hex, string fallback = "#000000")
    {
        if (!IsValid(hex))
        {
            return IsValid(fallback) ? ToRgb6(fallback) : "#000000";
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

        return "#" + s.ToUpperInvariant();
    }

    public static (double R, double G, double B)? Parse(string? hex)
    {
        if (!IsValid(hex))
        {
            return null;
        }

        var s = ToRgb6(hex).TrimStart('#');
        var rgb = int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);
    }

    public static double RelativeLuminance(string? hex)
    {
        if (Parse(hex) is not { } c)
        {
            return 1;
        }

        static double Lin(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>WCAG contrast ratio (1..21); null when either colour is invalid.</summary>
    public static double? Contrast(string? a, string? b)
    {
        if (!IsValid(a) || !IsValid(b))
        {
            return null;
        }

        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Mirror of the PDF renderer's dark-page test (perceived luminance below 0.45).</summary>
    public static bool IsDark(string? hex)
    {
        if (Parse(hex) is not { } c)
        {
            return false;
        }

        return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) < 0.45;
    }

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexPattern();
}
