using System.Text;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers.Rtf;

internal enum RtfPieceKind { Text, Break, FootnoteReference, Image }

/// <summary>One run of paragraph content with the formatting it was written in.</summary>
internal sealed class RtfPiece
{
    public RtfPieceKind Kind { get; init; }
    public StringBuilder Text { get; } = new();
    public InlineStyle Style { get; init; }
    public bool Monospace { get; init; }
    public int LinkId { get; init; }
    public string? Label { get; init; }
    public ImageBlock? Image { get; init; }
}

/// <summary>The content of the paragraph being read, collected until <c>\par</c> decides what block it becomes.</summary>
internal sealed class RtfParagraphBuffer
{
    public List<RtfPiece> Pieces { get; } = [];
    public List<string> Bookmarks { get; } = [];

    /// <summary>The paragraph had a <c>\pntext</c> group (a list marker written by WordPad and older writers).</summary>
    public bool LegacyMarker { get; set; }

    public StringBuilder MarkerText { get; } = new();

    /// <summary>Smallest font size (half-points) of the visible text.</summary>
    public int MinFontSize { get; private set; } = int.MaxValue;

    public bool AllBold { get; private set; } = true;

    public int VisibleChars { get; private set; }

    public bool HasContent => Pieces.Count > 0;

    public void AppendText(string text, InlineStyle style, bool mono, int linkId, int fontSize)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            VisibleChars += text.Length;
            MinFontSize = Math.Min(MinFontSize, fontSize);
            AllBold &= (style & InlineStyle.Bold) != 0;
        }

        if (Pieces.Count > 0 && Pieces[^1] is { Kind: RtfPieceKind.Text } last && last.Style == style && last.Monospace == mono && last.LinkId == linkId)
        {
            last.Text.Append(text);
            return;
        }

        var piece = new RtfPiece { Kind = RtfPieceKind.Text, Style = style, Monospace = mono, LinkId = linkId };
        piece.Text.Append(text);
        Pieces.Add(piece);
    }

    public void Add(RtfPiece piece)
    {
        if (piece.Kind is RtfPieceKind.Image or RtfPieceKind.FootnoteReference)
        {
            AllBold = false;
            VisibleChars++;
        }

        Pieces.Add(piece);
    }

    public string PlainText()
    {
        var sb = new StringBuilder();
        foreach (var piece in Pieces)
        {
            if (piece.Kind == RtfPieceKind.Text)
            {
                sb.Append(piece.Text);
            }
            else if (piece.Kind == RtfPieceKind.Break)
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }
}

internal sealed class RtfListFrame
{
    public required ListBlock List { get; init; }
    public ListItem? Item { get; set; }
    public required string Key { get; init; }
}

/// <summary>A list of blocks being built (the body, a footnote or a table cell), with its open list/code/quote runs.</summary>
internal sealed class RtfBlockSink
{
    public List<Block> Blocks { get; } = [];
    public List<RtfListFrame> Lists { get; } = [];
    public CodeBlock? OpenCode { get; set; }
    public QuoteBlock? OpenQuote { get; set; }

    /// <summary>Alignment of every paragraph added (table cells use it for column alignment).</summary>
    public List<TextAlignment> Alignments { get; } = [];

    public void Add(Block block)
    {
        Lists.Clear();
        OpenCode = null;
        OpenQuote = null;
        Blocks.Add(block);
    }
}

/// <summary>A place content flows into: the body or a footnote, each with its own paragraph and table.</summary>
internal sealed class RtfContext
{
    public RtfBlockSink Sink { get; } = new();
    public RtfParagraphBuffer Paragraph { get; set; } = new();
    public RtfTableBuilder? Table { get; set; }
    public Footnote? Note { get; init; }
    public bool IsBody => Note is null;
}
