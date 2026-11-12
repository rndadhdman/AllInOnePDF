using System.Collections.Frozen;

namespace AllInOnePdf.Core.Highlighting;

/// <summary>Per-language rules and the alias table (cs → csharp, yml → yaml ...).</summary>
internal static class Languages
{
    private static FrozenSet<string> Set(string words, bool ignoreCase = false) =>
        words.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .ToFrozenSet(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly LanguageSpec CSharp = new()
    {
        Name = "csharp",
        Keywords = Set("""
            abstract as base break case catch checked class const continue default delegate do else enum event explicit extern
            finally fixed for foreach goto if implicit in interface internal is lock namespace new operator out override params
            private protected public readonly ref return sealed sizeof stackalloc static struct switch this throw try typeof
            unchecked unsafe using virtual volatile while add alias and async await by descending equals from get global group
            init into join let managed nameof not or orderby partial record remove required scoped select set unmanaged value
            var when where with yield file
            """),
        Types = Set("bool byte char decimal double float int long nint nuint object sbyte short string uint ulong ushort void dynamic"),
        Literals = Set("true false null default"),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        TripleQuotes = true,
        CSharpStrings = true,
        BracketAttributes = true,
        Preprocessor = true,
        CapitalizedTypes = true,
    };

    private static readonly LanguageSpec JavaScript = new()
    {
        Name = "javascript",
        Keywords = Set("""
            async await break case catch class const continue debugger default delete do else export extends finally for from
            function get if import in instanceof let new of return set static super switch this throw try typeof var void
            while with yield as
            """),
        Types = Set("Array Boolean Date Error Function JSON Map Math Number Object Promise Proxy Reflect RegExp Set String Symbol WeakMap WeakSet BigInt"),
        Literals = Set("true false null undefined NaN Infinity"),
        Builtins = Set("console require module window document globalThis"),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        StringQuotes = "\"'`",
        MultilineQuotes = "`",
        AtAnnotations = true,
        CapitalizedTypes = true,
    };

    private static readonly LanguageSpec TypeScript = new()
    {
        Name = "typescript",
        Keywords = Set("""
            abstract any as async await break case catch class const constructor continue debugger declare default delete do
            else enum export extends finally for from function get if implements import in infer instanceof interface is keyof
            let module namespace new of override private protected public readonly return satisfies set static super switch
            this throw try type typeof var void while with yield
            """),
        Types = Set("string number boolean bigint symbol object unknown never any void Array Promise Record Partial Readonly Map Set Date Error"),
        Literals = Set("true false null undefined NaN Infinity"),
        Builtins = Set("console require module window document globalThis"),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        StringQuotes = "\"'`",
        MultilineQuotes = "`",
        AtAnnotations = true,
        CapitalizedTypes = true,
    };

    private static readonly LanguageSpec Python = new()
    {
        Name = "python",
        Keywords = Set("and as assert async await break class continue def del elif else except finally for from global if import in is lambda match case nonlocal not or pass raise return try while with yield"),
        Types = Set("int float str bool bytes list dict set tuple object complex frozenset bytearray type"),
        Literals = Set("True False None"),
        Builtins = Set("print len range open enumerate zip map filter sorted reversed sum min max abs round isinstance issubclass super input repr iter next hasattr getattr setattr any all"),
        SpecialVariables = Set("self cls"),
        LineComments = ["#"],
        TripleQuotes = true,
        StringPrefixes = Set("f r b u rb br fr rf F R B U", ignoreCase: false),
        AtAnnotations = true,
    };

    private static readonly LanguageSpec Bash = new()
    {
        Name = "bash",
        Keywords = Set("if then else elif fi for while until do done case esac function in select return exit local export readonly declare typeset unset shift break continue time"),
        Builtins = Set("echo printf cd pwd read source alias eval exec test trap set ls cat grep sed awk find xargs mkdir rm cp mv chmod chown curl wget git sudo apt dnf brew npm dotnet docker kubectl tar make touch head tail sort uniq wc tee"),
        Literals = Set("true false"),
        LineComments = ["#"],
        HashCommentNeedsSpace = true,
        DollarVariables = true,
        DashOptions = true,
        EscapeChar = '\\',
    };

    private static readonly LanguageSpec PowerShell = new()
    {
        Name = "powershell",
        CaseInsensitive = true,
        Keywords = Set("begin break catch class continue data define do dynamicparam else elseif end enum exit filter finally for foreach from function if in param process return switch throw trap try until using var while workflow", ignoreCase: true),
        Literals = Set("$true $false $null", ignoreCase: true),
        LineComments = ["#"],
        BlockComments = [("<#", "#>")],
        EscapeChar = '`',
        DollarVariables = true,
        DashOptions = true,
        DashInIdentifiers = true,
        BracketTypes = true,
        DashOperators = Set("-eq -ne -gt -ge -lt -le -like -notlike -match -notmatch -contains -notcontains -in -notin -replace -split -join -and -or -not -xor -is -isnot -as -band -bor -bxor -f -ceq -cne -ieq -clike -cmatch", ignoreCase: true),
    };

    private static readonly LanguageSpec Sql = new()
    {
        Name = "sql",
        CaseInsensitive = true,
        Keywords = Set("""
            select from where and or not insert into values update set delete create alter drop table view index primary key
            foreign references join inner left right full outer cross on as group by order having limit offset distinct union
            all exists in between like is case when then else end begin commit rollback transaction with recursive returning
            default constraint unique check cascade if procedure function trigger declare exec execute top asc desc over
            partition
            """, ignoreCase: true),
        Types = Set("int integer bigint smallint tinyint decimal numeric float real double varchar nvarchar char nchar text date time timestamp datetime datetime2 boolean bit blob uuid json jsonb serial money", ignoreCase: true),
        Literals = Set("null true false", ignoreCase: true),
        Builtins = Set("count sum avg min max coalesce cast convert now lower upper trim substring length round isnull nullif row_number rank", ignoreCase: true),
        LineComments = ["--"],
        BlockComments = [("/*", "*/")],
        StringQuotes = "'",
        IdentifierQuotes = "\"`",
        AtVariables = true,
    };

    private static readonly LanguageSpec Go = new()
    {
        Name = "go",
        Keywords = Set("break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var"),
        Types = Set("bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint uint8 uint16 uint32 uint64 uintptr any"),
        Literals = Set("true false nil iota"),
        Builtins = Set("append cap close copy delete len make new panic print println recover"),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        StringQuotes = "\"'`",
        MultilineQuotes = "`",
    };

    private static readonly LanguageSpec Rust = new()
    {
        Name = "rust",
        Keywords = Set("as async await break const continue crate dyn else enum extern fn for if impl in let loop match mod move mut pub ref return static struct super trait type unsafe use where while"),
        Types = Set("i8 i16 i32 i64 i128 isize u8 u16 u32 u64 u128 usize f32 f64 bool char str String Vec Option Result Box Rc Arc Self HashMap HashSet"),
        Literals = Set("true false None Some Ok Err"),
        SpecialVariables = Set("self"),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        StringPrefixes = Set("b r br"),
        HashBracketAttributes = true,
        Lifetimes = true,
        BangMacros = true,
        CapitalizedTypes = true,
    };

    private static readonly LanguageSpec Java = new()
    {
        Name = "java",
        Keywords = Set("""
            abstract assert break case catch class const continue default do else enum extends final finally for goto if
            implements import instanceof interface native new package private protected public record return sealed permits
            static strictfp super switch synchronized this throw throws transient try var void volatile while yield
            """),
        Types = Set("boolean byte char double float int long short String Object Integer Long Double Boolean List Map Set"),
        Literals = Set("true false null"),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        TripleQuotes = true,
        AtAnnotations = true,
        CapitalizedTypes = true,
    };

    private static readonly LanguageSpec Kotlin = new()
    {
        Name = "kotlin",
        Keywords = Set("""
            abstract actual annotation as break by catch class companion const constructor continue crossinline data do else
            enum expect external final finally for fun get if import in infix init inline inner interface internal is
            lateinit noinline object open operator out override package private protected public reified return sealed set
            super suspend tailrec this throw try typealias val var vararg when where while
            """),
        Types = Set("Any Boolean Byte Char Double Float Int Long Nothing Short String Unit List Map Set Array MutableList"),
        Literals = Set("true false null"),
        Builtins = Set("println print listOf mapOf setOf mutableListOf arrayOf require check"),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        TripleQuotes = true,
        AtAnnotations = true,
        CapitalizedTypes = true,
    };

    private static readonly LanguageSpec TomlValues = new()
    {
        Name = "toml",
        Lexer = LexerKind.Toml,
        Literals = Set("true false inf nan"),
        LineComments = ["#"],
        TripleQuotes = true,
    };

    private static readonly LanguageSpec Jsx = Derive(JavaScript, "jsx", jsx: true);
    private static readonly LanguageSpec Tsx = Derive(TypeScript, "tsx", jsx: true);

    private static LanguageSpec Derive(LanguageSpec b, string name, bool jsx) => new()
    {
        Name = name,
        Keywords = b.Keywords,
        Types = b.Types,
        Literals = b.Literals,
        Builtins = b.Builtins,
        LineComments = b.LineComments,
        BlockComments = b.BlockComments,
        StringQuotes = b.StringQuotes,
        MultilineQuotes = b.MultilineQuotes,
        AtAnnotations = b.AtAnnotations,
        CapitalizedTypes = b.CapitalizedTypes,
        Jsx = jsx,
    };

    private static readonly LanguageSpec Json = new() { Name = "json", Lexer = LexerKind.Json, Literals = Set("true false null"), LineComments = ["//"], BlockComments = [("/*", "*/")] };
    private static readonly LanguageSpec Yaml = new() { Name = "yaml", Lexer = LexerKind.Yaml, Literals = Set("true false null yes no on off ~ True False Null TRUE FALSE NULL Yes No") };
    private static readonly LanguageSpec Xml = new() { Name = "xml", Lexer = LexerKind.Markup };
    private static readonly LanguageSpec Html = new() { Name = "html", Lexer = LexerKind.Markup };
    private static readonly LanguageSpec Css = new() { Name = "css", Lexer = LexerKind.Css, BlockComments = [("/*", "*/")] };
    private static readonly LanguageSpec Scss = new() { Name = "scss", Lexer = LexerKind.Css, LineComments = ["//"], BlockComments = [("/*", "*/")], DollarVariables = true };
    private static readonly LanguageSpec Markdown = new() { Name = "markdown", Lexer = LexerKind.Markdown };
    private static readonly LanguageSpec Ini = new() { Name = "ini", Lexer = LexerKind.Ini, Literals = Set("true false yes no on off", ignoreCase: true) };
    private static readonly LanguageSpec Diff = new() { Name = "diff", Lexer = LexerKind.Diff };
    private static readonly LanguageSpec Plain = new() { Name = "plain", Lexer = LexerKind.Plain };

    private static readonly FrozenDictionary<string, LanguageSpec> Map = BuildMap();

    private static FrozenDictionary<string, LanguageSpec> BuildMap()
    {
        var d = new Dictionary<string, LanguageSpec>(StringComparer.OrdinalIgnoreCase);
        void Add(LanguageSpec spec, params string[] aliases)
        {
            d[spec.Name] = spec;
            foreach (var a in aliases)
            {
                d[a] = spec;
            }
        }

        Add(CSharp, "cs", "c#", "csharp", "csx", "dotnet");
        Add(JavaScript, "js", "javascript", "mjs", "cjs", "node", "ecmascript");
        Add(TypeScript, "ts", "typescript", "mts", "cts");
        Add(Jsx, "jsx");
        Add(Tsx, "tsx");
        Add(Python, "py", "python3", "py3", "gyp", "pyw");
        Add(Json, "jsonc", "json5", "geojson", "webmanifest");
        Add(Yaml, "yml");
        Add(Xml, "xaml", "svg", "xsd", "xsl", "xslt", "plist", "csproj", "props", "targets", "rss", "atom", "nuspec", "resx");
        Add(Html, "htm", "xhtml", "vue", "razor", "cshtml");
        Add(Css, "less");
        Add(Scss, "sass");
        Add(Bash, "sh", "zsh", "shell", "ksh", "console", "shellscript", "fish");
        Add(PowerShell, "ps", "ps1", "psm1", "psd1", "pwsh", "posh");
        Add(Sql, "mysql", "postgres", "postgresql", "psql", "sqlite", "tsql", "plsql", "mssql");
        Add(Go, "golang");
        Add(Rust, "rs");
        Add(Java);
        Add(Kotlin, "kt", "kts");
        Add(Markdown, "md", "mdown", "mkd", "mkdn");
        Add(TomlValues);
        Add(Ini, "cfg", "conf", "properties", "editorconfig", "gitconfig", "desktop", "env", "dotenv");
        Add(Diff, "patch", "udiff");
        Add(Plain, "text", "txt", "plaintext", "none", "nohighlight", "output", "log");
        return d.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolves a fence info string ("C#", "language-ts", "python {linenos}") to a spec, or null.</summary>
    public static LanguageSpec? Find(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var name = language.Trim();
        var cut = name.IndexOfAny([' ', '\t', '{', ',', ';']);
        if (cut > 0)
        {
            name = name[..cut];
        }

        if (name.StartsWith("language-", StringComparison.OrdinalIgnoreCase))
        {
            name = name["language-".Length..];
        }
        else if (name.StartsWith("lang-", StringComparison.OrdinalIgnoreCase))
        {
            name = name["lang-".Length..];
        }

        name = name.TrimStart('.');
        return Map.GetValueOrDefault(name);
    }

    /// <summary>Spec used for embedded &lt;script&gt; content.</summary>
    public static LanguageSpec Script => JavaScript;

    /// <summary>Spec used for embedded &lt;style&gt; content.</summary>
    public static LanguageSpec Style => Css;

    /// <summary>Value rules for TOML.</summary>
    public static LanguageSpec Toml => TomlValues;

    /// <summary>All canonical language names (for diagnostics and tests).</summary>
    public static IEnumerable<string> Names => Map.Values.Select(v => v.Name).Distinct(StringComparer.Ordinal);
}
