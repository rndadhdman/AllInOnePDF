using System.Collections.Frozen;
using System.Globalization;
using SkiaSharp;

namespace AllInOnePdf.Layout.Svg;

/// <summary>SVG/CSS colour values: <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>, <c>rgb()</c>/<c>rgba()</c> and the CSS named colours.</summary>
public static class SvgColors
{
    // The 148 CSS Color Module Level 4 named colours (grey/gray spellings included).
    private const string Table =
        "aliceblue f0f8ff antiquewhite faebd7 aqua 00ffff aquamarine 7fffd4 azure f0ffff beige f5f5dc bisque ffe4c4 " +
        "black 000000 blanchedalmond ffebcd blue 0000ff blueviolet 8a2be2 brown a52a2a burlywood deb887 cadetblue 5f9ea0 " +
        "chartreuse 7fff00 chocolate d2691e coral ff7f50 cornflowerblue 6495ed cornsilk fff8dc crimson dc143c cyan 00ffff " +
        "darkblue 00008b darkcyan 008b8b darkgoldenrod b8860b darkgray a9a9a9 darkgreen 006400 darkgrey a9a9a9 " +
        "darkkhaki bdb76b darkmagenta 8b008b darkolivegreen 556b2f darkorange ff8c00 darkorchid 9932cc darkred 8b0000 " +
        "darksalmon e9967a darkseagreen 8fbc8f darkslateblue 483d8b darkslategray 2f4f4f darkslategrey 2f4f4f " +
        "darkturquoise 00ced1 darkviolet 9400d3 deeppink ff1493 deepskyblue 00bfff dimgray 696969 dimgrey 696969 " +
        "dodgerblue 1e90ff firebrick b22222 floralwhite fffaf0 forestgreen 228b22 fuchsia ff00ff gainsboro dcdcdc " +
        "ghostwhite f8f8ff gold ffd700 goldenrod daa520 gray 808080 green 008000 greenyellow adff2f grey 808080 " +
        "honeydew f0fff0 hotpink ff69b4 indianred cd5c5c indigo 4b0082 ivory fffff0 khaki f0e68c lavender e6e6fa " +
        "lavenderblush fff0f5 lawngreen 7cfc00 lemonchiffon fffacd lightblue add8e6 lightcoral f08080 lightcyan e0ffff " +
        "lightgoldenrodyellow fafad2 lightgray d3d3d3 lightgreen 90ee90 lightgrey d3d3d3 lightpink ffb6c1 " +
        "lightsalmon ffa07a lightseagreen 20b2aa lightskyblue 87cefa lightslategray 778899 lightslategrey 778899 " +
        "lightsteelblue b0c4de lightyellow ffffe0 lime 00ff00 limegreen 32cd32 linen faf0e6 magenta ff00ff maroon 800000 " +
        "mediumaquamarine 66cdaa mediumblue 0000cd mediumorchid ba55d3 mediumpurple 9370db mediumseagreen 3cb371 " +
        "mediumslateblue 7b68ee mediumspringgreen 00fa9a mediumturquoise 48d1cc mediumvioletred c71585 " +
        "midnightblue 191970 mintcream f5fffa mistyrose ffe4e1 moccasin ffe4b5 navajowhite ffdead navy 000080 " +
        "oldlace fdf5e6 olive 808000 olivedrab 6b8e23 orange ffa500 orangered ff4500 orchid da70d6 palegoldenrod eee8aa " +
        "palegreen 98fb98 paleturquoise afeeee palevioletred db7093 papayawhip ffefd5 peachpuff ffdab9 peru cd853f " +
        "pink ffc0cb plum dda0dd powderblue b0e0e6 purple 800080 rebeccapurple 663399 red ff0000 rosybrown bc8f8f " +
        "royalblue 4169e1 saddlebrown 8b4513 salmon fa8072 sandybrown f4a460 seagreen 2e8b57 seashell fff5ee " +
        "sienna a0522d silver c0c0c0 skyblue 87ceeb slateblue 6a5acd slategray 708090 slategrey 708090 snow fffafa " +
        "springgreen 00ff7f steelblue 4682b4 tan d2b48c teal 008080 thistle d8bfd8 tomato ff6347 turquoise 40e0d0 " +
        "violet ee82ee wheat f5deb3 white ffffff whitesmoke f5f5f5 yellow ffff00 yellowgreen 9acd32";

    private static readonly FrozenDictionary<string, SKColor> Named = BuildNamed();

    /// <summary>Every named colour the parser knows.</summary>
    public static IReadOnlyCollection<string> Names => Named.Keys;

    /// <summary>Parses a paint value; false for anything not understood (the caller then inherits).</summary>
    public static bool TryParsePaint(string? value, out SvgPaint paint)
    {
        paint = SvgPaint.None;
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        if (s.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryParseColor(s, out var color))
        {
            paint = SvgPaint.FromColor(color);
            return true;
        }

        return false;
    }

    /// <summary>Parses a colour value (not <c>none</c>).</summary>
    public static bool TryParseColor(string? value, out SKColor color)
    {
        color = SKColors.Black;
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        if (s[0] == '#')
        {
            return TryParseHex(s[1..], out color);
        }

        if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRgb(s, out color);
        }

        if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            // CSS: transparent black (SKColors.Transparent is transparent white).
            color = new SKColor(0, 0, 0, 0);
            return true;
        }

        return Named.TryGetValue(s, out color);
    }

    private static bool TryParseHex(string hex, out SKColor color)
    {
        color = SKColors.Black;
        if (hex.Length is 3 or 4)
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }

        color = hex.Length == 6
            ? new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new SKColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    private static bool TryParseRgb(string s, out SKColor color)
    {
        color = SKColors.Black;
        var open = s.IndexOf('(', StringComparison.Ordinal);
        var close = s.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return false;
        }

        var parts = s[(open + 1)..close].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is not (3 or 4))
        {
            return false;
        }

        var channels = new byte[4];
        channels[3] = 255;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var percent = part.EndsWith('%');
            if (!float.TryParse(percent ? part[..^1] : part, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }

            // Colour channels: 0–255 or 0–100 %; alpha: 0–1 or 0–100 %.
            var unit = i == 3 ? (percent ? n / 100f : n) : (percent ? n / 100f : n / 255f);
            channels[i] = (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);
        }

        color = new SKColor(channels[0], channels[1], channels[2], channels[3]);
        return true;
    }

    private static FrozenDictionary<string, SKColor> BuildNamed()
    {
        var tokens = Table.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var map = new Dictionary<string, SKColor>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < tokens.Length; i += 2)
        {
            var v = uint.Parse(tokens[i + 1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            map[tokens[i]] = new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
