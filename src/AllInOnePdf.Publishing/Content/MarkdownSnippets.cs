using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;

namespace AllInOnePdf.Publishing.Content;

/// <summary>The one place the Publishing library parses Markdown.</summary>
internal static class MarkdownSnippets
{
    private static readonly MarkdownParseOptions Options = new();

    /// <summary>Parses a Markdown snippet: no front matter, no local image loading, no title detection.</summary>
    public static Document Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new Document();
        }

        return MarkdownImporter.Parse(markdown, Options);
    }
}
