using System.Runtime.CompilerServices;
using System.Text;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Text;

/// <summary>
/// The source text behind a <see cref="GlyphRun"/>: the characters the glyphs were shaped from and, per glyph, the
/// UTF-16 index of its cluster's first character in <see cref="Text"/>. The PDF canvas writes it next to the glyphs
/// so ligatures, alternates and fallback glyphs extract as the characters they stand for (Skia emits ActualText or
/// ToUnicode entries from the clusters, as it does for Skia's paragraph, which QuestPDF draws with).
/// </summary>
/// <param name="Text">The characters of the run, in logical order.</param>
/// <param name="Clusters">One entry per glyph: the UTF-16 index into <paramref name="Text"/> of the glyph's cluster.</param>
internal sealed record GlyphRunText(string Text, int[] Clusters)
{
    // Kept beside the run rather than inside it: GlyphRun belongs to the canvas contract (Infrastructure).
    private static readonly ConditionalWeakTable<GlyphRun, GlyphRunText> Attached = new();

    /// <summary>Remembers <paramref name="text"/> as the source of <paramref name="run"/> and answers the run.</summary>
    public static GlyphRun Attach(GlyphRun run, GlyphRunText text)
    {
        Attached.AddOrUpdate(run, text);
        return run;
    }

    /// <summary>The source text attached to <paramref name="run"/>, if any.</summary>
    public static GlyphRunText? Of(GlyphRun run) => Attached.TryGetValue(run, out var text) ? text : null;

    /// <summary>
    /// The text as UTF-8 and the clusters as byte offsets into it (what <c>SKTextBlobBuilder</c> takes), or null
    /// when the clusters do not fit the text or the glyph count.
    /// </summary>
    public (byte[] Utf8, uint[] Clusters)? ToUtf8(int glyphCount)
    {
        if (Text.Length == 0 || Clusters.Length < glyphCount)
        {
            return null;
        }

        // Byte offset of every UTF-16 index.
        var offsets = new int[Text.Length + 1];
        var bytes = 0;
        for (var i = 0; i < Text.Length; i++)
        {
            offsets[i] = bytes;
            if (char.IsHighSurrogate(Text[i]) && i + 1 < Text.Length && char.IsLowSurrogate(Text[i + 1]))
            {
                offsets[i + 1] = bytes;
                bytes += 4;
                i++;
                continue;
            }

            bytes += Encoding.UTF8.GetByteCount(Text.AsSpan(i, 1));
        }

        offsets[Text.Length] = bytes;
        var clusters = new uint[glyphCount];
        for (var g = 0; g < glyphCount; g++)
        {
            var c = Clusters[g];
            if (c < 0 || c >= Text.Length)
            {
                return null;
            }

            clusters[g] = (uint)offsets[c];
        }

        return (Encoding.UTF8.GetBytes(Text), clusters);
    }
}
