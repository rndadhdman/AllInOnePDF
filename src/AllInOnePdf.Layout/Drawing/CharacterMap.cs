using System.Buffers.Binary;

namespace AllInOnePdf.Layout.Drawing;

/// <summary>A character-to-glyph mapping read from or written to a TrueType <c>cmap</c> table.</summary>
/// <param name="CodePoint">The Unicode scalar value.</param>
/// <param name="Glyph">The glyph id it maps to.</param>
internal readonly record struct CharacterMapping(int CodePoint, ushort Glyph);

/// <summary>
/// Reads the Unicode mappings of a <c>cmap</c> table (formats 4 and 12) and writes a compact one (format 4 for the
/// BMP, plus format 12 when characters lie beyond it), for <see cref="TrueTypeSubsetter"/>. Stateless and thread-safe.
/// </summary>
internal static class CharacterMap
{
    /// <summary>Glyph mappings beyond this many are not expanded (a cmap mapping huge ranges is kept as it is).</summary>
    private const int MaxMappings = 200_000;

    /// <summary>
    /// The Unicode mappings (glyph 0 excluded), from the full-repertoire subtable when there is one, otherwise the BMP
    /// one; null for symbol fonts, other encodings and malformed tables (the subsetter keeps the table as it is then).
    /// </summary>
    public static List<CharacterMapping>? Read(ReadOnlySpan<byte> cmap)
    {
        if (cmap.Length < 4)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(cmap[2..]);
        int? full = null, bmp = null;
        for (var i = 0; i < count; i++)
        {
            var record = 4 + (i * 8);
            if (record + 8 > cmap.Length)
            {
                return null;
            }

            var platform = BinaryPrimitives.ReadUInt16BigEndian(cmap[record..]);
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(cmap[(record + 2)..]);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(cmap[(record + 4)..]);
            if (offset + 4 > cmap.Length || offset < 0)
            {
                return null;
            }

            if (platform == 3 && encoding == 0)
            {
                return null; // symbol font: its mapping is not Unicode
            }

            var format = BinaryPrimitives.ReadUInt16BigEndian(cmap[offset..]);
            var unicode = platform == 0 || (platform == 3 && encoding is 1 or 10);
            if (!unicode)
            {
                continue;
            }

            if (format == 12)
            {
                full ??= offset;
            }
            else if (format == 4)
            {
                bmp ??= offset;
            }
        }

        if (full is { } f)
        {
            return ReadFormat12(cmap[f..]);
        }

        return bmp is { } b ? ReadFormat4(cmap[b..]) : null;
    }

