using System.Diagnostics;
using System.Text;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Highlighting;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using Microsoft.Extensions.DependencyInjection;
using K = AllInOnePdf.Core.Rendering.SyntaxTokenKind;

namespace AllInOnePdf.Core.Tests;

public class HighlighterTests
{
    private static readonly SyntaxHighlighter Highlighter = new();

    public static TheoryData<string, string, K[]> Snippets => new()
    {
        { "csharp", "[Fact]\npublic static int Add(int a) { var s = @\"x\"; return a + 0x1F; } // done", [K.Attribute, K.Keyword, K.Type, K.Function, K.String, K.Number, K.Comment, K.Punctuation, K.Operator] },
        { "cs", "#region R\nnamespace A; class Foo : IBar { }", [K.Keyword, K.Type, K.Punctuation] },
        { "javascript", "const x = await fetch(`/api/${id}`); // go\nlet n = 3.5e2; /* c */ console.log(true)", [K.Keyword, K.Function, K.String, K.Comment, K.Number, K.Operator, K.Punctuation] },
        { "ts", "interface P { name: string; age?: number }\n@Component({})\nexport type Id = number | null;", [K.Keyword, K.Type, K.Attribute, K.Punctuation, K.Operator, K.Number] },
        { "tsx", "const App = () => <Button kind=\"primary\" onClick={go}>Hi</Button>;", [K.Keyword, K.Type, K.Attribute, K.String, K.Punctuation] },
        { "jsx", "return <div className='x'>{count}</div>", [K.Keyword, K.Attribute, K.String] },
        { "py", "@dataclass\ndef f(self, x: int) -> str:\n    \"\"\"Doc.\"\"\"\n    return f\"{x}\" if x > 1 else None  # c", [K.Attribute, K.Keyword, K.Function, K.Variable, K.Type, K.String, K.Number, K.Comment, K.Operator] },
        { "json", "{ \"name\": \"aio\", \"n\": -1.5, \"ok\": true, \"a\": [1, null] }", [K.Attribute, K.String, K.Number, K.Punctuation] },
        { "yml", "# config\nname: \"build\"\non: [push]\njobs:\n  test:\n    runs-on: ubuntu-latest\n    retries: 3\n    enabled: true\n    base: &b !!str x", [K.Comment, K.Attribute, K.String, K.Number, K.Punctuation, K.Variable, K.Type] },
        { "xml", "<?xml version=\"1.0\"?>\n<!-- c --><root a=\"1\"><![CDATA[x]]>&amp;</root>", [K.Keyword, K.Comment, K.Punctuation, K.Attribute, K.Operator, K.String, K.Variable] },
        { "html", "<p class=\"x\">Hi</p><script>const a = 1;</script><style>p { color: red; }</style>", [K.Keyword, K.Attribute, K.String, K.Number, K.Punctuation] },
        { "css", "@media screen { .card:hover > a#id { color: #fff; margin: 10px !important; width: calc(100% - 2px); } } /* c */", [K.Keyword, K.Type, K.Attribute, K.Number, K.Function, K.Punctuation, K.Comment, K.Operator] },
        { "scss", "$gap: 4px;\n.a { &:hover { padding: $gap; } } // c", [K.Variable, K.Type, K.Attribute, K.Number, K.Comment] },
        { "sh", "#!/bin/bash\nif [ -f \"$FILE\" ]; then echo 'ok' --verbose; fi # done\nexport N=3", [K.Comment, K.Keyword, K.Attribute, K.String, K.Function, K.Number] },
        { "zsh", "for f in *.md; do cat $f; done", [K.Keyword, K.Function, K.Variable] },
        { "ps1", "<# block #>\nfunction Get-Thing { param([string]$Name) if ($Name -eq $null) { Write-Host \"x\" -ForegroundColor Red } } # c", [K.Comment, K.Keyword, K.Function, K.Type, K.Variable, K.Operator, K.Number, K.String, K.Attribute] },
        { "sql", "SELECT id, COUNT(*) AS n FROM users u WHERE u.name = 'x' AND age > 18 -- c\n/* b */ GROUP BY id;", [K.Keyword, K.Function, K.String, K.Number, K.Comment, K.Operator, K.Punctuation] },
        { "go", "package main\nfunc main() { x := []int{1, 2}; fmt.Println(len(x), `raw`, nil) } // c", [K.Keyword, K.Function, K.Type, K.Number, K.String, K.Comment] },
        { "rs", "#[derive(Debug)]\nfn main<'a>() -> Option<i32> { let s = String::from(\"x\"); println!(\"{}\", 1u8); None } // c", [K.Attribute, K.Keyword, K.Function, K.Variable, K.Type, K.String, K.Number, K.Comment] },
        { "java", "@Override\npublic String toString() { return \"x\" + 42; } /* c */", [K.Attribute, K.Keyword, K.Type, K.Function, K.String, K.Number, K.Comment] },
        { "kt", "data class P(val n: Int)\nfun main() { println(\"\"\"raw\"\"\"); val x = null } // c", [K.Keyword, K.Type, K.Function, K.String, K.Number, K.Comment] },
        { "md", "# Title\n\nSome **bold**, *em* and `code` with [link](https://x.y).\n- item\n```js\nlet x\n```", [K.Keyword, K.Variable, K.String, K.Function, K.Punctuation, K.Attribute] },
        { "toml", "# c\n[package]\nname = \"aio\"\nversion = 1\nok = true\ntags = [\"a\", \"b\"]", [K.Comment, K.Type, K.Attribute, K.Operator, K.String, K.Number, K.Punctuation] },
        { "ini", "; c\n[core]\neditor = vim\ncount=3", [K.Comment, K.Type, K.Attribute, K.Operator, K.String, K.Number] },
        { "diff", "diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1,2 +1,2 @@ fn\n-old\n+new\n same", [K.Keyword, K.Attribute, K.Deleted, K.Inserted] },
        { "plain", "just text", [K.Plain] },
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void Tokenizes_each_language(string language, string code, K[] expected)
    {
        var runs = Highlighter.Highlight(code, language, SyntaxPalette.Light, darkBackground: false);

        Assert.NotNull(runs);
        Assert.Equal(code, string.Concat(runs!.Select(r => r.Text)));
        var kinds = runs!.Select(r => r.Kind).ToHashSet();
        foreach (var kind in expected)
        {
            Assert.True(kinds.Contains(kind), $"{language}: expected a {kind} token; got {string.Join(", ", runs!.Where(r => r.Kind != K.Plain).Select(r => $"{r.Kind}:'{r.Text}'"))}");
        }
    }

    [Fact]
    public void Classifies_specific_tokens()
    {
        var runs = Highlighter.Highlight("public int Count(string s) => s.Length; // n", "csharp", SyntaxPalette.Light, false)!;

        Assert.Contains(runs, r => r is { Text: "public", Kind: K.Keyword });
        Assert.Contains(runs, r => r is { Text: "int", Kind: K.Type });
        Assert.Contains(runs, r => r is { Text: "Count", Kind: K.Function });
        Assert.Contains(runs, r => r is { Text: "// n", Kind: K.Comment });
    }

    [Theory]
    [InlineData("klingon")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Unknown_language_returns_null(string? language)
    {
        Assert.Null(Highlighter.Highlight("x = 1", language, SyntaxPalette.Auto, false));
    }

    [Theory]
    [InlineData("C#")]
    [InlineData("language-ts")]
    [InlineData("Python {linenos=true}")]
    [InlineData("YML")]
    [InlineData("PS1")]
    [InlineData("golang")]
    public void Resolves_aliases(string language)
    {
        Assert.True(SyntaxHighlighter.IsSupported(language));
    }

    [Fact]
    public void Lists_every_required_language()
    {
        string[] required = ["csharp", "javascript", "typescript", "jsx", "tsx", "python", "json", "yaml", "xml", "html", "css", "scss", "bash", "powershell", "sql", "go", "rust", "java", "kotlin", "markdown", "toml", "ini", "diff", "plain"];
        foreach (var name in required)
        {
            Assert.Contains(name, SyntaxHighlighter.SupportedLanguages);
        }
    }

    [Fact]
    public void Palette_selection_follows_background()
    {
        const string code = "return 1;";
        var light = Highlighter.Highlight(code, "js", SyntaxPalette.Auto, darkBackground: false)!.First(r => r.Kind == K.Keyword);
        var dark = Highlighter.Highlight(code, "js", SyntaxPalette.Auto, darkBackground: true)!.First(r => r.Kind == K.Keyword);
        var forcedDark = Highlighter.Highlight(code, "js", SyntaxPalette.Dark, darkBackground: false)!.First(r => r.Kind == K.Keyword);
        var mono = Highlighter.Highlight(code, "js", SyntaxPalette.Mono, darkBackground: true)!;

        Assert.Equal(SyntaxHighlighter.ColorFor(K.Keyword, SyntaxPalette.Light, false), light.ColorHex);
        Assert.Equal(SyntaxHighlighter.ColorFor(K.Keyword, SyntaxPalette.Dark, false), dark.ColorHex);
        Assert.Equal(dark.ColorHex, forcedDark.ColorHex);
        Assert.NotEqual(light.ColorHex, dark.ColorHex);
        Assert.All(mono, r => Assert.Null(r.ColorHex));
        Assert.True(mono.First(r => r.Kind == K.Keyword).Bold);
        Assert.Equal(SyntaxPalette.Dark, SyntaxHighlighter.ResolvePalette(SyntaxPalette.Auto, true));
        Assert.Equal(SyntaxPalette.Light, SyntaxHighlighter.ResolvePalette(SyntaxPalette.Auto, false));
    }

    [Fact]
    public void Garbage_input_never_throws_and_round_trips()
    {
        var random = new Random(1234);
        const string alphabet = "abcXYZ019 \t\n\r\"'`$@#[]{}()<>/\\*-+=!?:;,.|&%^~_—é\u0000";
        foreach (var language in SyntaxHighlighter.SupportedLanguages)
        {
            for (var n = 0; n < 40; n++)
            {
                var sb = new StringBuilder();
                var length = random.Next(0, 300);
                for (var i = 0; i < length; i++)
                {
                    sb.Append(alphabet[random.Next(alphabet.Length)]);
                }

                var code = sb.ToString();
                var runs = Highlighter.Highlight(code, language, SyntaxPalette.Auto, false);
                Assert.NotNull(runs);
                var expected = code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                Assert.Equal(expected, string.Concat(runs!.Select(r => r.Text)));
            }

            // Unterminated constructs at the very end.
            foreach (var tail in new[] { "\"", "'", "/*", "<!--", "<a b=\"", "```", "$", "@", "#[", "<#", "\"\"\"", "[", "{" })
            {
                Assert.NotNull(Highlighter.Highlight("x " + tail, language, SyntaxPalette.Auto, false));
            }
        }
    }

    [Fact]
    public void Highlights_five_thousand_lines_quickly()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 5000; i++)
        {
            sb.Append("    public static async Task<int> Method").Append(i).Append("(string name, int count = 42) { var s = $\"{name}\"; return await Task.FromResult(count + 0x1F); } // line ").Append(i).Append('\n');
        }

        var code = sb.ToString();
        Highlighter.Highlight(code[..2000], "csharp", SyntaxPalette.Light, false); // warm-up (JIT)

        var sw = Stopwatch.StartNew();
        var runs = Highlighter.Highlight(code, "csharp", SyntaxPalette.Light, false);
        sw.Stop();

        Assert.NotNull(runs);
        // Guards against a quadratic lexer (which takes seconds here), not against a slow CI runner: the bound is loose.
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Html_renderer_emits_highlight_spans_and_stays_encoded()
    {
        var doc = new Document();
        doc.Blocks.Add(new CodeBlock { Code = "if (a < b) { return \"<x>\"; }", Language = "js" });

        var html = new DocumentHtmlRenderer(Highlighter).RenderFragment(doc);
        var plain = new DocumentHtmlRenderer().RenderFragment(doc);

        Assert.Contains("<span class=\"hl hl-kw\">if</span>", html);
        Assert.Contains("<span class=\"hl hl-str\">&quot;&lt;x&gt;&quot;</span>", html);
        Assert.DoesNotContain("<x>", html);
        Assert.DoesNotContain("class=\"hl", plain);
        Assert.Contains("&lt;x&gt;", plain);
    }

    [Fact]
    public void Html_renderer_leaves_unknown_languages_plain()
    {
        var doc = new Document();
        doc.Blocks.Add(new CodeBlock { Code = "a < b", Language = "klingon" });

        var html = new DocumentHtmlRenderer(Highlighter).RenderFragment(doc);

        Assert.DoesNotContain("class=\"hl", html);
        Assert.Contains("a &lt; b", html);
    }

    [Fact]
    public void Core_registration_provides_the_highlighter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aio-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var sp = new ServiceCollection().AddAllInOnePdfCore(dir).BuildServiceProvider();

            Assert.IsType<SyntaxHighlighter>(sp.GetRequiredService<ISyntaxHighlighter>());
            var html = sp.GetRequiredService<IDocumentHtmlRenderer>();
            var doc = new Document();
            doc.Blocks.Add(new CodeBlock { Code = "let x = 1", Language = "js" });
            Assert.Contains("hl-kw", html.RenderFragment(doc));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
