using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Layout.Drawing;
using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;
using AllInOnePdf.Layout.Text;
using SkiaSharp;

namespace AllInOnePdf.Layout.Tests;

public partial class DrawingFontSubsetTests
{
    private const string Sample = "Sphinx of black quartz, judge my vow! 0123 (Ærø) – “fine”";

    public DrawingFontSubsetTests() => TestFonts.EnsureRegistered();

    private static byte[] Inter => TestFonts.Read(Path.Combine("Inter", "Inter-Regular.ttf"));

    private static byte[] Literata => TestFonts.Read(Path.Combine("Literata", "Literata-Regular.ttf"));

    private static ushort[] Glyphs(SKFont font, string text) => font.GetGlyphs(text);

    private static SKTypeface Load(byte[] data) => SKTypeface.FromData(SKData.CreateCopy(data)) ?? throw new InvalidOperationException("Skia rejected the font.");

    [Fact]
    public void A_subset_keeps_glyph_ids_metrics_and_outlines()
    {
        var data = Inter;
        using var original = Load(data);
        using var originalFont = new SKFont(original, 14) { Subpixel = true, LinearMetrics = true };
        var glyphs = Glyphs(originalFont, Sample);

        var subsetData = TrueTypeSubsetter.Subset(data, 0, glyphs);
        Assert.NotNull(subsetData);
        Assert.True(subsetData.Length < data.Length / 5, $"subset is {subsetData.Length} bytes of {data.Length}");

        using var subset = Load(subsetData);
        using var subsetFont = new SKFont(subset, 14) { Subpixel = true, LinearMetrics = true };
        Assert.Equal(original.GlyphCount, subset.GlyphCount);
        Assert.Equal(original.UnitsPerEm, subset.UnitsPerEm);
        Assert.Equal(original.FamilyName, subset.FamilyName);

        // Same ids from the character map, same advances and outlines for every kept glyph.
        Assert.Equal(glyphs, Glyphs(subsetFont, Sample));
        Assert.Equal(originalFont.GetGlyphWidths(glyphs), subsetFont.GetGlyphWidths(glyphs));
        foreach (var glyph in glyphs.Distinct())
        {
            using var a = originalFont.GetGlyphPath(glyph);
            using var b = subsetFont.GetGlyphPath(glyph);
            Assert.Equal(a?.ToSvgPathData(), b?.ToSvgPathData());
        }

        var m1 = originalFont.Metrics;
        var m2 = subsetFont.Metrics;
        Assert.Equal(m1.Ascent, m2.Ascent);
        Assert.Equal(m1.Descent, m2.Descent);
        Assert.Equal(m1.Leading, m2.Leading);
        Assert.Equal(m1.Top, m2.Top);
        Assert.Equal(m1.Bottom, m2.Bottom);
        Assert.Equal(m1.CapHeight, m2.CapHeight);
        Assert.Equal(m1.XHeight, m2.XHeight);
        Assert.Equal(m1.UnderlinePosition, m2.UnderlinePosition);
        Assert.Equal(m1.UnderlineThickness, m2.UnderlineThickness);
        Assert.Equal(m1.StrikeoutPosition, m2.StrikeoutPosition);

        // A glyph that was not asked for keeps its id but has no outline.
        var unused = Glyphs(originalFont, "Z")[0];
        Assert.DoesNotContain(unused, glyphs);
        using var empty = subsetFont.GetGlyphPath(unused);
        Assert.True(empty is null || empty.IsEmpty);
    }

    [Fact]
    public void The_subset_file_has_valid_checksums_and_only_the_needed_tables()
    {
        var subset = TrueTypeSubsetter.Subset(Literata, 0, [36, 37, 38])!;
        var tables = ReadTables(subset);
        Assert.Contains("glyf", tables.Keys);
        Assert.Contains("cmap", tables.Keys);
        Assert.Contains("OS/2", tables.Keys);
        Assert.DoesNotContain("GPOS", tables.Keys);
        Assert.DoesNotContain("GSUB", tables.Keys);
        Assert.Equal(tables.Keys.Order(StringComparer.Ordinal), tables.Keys);

        foreach (var (tag, (offset, length, checksum)) in tables)
        {
            var table = subset.AsSpan(offset, (length + 3) & ~3).ToArray();
            if (tag == "head")
            {
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8), 0);
            }

