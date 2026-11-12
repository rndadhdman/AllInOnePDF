using AllInOnePdf.Core.Documents;
using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Content;

/// <summary>
/// Turns a publication into a plain <see cref="Document"/> for "Export as…" and "Read in Reader": layout is dropped,
/// content is kept in reading order (sections → rows → columns → modules).
/// </summary>
public static class PublicationFlattener
{
    /// <summary>
    /// Builds the document: metadata (title falls back to <see cref="Publication.Name"/>), assets as resources, module
    /// blocks in reading order (<see cref="Modules(Section)"/>; free-flow canvases top to bottom, then left to right)
    /// and section page breaks as <see cref="PageBreakBlock"/>s. Hidden and contents modules are
    /// skipped (exporters make their own contents). Page breaks never lead, trail or repeat, and heading ids are made
    /// unique so internal links and outlines work.
    /// </summary>
    public static Document ToDocument(Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);

        var document = new Document();
        var meta = document.Metadata;
        var source = publication.Metadata ?? new PublicationMetadata();
        meta.Title = string.IsNullOrWhiteSpace(source.Title) ? publication.Name : source.Title;
        meta.Subtitle = source.Subtitle;
        meta.Authors.AddRange((source.Authors ?? []).Where(a => !string.IsNullOrWhiteSpace(a)));
        meta.Date = source.Date;
        meta.Language = source.Language;
        meta.Keywords.AddRange((source.Keywords ?? []).Where(k => !string.IsNullOrWhiteSpace(k)));
        meta.Description = source.Description ?? publication.Description;

        foreach (var asset in publication.Assets ?? [])
        {
            if (asset is { Data.Length: > 0 } && document.Resources.All(r => !string.Equals(r.Id, asset.Id, StringComparison.Ordinal)))
            {
                document.AddResource(asset.MediaType, asset.Data, asset.FileName, asset.Id);
            }
        }

        foreach (var section in publication.Sections ?? [])
        {
            if (section is null)
            {
                continue;
            }

            if (section.PageBreakBefore)
            {
                AddPageBreak(document.Blocks);
            }

            foreach (var module in Modules(section))
            {
                if (module is null || module.Hidden || module is ContentsModule)
                {
                    continue;
                }

                foreach (var block in ModuleBlocks.ToBlocks(module, publication, document))
                {
                    if (block is PageBreakBlock)
                    {
                        AddPageBreak(document.Blocks);
                    }
                    else
                    {
                        document.Blocks.Add(block);
                    }
                }
            }

            if (section.PageBreakAfter)
            {
                AddPageBreak(document.Blocks);
            }
        }

        if (document.Blocks.Count > 0 && document.Blocks[^1] is PageBreakBlock)
        {
            document.Blocks.RemoveAt(document.Blocks.Count - 1);
        }

        MakeHeadingIdsUnique(document);
        return document;
    }

    /// <summary>
    /// A section's modules in reading order: rows → columns → modules for structure, the canvas items top to bottom,
    /// then left to right (bands of overlapping items, columns inside a band) for free flow.
    /// </summary>
    public static IEnumerable<Module> Modules(Section section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Layout == SectionLayout.FreeFlow)
        {
            return FreeFlowOrder.ReadingOrder((section.Items ?? []).Where(i => i?.Module is not null), i => i).Select(i => i.Module);
        }

        return (section.Rows ?? []).SelectMany(r => r?.Columns ?? []).SelectMany(c => c?.Modules ?? []).Where(m => m is not null);
    }

    private static void AddPageBreak(List<Block> blocks)
    {
        if (blocks.Count > 0 && blocks[^1] is not PageBreakBlock)
        {
            blocks.Add(new PageBreakBlock());
        }
    }

    private static void MakeHeadingIdsUnique(Document document)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in document.Headings)
        {
            if (string.IsNullOrWhiteSpace(heading.Id))
            {
                continue;
            }

            var id = heading.Id;
            for (var n = 2; !used.Add(id); n++)
            {
                id = heading.Id + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            heading.Id = id;
        }
    }
}
