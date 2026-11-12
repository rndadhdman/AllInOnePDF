using System.Buffers.Binary;
using System.Globalization;

namespace AllInOnePdf.Core.Exporters.Docx;

/// <summary>Unit conversions and colour helpers for WordprocessingML.</summary>
internal static class DocxUnits
{
    public const long EmuPerPixel = 9525;
    public const long EmuPerPoint = 12700;

    /// <summary>Twentieths of a point.</summary>
    public static int Twips(double pt) => (int)Math.Round(pt * 20);

    public static string TwipsString(double pt) => Twips(pt).ToString(CultureInfo.InvariantCulture);

    public static string HalfPoints(double pt) => Math.Max(2, (int)Math.Round(pt * 2)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Border widths are in eighths of a point (2..96).</summary>
    public static uint Eighths(double pt) => (uint)Math.Clamp((int)Math.Round(pt * 8), 2, 96);

    /// <summary>Border spacing is in whole points (0..31).</summary>
    public static uint BorderSpace(double pt) => (uint)Math.Clamp((int)Math.Round(pt), 0, 31);

    public static uint MmToTwips(double mm) => (uint)Math.Max(0, Math.Round(mm / 25.4 * 1440));

    public static double MmToPt(double mm) => mm * 72.0 / 25.4;

    /// <summary>Line spacing in 240ths of a line ("auto" rule).</summary>
    public static string Line(double multiple) => ((int)Math.Round(Math.Clamp(multiple, 0.5, 5) * 240)).ToString(CultureInfo.InvariantCulture);

    /// <summary>"#RRGGBB" / "#RGB" / "#AARRGGBB" → "RRGGBB"; null when invalid.</summary>
    public static string? Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var s = value.Trim().TrimStart('#');
        if (s.Length == 8)
        {
            s = s[2..];
        }

        if (s.Length == 3)
        {
            s = string.Concat(s.Select(ch => new string(ch, 2)));
        }

        return s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
            ? s.ToUpperInvariant()
            : null;
    }

    public static string Hex(string? value, string fallback) => Hex(value) ?? Hex(fallback) ?? "000000";

    public static bool IsDark(string? hex)
    {
        var s = Hex(hex);
        if (s is null)
        {
            return false;
        }

        var rgb = int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var luminance = (0.2126 * ((rgb >> 16) & 0xFF) + 0.7152 * ((rgb >> 8) & 0xFF) + 0.0722 * (rgb & 0xFF)) / 255.0;
        return luminance < 0.45;
    }
}

/// <summary>Reads pixel dimensions from PNG, JPEG, GIF and BMP headers (no decoding).</summary>
internal static class ImageDimensions
{
    public static (int Width, int Height)? Read(byte[] data)
    {
        try
        {
            if (data.Length >= 24 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                return (BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20)));
            }

            if (data.Length >= 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
            {
                return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8)));
            }

            if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M')
            {
                return (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18)), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22))));
            }

            if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
            {
                var i = 2;
                while (i + 9 < data.Length)
                {
                    if (data[i] != 0xFF)
                    {
                        i++;
                        continue;
                    }

                    var marker = data[i + 1];
                    if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
                    {
                        i += 2;
                        continue;
                    }

                    var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 2));
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    {
                        var h = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 5));
                        var w = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 7));
                        return (w, h);
                    }

                    i += 2 + length;
                }
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        return null;
    }
}
