namespace AllInOnePdf.Core.Importers.Html;

/// <summary>Resolves relative and absolute image paths of an HTML file against its folder on disk. Only real image files are returned.</summary>
internal static class LocalImageResolver
{
    public static HtmlImageResolver ForDirectory(string? baseDirectory) => (source, maxBytes) =>
    {
        var path = ToLocalPath(source, baseDirectory);
        if (path is null)
        {
            return ImageLookup.NotFound;
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return ImageLookup.NotFound;
        }

        if (info.Length > maxBytes)
        {
            return new ImageLookup(ImageLookupStatus.TooLarge);
        }

        var data = File.ReadAllBytes(info.FullName);
        var mediaType = ImageMediaTypes.Detect(data, info.Name);
        return mediaType is null
            ? new ImageLookup(ImageLookupStatus.NotAnImage)
            : new ImageLookup(ImageLookupStatus.Found, data, mediaType, info.Name, info.FullName);
    };

    private static string? ToLocalPath(string source, string? baseDirectory)
    {
        var value = source;
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
        }

        var cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            value = value[..cut];
        }

        if (value.Length == 0)
        {
            return null;
        }

        try
        {
            value = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return null;
        }

        value = value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        // "/images/a.png" is site-root relative on the web; try it below the page folder first.
        if (value[0] == Path.DirectorySeparatorChar && !value.StartsWith(@"\\", StringComparison.Ordinal) && baseDirectory is not null)
        {
            var underBase = Path.GetFullPath(Path.Combine(baseDirectory, value.TrimStart(Path.DirectorySeparatorChar)));
            if (File.Exists(underBase))
            {
                return underBase;
            }
        }

        if (Path.IsPathFullyQualified(value))
        {
            return value;
        }

        return baseDirectory is null ? null : Path.GetFullPath(Path.Combine(baseDirectory, value));
    }
}
