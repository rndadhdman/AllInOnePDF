using System.Globalization;
using System.Text;
using HarfBuzzSharp;
using Buffer = HarfBuzzSharp.Buffer;

namespace AllInOnePdf.Layout.Text;

/// <summary>One shaped glyph, in points.</summary>
/// <param name="GlyphId">Glyph index in the run's face.</param>
/// <param name="Advance">Horizontal advance, letter spacing included (added to the last glyph of each cluster).</param>
/// <param name="OffsetX">HarfBuzz x offset.</param>
/// <param name="OffsetY">HarfBuzz y offset, y-down (positive moves the glyph down).</param>
/// <param name="Cluster">UTF-16 index (into the shaped string) of the first character of the glyph's cluster.</param>
internal readonly record struct ShapedGlyph(ushort GlyphId, float Advance, float OffsetX, float OffsetY, int Cluster);

/// <summary>A piece of text shaped in one face: the characters <c>[Start, End)</c> of the source string.</summary>
internal sealed class ShapedRun(FontSelection font, float fontSize, int start, int end, ShapedGlyph[] glyphs)
{
    public FontSelection Font { get; } = font;

    public float FontSize { get; } = fontSize;

    public int Start { get; } = start;

    public int End { get; } = end;

    /// <summary>Glyphs in visual (left-to-right) order; clusters ascend.</summary>
    public ShapedGlyph[] Glyphs { get; } = glyphs;

    public float Width => Glyphs.Sum(g => g.Advance);
}

/// <summary>What a string is shaped with.</summary>
/// <param name="Fonts">Faces in order of preference (the style's family chain).</param>
/// <param name="FontSize">Effective size in points (already reduced for super/subscript).</param>
/// <param name="LetterSpacing">Extra advance after every cluster, in points.</param>
/// <param name="DisabledFeatures">OpenType features turned off ("liga", "calt").</param>
/// <param name="Weight">Requested weight (for system fallback matching).</param>
/// <param name="Italic">Requested slant (for system fallback matching).</param>
internal sealed record ShapingOptions(IReadOnlyList<FontSelection> Fonts, float FontSize, float LetterSpacing, IReadOnlyList<string> DisabledFeatures, int Weight, bool Italic);

/// <summary>
/// HarfBuzz shaping: splits a string into runs per face (the first face of the chain that has the character, then
/// the system's character fallback, else the primary face's .notdef) and shapes each run left to right, with script
/// and language guessed from the text and the style's disabled features turned off.
/// </summary>
internal static class TextShaper
{
    /// <summary>Shapes <paramref name="text"/>[<paramref name="start"/>..<paramref name="start"/>+<paramref name="length"/>) into runs; clusters index <paramref name="text"/>.</summary>
    public static List<ShapedRun> Shape(string text, int start, int length, ShapingOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        var runs = new List<ShapedRun>();
        if (length <= 0)
        {
            return runs;
        }

        var features = BuildFeatures(options.DisabledFeatures);
        foreach (var (font, runStart, runEnd) in Segment(text, start, start + length, options))
        {
            runs.Add(ShapeRun(text, runStart, runEnd, font, options, features));
        }

        return runs;
    }

    /// <summary>Shapes the whole string.</summary>
    public static List<ShapedRun> Shape(string text, ShapingOptions options) => Shape(text, 0, text.Length, options);

    /// <summary>
    /// Features off unless a style asks otherwise. QuestPDF's output (Skia's paragraph) never forms standard
    /// ligatures: in the reference corpus "fi", "ft" and "Th" are always two glyphs at their own advances, while
    /// kerning is applied. Matching that keeps line widths, and so line breaks, the same.
    /// </summary>
    internal static readonly IReadOnlyList<string> DefaultDisabledFeatures = ["liga", "clig"];

    private static Feature[] BuildFeatures(IReadOnlyList<string> disabled)
    {
        var list = new List<Feature>(disabled.Count + DefaultDisabledFeatures.Count);
        foreach (var name in disabled.Concat(DefaultDisabledFeatures).Distinct(StringComparer.Ordinal))
        {
            if (name.Length is >= 1 and <= 4)
            {
                list.Add(new Feature(Tag.Parse(name.PadRight(4)), 0));
            }
        }

        return [.. list];
    }

