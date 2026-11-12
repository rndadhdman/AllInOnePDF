using System.Text;
using AllInOnePdf.Core.Fonts;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Exporters.Epub;

/// <summary>Embeds the bundled (SIL OFL) font families a theme uses, plus their licence text.</summary>
internal static class EpubFonts
{
    public const string LicenseHref = "fonts/OFL.txt";

    public static (List<EpubItem> Items, List<EpubFontFace> Faces) Collect(PdfTheme theme, List<string> warnings)
    {
        var items = new List<EpubItem>();
        var faces = new List<EpubFontFace>();
        var families = new[] { theme.Typography.BodyFont, theme.Headings.Font ?? theme.Typography.BodyFont, theme.Code.Font }
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var licenses = new StringBuilder();
        var skipped = new List<string>();
        foreach (var requested in families)
        {
            var family = BundledFonts.Families.FirstOrDefault(f => string.Equals(f.Name, requested, StringComparison.OrdinalIgnoreCase));
            if (family is null)
            {
                skipped.Add(requested);
                continue;
            }

            foreach (var (fileName, data) in BundledFonts.GetFamilyFiles(family.Name).OrderBy(f => f.FileName, StringComparer.Ordinal))
            {
                var href = "fonts/" + fileName;
                items.Add(new EpubItem("font-" + EpubXml.Token(Path.GetFileNameWithoutExtension(fileName), "font"), href, "font/ttf", data));
                var (weight, italic) = Style(fileName);
                faces.Add(new EpubFontFace(family.Name, "../" + href, weight, italic));
            }

            var license = ReadLicense(family.Name);
            if (license is not null)
            {
                licenses.Append("==== ").Append(family.Name).Append(" ====\n\n").Append(license.Trim()).Append("\n\n");
            }
        }

        if (skipped.Count > 0)
        {
            warnings.Add($"Only bundled fonts can be embedded; {string.Join(", ", skipped)} will use the reader's fonts.");
        }

        if (items.Count > 0)
        {
            items.Add(new EpubItem("font-license", LicenseHref, "text/plain", EpubXml.Utf8.GetBytes(licenses.ToString())));
        }

        return (items, faces);
    }

    private static (int Weight, bool Italic) Style(string fileName)
    {
        var style = Path.GetFileNameWithoutExtension(fileName);
        var dash = style.LastIndexOf('-');
        style = dash >= 0 ? style[(dash + 1)..] : style;
        var italic = style.EndsWith("Italic", StringComparison.OrdinalIgnoreCase);
        if (italic)
        {
            style = style[..^"Italic".Length];
        }

        var weight = style.ToLowerInvariant() switch
        {
            "thin" => 100,
            "extralight" => 200,
            "light" => 300,
            "medium" => 500,
            "semibold" => 600,
            "bold" => 700,
            "extrabold" => 800,
            "black" => 900,
            _ => 400,
        };
        return (weight, italic);
    }

    private static string? ReadLicense(string family)
    {
        var folder = "." + family.Replace(" ", string.Empty, StringComparison.Ordinal) + ".OFL.txt";
        var assembly = typeof(BundledFonts).Assembly;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(folder, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
