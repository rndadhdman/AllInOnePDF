using AllInOnePdf.Layout.Infrastructure;
using SkiaSharp;

namespace AllInOnePdf.Layout.Drawing;

/// <summary>
/// The glyphs a document draws, per typeface: collected by the measuring pass so the drawing pass can embed subset
/// fonts. Not thread-safe; one instance per document.
/// </summary>
internal sealed class GlyphUsage
{
    private readonly Dictionary<SKFont, HashSet<ushort>> _byFont = new(ReferenceEqualityComparer.Instance);

    /// <summary>Remembers the glyphs of <paramref name="run"/>.</summary>
    public void Record(GlyphRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!_byFont.TryGetValue(run.Font, out var glyphs))
        {
            _byFont[run.Font] = glyphs = [];
        }

        var count = Math.Min(run.GlyphIds.Length, run.Positions.Length);
        for (var i = 0; i < count; i++)
        {
            glyphs.Add(run.GlyphIds[i]);
        }
    }

    /// <summary>The glyphs drawn with each typeface (fonts of several sizes share a typeface).</summary>
    public IReadOnlyList<(SKTypeface Typeface, HashSet<ushort> Glyphs)> ByTypeface()
    {
        var faces = new Dictionary<IntPtr, (SKTypeface Typeface, HashSet<ushort> Glyphs)>();
        foreach (var (font, glyphs) in _byFont)
        {
            if (font.Typeface is not { } typeface)
            {
                continue;
            }

            if (faces.TryGetValue(typeface.Handle, out var face))
            {
                face.Glyphs.UnionWith(glyphs);
            }
            else
            {
                faces[typeface.Handle] = (typeface, [.. glyphs]);
            }
        }

        return [.. faces.Values];
    }
}

/// <summary>
/// Per-document subset typefaces for the drawing pass: every TrueType face the measuring pass saw is cut down to the
/// glyphs it drew (<see cref="TrueTypeSubsetter"/>, glyph ids unchanged) and loaded as a Skia typeface, so Skia's PDF
/// backend embeds the small font instead of the whole file. The digits and <c>?</c> are always included (page numbers
/// are only known in the drawing pass). A run that draws a glyph the measuring pass did not see (text that exists only
/// in the drawing pass) gets a new, larger subset of the same face that also holds the earlier glyphs; later runs use
/// that one, so each such event embeds one more small font. Only when a subset cannot be built does a run keep the
/// original font (correct, just larger). Not thread-safe; one instance per document.
/// </summary>
internal sealed class FontSubsets : IDisposable
{
    /// <summary>Characters added to every subset because the drawing pass may print them where the measuring pass did not (page counts).</summary>
    private const string DynamicCharacters = "0123456789?";

    private static long _totalSubsetRuns;
    private static long _totalExtendedRuns;
    private static long _totalFallbackRuns;

    private readonly Dictionary<IntPtr, Face> _faces = [];
    private readonly Dictionary<SKFont, FontState?> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly List<SKFont> _created = [];

    private FontSubsets()
    {
    }

    /// <summary>Glyph runs drawn with a subset font (this document), extended ones included.</summary>
    public int SubsetRuns { get; private set; }

    /// <summary>Glyph runs that needed a glyph the subset lacked, so the face's subset was rebuilt with it (this document).</summary>
    public int ExtendedRuns { get; private set; }

    /// <summary>Glyph runs drawn with the full font because no subset could be built for their glyphs (this document).</summary>
    public int FallbackRuns { get; private set; }

    /// <summary>The number of typefaces replaced by a subset.</summary>
    public int SubsetFaces => _faces.Count;

    /// <summary>Process-wide totals (diagnostics for the corpus tool).</summary>
    public static (long SubsetRuns, long ExtendedRuns, long FallbackRuns) Totals =>
        (Interlocked.Read(ref _totalSubsetRuns), Interlocked.Read(ref _totalExtendedRuns), Interlocked.Read(ref _totalFallbackRuns));

    /// <summary>Builds the subsets for the faces in <paramref name="usage"/>; faces that cannot be subset are left out.</summary>
    public static FontSubsets Build(GlyphUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var subsets = new FontSubsets();
        foreach (var (typeface, glyphs) in usage.ByTypeface())
        {
            if (Face.Create(typeface, glyphs) is { } face)
            {
                subsets._faces[typeface.Handle] = face;
            }
        }

        return subsets;
    }

    /// <summary>
    /// The font to draw <paramref name="glyphs"/> of <paramref name="font"/> with: the same font (size, scale, skew,
    /// embolden, hinting) on the face's subset typeface, extended first when it lacks one of the glyphs; or
    /// <paramref name="font"/> itself for faces that are not subset.
    /// </summary>
    public SKFont Resolve(SKFont font, ReadOnlySpan<ushort> glyphs)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (!_fonts.TryGetValue(font, out var state))
        {
            state = font.Typeface is { } typeface && _faces.TryGetValue(typeface.Handle, out var face) ? new FontState(face) : null;
            _fonts[font] = state;
        }

