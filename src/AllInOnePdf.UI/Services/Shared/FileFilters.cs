namespace AllInOnePdf.UI.Services.Shared;

/// <summary>
/// Keeps native file-dialog filters inside the limits of the host's dialog code. Photino's Windows dialog copies
/// each filter into a fixed buffer: one filter with ~55 extensions crashes the process (stack-buffer overrun),
/// while 30 extensions (~150 characters) are fine. Long filters are therefore split into numbered chunks.
/// </summary>
public static class FileFilters
{
    /// <summary>Longest "*.ext;*.ext;…" pattern string allowed per filter. Well under the native buffer.</summary>
    public const int MaxPatternLength = 160;

    /// <summary>Longest filter display name.</summary>
    public const int MaxNameLength = 80;

    public static IReadOnlyList<FileFilter> Normalize(IReadOnlyList<FileFilter>? filters, int maxPatternLength = MaxPatternLength)
    {
        if (filters is null || filters.Count == 0)
        {
            return [];
        }

        var result = new List<FileFilter>();
        foreach (var filter in filters)
        {
            var name = string.IsNullOrWhiteSpace(filter.Name) ? "Files" : filter.Name.Trim();
            if (name.Length > MaxNameLength)
            {
                name = name[..MaxNameLength];
            }

            var extensions = filter.Extensions
                .Select(e => e.Trim().TrimStart('*').TrimStart('.'))
                .Where(e => e.Length > 0 && e.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (extensions.Count == 0)
            {
                continue;
            }

            var chunks = new List<List<string>>();
            var current = new List<string>();
            var length = 0;
            foreach (var ext in extensions)
            {
                var cost = ext.Length + 3; // "*." + ext + ";"
                if (current.Count > 0 && length + cost > maxPatternLength)
                {
                    chunks.Add(current);
                    current = [];
                    length = 0;
                }

                current.Add(ext);
                length += cost;
            }

            if (current.Count > 0)
            {
                chunks.Add(current);
            }

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunkName = chunks.Count == 1 ? name : $"{name} ({i + 1} of {chunks.Count})";
                result.Add(new FileFilter(chunkName, chunks[i]));
            }
        }

        return result;
    }
}
