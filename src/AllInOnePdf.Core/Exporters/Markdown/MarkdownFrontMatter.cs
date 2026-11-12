using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Exporters.Markdown;

/// <summary>Writes document metadata as a YAML front-matter block that <see cref="Importers.MarkdownImporter"/> reads back.</summary>
internal static partial class MarkdownFrontMatter
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "subtitle", "author", "authors", "date", "lang", "language", "description", "summary", "abstract", "keywords", "tags",
    };

    /// <summary>Returns the block including both <c>---</c> fences, or null when there is nothing to write.</summary>
    public static string? Write(DocumentMetadata metadata)
    {
        var sb = new StringBuilder();
        Scalar(sb, "title", metadata.Title);
        Scalar(sb, "subtitle", metadata.Subtitle);

        var authors = metadata.Authors.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
        if (authors.Count == 1)
        {
            Scalar(sb, "author", authors[0]);
        }
        else if (authors.Count > 1)
        {
            Sequence(sb, "author", authors);
        }

        if (metadata.Date is { } date)
        {
            var value = date.TimeOfDay == TimeSpan.Zero
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
            sb.Append("date: ").Append(value).Append('\n');
        }

        Scalar(sb, "lang", metadata.Language);
        var keywords = metadata.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList();
        if (keywords.Count > 0)
        {
            Sequence(sb, "keywords", keywords);
        }

        Scalar(sb, "description", metadata.Description);

        foreach (var (key, value) in metadata.Custom.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!KnownKeys.Contains(key) && SafeKey().IsMatch(key))
            {
                Scalar(sb, key, value);
            }
        }

        return sb.Length == 0 ? null : "---\n" + sb + "---";
    }

    private static void Scalar(StringBuilder sb, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sb.Append(key).Append(": ").Append(Quote(value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ').Trim(), inSequence: false)).Append('\n');
    }

    private static void Sequence(StringBuilder sb, string key, List<string> values)
    {
        sb.Append(key).Append(": [").Append(string.Join(", ", values.Select(v => Quote(v.Replace('\n', ' '), inSequence: true)))).Append("]\n");
    }

    /// <summary>Quotes a YAML scalar only when it would otherwise be read as something else.</summary>
    private static string Quote(string value, bool inSequence)
    {
        var needs = value.Length == 0
                    || value != value.Trim()
                    || "-?:,[]{}#&*!|>'\"%@`".Contains(value[0], StringComparison.Ordinal)
                    || value.Contains(": ", StringComparison.Ordinal)
                    || value.Contains(" #", StringComparison.Ordinal)
                    || value.EndsWith(':')
                    || (inSequence && (value.Contains(',', StringComparison.Ordinal) || value.Contains(']', StringComparison.Ordinal)))
                    || ReservedScalar().IsMatch(value);
        if (!needs)
        {
            return value;
        }

        // Single quotes need no backslash escaping (the importer does not unescape); fall back to double quotes for apostrophes.
        if (!value.Contains('\'', StringComparison.Ordinal))
        {
            return "'" + value + "'";
        }

        if (!value.Contains('"', StringComparison.Ordinal) && !value.Contains('\\', StringComparison.Ordinal))
        {
            return "\"" + value + "\"";
        }

        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex SafeKey();

    [GeneratedRegex(@"^(true|false|yes|no|on|off|null|~|[-+]?[0-9][0-9_.:eE+-]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedScalar();
}
