using System.Text;

namespace AllInOnePdf.Core.Documents;

/// <summary>Base class for inline (within-paragraph) content.</summary>
public abstract class Inline
{
    public virtual IEnumerable<Inline> Children => [];

    public static void AppendPlainText(IEnumerable<Inline> inlines, StringBuilder sb)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline t:
                    sb.Append(t.Text);
                    break;
                case CodeInline c:
                    sb.Append(c.Code);
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case ImageInline img:
                    sb.Append(img.AltText);
                    break;
                case FootnoteReferenceInline fr:
                    sb.Append('[').Append(fr.Label).Append(']');
                    break;
                case MathInline m:
                    sb.Append(m.Latex);
                    break;
                case HtmlInline:
                    break;
                default:
                    AppendPlainText(inline.Children, sb);
                    break;
            }
        }
    }

    public static string ToPlainText(IEnumerable<Inline> inlines)
    {
        var sb = new StringBuilder();
        AppendPlainText(inlines, sb);
        return sb.ToString();
    }
}

public sealed class TextInline : Inline
{
    public string Text { get; set; }

    public TextInline(string text)
    {
        Text = text;
    }
}

[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Strikethrough = 4,
    Underline = 8,
    Highlight = 16,
    Superscript = 32,
    Subscript = 64,
    SmallCaps = 128,
}

/// <summary>A styled run (bold, italic, ...). Styles nest: a <see cref="StyledInline"/> inside another combines flags.</summary>
public sealed class StyledInline : Inline
{
    public InlineStyle Style { get; set; }
    public List<Inline> Inlines { get; } = [];

    public StyledInline() { }

    public StyledInline(InlineStyle style, params Inline[] inlines)
    {
        Style = style;
        Inlines.AddRange(inlines);
    }

    public StyledInline(InlineStyle style, string text)
        : this(style, new TextInline(text))
    {
    }

    public override IEnumerable<Inline> Children => Inlines;
}

public sealed class CodeInline : Inline
{
    public string Code { get; set; }

    public CodeInline(string code)
    {
        Code = code;
    }
}

public sealed class LinkInline : Inline
{
    public required string Url { get; set; }
    public string? Title { get; set; }
    public List<Inline> Inlines { get; } = [];

    /// <summary>True for links that target an anchor within this document (<c>#heading-id</c>).</summary>
    public bool IsInternal => Url.StartsWith('#');

    public override IEnumerable<Inline> Children => Inlines;
}

public sealed class ImageInline : Inline
{
    public required string Source { get; set; }
    public string? AltText { get; set; }
    public string? Title { get; set; }
}

public sealed class LineBreakInline : Inline
{
    /// <summary>Hard breaks always break; soft breaks are rendered as a space (Markdown semantics).</summary>
    public bool IsHard { get; set; } = true;
}

public sealed class FootnoteReferenceInline : Inline
{
    public required string Label { get; set; }
}

public sealed class HtmlInline : Inline
{
    public required string Html { get; set; }
}

public sealed class MathInline : Inline
{
    public required string Latex { get; set; }
}
