using AllInOnePdf.Layout.Infrastructure;
using AllInOnePdf.Layout.Text;

namespace AllInOnePdf.Layout.Tests;

public class TextShapingTests
{
    public TextShapingTests() => TestFonts.EnsureRegistered();

    private static ShapingOptions Options(string family, float size = 12, float letterSpacing = 0, params string[] disabled) =>
        new(FontRegistry.ResolveChain([family], 400, false), size, letterSpacing, disabled, 400, false);

    private static float Width(string text, ShapingOptions options) => TextShaper.Shape(text, options).Sum(r => r.Width);

    [Fact]
    public void Advances_scale_with_the_font_size()
    {
        var small = Width("Hello world", Options("Inter", 10));
        var large = Width("Hello world", Options("Inter", 20));
        Assert.True(small > 0);
        Assert.Equal(small * 2, large, 3);
    }

    [Fact]
    public void Letter_spacing_adds_once_per_character()
    {
        var plain = Width("abcde", Options("Inter", 10));
        var spaced = Width("abcde", Options("Inter", 10, letterSpacing: 1.5f));
        Assert.Equal(plain + (5 * 1.5f), spaced, 3);
    }

    [Fact]
    public void Standard_ligatures_are_off_like_QuestPDF()
    {
        // Literata has a standard fi ligature; QuestPDF's output never uses it.
        Assert.Equal(2, TextShaper.Shape("fi", Options("Literata")).Sum(r => r.Glyphs.Length));
        Assert.Equal(2, TextShaper.Shape("fi", Options("Literata", disabled: "liga")).Sum(r => r.Glyphs.Length));
    }

    [Fact]
    public void Disabling_calt_keeps_Inter_arrows_as_typed()
    {
        Assert.Single(TextShaper.Shape("->", Options("Inter")).SelectMany(r => r.Glyphs));
        Assert.Equal(2, TextShaper.Shape("->", Options("Inter", disabled: "calt")).Sum(r => r.Glyphs.Length));
    }

    [Fact]
    public void Clusters_map_back_to_utf16_indices()
    {
        // Inter's calt turns "->" into one arrow glyph: one cluster over two characters.
        var glyphs = TextShaper.Shape("-> x", Options("Inter")).SelectMany(r => r.Glyphs).ToArray();
        Assert.Equal([0, 2, 3], glyphs.Select(g => g.Cluster));
    }

    [Fact]
    public void Shaping_a_substring_keeps_absolute_clusters()
    {
        var runs = TextShaper.Shape("abc def", 4, 3, Options("Inter"));
        var run = Assert.Single(runs);
        Assert.Equal(4, run.Start);
        Assert.Equal(7, run.End);
        Assert.Equal([4, 5, 6], run.Glyphs.Select(g => g.Cluster));
    }

    [Fact]
    public void Resolved_styles_reduce_and_shift_scripts_like_QuestPDF()
    {
        var sup = new ResolvedTextStyle(TextStyle.Default.FontSize(10).Superscript());
        var sub = new ResolvedTextStyle(TextStyle.Default.FontSize(10).Subscript());
        Assert.Equal(6, sup.FontSize, 3);
        Assert.Equal(-3.5f, sup.BaselineShift, 3);
        Assert.Equal(2.5f, sub.BaselineShift, 3);
        Assert.Equal(1f, new ResolvedTextStyle(TextStyle.Default.FontSize(10).LetterSpacing(0.1f)).LetterSpacing, 3);
    }
}

public class TextLineBreakerTests
{
    private static BreakUnit Word(float width, bool breakAfter = false) => new() { Width = width, Kind = BreakUnitKind.Content, CanBreakAfter = breakAfter };

    private static BreakUnit Space(float width = 2) => new() { Width = width, Kind = BreakUnitKind.Space, CanBreakAfter = true };

    [Fact]
    public void Lines_break_after_spaces_and_spaces_hang()
    {
        BreakUnit[] units = [Word(10), Word(10), Space(), Word(10), Word(10), Space(), Word(10)];
        var lines = LineBreaker.Break(units, 45);
        Assert.Equal(2, lines.Count);
        Assert.Equal((0, 6), (lines[0].Start, lines[0].End));
        Assert.Equal(42, lines[0].Width, 3);
        Assert.Equal((6, 7), (lines[1].Start, lines[1].End));
    }

