using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using HarfBuzzSharp;
using SkiaSharp;

namespace AllInOnePdf.Layout.Text;

/// <summary>
/// One loaded font face: the Skia typeface used for drawing and the HarfBuzz face/font used for shaping, plus the
/// family names, weight and slant read from the font's own tables. Instances live for the life of the process (the
/// registry never unloads fonts) and are safe to use from several threads: HarfBuzz objects are made immutable, and
/// <see cref="SKFont"/> instances are cached per size and synthetic style and only read after creation.
/// </summary>
internal sealed class FontFace
{
    private const uint NameTag = ('n' << 24) | ('a' << 16) | ('m' << 8) | 'e';
    private const uint Os2Tag = ('O' << 24) | ('S' << 16) | ('/' << 8) | '2';

    private readonly ConcurrentDictionary<(float Size, bool Bold, bool Italic), SKFont> _fonts = new();
    private readonly ConcurrentDictionary<int, bool> _glyphCoverage = new();

    private FontFace(SKTypeface typeface, HarfBuzzSharp.Font font, int unitsPerEm, IReadOnlyList<string> familyNames, int weight, bool isItalic)
    {
        Typeface = typeface;
        HarfBuzzFont = font;
        UnitsPerEm = unitsPerEm;
        FamilyNames = familyNames;
        Weight = weight;
        IsItalic = isItalic;
        Metrics = ReadMetrics(font, unitsPerEm);
    }

    /// <summary>The typeface glyph runs are drawn with.</summary>
    public SKTypeface Typeface { get; }

    /// <summary>The (immutable) HarfBuzz font used for shaping; its scale is <see cref="UnitsPerEm"/>.</summary>
    public HarfBuzzSharp.Font HarfBuzzFont { get; }

    public int UnitsPerEm { get; }

    /// <summary>The family names the face answers to: the typographic family (name ID 16) first, then the legacy family (ID 1).</summary>
    public IReadOnlyList<string> FamilyNames { get; }

    /// <summary>The preferred family name.</summary>
    public string FamilyName => FamilyNames[0];

    /// <summary>CSS weight (100–900) from the OS/2 table.</summary>
    public int Weight { get; }

    public bool IsItalic { get; }

    /// <summary>Vertical metrics as fractions of the em (multiply by the font size).</summary>
    public FaceMetrics Metrics { get; }

    /// <summary>True when the face maps <paramref name="codePoint"/> to a real glyph (not .notdef).</summary>
    public bool HasGlyph(int codePoint) =>
        _glyphCoverage.GetOrAdd(codePoint, static (cp, font) => font.TryGetGlyph((uint)cp, out var glyph) && glyph != 0, HarfBuzzFont);

    /// <summary>A cached Skia font at <paramref name="size"/>, with synthetic bold (embolden) and italic (skew) when asked.</summary>
    public SKFont GetFont(float size, bool syntheticBold, bool syntheticItalic) =>
        _fonts.GetOrAdd((size, syntheticBold, syntheticItalic), static (key, typeface) => new SKFont(typeface, key.Size)
        {
            Subpixel = true,
            Hinting = SKFontHinting.None,
            Edging = SKFontEdging.Antialias,
            LinearMetrics = true,
            Embolden = key.Bold,
            SkewX = key.Italic ? -0.25f : 0,
        }, Typeface);

    /// <summary>Loads a face from font file bytes; null when the data is not a font Skia and HarfBuzz can read.</summary>
    public static FontFace? FromData(byte[] data, int index = 0, SKTypeface? typeface = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12)
        {
            return null;
        }

        typeface ??= SKTypeface.FromData(SKData.CreateCopy(data), index);
        if (typeface is null)
        {
            return null;
        }

