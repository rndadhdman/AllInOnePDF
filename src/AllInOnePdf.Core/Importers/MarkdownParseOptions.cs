namespace AllInOnePdf.Core.Importers;

/// <summary>Switches for <see cref="MarkdownImporter.Parse"/>. The defaults suit snippets typed into an editor.</summary>
public sealed class MarkdownParseOptions
{
    /// <summary>Read a leading YAML front matter block into the document metadata. Off: the block is skipped silently.</summary>
    public bool FrontMatter { get; init; }

    /// <summary>Treat every newline inside a paragraph as a hard line break (like chat apps).</summary>
    public bool HardLineBreaks { get; init; }

    /// <summary>Convert straight quotes, <c>--</c> and <c>...</c> to typographic equivalents.</summary>
    public bool SmartQuotes { get; init; }

    /// <summary>When the metadata has no title, use the first level-1 heading as the document title.</summary>
    public bool TitleFromHeading { get; init; }
}