    private static ShapedRun ShapeRun(string text, int start, int end, FontSelection font, ShapingOptions options, Feature[] features)
    {
        using var buffer = new Buffer();
        buffer.AddUtf16(text, start, end - start);
        buffer.Direction = Direction.LeftToRight;
        buffer.GuessSegmentProperties();
        buffer.ClusterLevel = ClusterLevel.MonotoneCharacters;
        font.Face.HarfBuzzFont.Shape(buffer, features);

        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        var scale = options.FontSize / font.Face.UnitsPerEm;
        var advanceScale = AdvanceSize(options.FontSize) / font.Face.UnitsPerEm;
        var hb = font.Face.HarfBuzzFont;
        var glyphs = new ShapedGlyph[infos.Length];
        for (var i = 0; i < infos.Length; i++)
        {
            var cluster = (int)infos[i].Cluster;

            // The glyph's own advance at the 26.6 size, plus the shaper's adjustment (kerning) at the exact size.
            var nominal = hb.GetHorizontalGlyphAdvance(infos[i].Codepoint);
            var advance = (nominal * advanceScale) + ((positions[i].XAdvance - nominal) * scale);
            var lastOfCluster = i == infos.Length - 1 || (int)infos[i + 1].Cluster != cluster;
            if (lastOfCluster)
            {
                advance += options.LetterSpacing;
            }

            glyphs[i] = new ShapedGlyph((ushort)infos[i].Codepoint, advance, positions[i].XOffset * scale, -positions[i].YOffset * scale, cluster);
        }

        return new ShapedRun(font, options.FontSize, start, end, glyphs);
    }

    /// <summary>
    /// The size glyph advances are measured at: Skia (QuestPDF) takes them from its font scaler, which sets the size
    /// in 26.6 fixed point truncated (9.9 pt advances like 9.890625 pt), while kerning and the drawn glyphs use the
    /// exact size. Whole and 1/64 sizes are unaffected.
    /// </summary>
    internal static float AdvanceSize(float fontSize) => fontSize > 0 ? (int)(fontSize * 64f) / 64f : fontSize;

    /// <summary>Splits [start, end) into maximal ranges drawn with the same face.</summary>
    private static List<(FontSelection Font, int Start, int End)> Segment(string text, int start, int end, ShapingOptions options)
    {
        var result = new List<(FontSelection, int, int)>();
        var primary = options.Fonts[0];
        FontSelection? current = null;
        var runStart = start;
        var i = start;
        while (i < end)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i, end - i), out var rune, out var width);
            width = Math.Max(1, width);
            FontSelection chosen;
            if (current is not null && Sticks(rune))
            {
                chosen = current;
            }
            else
            {
                chosen = Pick(rune.Value, options) ?? primary;
            }

            if (current is null)
            {
                current = chosen;
            }
            else if (!ReferenceEquals(chosen, current))
            {
                result.Add((current, runStart, i));
                current = chosen;
                runStart = i;
            }

            i += width;
        }

        if (current is not null)
        {
            result.Add((current, runStart, end));
        }

        return result;
    }

    private static FontSelection? Pick(int codePoint, ShapingOptions options)
    {
        foreach (var font in options.Fonts)
        {
            if (font.Face.HasGlyph(codePoint))
            {
                return font;
            }
        }

        // Whitespace and controls the primary face lacks are not worth a system lookup.
        if (codePoint < 0x20 || Rune.IsWhiteSpace(new Rune(codePoint)))
        {
            return options.Fonts[0];
        }

        return FontRegistry.MatchCharacter(codePoint, options.Weight, options.Italic);
    }

    /// <summary>Characters that stay in the face of the character before them (marks, joiners, variation selectors).</summary>
    private static bool Sticks(Rune rune)
    {
        var v = rune.Value;
        if (v is 0x200C or 0x200D || v is >= 0xFE00 and <= 0xFE0F || v is >= 0xE0100 and <= 0xE01EF || v is >= 0x1F3FB and <= 0x1F3FF)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }
}
