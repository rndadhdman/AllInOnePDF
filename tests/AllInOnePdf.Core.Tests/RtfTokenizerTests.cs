using System.Text;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Importers.Rtf;

namespace AllInOnePdf.Core.Tests;

public class RtfTokenizerTests
{
    private static List<RtfToken> Tokens(string rtf, out RtfTokenizer tokenizer)
    {
        tokenizer = new RtfTokenizer(Encoding.Latin1.GetBytes(rtf));
        var tokens = new List<RtfToken>();
        while (true)
        {
            var t = tokenizer.Next();
            if (t.Kind == RtfTokenKind.Eof)
            {
                return tokens;
            }

            tokens.Add(t);
        }
    }

    private static string Text(RtfTokenizer tokenizer, RtfToken token) =>
        Encoding.Latin1.GetString(tokenizer.Slice(token.Start, token.Length));

    [Fact]
    public void Control_words_with_and_without_parameters_and_delimiters()
    {
        var tokens = Tokens(@"{\rtf1\b0 hello\li-720\par}", out var tok);

        Assert.Equal(RtfTokenKind.GroupStart, tokens[0].Kind);
        Assert.Equal(("rtf", 1, true), (tokens[1].Word, tokens[1].Parameter, tokens[1].HasParameter));
        Assert.Equal(("b", 0, true), (tokens[2].Word, tokens[2].Parameter, tokens[2].HasParameter));
        Assert.Equal(RtfTokenKind.Text, tokens[3].Kind);
        Assert.Equal("hello", Text(tok, tokens[3])); // the space after \b0 is the delimiter
        Assert.Equal(("li", -720), (tokens[4].Word, tokens[4].Parameter));
        Assert.Equal(("par", false), (tokens[5].Word, tokens[5].HasParameter));
        Assert.Equal(RtfTokenKind.GroupEnd, tokens[6].Kind);
        Assert.Equal(7, tokens.Count);
        Assert.False(tok.Unbalanced);
    }

    [Fact]
    public void Only_one_space_is_consumed_as_the_delimiter()
    {
        var tokens = Tokens(@"\i  two", out var tok);
        Assert.Equal(" two", Text(tok, tokens[1]));
    }

    [Fact]
    public void Control_symbols_and_hex_bytes()
    {
        var tokens = Tokens(@"\~\-\_\{\}\\\*\:\|\'e9\'4A", out _);

        Assert.Equal("~-_{}\\*:|", new string(tokens.Take(9).Select(t => t.Symbol).ToArray()));
        Assert.All(tokens.Take(9), t => Assert.Equal(RtfTokenKind.ControlSymbol, t.Kind));
        Assert.Equal((RtfTokenKind.HexByte, (byte)0xE9), (tokens[9].Kind, tokens[9].Byte));
        Assert.Equal((byte)0x4A, tokens[10].Byte);
    }

    [Fact]
    public void Line_breaks_are_ignored_and_backslash_newline_is_a_paragraph()
    {
        var tokens = Tokens("ab\r\ncd\\\nx", out var tok);

        Assert.Equal("ab", Text(tok, tokens[0]));
        Assert.Equal("cd", Text(tok, tokens[1]));
        Assert.Equal("par", tokens[2].Word);
        Assert.Equal("x", Text(tok, tokens[3]));
    }

    [Fact]
    public void Bin_reads_raw_bytes_including_braces()
    {
        var tokens = Tokens(@"{\bin4 {}\x}", out var tok);

        Assert.Equal(RtfTokenKind.Binary, tokens[1].Kind);
        Assert.Equal(@"{}\x", Text(tok, tokens[1]));
        Assert.Equal(RtfTokenKind.GroupEnd, tokens[2].Kind);
        Assert.False(tok.Unbalanced);
    }

    [Fact]
    public void Nested_groups_and_missing_closing_brace()
    {
        var tokens = Tokens(@"{a{b{c}}", out var tok);

        Assert.Equal(3, tokens.Count(t => t.Kind == RtfTokenKind.GroupStart));
        Assert.Equal(2, tokens.Count(t => t.Kind == RtfTokenKind.GroupEnd));
        Assert.True(tok.Unbalanced);
        Assert.Equal(1, tok.Depth);
    }

    [Fact]
    public void Extra_closing_brace_is_dropped_and_flagged()
    {
        var tokens = Tokens(@"{a}}b", out var tok);

        Assert.Single(tokens, t => t.Kind == RtfTokenKind.GroupEnd);
        Assert.True(tok.Unbalanced);
    }

    [Fact]
    public void Start_must_be_an_rtf_group_after_optional_bom_and_white_space()
    {
        Assert.Equal(0, RtfImporter.FindStart(Encoding.ASCII.GetBytes(@"{\rtf1 x}")));
        Assert.Equal(5, RtfImporter.FindStart([0xEF, 0xBB, 0xBF, (byte)'\r', (byte)'\n', .. Encoding.ASCII.GetBytes(@"{\rtf1}")]));
        Assert.Null(RtfImporter.FindStart(Encoding.ASCII.GetBytes("hello")));
        Assert.Null(RtfImporter.FindStart([]));
    }

    [Theory]
    [InlineData("HYPERLINK \"https://example.com/a\"", "https://example.com/a")]
    [InlineData("HYPERLINK https://example.com/b \\o \"tip\"", "https://example.com/b")]
    [InlineData(" HYPERLINK \\l \"_Toc123\" ", "#toc123")]
    [InlineData("HYPERLINK \"https://x.test/p\" \\l \"part\"", "https://x.test/p#part")]
    [InlineData("PAGE \\* MERGEFORMAT", null)]
    public void Hyperlink_field_instructions(string instruction, string? expected)
    {
        Assert.Equal(expected, RtfDocumentBuilder.ParseHyperlink(instruction));
    }
}
