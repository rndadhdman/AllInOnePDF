namespace AllInOnePdf.Core.Documents;

/// <summary>
/// The format-neutral document model. Every importer produces one of these and every
/// exporter consumes one, so adding a format means writing one importer or one exporter,
/// never a pairwise converter.
/// </summary>
public sealed class Document
{
    public DocumentMetadata Metadata { get; init; } = new();

    /// <summary>Top-level blocks in reading order.</summary>
    public List<Block> Blocks { get; } = [];

    /// <summary>Binary resources (images etc.) referenced by <see cref="ImageBlock.Source"/> or <see cref="ImageInline.Source"/> using the <c>res:</c> scheme.</summary>
    public List<DocumentResource> Resources { get; } = [];

    /// <summary>Footnotes keyed by their label; referenced by <see cref="FootnoteReferenceInline"/>.</summary>
    public List<Footnote> Footnotes { get; } = [];

    /// <summary>Non-fatal notes the importer wants the user to see ("3 images skipped"). Not part of the content.</summary>
    public List<string> ImportWarnings { get; } = [];

    /// <summary>Resource id prefix for embedded images: <c>res:&lt;id&gt;</c>.</summary>
    public const string ResourceScheme = "res:";

    /// <summary>
    /// Resource id under which an importer may keep the original file bytes, so an exporter that can use them
    /// directly does (PDF → page images rasterises the original rather than a re-rendered copy).
    /// </summary>
    public const string SourceFileResourceId = "source-file";

    /// <summary>The original file bytes when the importer kept them (see <see cref="SourceFileResourceId"/>).</summary>
    public DocumentResource? SourceFile => Resources.FirstOrDefault(r => string.Equals(r.Id, SourceFileResourceId, StringComparison.Ordinal));

    public DocumentResource? FindResource(string? source)
    {
        if (string.IsNullOrEmpty(source) || !source.StartsWith(ResourceScheme, StringComparison.Ordinal))
        {
            return null;
        }

        var id = source[ResourceScheme.Length..];
        return Resources.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
    }

    public DocumentResource AddResource(string mediaType, byte[] data, string? fileName = null, string? id = null)
    {
        var resource = new DocumentResource
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            MediaType = mediaType,
            Data = data,
            FileName = fileName,
        };
        Resources.Add(resource);
        return resource;
    }

    /// <summary>Enumerates all blocks depth-first, including those nested inside lists, quotes and table cells.</summary>
    public IEnumerable<Block> EnumerateAllBlocks() => Block.EnumerateRecursive(Blocks);

    /// <summary>Headings in document order, useful for a table of contents or an outline.</summary>
    public IEnumerable<HeadingBlock> Headings => EnumerateAllBlocks().OfType<HeadingBlock>();

    /// <summary>Best-effort title: metadata first, then the first level-1 heading.</summary>
    public string? ResolveTitle()
    {
        if (!string.IsNullOrWhiteSpace(Metadata.Title))
        {
            return Metadata.Title;
        }

        var h1 = Headings.FirstOrDefault(h => h.Level == 1);
        return h1?.ToPlainText();
    }

    /// <summary>Rough word count over all inline text.</summary>
    public int CountWords()
    {
        var count = 0;
        foreach (var block in EnumerateAllBlocks())
        {
            var text = block switch
            {
                HeadingBlock h => h.ToPlainText(),
                ParagraphBlock p => p.ToPlainText(),
                CodeBlock c => c.Code,
                _ => null,
            };
            if (text is null)
            {
                continue;
            }

            count += text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        return count;
    }
}

public sealed class DocumentMetadata
{
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public List<string> Authors { get; } = [];
    public DateTimeOffset? Date { get; set; }
    public string? Language { get; set; }
    public List<string> Keywords { get; } = [];
    public string? Description { get; set; }

    /// <summary>Absolute path of the source file, when imported from disk.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Format id of the importer that produced this document (see <c>FileFormat.Id</c>).</summary>
    public string? SourceFormat { get; set; }

    /// <summary>Number of pages in the source, when the source was paginated (PDF).</summary>
    public int? SourcePageCount { get; set; }

    /// <summary>Free-form values importers can preserve (e.g. YAML front matter keys).</summary>
    public Dictionary<string, string> Custom { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DocumentResource
{
    public required string Id { get; init; }
    public required string MediaType { get; init; }
    public required byte[] Data { get; init; }
    public string? FileName { get; init; }

    public string Reference => Document.ResourceScheme + Id;

    public string SuggestedExtension => MediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "image/bmp" => ".bmp",
        "image/jp2" => ".jp2",
        "image/tiff" => ".tiff",
        _ => ".bin",
    };
}

public sealed class Footnote
{
    public required string Label { get; init; }
    public List<Block> Blocks { get; } = [];
}
