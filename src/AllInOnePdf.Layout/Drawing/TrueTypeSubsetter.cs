using System.Buffers.Binary;

namespace AllInOnePdf.Layout.Drawing;

/// <summary>
/// Cuts a TrueType font (<c>glyf</c> outlines) down to the glyphs a document draws, keeping every glyph id: unused
/// glyphs become empty (zero-length <c>loca</c> entries), so positions, advances (<c>hmtx</c> is kept whole) and the
/// text engine's clusters stay valid and the PDF's <c>CIDToGIDMap</c> stays the identity. Composite glyphs keep their
/// components. The tables a rasteriser and Skia need are copied (<c>head</c>, <c>hhea</c>, <c>maxp</c>, <c>hmtx</c>,
/// <c>cmap</c>, <c>name</c>, <c>OS/2</c>, the hinting tables <c>cvt </c>, <c>fpgm</c>, <c>prep</c>, <c>gasp</c>, and
/// <c>post</c> reduced to format 3); the layout tables (<c>GSUB</c>, <c>GPOS</c>, <c>kern</c>…) are dropped, since
/// shaping happens on the original font. Fonts without plain <c>glyf</c> outlines (CFF, variable fonts) are not
/// subset. Stateless and thread-safe.
/// </summary>
internal static class TrueTypeSubsetter
{
    private const uint TtcTag = 0x74746366; // 'ttcf'
    private const uint TrueTag = 0x74727565; // 'true'
    private const uint VersionOne = 0x00010000;

    /// <summary>The tables copied into the subset as they are (besides the rebuilt <c>loca</c>/<c>glyf</c> and the patched <c>head</c>/<c>post</c>).</summary>
    private static readonly HashSet<uint> CopiedTables =
    [
        Tag("hhea"), Tag("maxp"), Tag("hmtx"), Tag("cmap"), Tag("name"), Tag("OS/2"), Tag("cvt "), Tag("fpgm"), Tag("prep"), Tag("gasp"),
    ];

    /// <summary>Tables that mark a font this subsetter does not handle (CFF outlines, variations).</summary>
    private static readonly HashSet<uint> UnsupportedTables = [Tag("CFF "), Tag("CFF2"), Tag("fvar"), Tag("gvar")];

    // Composite glyph flags (OpenType 'glyf' specification).
    private const int ArgsAreWords = 0x0001;
    private const int HaveScale = 0x0008;
    private const int MoreComponents = 0x0020;
    private const int HaveXYScale = 0x0040;
    private const int HaveTwoByTwo = 0x0080;