        // HarfBuzz reads the font from unmanaged memory that lives as long as the blob (forever, in practice).
        var memory = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, memory, data.Length);
        var blob = new Blob(memory, data.Length, MemoryMode.ReadOnly, () => Marshal.FreeHGlobal(memory));
        blob.MakeImmutable();
        var face = new Face(blob, index);
        var unitsPerEm = face.UnitsPerEm;
        if (unitsPerEm <= 0 || face.GlyphCount == 0)
        {
            face.Dispose();
            blob.Dispose();
            return null;
        }

        face.MakeImmutable();
        var font = new HarfBuzzSharp.Font(face);
        font.SetFunctionsOpenType();
        font.SetScale(unitsPerEm, unitsPerEm);

        // hb_font_t is safe to shape with from several threads once configured; nothing changes it after this point.

        var names = ReadFamilyNames(typeface);
        var (weight, italic) = ReadStyle(typeface);
        return new FontFace(typeface, font, unitsPerEm, names, weight, italic);
    }

    /// <summary>Loads a face Skia found on the system (reads the font bytes back from the typeface for HarfBuzz).</summary>
    public static FontFace? FromSystemTypeface(SKTypeface typeface)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        using var stream = typeface.OpenStream(out var ttcIndex);
        if (stream is null)
        {
            return null;
        }

        using var data = SKData.Create(stream);
        if (data is null || data.Size == 0)
        {
            return null;
        }

        return FromData(data.ToArray(), ttcIndex, typeface);
    }

    private static FaceMetrics ReadMetrics(HarfBuzzSharp.Font font, int unitsPerEm)
    {
        float em = unitsPerEm;
        float ascent = 0.8f, descent = 0.2f;
        if (font.TryGetHorizontalFontExtents(out var extents) && extents.Ascender - extents.Descender > 0)
        {
            ascent = extents.Ascender / em;
            descent = -extents.Descender / em;
        }

        float? Metric(OpenTypeMetricsTag tag) =>
            font.OpenTypeMetrics.TryGetPosition(tag, out var value) ? value / em : null;

        // HarfBuzz reports offsets in y-up font space; the engine uses y-down (positive = below the baseline).
        var underlineOffset = Metric(OpenTypeMetricsTag.UnderlineOffset);
        var strikeoutOffset = Metric(OpenTypeMetricsTag.StrikeoutOffset);
        return new FaceMetrics(
            ascent,
            descent,
            underlineOffset is { } u ? -u : null,
            Metric(OpenTypeMetricsTag.UnderlineSize),
            strikeoutOffset is { } s ? -s : null,
            Metric(OpenTypeMetricsTag.StrikeoutSize),
            Metric(OpenTypeMetricsTag.XHeight) ?? 0.5f);
    }

    private static (int Weight, bool Italic) ReadStyle(SKTypeface typeface)
    {
        var weight = typeface.FontWeight;
        var italic = typeface.FontSlant != SKFontStyleSlant.Upright;
        try
        {
            var os2 = typeface.GetTableData(Os2Tag);
            if (os2 is { Length: >= 64 })
            {
                var weightClass = (os2[4] << 8) | os2[5];
                if (weightClass is >= 1 and <= 1000)
                {
                    weight = weightClass;
                }

                var fsSelection = (os2[62] << 8) | os2[63];
                italic = (fsSelection & 0x0001) != 0 || (fsSelection & 0x0200) != 0;
            }
        }
        catch (ArgumentException)
        {
            // Some platform typefaces do not expose their tables; keep Skia's style.
        }

        return (weight, italic);
    }

    private static List<string> ReadFamilyNames(SKTypeface typeface)
    {
        var names = new List<string>();
        try
        {
            var table = typeface.GetTableData(NameTag);
            if (table is { Length: > 6 })
            {
                foreach (var id in (int[])[16, 1])
                {
                    var name = ReadName(table, id);
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(name.Trim());
                    }
                }
            }
        }
        catch (ArgumentException)
        {
            // Table access not supported: fall back to Skia's family name below.
        }

        var skiaName = typeface.FamilyName;
        if (!string.IsNullOrWhiteSpace(skiaName) && !names.Contains(skiaName, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(skiaName);
        }

        if (names.Count == 0)
        {
            names.Add("Unknown");
        }

        return names;
    }

    /// <summary>Reads one name record, preferring Windows English (UTF-16BE), then any Windows/Unicode record, then Mac Roman.</summary>
    private static string? ReadName(byte[] table, int nameId)
    {
        int U16(int offset) => offset + 1 < table.Length ? (table[offset] << 8) | table[offset + 1] : 0;

        var count = U16(2);
        var storage = U16(4);
        string? best = null;
        var bestRank = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var record = 6 + (i * 12);
            if (record + 12 > table.Length)
            {
                break;
            }

            if (U16(record + 6) != nameId)
            {
                continue;
            }

            var platform = U16(record);
            var language = U16(record + 4);
            var length = U16(record + 8);
            var offset = storage + U16(record + 10);
            if (offset + length > table.Length || length == 0)
            {
                continue;
            }

            var rank = platform switch
            {
                3 when language == 0x409 => 0,
                3 or 0 => 1,
                1 when language == 0 => 2,
                _ => 3,
            };
            if (rank >= bestRank || rank == 3)
            {
                continue;
            }

            best = platform == 1
                ? Encoding.Latin1.GetString(table, offset, length)
                : Encoding.BigEndianUnicode.GetString(table, offset, length);
            bestRank = rank;
        }

        return best;
    }
}

/// <summary>Vertical font metrics as fractions of the em; positions are y-down (positive below the baseline).</summary>
/// <param name="Ascent">Distance from the baseline up to the ascender line (positive).</param>
/// <param name="Descent">Distance from the baseline down to the descender line (positive).</param>
/// <param name="UnderlinePosition">Underline centre below the baseline, when the font declares it.</param>
/// <param name="UnderlineThickness">Underline thickness, when the font declares it.</param>
/// <param name="StrikeoutPosition">Strikeout centre (negative: above the baseline), when declared.</param>
/// <param name="StrikeoutThickness">Strikeout thickness, when declared.</param>
/// <param name="XHeight">Height of lower-case x.</param>
internal readonly record struct FaceMetrics(
    float Ascent,
    float Descent,
    float? UnderlinePosition,
    float? UnderlineThickness,
    float? StrikeoutPosition,
    float? StrikeoutThickness,
    float XHeight);