        if (state is null)
        {
            return font;
        }

        foreach (var glyph in glyphs)
        {
            if (state.Face.Glyphs.Contains(glyph))
            {
                continue;
            }

            if (!state.Face.Extend(glyphs))
            {
                FallbackRuns++;
                Interlocked.Increment(ref _totalFallbackRuns);
                return font;
            }

            ExtendedRuns++;
            Interlocked.Increment(ref _totalExtendedRuns);
            break;
        }

        if (state.Derived is null || state.Version != state.Face.Version)
        {
            state.Derived = Derive(font, state.Face.Typeface);
            state.Version = state.Face.Version;
        }

        SubsetRuns++;
        Interlocked.Increment(ref _totalSubsetRuns);
        return state.Derived;
    }

    /// <summary>Releases the subset fonts and typefaces; call after the PDF is closed.</summary>
    public void Dispose()
    {
        foreach (var font in _created)
        {
            font.Dispose();
        }

        foreach (var face in _faces.Values)
        {
            face.Dispose();
        }

        _created.Clear();
        _faces.Clear();
        _fonts.Clear();
    }

    private SKFont Derive(SKFont font, SKTypeface typeface)
    {
        var derived = new SKFont(typeface, font.Size, font.ScaleX, font.SkewX)
        {
            Embolden = font.Embolden,
            Subpixel = font.Subpixel,
            Hinting = font.Hinting,
            Edging = font.Edging,
            LinearMetrics = font.LinearMetrics,
            ForceAutoHinting = font.ForceAutoHinting,
            EmbeddedBitmaps = font.EmbeddedBitmaps,
            BaselineSnap = font.BaselineSnap,
        };
        _created.Add(derived);
        return derived;
    }

    /// <summary>A run font's subset counterpart, re-derived when its face's subset is extended.</summary>
    private sealed class FontState(Face face)
    {
        public Face Face { get; } = face;

        public SKFont? Derived { get; set; }

        public int Version { get; set; }
    }

    /// <summary>One original face: its font file and the current subset typeface with the glyphs it holds.</summary>
    private sealed class Face : IDisposable
    {
        private readonly byte[] _data;
        private readonly int _index;
        private readonly List<SKTypeface> _typefaces = [];

        private Face(byte[] data, int index, SKTypeface typeface, HashSet<ushort> glyphs)
        {
            _data = data;
            _index = index;
            Typeface = typeface;
            Glyphs = glyphs;
            _typefaces.Add(typeface);
        }

        /// <summary>The current subset typeface.</summary>
        public SKTypeface Typeface { get; private set; }

        /// <summary>The glyphs the current subset holds outlines for (as requested; composite components come on top).</summary>
        public HashSet<ushort> Glyphs { get; private set; }

        /// <summary>Incremented by every <see cref="Extend"/>.</summary>
        public int Version { get; private set; }

        public static Face? Create(SKTypeface typeface, HashSet<ushort> glyphs)
        {
            byte[] data;
            int index;
            using (var stream = typeface.OpenStream(out index))
            {
                if (stream is null)
                {
                    return null;
                }

                using var skData = SKData.Create(stream);
                if (skData is null || skData.Size == 0)
                {
                    return null;
                }

                data = skData.ToArray();
            }

            if (!TrueTypeSubsetter.CanSubset(data, index))
            {
                return null;
            }

            var kept = new HashSet<ushort>(glyphs);
            using (var probe = new SKFont(typeface))
            {
                foreach (var glyph in probe.GetGlyphs(DynamicCharacters))
                {
                    if (glyph != 0)
                    {
                        kept.Add(glyph);
                    }
                }
            }

            return Load(data, index, kept) is { } subset ? new Face(data, index, subset, kept) : null;
        }

        /// <summary>Replaces the subset with one that also holds <paramref name="glyphs"/>; false when it cannot be built.</summary>
        public bool Extend(ReadOnlySpan<ushort> glyphs)
        {
            var kept = new HashSet<ushort>(Glyphs);
            foreach (var glyph in glyphs)
            {
                kept.Add(glyph);
            }

            if (Load(_data, _index, kept) is not { } subset)
            {
                return false;
            }

            Typeface = subset;
            Glyphs = kept;
            Version++;
            _typefaces.Add(subset);
            return true;
        }

        public void Dispose()
        {
            foreach (var typeface in _typefaces)
            {
                typeface.Dispose();
            }

            _typefaces.Clear();
        }

        private static SKTypeface? Load(byte[] data, int index, HashSet<ushort> glyphs)
        {
            var subset = TrueTypeSubsetter.Subset(data, index, glyphs);
            return subset is null || subset.Length >= data.Length ? null : SKTypeface.FromData(SKData.CreateCopy(subset));
        }
    }
}