    /// <summary>
    /// A font file holding only the outlines of <paramref name="glyphs"/> (plus .notdef and the components of
    /// composite glyphs), with every glyph id unchanged; null when the font is not a plain TrueType font or its tables
    /// are inconsistent (embed the original then).
    /// </summary>
    /// <param name="data">The font file (TTF, or a TTC with <paramref name="faceIndex"/>).</param>
    /// <param name="faceIndex">The face inside a collection; 0 for a single font.</param>
    /// <param name="glyphs">The glyph ids to keep; ids past the font's glyph count are ignored.</param>
    public static byte[]? Subset(ReadOnlySpan<byte> data, int faceIndex, IEnumerable<ushort> glyphs)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        try
        {
            return SubsetCore(data, faceIndex, glyphs);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            // Truncated or malformed tables.
            return null;
        }
    }

    /// <summary>True when <paramref name="data"/> is a font this class can subset (plain <c>glyf</c> outlines).</summary>
    public static bool CanSubset(ReadOnlySpan<byte> data, int faceIndex = 0)
    {
        try
        {
            return ReadDirectory(data, faceIndex) is { } tables && IsSupported(tables);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    private static byte[]? SubsetCore(ReadOnlySpan<byte> data, int faceIndex, IEnumerable<ushort> glyphs)
    {
        if (ReadDirectory(data, faceIndex) is not { } tables || !IsSupported(tables))
        {
            return null;
        }

        var head = tables[Tag("head")].Slice(data);
        var maxp = tables[Tag("maxp")].Slice(data);
        var loca = tables[Tag("loca")].Slice(data);
        var glyf = tables[Tag("glyf")].Slice(data);
        if (head.Length < 54 || maxp.Length < 6)
        {
            return null;
        }

        var glyphCount = BinaryPrimitives.ReadUInt16BigEndian(maxp[4..]);
        var longOffsets = BinaryPrimitives.ReadInt16BigEndian(head[50..]) != 0;
        if (glyphCount == 0 || loca.Length < (glyphCount + 1) * (longOffsets ? 4 : 2))
        {
            return null;
        }

        var offsets = new uint[glyphCount + 1];
        for (var i = 0; i <= glyphCount; i++)
        {
            offsets[i] = longOffsets
                ? BinaryPrimitives.ReadUInt32BigEndian(loca[(i * 4)..])
                : (uint)BinaryPrimitives.ReadUInt16BigEndian(loca[(i * 2)..]) * 2;
            if (offsets[i] > glyf.Length || (i > 0 && offsets[i] < offsets[i - 1]))
            {
                return null;
            }
        }

        var keep = Closure(glyf, offsets, glyphs, glyphCount);

        // Rebuilt glyf (4-byte aligned glyphs) and loca (short format when the offsets fit).
        var size = 0L;
        for (var g = 0; g < glyphCount; g++)
        {
            if (keep[g])
            {
                size += Align4(offsets[g + 1] - offsets[g]);
            }
        }

        var shortLoca = size < 0x20000;
        var entry = shortLoca ? 2 : 4;
        var newGlyf = new byte[size];
        var newLoca = new byte[(glyphCount + 1) * entry];
        void WriteLoca(int glyph, int offset)
        {
            if (shortLoca)
            {
                BinaryPrimitives.WriteUInt16BigEndian(newLoca.AsSpan(glyph * 2), (ushort)(offset / 2));
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(glyph * 4), (uint)offset);
            }
        }

        var at = 0;
        for (var g = 0; g < glyphCount; g++)
        {
            WriteLoca(g, at);
            if (keep[g])
            {
                var length = (int)(offsets[g + 1] - offsets[g]);
                glyf.Slice((int)offsets[g], length).CopyTo(newGlyf.AsSpan(at));
                at += (int)Align4((uint)length);
            }
        }

        WriteLoca(glyphCount, at);

        var newHead = head.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(newHead.AsSpan(8), 0); // checkSumAdjustment, set once the file is complete
        BinaryPrimitives.WriteInt16BigEndian(newHead.AsSpan(50), (short)(shortLoca ? 0 : 1)); // indexToLocFormat

        var output = new SortedDictionary<uint, byte[]>
        {
            [Tag("head")] = newHead,
            [Tag("loca")] = newLoca,
            [Tag("glyf")] = newGlyf,
        };

        foreach (var (tag, record) in tables)
        {
            if (CopiedTables.Contains(tag))
            {
                output[tag] = record.Slice(data).ToArray();
            }
        }

        // Unused glyphs keep their id but lose their metrics (nothing draws them); the kept ones are unchanged.
        if (tables.TryGetValue(Tag("hhea"), out var hheaRecord) && hheaRecord.Length >= 36)
        {
            var metricCount = BinaryPrimitives.ReadUInt16BigEndian(hheaRecord.Slice(data)[34..]);
            ClearUnusedMetrics(output[Tag("hmtx")], metricCount, keep);
        }

        // The character map lists only the characters of kept glyphs (the PDF's ToUnicode is read from it).
        if (tables.TryGetValue(Tag("cmap"), out var cmapRecord) && CharacterMap.Read(cmapRecord.Slice(data)) is { } map
            && CharacterMap.Write(map.Where(m => m.Glyph < glyphCount && keep[m.Glyph]).ToList()) is { } cmap)
        {
            output[Tag("cmap")] = cmap;
        }
        if (tables.TryGetValue(Tag("post"), out var postRecord))
        {
            var post = postRecord.Slice(data);
            if (post.Length >= 32)
            {
                // Format 3: the header without glyph names.
                var newPost = post[..32].ToArray();
                BinaryPrimitives.WriteUInt32BigEndian(newPost, 0x00030000);
                output[Tag("post")] = newPost;
            }
        }

        return Write(output);
    }

    /// <summary>The glyphs to keep: the requested ones, .notdef and, transitively, every component of a composite glyph.</summary>
    private static bool[] Closure(ReadOnlySpan<byte> glyf, uint[] offsets, IEnumerable<ushort> glyphs, int glyphCount)
    {
        var keep = new bool[glyphCount];
        var pending = new Stack<int>();
        void Add(int glyph)
        {
            if (glyph < glyphCount && !keep[glyph])
            {
                keep[glyph] = true;
                pending.Push(glyph);
            }
        }

        Add(0);
        foreach (var glyph in glyphs)
        {
            Add(glyph);
        }

        while (pending.Count > 0)
        {
            var g = pending.Pop();
            var start = (int)offsets[g];
            var end = (int)offsets[g + 1];
            if (end - start < 10 || BinaryPrimitives.ReadInt16BigEndian(glyf[start..]) >= 0)
            {
                continue; // empty or simple glyph
            }

            var p = start + 10;
            while (p + 4 <= end)
            {
                var flags = BinaryPrimitives.ReadUInt16BigEndian(glyf[p..]);
                Add(BinaryPrimitives.ReadUInt16BigEndian(glyf[(p + 2)..]));
                p += 4 + ((flags & ArgsAreWords) != 0 ? 4 : 2);
                p += (flags & HaveTwoByTwo) != 0 ? 8 : (flags & HaveXYScale) != 0 ? 4 : (flags & HaveScale) != 0 ? 2 : 0;
                if ((flags & MoreComponents) == 0)
                {
                    break;
                }
            }
        }

        return keep;
    }

    /// <summary>
    /// Zeroes the advance and side bearing of every unused glyph. The last long metric stays: its advance applies to
    /// every glyph after it.
    /// </summary>
    private static void ClearUnusedMetrics(byte[] hmtx, int metricCount, bool[] keep)
    {
        for (var g = 0; g < keep.Length; g++)
        {
            if (keep[g] || g == metricCount - 1)
            {
                continue;
            }

            var (offset, length) = g < metricCount ? (g * 4, 4) : ((metricCount * 4) + ((g - metricCount) * 2), 2);
            if (offset + length <= hmtx.Length)
            {
                hmtx.AsSpan(offset, length).Clear();
            }
        }
    }
    /// <summary>Lays out an sfnt file: table directory sorted by tag, 4-byte aligned tables, checksums and <c>head.checkSumAdjustment</c>.</summary>
    private static byte[] Write(SortedDictionary<uint, byte[]> tables)
    {
        var count = tables.Count;
        var entrySelector = 0;
        while ((2 << entrySelector) <= count)
        {
            entrySelector++;
        }

        var searchRange = (1 << entrySelector) * 16;
        var headerSize = 12 + (16 * count);
        var total = headerSize + tables.Values.Sum(static t => (int)Align4((uint)t.Length));
        var file = new byte[total];
        var span = file.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, VersionOne);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)((count * 16) - searchRange));

        var record = 12;
        var offset = headerSize;
        var headOffset = -1;
        foreach (var (tag, table) in tables)
        {
            table.CopyTo(span[offset..]);
            var padded = (int)Align4((uint)table.Length);
            BinaryPrimitives.WriteUInt32BigEndian(span[record..], tag);
            BinaryPrimitives.WriteUInt32BigEndian(span[(record + 4)..], Checksum(span.Slice(offset, padded)));
            BinaryPrimitives.WriteUInt32BigEndian(span[(record + 8)..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(span[(record + 12)..], (uint)table.Length);
            if (tag == Tag("head"))
            {
                headOffset = offset;
            }

            record += 16;
            offset += padded;
        }

        if (headOffset >= 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span[(headOffset + 8)..], unchecked(0xB1B0AFBA - Checksum(span)));
        }

        return file;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        var sum = 0u;
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        }

        if (i < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            data[i..].CopyTo(last);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(last));
        }

        return sum;
    }

    private static Dictionary<uint, TableRecord>? ReadDirectory(ReadOnlySpan<byte> data, int faceIndex)
    {
        if (data.Length < 12 || faceIndex < 0)
        {
            return null;
        }

        var start = 0;
        if (BinaryPrimitives.ReadUInt32BigEndian(data) == TtcTag)
        {
            var fonts = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
            if (faceIndex >= fonts || data.Length < 16 + (4 * faceIndex))
            {
                return null;
            }

            start = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(12 + (4 * faceIndex))..]);
        }
        else if (faceIndex != 0)
        {
            return null;
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(data[start..]);
        if (version is not (VersionOne or TrueTag))
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(data[(start + 4)..]);
        var tables = new Dictionary<uint, TableRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var r = data[(start + 12 + (i * 16))..];
            var record = new TableRecord(BinaryPrimitives.ReadUInt32BigEndian(r[8..]), BinaryPrimitives.ReadUInt32BigEndian(r[12..]));
            if ((long)record.Offset + record.Length > data.Length)
            {
                return null;
            }

            tables[BinaryPrimitives.ReadUInt32BigEndian(r)] = record;
        }

        return tables;
    }

    private static bool IsSupported(Dictionary<uint, TableRecord> tables) =>
        !tables.Keys.Any(UnsupportedTables.Contains)
        && tables.ContainsKey(Tag("glyf")) && tables.ContainsKey(Tag("loca")) && tables.ContainsKey(Tag("head"))
        && tables.ContainsKey(Tag("maxp")) && tables.ContainsKey(Tag("hhea")) && tables.ContainsKey(Tag("hmtx"));

    private static uint Align4(uint length) => (length + 3) & ~3u;

    private static uint Tag(string tag) => ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

    private readonly record struct TableRecord(uint Offset, uint Length)
    {
        public ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data) => data.Slice((int)Offset, (int)Length);
    }
}
