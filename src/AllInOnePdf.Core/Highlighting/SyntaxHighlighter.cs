using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using K = AllInOnePdf.Core.Rendering.SyntaxTokenKind;

namespace AllInOnePdf.Core.Highlighting;

/// <summary>
/// Dependency-free syntax highlighter for code blocks. A single-pass tokenizer with per-language rules
/// (keywords, types, strings, numbers, comments, attributes/decorators, punctuation, functions, variables, operators).
/// Thread-safe and stateless; register it as a singleton.
/// </summary>
/// <remarks>
/// Supported: csharp, javascript, typescript, jsx, tsx, python, json, yaml, xml, html, css, scss, bash, powershell, sql,
/// go, rust, java, kotlin, markdown, toml, ini, diff, plain (plus common aliases such as cs, js, ts, py, yml, sh, zsh, ps1).
/// Unknown languages return null so callers render plain text.
/// </remarks>
public sealed class SyntaxHighlighter : ISyntaxHighlighter
{
    private readonly record struct Style(string? Color, bool Bold = false, bool Italic = false);

    // Indexed by (int)SyntaxTokenKind.
    private static readonly Style[] LightStyles =
    [
        new(null),                         // Plain
        new("#CF222E"),                    // Keyword
        new("#953800"),                    // Type
        new("#0A3069"),                    // String
        new("#0550AE"),                    // Number
        new("#6E7781", Italic: true),      // Comment
        new("#116329"),                    // Attribute
        new(null),                         // Punctuation
        new("#8250DF"),                    // Function
        new("#953800"),                    // Variable
        new("#CF222E"),                    // Operator
        new("#116329"),                    // Inserted
        new("#82071E"),                    // Deleted
    ];

    private static readonly Style[] DarkStyles =
    [
        new(null),
        new("#C678DD"),
        new("#E5C07B"),
        new("#98C379"),
        new("#D19A66"),
        new("#7F848E", Italic: true),
        new("#D19A66"),
        new(null),
        new("#61AFEF"),
        new("#E06C75"),
        new("#56B6C2"),
        new("#98C379"),
        new("#E06C75"),
    ];

    private static readonly Style[] MonoStyles =
    [
        new(null),
        new(null, Bold: true),
        new(null),
        new(null),
        new(null),
        new(null, Italic: true),
        new(null),
        new(null),
        new(null),
        new(null),
        new(null),
        new(null, Bold: true),
        new(null, Italic: true),
    ];

    /// <summary>Canonical language names this highlighter understands.</summary>
    public static IReadOnlyCollection<string> SupportedLanguages { get; } = Languages.Names.Order(StringComparer.Ordinal).ToArray();

    /// <summary>True when <paramref name="language"/> (or one of its aliases) has rules.</summary>
    public static bool IsSupported(string? language) => Languages.Find(language) is not null;

    /// <summary>Resolves <see cref="SyntaxPalette.Auto"/> using the code background luminance.</summary>
    public static SyntaxPalette ResolvePalette(SyntaxPalette palette, bool darkBackground) =>
        palette == SyntaxPalette.Auto ? (darkBackground ? SyntaxPalette.Dark : SyntaxPalette.Light) : palette;

    /// <summary>Colour used for a token kind under a palette (null = default text colour).</summary>
    public static string? ColorFor(SyntaxTokenKind kind, SyntaxPalette palette, bool darkBackground) =>
        StylesFor(ResolvePalette(palette, darkBackground))[(int)kind].Color;

    public IReadOnlyList<HighlightedRun>? Highlight(string code, string? language, SyntaxPalette palette, bool darkBackground)
    {
        var spec = Languages.Find(language);
        if (spec is null)
        {
            return null;
        }

        var text = code ?? string.Empty;
        if (text.Contains('\r', StringComparison.Ordinal))
        {
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        }

        List<Token> tokens;
        try
        {
            tokens = Lexer.Run(text, spec);
        }
        catch (Exception)
        {
            // The lexer is defensive, but a highlighter must never break rendering.
            tokens = [new Token(K.Plain, 0, text.Length)];
        }

        var styles = StylesFor(ResolvePalette(palette, darkBackground));
        var runs = new List<HighlightedRun>(tokens.Count);
        foreach (var t in tokens)
        {
            if (t.Length == 0)
            {
                continue;
            }

            var st = styles[(int)t.Kind];
            runs.Add(new HighlightedRun(text.Substring(t.Start, t.Length), st.Color, st.Bold, st.Italic, t.Kind));
        }

        return runs;
    }

    private static Style[] StylesFor(SyntaxPalette resolved) => resolved switch
    {
        SyntaxPalette.Dark => DarkStyles,
        SyntaxPalette.Mono => MonoStyles,
        _ => LightStyles,
    };
}
