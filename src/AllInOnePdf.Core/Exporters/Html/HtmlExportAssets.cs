using System.Globalization;
using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Fonts;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Exporters.Html;

internal enum HtmlImageMode
{
    DataUrl,
    Folder,
    Omit,
}

/// <summary>
/// Image sources for the exported page: embedded resources become data URLs, files in a side folder
/// (extra, non-primary <see cref="OutputFile"/>s), or are left out; other sources (URLs, paths) pass through.
/// </summary>
internal sealed class HtmlImageWriter
{
    private readonly HtmlImageMode _mode;
    private readonly string _folder;
    private readonly ExportResult _result;
    private readonly Dictionary<string, string> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private int _counter;

    public HtmlImageWriter(HtmlImageMode mode, string baseName, ExportResult result)
    {
        _mode = mode;
        _result = result;
        _folder = ConversionService.SanitizeBaseName(baseName + "_files");
    }

    public string? Resolve(Document document, string source)
    {
        if (_mode == HtmlImageMode.Omit)
        {
            return null;
        }

        var resource = document.FindResource(source);
        if (resource is null)
        {
            if (source.StartsWith(Document.ResourceScheme, StringComparison.Ordinal))
            {
                if (_missing.Add(source))
                {
                    _result.Warnings.Add($"An embedded image ({source}) is missing from the document; its alt text is shown instead.");
                }

                return null;
            }

            return source;
        }

        if (_mode == HtmlImageMode.DataUrl)
        {
            return "data:" + resource.MediaType + ";base64," + Convert.ToBase64String(resource.Data);
        }

        if (_written.TryGetValue(resource.Id, out var existing))
        {
            return existing;
        }

        var name = UniqueName(resource);
        _result.Files.Add(new OutputFile(_folder + "/" + name, resource.Data, resource.MediaType, IsPrimary: false));
        var href = Uri.EscapeDataString(_folder) + "/" + Uri.EscapeDataString(name);
        _written[resource.Id] = href;
        return href;
    }

    private string UniqueName(DocumentResource resource)
    {
        var extension = resource.SuggestedExtension;
        var stem = string.Empty;
        if (!string.IsNullOrWhiteSpace(resource.FileName))
        {
            var file = resource.FileName.Replace('\\', '/').Split('/')[^1];
            stem = Sanitize(Path.GetFileNameWithoutExtension(file));
        }

        if (stem.Length == 0)
        {
            stem = "image-" + (++_counter).ToString("000", CultureInfo.InvariantCulture);
        }

        var candidate = stem + extension;
        for (var n = 2; !_names.Add(candidate); n++)
        {
            candidate = stem + "-" + n.ToString(CultureInfo.InvariantCulture) + extension;
        }

        return candidate;
    }

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var cleaned = sb.ToString().Trim('-');
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}

/// <summary>Base64 <c>@font-face</c> rules for the bundled fonts a theme uses.</summary>
internal static class HtmlFontEmbedder
{
    public static string Build(PdfTheme theme, ExportResult result, bool minify)
    {
        var families = new[] { theme.Typography.BodyFont, theme.Headings.Font ?? theme.Typography.BodyFont, theme.Code.Font }
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var css = new StringBuilder();
        long bytes = 0;
        foreach (var family in families)
        {
            var bundled = BundledFonts.Families.FirstOrDefault(f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase));
            if (bundled is null)
            {
                result.Warnings.Add($"The font \"{CssValues.SafeFontName(family)}\" is not bundled with the app and cannot be embedded; readers see it only if it is installed.");
                continue;
            }

            foreach (var (fileName, data) in BundledFonts.GetFamilyFiles(bundled.Name).OrderBy(f => f.FileName, StringComparer.Ordinal))
            {
                var style = Path.GetFileNameWithoutExtension(fileName);
                style = style[(style.IndexOf('-', StringComparison.Ordinal) + 1)..];
                var italic = style.Contains("Italic", StringComparison.OrdinalIgnoreCase);
                var weight = style.Replace("Italic", string.Empty, StringComparison.OrdinalIgnoreCase) switch
                {
                    "Medium" => 500,
                    "SemiBold" => 600,
                    "Bold" => 700,
                    _ => 400,
                };
                bytes += data.LongLength;
                css.Append("@font-face{font-family:").Append(CssValues.CssString(bundled.Name))
                    .Append(";font-style:").Append(italic ? "italic" : "normal")
                    .Append(";font-weight:").Append(weight.ToString(CultureInfo.InvariantCulture))
                    .Append(";font-display:swap;src:url(data:font/ttf;base64,").Append(Convert.ToBase64String(data)).Append(") format(\"truetype\");}");
                if (!minify)
                {
                    css.Append('\n');
                }
            }
        }

        if (bytes > 0)
        {
            var mb = (bytes * 4.0 / 3.0 / (1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);
            result.Warnings.Add($"Embedded fonts add about {mb} MB to the HTML file. Choose \"System fonts\" for a smaller file.");
        }

        return css.ToString();
    }
}