    /// <summary>A cmap table for <paramref name="mappings"/>; null when they do not fit a format 4 subtable.</summary>
    public static byte[]? Write(IReadOnlyList<CharacterMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var sorted = mappings
            .Where(static m => m.Glyph != 0 && m.CodePoint is >= 0 and <= 0x10FFFF and not 0xFFFF)
            .DistinctBy(static m => m.CodePoint)
            .OrderBy(static m => m.CodePoint)
            .ToList();

        var format4 = Format4(sorted.Where(static m => m.CodePoint <= 0xFFFF).ToList());
        if (format4 is null)
        {
            return null;
        }

        var format12 = sorted.Exists(static m => m.CodePoint > 0xFFFF) ? Format12(sorted) : null;
        var tables = format12 is null ? 1 : 2;
        var header = 4 + (8 * tables);
        var table = new byte[header + format4.Length + (format12?.Length ?? 0)];
        var span = table.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)tables);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], 3);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], (uint)header);
        format4.CopyTo(span[header..]);
        if (format12 is not null)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[12..], 3);
            BinaryPrimitives.WriteUInt16BigEndian(span[14..], 10);
            BinaryPrimitives.WriteUInt32BigEndian(span[16..], (uint)(header + format4.Length));
            format12.CopyTo(span[(header + format4.Length)..]);
        }

        return table;
    }

    private static List<CharacterMapping>? ReadFormat4(ReadOnlySpan<byte> table)
    {
        if (table.Length < 14)
        {
            return null;
        }

        var segments = BinaryPrimitives.ReadUInt16BigEndian(table[6..]) / 2;
        var ends = 14;
        var starts = ends + (segments * 2) + 2;
        var deltas = starts + (segments * 2);
        var ranges = deltas + (segments * 2);
        if (ranges + (segments * 2) > table.Length)
        {
            return null;
        }

        var result = new List<CharacterMapping>();
        for (var s = 0; s < segments; s++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(table[(ends + (s * 2))..]);
            int start = BinaryPrimitives.ReadUInt16BigEndian(table[(starts + (s * 2))..]);
            var delta = BinaryPrimitives.ReadInt16BigEndian(table[(deltas + (s * 2))..]);
            int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(table[(ranges + (s * 2))..]);
            for (var cp = start; cp <= end && cp != 0xFFFF; cp++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (cp + delta) & 0xFFFF;
                }
                else
                {
                    var at = ranges + (s * 2) + rangeOffset + ((cp - start) * 2);
                    if (at + 2 > table.Length)
                    {
                        return null;
                    }

                    glyph = BinaryPrimitives.ReadUInt16BigEndian(table[at..]);
                    if (glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xFFFF;
                    }
                }

                if (glyph != 0)
                {
                    result.Add(new CharacterMapping(cp, (ushort)glyph));
                }
            }
        }

        return result;
    }

    private static List<CharacterMapping>? ReadFormat12(ReadOnlySpan<byte> table)
    {
        if (table.Length < 16)
        {
            return null;
        }

        var groups = BinaryPrimitives.ReadUInt32BigEndian(table[12..]);
        if (16 + ((long)groups * 12) > table.Length)
        {
            return null;
        }

        var result = new List<CharacterMapping>();
        for (var g = 0; g < groups; g++)
        {
            var group = table[(16 + (g * 12))..];
            var start = BinaryPrimitives.ReadUInt32BigEndian(group);
            var end = Math.Min(BinaryPrimitives.ReadUInt32BigEndian(group[4..]), 0x10FFFFu);
            var glyph = BinaryPrimitives.ReadUInt32BigEndian(group[8..]);
            if (end < start)
            {
                continue;
            }

            if (result.Count + (end - start) > MaxMappings)
            {
                return null;
            }

            for (var cp = start; cp <= end; cp++, glyph++)
            {
                if (glyph is > 0 and <= 0xFFFF)
                {
                    result.Add(new CharacterMapping((int)cp, (ushort)glyph));
                }
            }
        }

        return result;
    }

    /// <summary>Runs of consecutive characters mapped to consecutive glyphs.</summary>
    private static List<(int Start, int End, int Glyph)> Runs(List<CharacterMapping> sorted)
    {
        var runs = new List<(int Start, int End, int Glyph)>();
        foreach (var m in sorted)
        {
            if (runs.Count > 0 && runs[^1] is var last && m.CodePoint == last.End + 1 && m.Glyph == last.Glyph + (m.CodePoint - last.Start))
            {
                runs[^1] = (last.Start, m.CodePoint, last.Glyph);
            }
            else
            {
                runs.Add((m.CodePoint, m.CodePoint, m.Glyph));
            }
        }

        return runs;
    }

    private static byte[]? Format4(List<CharacterMapping> bmp)
    {
        var runs = Runs(bmp);
        runs.Add((0xFFFF, 0xFFFF, 0)); // the required closing segment (maps to .notdef)
        var segments = runs.Count;
        var length = 16 + (segments * 8);
        if (length > 0xFFFF)
        {
            return null;
        }

        var entrySelector = 0;
        while ((2 << entrySelector) <= segments)
        {
            entrySelector++;
        }

        var searchRange = 2 << entrySelector;
        var table = new byte[length];
        var span = table.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, 4);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)length);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)(segments * 2));
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..], (ushort)((segments * 2) - searchRange));
        var ends = 14;
        var starts = ends + (segments * 2) + 2;
        var deltas = starts + (segments * 2);
        for (var s = 0; s < segments; s++)
        {
            var (start, end, glyph) = runs[s];
            var delta = s == segments - 1 ? 1 : (glyph - start) & 0xFFFF;
            BinaryPrimitives.WriteUInt16BigEndian(span[(ends + (s * 2))..], (ushort)end);
            BinaryPrimitives.WriteUInt16BigEndian(span[(starts + (s * 2))..], (ushort)start);
            BinaryPrimitives.WriteUInt16BigEndian(span[(deltas + (s * 2))..], (ushort)delta);
        }

        // idRangeOffset entries stay zero.
        return table;
    }

    private static byte[] Format12(List<CharacterMapping> sorted)
    {
        var runs = Runs(sorted);
        var length = 16 + (runs.Count * 12);
        var table = new byte[length];
        var span = table.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, 12);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], (uint)runs.Count);
        for (var i = 0; i < runs.Count; i++)
        {
            var group = span[(16 + (i * 12))..];
            BinaryPrimitives.WriteUInt32BigEndian(group, (uint)runs[i].Start);
            BinaryPrimitives.WriteUInt32BigEndian(group[4..], (uint)runs[i].End);
            BinaryPrimitives.WriteUInt32BigEndian(group[8..], (uint)runs[i].Glyph);
        }

        return table;
    }
}
