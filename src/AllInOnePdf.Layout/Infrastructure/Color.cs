using System.Globalization;
using SkiaSharp;

namespace AllInOnePdf.Layout.Infrastructure;

/// <summary>An RGBA colour. Hex strings in the forms #RGB, #RRGGBB and #AARRGGBB (QuestPDF's and Skia's order) are accepted.</summary>
public readonly record struct Color(byte Red, byte Green, byte Blue, byte Alpha = 255)
{
    public static readonly Color Black = new(0, 0, 0);
    public static readonly Color White = new(255, 255, 255);
    public static readonly Color Transparent = new(0, 0, 0, 0);

    /// <summary>Parses a hex colour; throws <see cref="FormatException"/> for anything else (callers validate first).</summary>
    public static Color FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3)
        {
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }

        if (s.Length is not (6 or 8) || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"'{hex}' is not a hex colour such as #1F2937 or #CC1F2937.");
        }

        return s.Length == 6
            ? new Color((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new Color((byte)(value >> 16), (byte)(value >> 8), (byte)value, (byte)(value >> 24));
    }

    public static bool TryFromHex(string? hex, out Color color)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
            {
                color = FromHex(hex);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        color = Black;
        return false;
    }

    public SKColor ToSKColor() => new(Red, Green, Blue, Alpha);

    public Color WithAlpha(byte alpha) => this with { Alpha = alpha };

    /// <summary>#RRGGBB, or #AARRGGBB when the colour is not opaque (round-trips through <see cref="FromHex"/>).</summary>
    public override string ToString() => Alpha == 255
        ? $"#{Red:X2}{Green:X2}{Blue:X2}"
        : $"#{Alpha:X2}{Red:X2}{Green:X2}{Blue:X2}";

    public static implicit operator Color(string hex) => FromHex(hex);

    public static Color FromString(string hex) => FromHex(hex);
}
