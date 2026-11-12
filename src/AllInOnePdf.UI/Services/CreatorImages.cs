using System.Text;
using AllInOnePdf.UI.Services.Shared;

namespace AllInOnePdf.UI.Services;

/// <summary>Which images the Creator accepts and how it sniffs their type and pixel size.</summary>
public static class CreatorImages
{
    /// <summary>Largest image accepted for a publication (the app-wide image limit).</summary>
    public const long MaxImageBytes = UploadLimits.MaxImageBytes;

    /// <summary>Extensions accepted by the image pickers.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg"];

    /// <summary>Value for an <c>accept</c> attribute.</summary>
    public static string Accept => string.Join(',', Extensions) + ",image/png,image/jpeg,image/webp,image/gif,image/svg+xml";

    /// <summary>Filter for the desktop open dialog.</summary>
    public static IReadOnlyList<FileFilter> Filters { get; } = [new FileFilter("Images", Extensions)];

    /// <summary>
    /// The media type of <paramref name="bytes"/> from its signature (falling back to the file extension for SVG),
    /// or null when it is not a PNG, JPEG, WebP, GIF or SVG image.
    /// </summary>
    public static string? DetectMediaType(ReadOnlySpan<byte> bytes, string? fileName)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8')
        {
            return "image/gif";
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        // SVG is text: look for an <svg element near the start (after an optional XML prolog or comments).
        var head = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 4096)]);
        if (head.Contains("<svg", StringComparison.OrdinalIgnoreCase)
            || (fileName is not null && fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && head.TrimStart().StartsWith('<')))
        {
            return "image/svg+xml";
        }

        return null;
    }
}
