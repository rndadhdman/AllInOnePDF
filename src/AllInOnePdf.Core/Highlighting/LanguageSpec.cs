using System.Collections.Frozen;

namespace AllInOnePdf.Core.Highlighting;

/// <summary>Which lexer handles a language.</summary>
internal enum LexerKind { Generic, Json, Yaml, Markup, Css, Markdown, Ini, Toml, Diff, Plain }

/// <summary>Declarative rules for the generic C-family / scripting lexer.</summary>
internal sealed class LanguageSpec
{
    public required string Name { get; init; }
    public LexerKind Lexer { get; init; } = LexerKind.Generic;
    public FrozenSet<string> Keywords { get; init; } = FrozenSet<string>.Empty;
    public FrozenSet<string> Types { get; init; } = FrozenSet<string>.Empty;
    /// <summary>Constants such as true/false/null; coloured like numbers.</summary>
    public FrozenSet<string> Literals { get; init; } = FrozenSet<string>.Empty;
    /// <summary>Built-in functions/commands coloured as functions even without a following parenthesis.</summary>
    public FrozenSet<string> Builtins { get; init; } = FrozenSet<string>.Empty;
    /// <summary>Identifiers coloured as variables (self, this ...).</summary>
    public FrozenSet<string> SpecialVariables { get; init; } = FrozenSet<string>.Empty;
    public bool CaseInsensitive { get; init; }
    public string[] LineComments { get; init; } = [];
    public (string Open, string Close)[] BlockComments { get; init; } = [];
    public string StringQuotes { get; init; } = "\"'";
    /// <summary>Quotes that delimit identifiers (SQL "name", `name`), coloured as variables.</summary>
    public string IdentifierQuotes { get; init; } = string.Empty;
    /// <summary>Quotes that may span lines (JavaScript template literals, Go raw strings).</summary>
    public string MultilineQuotes { get; init; } = string.Empty;
    public char EscapeChar { get; init; } = '\\';
    /// <summary>Python/Kotlin/TOML/C# 11 style triple-quoted strings.</summary>
    public bool TripleQuotes { get; init; }
    /// <summary>Identifier prefixes that turn a following quote into a string (Python f/r/b, Rust b/r).</summary>
    public FrozenSet<string> StringPrefixes { get; init; } = FrozenSet<string>.Empty;
    /// <summary>A '#' comment only starts at line start or after whitespace (shells).</summary>
    public bool HashCommentNeedsSpace { get; init; }
    /// <summary>$name / ${name} variables (shells, PowerShell).</summary>
    public bool DollarVariables { get; init; }
    /// <summary>@name decorators / annotations.</summary>
    public bool AtAnnotations { get; init; }
    /// <summary>@name / :name parameters (SQL).</summary>
    public bool AtVariables { get; init; }
    /// <summary>C# verbatim (@"") and interpolated ($"") strings.</summary>
    public bool CSharpStrings { get; init; }
    /// <summary>C# [Attribute] at the start of a line.</summary>
    public bool BracketAttributes { get; init; }
    /// <summary>PowerShell [TypeName] anywhere.</summary>
    public bool BracketTypes { get; init; }
    /// <summary>Rust #[attr] / #![attr].</summary>
    public bool HashBracketAttributes { get; init; }
    /// <summary>#if / #region / #include lines.</summary>
    public bool Preprocessor { get; init; }
    /// <summary>Capitalised identifiers are treated as type names.</summary>
    public bool CapitalizedTypes { get; init; }
    /// <summary>Command-line options (-x, --long, PowerShell -Param) are attributes.</summary>
    public bool DashOptions { get; init; }
    /// <summary>Identifiers may contain '-' (PowerShell Verb-Noun cmdlets).</summary>
    public bool DashInIdentifiers { get; init; }
    /// <summary>Operators spelled as dash words (PowerShell -eq, -like ...).</summary>
    public FrozenSet<string> DashOperators { get; init; } = FrozenSet<string>.Empty;
    /// <summary>Rust 'lifetime labels.</summary>
    public bool Lifetimes { get; init; }
    /// <summary>name! macro calls (Rust).</summary>
    public bool BangMacros { get; init; }
    /// <summary>Basic JSX/TSX tag recognition.</summary>
    public bool Jsx { get; init; }
}
