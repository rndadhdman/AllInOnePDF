using System.Collections.Concurrent;
using SkiaSharp;

namespace AllInOnePdf.Layout.Text;

/// <summary>A face chosen for a requested style, with the synthetic styles needed to fake what the family lacks.</summary>
/// <param name="Face">The face.</param>
/// <param name="SyntheticBold">Draw emboldened (the family has no face heavy enough).</param>
/// <param name="SyntheticItalic">Draw skewed (the family has no italic face).</param>
internal sealed record FontSelection(FontFace Face, bool SyntheticBold, bool SyntheticItalic)
{
    /// <summary>The Skia font to draw with at <paramref name="size"/>.</summary>
    public SKFont GetFont(float size) => Face.GetFont(size, SyntheticBold, SyntheticItalic);
}

/// <summary>
/// The process-wide font registry behind <see cref="Drawing.FontManager"/>: registered faces indexed by family name
/// (case-insensitive), weight and slant; system families found through <see cref="SKFontManager.Default"/>; and
/// per-character fallback. Every lookup is cached; registration clears the caches. Thread-safe.
/// </summary>
internal static class FontRegistry
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, List<FontFace>> Registered = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> RegistrationOrder = [];

    private static readonly ConcurrentDictionary<string, List<FontFace>?> SystemFamilies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, FontFace?> SystemFaces = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Families, int Weight, bool Italic), IReadOnlyList<FontSelection>> Chains = new();
    private static readonly ConcurrentDictionary<(int CodePoint, int Weight, bool Italic), FontSelection?> CharacterFallbacks = new();

    /// <summary>Weights at or above this count as bold for synthesis.</summary>
    private const int BoldThreshold = 600;

    /// <summary>Registers a font file (TTF/OTF). Returns the loaded face; throws <see cref="ArgumentException"/> when the data is not a font.</summary>
    public static FontFace Register(byte[] data)
    {
        var face = FontFace.FromData(data) ?? throw new ArgumentException("The data is not a font file Skia and HarfBuzz can read.", nameof(data));
        lock (Gate)
        {
            foreach (var name in face.FamilyNames)
            {
                if (!Registered.TryGetValue(name, out var faces))
                {
                    Registered[name] = faces = [];
                    RegistrationOrder.Add(name);
                }

                if (!faces.Exists(f => f.Weight == face.Weight && f.IsItalic == face.IsItalic))
                {
                    faces.Add(face);
                }
            }

            Chains.Clear();
            CharacterFallbacks.Clear();
        }

        return face;
    }

    /// <summary>True when <paramref name="family"/> was registered (not just available on the system).</summary>
    public static bool IsRegistered(string family)
    {
        lock (Gate)
        {
            return Registered.ContainsKey(family);
        }
    }

    /// <summary>The best face of <paramref name="family"/> for the style, from registered fonts first, then the system; null when the family exists nowhere.</summary>
    public static FontSelection? FindFamily(string family, int weight, bool italic)
    {
        ArgumentNullException.ThrowIfNull(family);
        List<FontFace>? faces;
        lock (Gate)
        {
            faces = Registered.TryGetValue(family, out var registered) ? [.. registered] : null;
        }

        if (faces is not null)
        {
            return Select(faces, weight, italic);
        }

        var system = FindSystemFace(family, weight, italic);
        return system is null ? null : Synthesize(system, weight, italic);
    }

    /// <summary>
    /// The faces to try, in order, for a style's family list: every family that exists (registered or system), or
    /// the first registered family when none does. Never empty while any font is available.
    /// </summary>
    public static IReadOnlyList<FontSelection> ResolveChain(IReadOnlyList<string>? families, int weight, bool italic)
    {
        var key = (families is null ? string.Empty : string.Join('\u001F', families), weight, italic);
        return Chains.GetOrAdd(key, static (k, list) => BuildChain(list, k.Weight, k.Italic), families);
    }

    /// <summary>A system face that has a glyph for <paramref name="codePoint"/> (Skia's character matching), or null.</summary>
    public static FontSelection? MatchCharacter(int codePoint, int weight, bool italic) =>
        CharacterFallbacks.GetOrAdd((codePoint, weight, italic), static key =>
        {
            SKTypeface? typeface;
            try
            {
                typeface = SKFontManager.Default.MatchCharacter(
                    null, key.Weight, (int)SKFontStyleWidth.Normal, key.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright, null, key.CodePoint);
            }
            catch (InvalidOperationException)
            {
                typeface = null;
            }

            if (typeface is null)
            {
                return null;
            }

            var face = SystemFace(typeface);
            return face is not null && face.HasGlyph(key.CodePoint) ? Synthesize(face, key.Weight, key.Italic) : null;
        });

    private static List<FontSelection> BuildChain(IReadOnlyList<string>? families, int weight, bool italic)
    {
        var chain = new List<FontSelection>();
        foreach (var family in families ?? [])
        {
            if (string.IsNullOrWhiteSpace(family))
            {
                continue;
            }

            var selection = FindFamily(family.Trim(), weight, italic);
            if (selection is not null && !chain.Exists(s => ReferenceEquals(s.Face, selection.Face)))
            {
                chain.Add(selection);
            }
        }

        if (chain.Count == 0)
        {
            string? first;
            lock (Gate)
            {
                first = RegistrationOrder.Count > 0 ? RegistrationOrder[0] : null;
            }

            var fallback = first is not null ? FindFamily(first, weight, italic) : DefaultSystemFace(weight, italic);
            if (fallback is null)
            {
                throw new InvalidOperationException("No font is available: register a font with FontManager.RegisterFont before laying out text.");
            }

            chain.Add(fallback);
        }

        return chain;
    }

    /// <summary>Closest face of a family: same slant when available, then the nearest weight (CSS tie-breaking).</summary>
    internal static FontSelection Select(List<FontFace> faces, int weight, bool italic)
    {
        var sameSlant = faces.FindAll(f => f.IsItalic == italic);
        var candidates = sameSlant.Count > 0 ? sameSlant : faces;
        var best = candidates[0];
        foreach (var face in candidates)
        {
            if (Better(face.Weight, best.Weight, weight))
            {
                best = face;
            }
        }

        return Synthesize(best, weight, italic);
    }

    /// <summary>True when weight <paramref name="a"/> is a closer match for <paramref name="wanted"/> than <paramref name="b"/>.</summary>
    private static bool Better(int a, int b, int wanted)
    {
        var da = Math.Abs(a - wanted);
        var db = Math.Abs(b - wanted);
        if (da != db)
        {
            return da < db;
        }

        // Equal distance: heavier for bold requests, lighter for light ones (CSS font matching).
        return wanted >= 500 ? a > b : a < b;
    }

    private static FontSelection Synthesize(FontFace face, int weight, bool italic) =>
        new(face, weight >= BoldThreshold && face.Weight < BoldThreshold, italic && !face.IsItalic);

    private static FontFace? FindSystemFace(string family, int weight, bool italic)
    {
        var faces = SystemFamilies.GetOrAdd(family, static name =>
        {
            var list = new List<FontFace>();
            foreach (var (w, slant) in (ReadOnlySpan<(int, SKFontStyleSlant)>)[(400, SKFontStyleSlant.Upright), (700, SKFontStyleSlant.Upright), (400, SKFontStyleSlant.Italic), (700, SKFontStyleSlant.Italic)])
            {
                SKTypeface? typeface;
                try
                {
                    typeface = SKFontManager.Default.MatchFamily(name, new SKFontStyle(w, (int)SKFontStyleWidth.Normal, slant));
                }
                catch (InvalidOperationException)
                {
                    typeface = null;
                }

                // Some platforms answer an unknown family with their default font: only accept the family asked for.
                if (typeface is null || !string.Equals(typeface.FamilyName, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var face = SystemFace(typeface);
                if (face is not null && !list.Exists(f => f.Weight == face.Weight && f.IsItalic == face.IsItalic))
                {
                    list.Add(face);
                }
            }

            return list.Count > 0 ? list : null;
        });

        if (faces is null)
        {
            return null;
        }

        var sameSlant = faces.FindAll(f => f.IsItalic == italic);
        var candidates = sameSlant.Count > 0 ? sameSlant : faces;
        var best = candidates[0];
        foreach (var face in candidates)
        {
            if (Better(face.Weight, best.Weight, weight))
            {
                best = face;
            }
        }

        // Ask the system for the exact style too: families with many weights (Segoe UI) have faces the probe missed.
        try
        {
            var exact = SKFontManager.Default.MatchFamily(family, new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright));
            if (exact is not null && string.Equals(exact.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                var face = SystemFace(exact);
                if (face is not null && (face.IsItalic == italic || best.IsItalic != italic) && Better(face.Weight, best.Weight, weight))
                {
                    best = face;
                }
            }
        }
        catch (InvalidOperationException)
        {
        }

        return best;
    }

    private static FontSelection? DefaultSystemFace(int weight, bool italic)
    {
        var typeface = SKTypeface.Default;
        var face = typeface is null ? null : SystemFace(typeface);
        return face is null ? null : Synthesize(face, weight, italic);
    }

    private static FontFace? SystemFace(SKTypeface typeface)
    {
        var key = $"{typeface.FamilyName}|{typeface.FontWeight}|{typeface.FontWidth}|{(int)typeface.FontSlant}";
        return SystemFaces.GetOrAdd(key, static (_, t) => FontFace.FromSystemTypeface(t), typeface);
    }
}
