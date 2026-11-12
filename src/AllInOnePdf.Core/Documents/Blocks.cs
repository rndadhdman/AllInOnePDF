using System.Text;

namespace AllInOnePdf.Core.Documents;

/// <summary>Base class for block-level content (headings, paragraphs, lists, tables ...).</summary>
public abstract class Block
{
    /// <summary>Optional anchor id (used for intra-document links and the table of contents).</summary>
    public string? Id { get; set; }

    /// <summary>Optional source location hint (e.g. PDF page number) for round-tripping and diagnostics.</summary>
    public int? SourcePage { get; set; }

    /// <summary>Child blocks for container blocks; empty for leaf blocks.</summary>
    public virtual IEnumerable<Block> Children => [];

    public static IEnumerable<Block> EnumerateRecursive(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            foreach (var child in EnumerateRecursive(block.Children))
            {
                yield return child;
            }
        }
    }
}

/// <summary>A block whose content is a run of inlines (heading, paragraph).</summary>
public abstract class InlineContainerBlock : Block
{
    public List<Inline> Inlines { get; } = [];

    public string ToPlainText()
    {
        var sb = new StringBuilder();
        Inline.AppendPlainText(Inlines, sb);
        return sb.ToString();
    }
}

public sealed class HeadingBlock : InlineContainerBlock
{
    private int _level = 1;

    /// <summary>1 (largest) to 6.</summary>
    public int Level
    {
        get => _level;
        set => _level = Math.Clamp(value, 1, 6);
    }

    public HeadingBlock() { }

    public HeadingBlock(int level, string text)
    {
        Level = level;
        Inlines.Add(new TextInline(text));
    }

    /// <summary>Alignment override from the source (null = the theme's heading level decides). Justify behaves like Left.</summary>
    public TextAlignment? Alignment { get; set; }
}

public sealed class ParagraphBlock : InlineContainerBlock
{
    public ParagraphBlock() { }

    public ParagraphBlock(string text)
    {
        Inlines.Add(new TextInline(text));
    }

    /// <summary>Alignment override from the source (rare; most sources leave this null so the theme decides).</summary>
    public TextAlignment? Alignment { get; set; }
}

public enum TextAlignment { Left, Center, Right, Justify }

public sealed class ListBlock : Block
{
    public bool Ordered { get; set; }

    /// <summary>First number for ordered lists.</summary>
    public int Start { get; set; } = 1;

    /// <summary>Tight lists render without paragraph spacing between items.</summary>
    public bool Tight { get; set; } = true;

    public List<ListItem> Items { get; } = [];

    public override IEnumerable<Block> Children => Items.SelectMany(i => i.Blocks);
}

public sealed class ListItem
{
    public List<Block> Blocks { get; } = [];

    /// <summary>Task-list state: null for normal items, true/false for checkboxes.</summary>
    public bool? Checked { get; set; }

    public ListItem() { }

    public ListItem(string text)
    {
        Blocks.Add(new ParagraphBlock(text));
    }
}

public sealed class CodeBlock : Block
{
    public required string Code { get; set; }

    /// <summary>Language hint such as "csharp", "json"; null when unknown.</summary>
    public string? Language { get; set; }

    /// <summary>Optional caption or file name shown above the block.</summary>
    public string? Caption { get; set; }
}

public sealed class QuoteBlock : Block
{
    public List<Block> Blocks { get; } = [];

    /// <summary>Optional attribution shown after the quote.</summary>
    public string? Attribution { get; set; }

    public override IEnumerable<Block> Children => Blocks;
}

/// <summary>Callout / admonition (note, tip, warning ...). Importers map GitHub-style "> [!NOTE]" and similar syntaxes here.</summary>
public sealed class CalloutBlock : Block
{
    public CalloutKind Kind { get; set; } = CalloutKind.Note;
    public string? Title { get; set; }
    public List<Block> Blocks { get; } = [];

    public override IEnumerable<Block> Children => Blocks;
}

public enum CalloutKind { Note, Tip, Important, Warning, Caution }

public enum ColumnAlignment { Default, Left, Center, Right }

public sealed class TableBlock : Block
{
    public List<TableRow> Rows { get; } = [];

    /// <summary>Per-column alignment; shorter than the column count means "Default" for the rest.</summary>
    public List<ColumnAlignment> Alignments { get; } = [];

    public string? Caption { get; set; }

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Cells.Sum(c => c.ColumnSpan));

    public override IEnumerable<Block> Children => Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks);

    public ColumnAlignment GetAlignment(int column) =>
        column < Alignments.Count ? Alignments[column] : ColumnAlignment.Default;
}

public sealed class TableRow
{
    public bool IsHeader { get; set; }
    public List<TableCell> Cells { get; } = [];
}

public sealed class TableCell
{
    public List<Block> Blocks { get; } = [];
    public int ColumnSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;

    public TableCell() { }

    public TableCell(string text)
    {
        Blocks.Add(new ParagraphBlock(text));
    }

    public string ToPlainText()
    {
        var sb = new StringBuilder();
        foreach (var block in Blocks)
        {
            if (block is InlineContainerBlock icb)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(icb.ToPlainText());
            }
            else if (block is CodeBlock cb)
            {
                sb.Append(cb.Code);
            }
        }

        return sb.ToString();
    }
}

public sealed class ImageBlock : Block
{
    /// <summary>File path, URL, or <c>res:&lt;id&gt;</c> for an embedded <see cref="DocumentResource"/>.</summary>
    public required string Source { get; set; }
    public string? AltText { get; set; }
    public string? Title { get; set; }

    /// <summary>Requested width as a percentage of the content width (null = natural size, capped by the theme).</summary>
    public double? WidthPercent { get; set; }

    /// <summary>Intrinsic pixel size when known.</summary>
    public int? PixelWidth { get; set; }
    public int? PixelHeight { get; set; }

    /// <summary>Explicit caption; when null the caption falls back to <see cref="Title"/>, then <see cref="AltText"/>.</summary>
    public string? Caption { get; set; }

    /// <summary>null = theme decides (default); false = never show a caption; true = show <see cref="ResolveCaption"/> when the theme allows.</summary>
    public bool? ShowCaption { get; set; }

    /// <summary>The caption text to display, or null when none should be shown.</summary>
    public string? ResolveCaption()
    {
        if (ShowCaption == false)
        {
            return null;
        }

        var text = Caption ?? Title ?? AltText;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

public sealed class ThematicBreakBlock : Block;

/// <summary>Explicit page break (PDF exporter honours it; text exporters emit a separator).</summary>
public sealed class PageBreakBlock : Block;

/// <summary>Raw HTML passthrough for sources that contain it. Exporters that cannot render HTML show it as text or drop it.</summary>
public sealed class HtmlBlock : Block
{
    public required string Html { get; set; }
}

/// <summary>Display math (LaTeX). Rendered as monospace text until a math renderer exists.</summary>
public sealed class MathBlock : Block
{
    public required string Latex { get; set; }
}