            Assert.Equal(checksum, Checksum(table));
        }

        Assert.Equal(0xB1B0AFBA, Checksum(subset));
    }

    [Fact]
    public void Composite_glyphs_keep_their_components()
    {
        var data = Inter;
        using var original = Load(data);
        using var font = new SKFont(original, 20);
        var composite = "éèêëàáâäöüñçÅ".Select(c => Glyphs(font, c.ToString())[0]).FirstOrDefault(g => IsComposite(data, g));
        Assert.NotEqual(0, composite);

        using var subset = Load(TrueTypeSubsetter.Subset(data, 0, [composite])!);
        using var subsetFont = new SKFont(subset, 20);
        using var expected = font.GetGlyphPath(composite);
        using var actual = subsetFont.GetGlyphPath(composite);
        Assert.False(expected.IsEmpty);
        Assert.Equal(expected.ToSvgPathData(), actual.ToSvgPathData());
    }

    [Fact]
    public void Fonts_without_plain_TrueType_outlines_are_not_subset()
    {
        // A CFF-flavoured OpenType header and garbage are both passed through (null: embed the original).
        var otto = new byte[64];
        Encoding.ASCII.GetBytes("OTTO").CopyTo(otto, 0);
        Assert.Null(TrueTypeSubsetter.Subset(otto, 0, [1, 2]));
        Assert.False(TrueTypeSubsetter.CanSubset(otto));
        Assert.Null(TrueTypeSubsetter.Subset(new byte[200], 0, [1]));
        Assert.Null(TrueTypeSubsetter.Subset(Inter.AsSpan(0, 400), 0, [1]));
        Assert.Null(TrueTypeSubsetter.Subset(Inter, 1, [1]));
        Assert.True(TrueTypeSubsetter.CanSubset(Inter));
    }

    [Fact]
    public void The_drawing_pass_draws_identical_pixels_with_the_subset_and_falls_back_for_unseen_glyphs()
    {
        var font = FontRegistry.FindFamily("Inter", 400, false)!.GetFont(14);
        var glyphs = Glyphs(font, Sample);
        var positions = font.GetGlyphPositions(glyphs);
        var run = new GlyphRun(font, glyphs, positions, font.MeasureText(glyphs), Color.FromHex("#000000"));

        var usage = new GlyphUsage();
        usage.Record(run);
        using var subsets = FontSubsets.Build(usage);
        Assert.Equal(1, subsets.SubsetFaces);

        var subsetFont = subsets.Resolve(font, glyphs);
        Assert.NotSame(font, subsetFont);
        Assert.Equal(font.Size, subsetFont.Size);
        Assert.NotEqual(font.Typeface!.Handle, subsetFont.Typeface!.Handle);

        // Digits are always in the subset (page numbers are only known in the drawing pass).
        Assert.Same(subsetFont, subsets.Resolve(font, Glyphs(font, "987")));
        Assert.Equal(0, subsets.ExtendedRuns);

        // An unseen glyph extends the subset: a new typeface that holds the old glyphs too; the full font is never used.
        var zebra = Glyphs(font, "Zebra");
        var extended = subsets.Resolve(font, zebra);
        Assert.NotSame(font, extended);
        Assert.NotSame(subsetFont, extended);
        Assert.Equal(1, subsets.ExtendedRuns);
        Assert.Equal(0, subsets.FallbackRuns);
        Assert.Same(extended, subsets.Resolve(font, glyphs));
        using (var path = extended.GetGlyphPath(zebra[0]))
        {
            Assert.False(path.IsEmpty);
        }

        var expected = Draw(run, null);
        var actual = Draw(run, subsets);
        Assert.Equal(expected.Bytes, actual.Bytes);
        Assert.Contains(expected.Pixels, p => p != SKColors.White);
    }

    [Fact]
    public void A_document_with_two_faces_embeds_a_small_fraction_of_the_fonts()
    {
        var pdf = TwoFaceDocument();
        var fonts = Inter.Length + Literata.Length;
        Assert.True(pdf.Length < fonts / 10, $"the PDF is {pdf.Length} bytes; the two font files are {fonts}");

        var fontFiles = FontFileRegex().Count(Encoding.Latin1.GetString(pdf));
        Assert.Equal(2, fontFiles);
    }

    [Fact]
    public void Text_in_a_subset_font_maps_back_to_its_characters()
    {
        const string text = "Quartz judge vow";
        var pdf = Document.Create(doc => doc.Page(page =>
        {
            page.Size(300, 200);
            page.Margin(20);
            page.Content().Text(text).FontFamily("Literata").FontSize(16);
        })).GeneratePdf();

        using var font = new SKFont(FontRegistry.FindFamily("Literata", 400, false)!.Face.Typeface, 16);
        var map = ToUnicode(pdf);
        foreach (var c in text.Distinct())
        {
            var glyph = Glyphs(font, c.ToString())[0];
            Assert.True(map.TryGetValue(glyph, out var mapped), $"glyph {glyph} ('{c}') has no ToUnicode entry");
            Assert.Equal(c.ToString(), mapped);
        }
    }

    private static byte[] TwoFaceDocument() => Document.Create(doc => doc.Page(page =>
    {
        page.Size(595, 842);
        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text("A heading in Inter").FontFamily("Inter").FontSize(24);
            col.Item().Text(Sample + " " + Sample).FontFamily("Literata").FontSize(11);
        });
        page.Footer().Text(t =>
        {
            t.CurrentPageNumber();
            t.Span(" / ");
            t.TotalPages();
        });
    })).GeneratePdf();

    private static (byte[] Bytes, SKColor[] Pixels) Draw(GlyphRun run, FontSubsets? subsets)
    {
        using var bitmap = new SKBitmap(500, 40);
        using (var surface = new SKCanvas(bitmap))
        {
            surface.Clear(SKColors.White);
            using var canvas = new SkiaCanvas(surface);
            if (subsets is not null)
            {
                canvas.Subsets = subsets;
            }

            canvas.DrawGlyphRun(run, new Position(4.3f, 25.7f));
        }

        return (bitmap.Bytes, bitmap.Pixels);
    }

    /// <summary>Glyph → text of every ToUnicode CMap in the PDF (bfchar and bfrange entries).</summary>
    private static Dictionary<int, string> ToUnicode(byte[] pdf)
    {
        var map = new Dictionary<int, string>();
        var text = Encoding.Latin1.GetString(pdf);
        foreach (Match stream in StreamRegex().Matches(text))
        {
            var start = stream.Index + stream.Length;
            var end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            string content;
            try
            {
                using var input = new ZLibStream(new MemoryStream(pdf, start, end - start), CompressionMode.Decompress);
                using var reader = new StreamReader(input, Encoding.Latin1);
                content = reader.ReadToEnd();
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!content.Contains("begincmap", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var section in Sections(content, "bfchar"))
            {
                foreach (Match m in BfCharRegex().Matches(section))
                {
                    map[Convert.ToInt32(m.Groups[1].Value, 16)] = Utf16(m.Groups[2].Value);
                }
            }

            foreach (Match m in Sections(content, "bfrange").SelectMany(static s => BfRangeRegex().Matches(s)))
            {
                var from = Convert.ToInt32(m.Groups[1].Value, 16);
                var to = Convert.ToInt32(m.Groups[2].Value, 16);
                var first = Convert.ToInt32(m.Groups[3].Value, 16);
                for (var g = from; g <= to; g++)
                {
                    map[g] = char.ConvertFromUtf32(first + (g - from));
                }
            }
        }

        return map;
    }

    private static IEnumerable<string> Sections(string content, string name)
    {
        var at = 0;
        while ((at = content.IndexOf("begin" + name, at, StringComparison.Ordinal)) >= 0)
        {
            var end = content.IndexOf("end" + name, at, StringComparison.Ordinal);
            if (end < 0)
            {
                yield break;
            }

            yield return content[(at + 5 + name.Length)..end];
            at = end;
        }
    }

    private static string Utf16(string hex) =>
        Encoding.BigEndianUnicode.GetString(Convert.FromHexString(hex));

    private static bool IsComposite(byte[] font, ushort glyph)
    {
        var tables = ReadTables(font);
        var head = tables["head"].Offset;
        var loca = tables["loca"].Offset;
        var glyf = tables["glyf"].Offset;
        var longLoca = BinaryPrimitives.ReadInt16BigEndian(font.AsSpan(head + 50)) != 0;
        var offset = longLoca
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(loca + (glyph * 4)))
            : BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(loca + (glyph * 2))) * 2;
        var next = longLoca
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(loca + (glyph * 4) + 4))
            : BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(loca + (glyph * 2) + 2)) * 2;
        return next > offset && BinaryPrimitives.ReadInt16BigEndian(font.AsSpan(glyf + offset)) < 0;
    }

    private static Dictionary<string, (int Offset, int Length, uint Checksum)> ReadTables(byte[] font)
    {
        var tables = new Dictionary<string, (int, int, uint)>();
        var count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var r = 12 + (i * 16);
            tables[Encoding.ASCII.GetString(font, r, 4)] = (
                (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(r + 8)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(r + 12)),
                BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(r + 4)));
        }

        return tables;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        var sum = 0u;
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        }

        return sum;
    }

    [GeneratedRegex(@"/FontFile2\b")]
    private static partial Regex FontFileRegex();

    [GeneratedRegex(@"(?<!end)stream\r?\n")]
    private static partial Regex StreamRegex();

    [GeneratedRegex(@"<([0-9A-Fa-f]{4})>\s*<([0-9A-Fa-f]+)>")]
    private static partial Regex BfCharRegex();

    [GeneratedRegex(@"<([0-9A-Fa-f]{4})>\s*<([0-9A-Fa-f]{4})>\s*<([0-9A-Fa-f]{4,8})>")]
    private static partial Regex BfRangeRegex();
}
