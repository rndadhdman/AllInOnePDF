using AllInOnePdf.Core.Themes;

// The contract lives in the Rendering namespace (where it was first declared) so existing callers keep compiling;
// the implementation is AllInOnePdf.Core.Highlighting.SyntaxHighlighter.
namespace AllInOnePdf.Core.Rendering;

/// <summary>Optional hook for syntax highlighting: returns coloured runs for a code block.</summary>
public interface ISyntaxHighlighter
{
    /// <summary>
    /// Splits <paramref name="code"/> into runs whose concatenated text equals the input (line endings normalised to <c>\n</c>).
    /// Returns null when the language is unknown so the renderer falls back to plain text.
    /// </summary>
    IReadOnlyList<HighlightedRun>? Highlight(string code, string? language, SyntaxPalette palette, bool darkBackground);
}

/// <summary>A run of code text with its colour (null = default text colour) and token kind.</summary>
public readonly record struct HighlightedRun(string Text, string? ColorHex, bool Bold = false, bool Italic = false, SyntaxTokenKind Kind = SyntaxTokenKind.Plain);

/// <summary>Token categories produced by the highlighter. <see cref="SyntaxTokenKinds.CssClass"/> maps them to <c>hl-*</c> classes.</summary>
public enum SyntaxTokenKind
{
    Plain,
    Keyword,
    Type,
    String,
    Number,
    Comment,
    Attribute,
    Punctuation,
    Function,
    Variable,
    Operator,
    /// <summary>Added line in a diff.</summary>
    Inserted,
    /// <summary>Removed line in a diff.</summary>
    Deleted,
}

public static class SyntaxTokenKinds
{
    /// <summary>Short CSS class suffix used by the HTML renderer (<c>hl-kw</c>, <c>hl-str</c> ...); null for plain text.</summary>
    public static string? CssClass(SyntaxTokenKind kind) => kind switch
    {
        SyntaxTokenKind.Keyword => "kw",
        SyntaxTokenKind.Type => "type",
        SyntaxTokenKind.String => "str",
        SyntaxTokenKind.Number => "num",
        SyntaxTokenKind.Comment => "cmt",
        SyntaxTokenKind.Attribute => "attr",
        SyntaxTokenKind.Punctuation => "punct",
        SyntaxTokenKind.Function => "fn",
        SyntaxTokenKind.Variable => "var",
        SyntaxTokenKind.Operator => "op",
        SyntaxTokenKind.Inserted => "ins",
        SyntaxTokenKind.Deleted => "del",
        _ => null,
    };
}
