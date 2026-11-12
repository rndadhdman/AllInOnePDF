using AllInOnePdf.Layout.Text;

namespace AllInOnePdf.Layout.Drawing;

/// <summary>
/// Registers font files with the layout engine (QuestPDF's name and namespace shape, so callers switch with a
/// <c>using</c>). Faces are indexed by the family name, weight and slant read from the font itself; text styles then
/// refer to them by family name. Families that are not registered are looked up among the system fonts.
/// Thread-safe; registering a face twice is harmless.
/// </summary>
public static class FontManager
{
    /// <summary>Registers the TTF/OTF font in <paramref name="stream"/> (read to the end; the stream is not disposed).</summary>
    /// <exception cref="ArgumentException">The data is not a font file.</exception>
    public static void RegisterFontFromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        RegisterFont(buffer.ToArray());
    }

    /// <summary>Registers a TTF/OTF font file's bytes.</summary>
    /// <exception cref="ArgumentException">The data is not a font file.</exception>
    public static void RegisterFont(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        FontRegistry.Register(data);
    }
}
