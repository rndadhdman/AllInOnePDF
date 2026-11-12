using System.Globalization;

namespace AllInOnePdf.UI.Services.Shared;

/// <summary>
/// Parsing, normalising and validating hex colours (<c>#RGB</c> / <c>#RRGGBB</c>) plus WCAG contrast helpers.
/// Shared by Settings (accent), the Convert options (colour fields) and anything that writes a colour into a style
/// attribute: always validate untrusted colours with <see cref="IsValid"/> or <see cref="Normalize"/> first.
/// </summary>
public static class HexColor
{
    /// <summary>True for <c>#RGB</c> or <c>#RRGGBB</c> (the leading <c>#</c> is required).</summary>
    public static bool IsValid(string? value) =>
        value is { Length: 4 or 7 } && value[0] == '#' && IsHexDigits(value.AsSpan(1));

    /// <summary>
    /// Accepts <c>#RGB</c> or <c>#RRGGBB</c>, with or without <c>#</c> and surrounding white space.
    /// Returns <c>#RRGGBB</c> in upper case, or null when the value is empty or invalid.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hex = value.Trim();
        if (hex.StartsWith('#'))
        {
            hex = hex[1..];
        }

        if (hex.Length is not (3 or 6) || !IsHexDigits(hex))
        {
            return null;
        }

        if (hex.Length == 3)
        {
            hex = string.Create(6, hex, static (span, h) =>
            {
                for (var i = 0; i < 3; i++)
                {
                    span[i * 2] = h[i];
                    span[(i * 2) + 1] = h[i];
                }
            });
        }

        return "#" + hex.ToUpperInvariant();
    }

    /// <summary>Lower-case <c>#rrggbb</c> for <c>&lt;input type="color"&gt;</c>, or <paramref name="fallback"/> when invalid.</summary>
    public static string ToColorInputValue(string? value, string fallback = "#000000") =>
        Normalize(value)?.ToLowerInvariant() ?? fallback;

    /// <summary>Parses a colour accepted by <see cref="Normalize"/> into its channels.</summary>
    public static bool TryParse(string? value, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var normalized = Normalize(value);
        if (normalized is null)
        {
            return false;
        }

        r = byte.Parse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        g = byte.Parse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        b = byte.Parse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>WCAG relative luminance (0 = black, 1 = white), or null when the colour is invalid.</summary>
    public static double? RelativeLuminance(string? value)
    {
        if (!TryParse(value, out var r, out var g, out var b))
        {
            return null;
        }

        return (0.2126 * Linear(r)) + (0.7152 * Linear(g)) + (0.0722 * Linear(b));

        static double Linear(byte channel)
        {
            var c = channel / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>WCAG contrast ratio between two colours (1–21), or null when either colour is invalid.</summary>
    public static double? ContrastRatio(string? foreground, string? background)
    {
        if (RelativeLuminance(foreground) is not { } a || RelativeLuminance(background) is not { } b)
        {
            return null;
        }

        var (light, dark) = a >= b ? (a, b) : (b, a);
        return (light + 0.05) / (dark + 0.05);
    }

    /// <summary>True when text in <paramref name="foreground"/> on <paramref name="background"/> meets <paramref name="minimum"/> (4.5 = WCAG AA body text).</summary>
    public static bool MeetsContrast(string? foreground, string? background, double minimum = 4.5) =>
        ContrastRatio(foreground, background) is { } ratio && ratio >= minimum;

    private static bool IsHexDigits(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return span.Length > 0;
    }
}
