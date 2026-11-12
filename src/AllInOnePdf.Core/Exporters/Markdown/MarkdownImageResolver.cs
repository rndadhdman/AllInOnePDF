using System.Globalization;
using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Exporters.Markdown;

/// <summary>
/// Turns image sources into Markdown destinations: embedded <c>res:</c> resources are written to a side folder
/// (as extra, non-primary <see cref="OutputFile"/>s) or inlined as data URLs; other sources pass through.
/// </summary>
internal sealed class MarkdownImageResolver
{
    private readonly Document _document;
    private readonly ImageMode _mode;
    private readonly string _folder;
    private readonly ExportResult _result;
    private readonly Dictionary<string, string> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private int _counter;

    public MarkdownImageResolver(Document document, MarkdownExportSettings settings, string baseName, ExportResult result)
    {
        _document = document;
        _mode = settings.Images;
        _result = result;
        _folder = ConversionService.SanitizeBaseName(settings.ImageFolderName.Replace("{name}", baseName, StringComparison.OrdinalIgnoreCase));
    }

    public string Folder => _folder;

    /// <summary>Returns the destination to write, or null when the image should be reduced to its alt text.</summary>
    public string? Resolve(string source)
    {
        if (_mode == ImageMode.Omit)
        {
            return null;
        }

        var resource = _document.FindResource(source);
        if (resource is null)
        {
            if (source.StartsWith(Document.ResourceScheme, StringComparison.Ordinal))
            {
                if (_missing.Add(source))
                {
                    _result.Warnings.Add($"An embedded image ({source}) is missing from the document; only its alt text was written.");
                }

                return null;
            }

            return source;
        }

        if (_mode == ImageMode.DataUrl)
        {
            return "data:" + resource.MediaType + ";base64," + Convert.ToBase64String(resource.Data);
        }

        if (_written.TryGetValue(resource.Id, out var existing))
        {
            return existing;
        }

        var name = UniqueName(resource);
        var path = _folder + "/" + name;
        _result.Files.Add(new OutputFile(path, resource.Data, resource.MediaType, IsPrimary: false));
        _written[resource.Id] = path;
        return path;
    }

    private string UniqueName(DocumentResource resource)
    {
        var extension = resource.SuggestedExtension;
        string stem;
        if (!string.IsNullOrWhiteSpace(resource.FileName))
        {
            var file = Path.GetFileName(resource.FileName.Replace('\\', '/').Split('/')[^1]);
            var ext = Path.GetExtension(file);
            if (ext.Length > 1)
            {
                extension = SanitizeSegment(ext[1..]).ToLowerInvariant() is { Length: > 0 } e ? "." + e : extension;
            }

            stem = SanitizeSegment(Path.GetFileNameWithoutExtension(file));
            if (stem.Length == 0)
            {
                stem = NextGenericStem();
            }
        }
        else
        {
            stem = NextGenericStem();
        }

        var candidate = stem + extension;
        for (var n = 2; !_names.Add(candidate); n++)
        {
            candidate = stem + "-" + n.ToString(CultureInfo.InvariantCulture) + extension;
        }

        return candidate;
    }

    private string NextGenericStem() => "image-" + (++_counter).ToString("000", CultureInfo.InvariantCulture);

    /// <summary>Keeps letters, digits, '-', '_' and '.'; everything else becomes '-'. Never returns a path or a dot-only name.</summary>
    private static string SanitizeSegment(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var cleaned = sb.ToString().Trim('-', '.');
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
