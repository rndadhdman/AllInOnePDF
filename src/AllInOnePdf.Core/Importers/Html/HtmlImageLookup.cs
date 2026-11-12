using System.Text;

namespace AllInOnePdf.Core.Importers.Html;

internal enum ImageLookupStatus
{
    Found,
    NotFound,
    TooLarge,
    NotAnImage,
}

/// <summary>
/// Result of resolving a local image reference. <see cref="Key"/> identifies the underlying file (absolute path,
/// zip entry) so the same picture referenced from several places is embedded once.
/// </summary>
internal readonly record struct ImageLookup(ImageLookupStatus Status, byte[]? Data = null, string? MediaType = null, string? FileName = null, string? Key = null)
{
    public static ImageLookup NotFound => new(ImageLookupStatus.NotFound);
}

/// <summary>Loads the bytes behind a local (non-remote, non-data) image reference; never touches the network.</summary>
internal delegate ImageLookup HtmlImageResolver(string source, long maxBytes);

/// <summary>Media type sniffing for embedded images (magic bytes first, extension as a fallback for SVG).</summary>
internal static class ImageMediaTypes
{
    public static string? Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8')
        {
            return "image/gif";
        }

        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
        {
            return "image/webp";
        }

        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
        {
            return "image/bmp";
        }

        if (data.Length >= 4 && ((data[0] == 'I' && data[1] == 'I' && data[2] == 42 && data[3] == 0) || (data[0] == 'M' && data[1] == 'M' && data[2] == 0 && data[3] == 42)))
        {
            return "image/tiff";
        }

        if (data.Length >= 12 && data[4] == 'j' && data[5] == 'P' && data[6] == ' ' && data[7] == ' ')
        {
            return "image/jp2";
        }

        if (LooksLikeSvg(data) || string.Equals(Path.GetExtension(fileName), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return LooksLikeSvg(data) ? "image/svg+xml" : null;
        }

        return null;
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> data)
    {
        var head = Encoding.UTF8.GetString(data[..Math.Min(data.Length, 1024)]);
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }
}