    [Fact]
    public void A_word_wider_than_the_line_breaks_mid_word()
    {
        BreakUnit[] units = [Word(10), Word(10), Word(10), Word(10)];
        var lines = LineBreaker.Break(units, 25);
        Assert.Equal([2, 2], lines.Select(l => l.End - l.Start));
    }

    [Fact]
    public void Hard_breaks_end_lines_and_start_paragraphs()
    {
        BreakUnit[] units = [Word(10), new() { Kind = BreakUnitKind.HardBreak }, Word(10)];
        var lines = LineBreaker.Break(units, 100, indent: 5);
        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].EndsWithHardBreak);
        Assert.True(lines[0].StartsParagraph);
        Assert.True(lines[1].StartsParagraph);
    }

    [Fact]
    public void The_first_line_indent_narrows_only_paragraph_first_lines()
    {
        BreakUnit[] units = [Word(10), Space(0), Word(10), Space(0), Word(10)];
        Assert.Equal(2, LineBreaker.Break(units, 25, indent: 6).Count);
        Assert.Single(LineBreaker.Break(units, 30, indent: 0));
    }

    [Fact]
    public void Soft_hyphen_breaks_only_when_the_hyphen_fits()
    {
        BreakUnit[] units =
        [
            Word(10), new() { Width = 10, Kind = BreakUnitKind.Content, CanBreakAfter = true, SoftHyphenAfter = true, HyphenWidth = 4 }, Word(10), Word(10),
        ];
        var fits = LineBreaker.Break(units, 25);
        Assert.True(fits[0].Hyphenated);
        Assert.Equal(24, fits[0].Width, 3);

        var tooNarrow = LineBreaker.Break(units, 22);
        Assert.False(tooNarrow[0].Hyphenated);
    }

    [Fact]
    public void Break_anywhere_spaces_hang_at_the_line_end()
    {
        // "ab; c" in a 3-wide line, break-anywhere: QuestPDF (Skia's paragraph) fills the line with "ab;", lets the
        // space hang and wraps "c" ("… ct = default)" | "=> await …" in the code stress documents).
        static BreakUnit Char(bool canBreakAfter = true) => new() { Width = 1, Kind = BreakUnitKind.Content, CanBreakAfter = canBreakAfter };
        BreakUnit[] units = [Char(), Char(), Char(canBreakAfter: false), new() { Width = 1, Kind = BreakUnitKind.Space, CanBreakAfter = true }, Char()];
        var lines = LineBreaker.Break(units, 3);
        Assert.Equal(2, lines.Count);
        Assert.Equal(4, lines[0].End);
        Assert.Equal(3, lines[0].Width, 3);
    }

    [Theory]
    [InlineData('a', '-', 'b', false, true)]
    [InlineData(' ', '-', 'b', false, false)]
    [InlineData('a', '-', '5', false, false)]
    [InlineData('a', '–', 'b', false, true)]
    [InlineData('\0', 'a', 'b', false, false)]
    [InlineData('\0', 'a', 'b', true, true)]
    [InlineData('\0', 'a', ',', true, true)]
    [InlineData('\0', 'a', ',', false, false)]
    [InlineData('/', '/', 'e', false, true)]
    [InlineData(':', '/', '/', false, false)]
    [InlineData('a', '/', '2', false, false)]
    [InlineData('\0', '中', '文', false, true)]
    [InlineData('\0', 'a', '文', false, true)]
    [InlineData('\0', '中', '。', false, false)]
    public void Break_opportunities_between_characters(char beforePrevious, char before, char after, bool anywhere, bool expected) =>
        Assert.Equal(expected, LineBreaker.CanBreakBetween(beforePrevious, before, after, anywhere));

    [Fact]
    public void No_break_spaces_do_not_break()
    {
        Assert.True(LineBreaker.IsBreakingSpace(' '));
        Assert.False(LineBreaker.IsBreakingSpace(' '));
        Assert.False(LineBreaker.IsBreakingSpace(' '));
    }
}
