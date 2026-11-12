using AllInOnePdf.Core.Documents;
using AllInOnePdf.UI.Services.Shared;

namespace AllInOnePdf.UI.Services;

/// <summary>
/// Image source resolution for the Reader's HTML render. Embedded <c>res:</c> resources and <c>data:image/</c>
/// URLs are inlined; local files (absolute, or relative to the document's source path) are read and inlined as
/// data URLs when allowed and small enough. Remote URLs are dropped: the app is offline by design.
/// Local images are capped per image (<see cref="UploadLimits.MaxImageBytes"/>) and per render
/// (<see cref="UploadLimits.MaxTotalImagesBytes"/>), so a document cannot make the app read gigabytes.
/// </summary>
public static class ReaderImageResolver
{
    /// <summary>Largest local image the Reader will inline.</summary>
    public const long MaxLocalImageBytes = UploadLimits.MaxImageBytes;

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".bmp"] = "image/bmp",
        [".avif"] = "image/avif",
        [".ico"] = "image/x-icon",
    };

    /// <summary>
    /// Creates a resolver for <c>HtmlRenderOptions.ResolveImage</c>. Create one per render: it tracks the total
    /// size of local images it has inlined and stops at <paramref name="maxTotalBytes"/>.
    /// </summary>
    /// <param name="allowLocalFiles">
    /// True on the desktop host. The web host never reads server paths named by an untrusted document,
    /// except relative to the document's own source path (which only exists for files opened from disk).
    /// </param>
    /// <param name="maxTotalBytes">Budget for all local images in this render.</param>
    public static Func<Document, string, string?> Create(bool allowLocalFiles, long maxTotalBytes = UploadLimits.MaxTotalImagesBytes)
    {
        var budget = new ImageBudget(maxTotalBytes);
        return (document, source) => Resolve(document, source, allowLocalFiles, budget);
    }

    public static string? Resolve(Document document, string? source, bool allowLocalFiles) =>
        Resolve(document, source, allowLocalFiles, new ImageBudget(UploadLimits.MaxTotalImagesBytes));

    private static string? Resolve(Document document, string? source, bool allowLocalFiles, ImageBudget budget)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        source = source.Trim();
        if (source.StartsWith(Document.ResourceScheme, StringComparison.Ordinal))
        {
            var res = document.FindResource(source);
            return res is null ? null : $"data:{res.MediaType};base64,{Convert.ToBase64String(res.Data)}";
        }

        if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("//", StringComparison.Ordinal)
            || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = ResolveLocalPath(document.Metadata.SourcePath, source, allowLocalFiles);
        return path is null ? null : TryReadAsDataUrl(path, budget);
    }

    internal static string? ResolveLocalPath(string? documentPath, string source, bool allowLocalFiles)
    {
        try
        {
            string candidate;
            if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!allowLocalFiles || !Uri.TryCreate(source, UriKind.Absolute, out var uri) || !uri.IsFile)
                {
                    return null;
                }

                candidate = uri.LocalPath;
            }
            else
            {
                // Strip query/fragment and decode %20-style escapes used by Markdown authors.
                var cut = source.IndexOfAny(['?', '#']);
                var raw = Uri.UnescapeDataString(cut >= 0 ? source[..cut] : source);
                if (raw.Length == 0 || raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    return null;
                }

                if (Path.IsPathRooted(raw))
                {
                    if (!allowLocalFiles)
                    {
                        return null;
                    }

                    candidate = raw;
                }
                else
                {
                    var baseDir = string.IsNullOrEmpty(documentPath) ? null : Path.GetDirectoryName(Path.GetFullPath(documentPath));
                    if (baseDir is null)
                    {
                        return null;
                    }

                    candidate = Path.Combine(baseDir, raw);
                    if (!allowLocalFiles)
                    {
                        // Relative paths must stay inside the document's folder when local access is restricted.
                        // Case-insensitive only where the file system is (Windows).
                        var full = Path.GetFullPath(candidate);
                        var root = baseDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!full.StartsWith(root, FilePaths.Comparison))
                        {
                            return null;
                        }
                    }
                }
            }

            return Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return null;
        }
    }

    private static string? TryReadAsDataUrl(string path, ImageBudget budget)
    {
        if (!MediaTypes.TryGetValue(Path.GetExtension(path), out var mediaType))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxLocalImageBytes || !budget.TryTake(info.Length))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(info.FullName);
            return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Running total of local image bytes inlined into one render.</summary>
    private sealed class ImageBudget(long maxBytes)
    {
        private long _used;

        public bool TryTake(long bytes)
        {
            if (Interlocked.Add(ref _used, bytes) <= maxBytes)
            {
                return true;
            }

            Interlocked.Add(ref _used, -bytes);
            return false;
        }
    }
}
