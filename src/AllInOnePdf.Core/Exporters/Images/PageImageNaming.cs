using System.Globalization;

namespace AllInOnePdf.Core.Exporters.Images;

/// <summary>Builds per-page output file names from a pattern with <c>{name}</c>, <c>{page}</c> and <c>{count}</c>.</summary>
internal static class PageImageNaming
{
    public const string DefaultPattern = "{name}-p{page}";

    /// <summary>
    /// Expands <paramref name="pattern"/>; <c>{page}</c> is zero-padded to the digits of <paramref name="pageCount"/>.
    /// A pattern without <c>{page}</c> gets "-{page}" appended when there is more than one page, so names never
    /// collide. Path separators and characters invalid in file names become underscores.
    /// </summary>
    public static string Build(string? pattern, string baseName, int pageNumber, int pageCount, bool multiplePages, string extension)
    {
        pattern = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern.Trim();
        if (multiplePages && !pattern.Contains("{page}", StringComparison.OrdinalIgnoreCase))
        {
            pattern += "-{page}";
        }

        var width = Math.Max(1, pageCount.ToString(CultureInfo.InvariantCulture).Length);
        var name = pattern
            .Replace("{name}", baseName, StringComparison.OrdinalIgnoreCase)
            .Replace("{page}", pageNumber.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'), StringComparison.OrdinalIgnoreCase)
            .Replace("{count}", pageCount.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim(' ', '.');
        if (cleaned.Length == 0)
        {
            cleaned = "page-" + pageNumber.ToString(CultureInfo.InvariantCulture);
        }

        return cleaned + extension;
    }
}
