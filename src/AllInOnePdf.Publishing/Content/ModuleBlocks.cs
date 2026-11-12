using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Content;

/// <summary>
/// Maps a module to Core <see cref="Block"/>s: the single source of truth for the renderer, the flattener and the
/// design canvas. Spacer and contents modules (and images without an asset) produce no blocks; their drawing is a
/// renderer special. <see cref="Module.Hidden"/> is not consulted here; callers decide whether to skip hidden modules.
/// </summary>
public static partial class ModuleBlocks
{
    /// <summary>The blocks for <paramref name="module"/>. Images resolve their asset against <paramref name="publication"/>.</summary>
    public static IReadOnlyList<Block> ToBlocks(Module module, Publication publication) => ToBlocks(module, publication, footnotes: null);

    /// <summary>As <see cref="ToBlocks(Module, Publication)"/>; footnotes defined in Markdown are added to <paramref name="footnotes"/>.</summary>
    internal static IReadOnlyList<Block> ToBlocks(Module module, Publication publication, Document? footnotes)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(publication);

        switch (module)
        {
            case TextModule text:
                return Markdown(text.Markdown, footnotes);

            case HeadingModule heading:
            {
                var block = new HeadingBlock { Level = heading.Level, Alignment = heading.Design?.Alignment };
                block.Inlines.AddRange(InlineMarkdown(heading.Text));
                block.Id = Slug(block.ToPlainText());
                return [block];
            }

            case ImageModule image:
            {
                var asset = publication.FindAsset(image.AssetId);
                if (asset is null)
                {
                    return [];
                }

                return
                [
                    new ImageBlock
                    {
                        Source = Document.ResourceScheme + asset.Id,
                        AltText = image.Alt,
                        Caption = image.Caption,
                        ShowCaption = !string.IsNullOrWhiteSpace(image.Caption),
                        WidthPercent = image.WidthPercent,
                        PixelWidth = asset.PixelWidth,
                        PixelHeight = asset.PixelHeight,
                    },
                ];
            }

            case QuoteModule quote:
            {
                var block = new QuoteBlock { Attribution = string.IsNullOrWhiteSpace(quote.Attribution) ? null : quote.Attribution };
                block.Blocks.AddRange(Markdown(quote.Markdown, footnotes));
                return [block];
            }

            case CalloutModule callout:
            {
                var block = new CalloutBlock { Kind = callout.Kind, Title = string.IsNullOrWhiteSpace(callout.Title) ? null : callout.Title };
                block.Blocks.AddRange(Markdown(callout.Markdown, footnotes));
                return [block];
            }

            case CodeModule code:
                return
                [
                    new CodeBlock
                    {
                        Code = code.Code ?? "",
                        Language = string.IsNullOrWhiteSpace(code.Language) ? null : code.Language,
                        Caption = string.IsNullOrWhiteSpace(code.Caption) ? null : code.Caption,
                    },
                ];

            case TableModule table:
                return Table(table);

            case DividerModule:
                return [new ThematicBreakBlock()];

            case PageBreakModule:
                return [new PageBreakBlock()];

            case ButtonModule button:
            {
                var paragraph = new ParagraphBlock { Alignment = ToTextAlignment(button.Align) };
                var label = new TextInline(button.Text ?? "");
                if (string.IsNullOrWhiteSpace(button.Url))
                {
                    paragraph.Inlines.Add(label);
                }
                else
                {
                    var link = new LinkInline { Url = button.Url.Trim() };
                    link.Inlines.Add(label);
                    paragraph.Inlines.Add(link);
                }

                return [paragraph];
            }

            default:
                // SpacerModule, ContentsModule: drawn by the renderer, no document content.
                return [];
        }
    }

    /// <summary>Paragraph alignment for a block alignment.</summary>
    public static TextAlignment ToTextAlignment(BlockAlign align) => align switch
    {
        BlockAlign.Center => TextAlignment.Center,
        BlockAlign.Right => TextAlignment.Right,
        _ => TextAlignment.Left,
    };

    /// <summary>Parses one line of inline Markdown (heading text, table cells). Block syntax at the start is taken literally.</summary>
    internal static List<Inline> InlineMarkdown(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var line = text.ReplaceLineEndings(" ").Trim();
        var parsed = MarkdownSnippets.Parse(EscapeBlockSyntax(line));
        if (parsed.Blocks is [InlineContainerBlock container])
        {
            return [.. container.Inlines];
        }

        return [new TextInline(line)];
    }

    private static List<Block> Markdown(string? markdown, Document? footnotes)
    {
        var parsed = MarkdownSnippets.Parse(markdown ?? "");
        if (footnotes is not null)
        {
            foreach (var note in parsed.Footnotes)
            {
                if (!footnotes.Footnotes.Any(f => string.Equals(f.Label, note.Label, StringComparison.Ordinal)))
                {
                    footnotes.Footnotes.Add(note);
                }
            }
        }

        return [.. parsed.Blocks];
    }

    private static IReadOnlyList<Block> Table(TableModule module)
    {
        if (module.Cells is not { Count: > 0 })
        {
            return [];
        }

        var table = new TableBlock { Caption = string.IsNullOrWhiteSpace(module.Caption) ? null : module.Caption };
        table.Alignments.AddRange(module.Alignments ?? []);
        for (var r = 0; r < module.Cells.Count; r++)
        {
            var row = new TableRow { IsHeader = module.HeaderRow && r == 0 };
            foreach (var text in module.Cells[r] ?? [])
            {
                var cell = new TableCell();
                var inlines = InlineMarkdown(text);
                if (inlines.Count > 0)
                {
                    var paragraph = new ParagraphBlock();
                    paragraph.Inlines.AddRange(inlines);
                    cell.Blocks.Add(paragraph);
                }

                row.Cells.Add(cell);
            }

            table.Rows.Add(row);
        }

        return [table];
    }

    /// <summary>Backslash-escapes a leading heading, quote, list, fence or rule marker so a single line stays inline text.</summary>
    internal static string EscapeBlockSyntax(string line)
    {
        if (line.Length == 0)
        {
            return line;
        }

        if (OrderedListMarker().Match(line) is { Success: true } ordered)
        {
            var at = ordered.Groups["digits"].Length;
            return line[..at] + "\\" + line[at..];
        }

        return BlockMarker().IsMatch(line) || ThematicBreak().IsMatch(line) ? "\\" + line : line;
    }

    /// <summary>A lowercase anchor id from heading text ("Getting started" → "getting-started").</summary>
    internal static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if ((c is ' ' or '-' or '_') && sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().TrimEnd('-');
        return slug.Length == 0 ? "section" : slug;
    }

    [GeneratedRegex(@"^(?<digits>\d{1,9})[.)](\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedListMarker();

    [GeneratedRegex(@"^(#{1,6}(\s|$)|>|[-+*](\s|$)|```|~~~)", RegexOptions.CultureInvariant)]
    private static partial Regex BlockMarker();

    [GeneratedRegex(@"^([-*_]\s*){3,}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThematicBreak();
}
