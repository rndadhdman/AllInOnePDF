using AllInOnePdf.Core.Conversion;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace AllInOnePdf.UI.Services.Shared;

/// <summary>A readable format with the icon the UI shows for it.</summary>
public sealed record FormatEntry(FileFormat Format, Icon Icon)
{
    public string Name => Format.DisplayName;

    /// <summary>".md, .markdown" style list for tooltips.</summary>
    public string ExtensionList => string.Join(", ", Format.Extensions);

    /// <summary>
    /// The first <paramref name="max"/> extensions, then "and N more" when the type has more (source code has about
    /// thirty), so a list row stays one line. The full list is <see cref="ExtensionList"/>.
    /// </summary>
    public string ExtensionSummary(int max = 6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var extensions = Format.Extensions;
        if (extensions.Count <= max)
        {
            return ExtensionList;
        }

        return $"{string.Join(", ", extensions.Take(max))} and {extensions.Count - max} more";
    }
}

/// <summary>Formats grouped by the kind of file people have (documents, spreadsheets and presentations, web pages, e-books, images, data and code).</summary>
public sealed record FormatGroup(string Name, Icon Icon, IReadOnlyList<FormatEntry> Formats);

/// <summary>
/// Builds the "what can I open" views from the format registry: grouped file types for the Reader start page and menus,
/// native dialog filters with one entry per type, and the accept attribute for browser uploads.
/// </summary>
public static class FormatCatalog
{
    // Grouped by the kind of file a person is holding, most common first inside each group. Word stays with the
    // other documents (people look for a .docx under documents, not under "Office"), so the second group names what
    // it holds instead of the suite. Web pages and e-books are different things people have, so they are separate.
    private static readonly (string Name, Func<Icon> Icon, string[] FormatIds)[] GroupDefinitions =
    [
        ("Documents", () => new Icons.Regular.Size20.DocumentText(), ["pdf", "docx", "rtf", "text", "markdown"]),
        ("Spreadsheets and presentations", () => new Icons.Regular.Size20.Table(), ["xlsx", "pptx"]),
        ("Web pages", () => new Icons.Regular.Size20.Globe(), ["html"]),
        ("E-books", () => new Icons.Regular.Size20.BookOpen(), ["epub"]),
        ("Images", () => new Icons.Regular.Size20.Image(), ["image", "png", "jpeg", "webp", "gif", "bmp"]),
        ("Data and code", () => new Icons.Regular.Size20.Code(), ["csv", "json", "code"]),
    ];

    /// <summary>Readable formats (importers that are not merely planned), grouped and ordered for display.</summary>
    public static IReadOnlyList<FormatGroup> ReadableGroups(FormatRegistry registry)
    {
        var readable = ReadableFormats(registry);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<FormatGroup>();

        foreach (var (name, icon, ids) in GroupDefinitions)
        {
            var entries = ids
                .Select(id => readable.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)))
                .Where(f => f is not null)
                .Select(f => new FormatEntry(f!, ConvertFormatting.FileIcon(f)))
                .ToList();
            foreach (var e in entries)
            {
                placed.Add(e.Format.Id);
            }

            if (entries.Count > 0)
            {
                groups.Add(new FormatGroup(name, icon(), entries));
            }
        }

        // Anything a future importer adds that the table above does not know lands in a trailing group.
        var other = readable.Where(f => !placed.Contains(f.Id)).Select(f => new FormatEntry(f, ConvertFormatting.FileIcon(f))).ToList();
        if (other.Count > 0)
        {
            groups.Add(new FormatGroup("Other", new Icons.Regular.Size20.Document(), other));
        }

        return groups;
    }

    /// <summary>
    /// Filters for a native open dialog: the preferred type first (when given), then "All readable files",
    /// then one entry per readable type so the dialog's type dropdown shows every option.
    /// </summary>
    public static IReadOnlyList<FileFilter> OpenFilters(FormatRegistry registry, FileFormat? preferred = null)
    {
        var readable = ReadableFormats(registry);
        var filters = new List<FileFilter>();
        if (preferred is not null)
        {
            filters.Add(new FileFilter(preferred.DisplayName, preferred.Extensions));
        }

        filters.Add(new FileFilter("All readable files", registry.ImportableExtensions));
        filters.AddRange(readable
            .Where(f => preferred is null || !string.Equals(f.Id, preferred.Id, StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileFilter(f.DisplayName, f.Extensions)));
        return filters;
    }

    public static string AcceptAttribute(FormatRegistry registry) => string.Join(",", registry.ImportableExtensions);

    private static List<FileFormat> ReadableFormats(FormatRegistry registry) =>
        registry.Importers
            .Where(i => i.Status != ConverterStatus.Planned)
            .Select(i => i.Format)
            .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
}
