using AllInOnePdf.Layout.Drawing;
using AllInOnePdf.Layout.Text;

namespace AllInOnePdf.Layout.Tests;

public class TextFontTests
{
    public TextFontTests() => TestFonts.EnsureRegistered();

    [Theory]
    [InlineData("Inter")]
    [InlineData("inter")]
    [InlineData("LITERATA")]
    [InlineData("JetBrains Mono")]
    public void Bundled_families_are_registered_and_found_case_insensitively(string family)
    {
        Assert.True(FontRegistry.IsRegistered(family));
        var selection = FontRegistry.FindFamily(family, 400, false);
        Assert.NotNull(selection);
        Assert.Equal(400, selection.Face.Weight);
        Assert.False(selection.Face.IsItalic);
        Assert.False(selection.SyntheticBold);
        Assert.False(selection.SyntheticItalic);
    }

    [Theory]
    [InlineData(700, false, 700)]
    [InlineData(600, false, 600)]
    [InlineData(500, true, 500)]
    [InlineData(900, false, 700)]
    [InlineData(300, false, 400)]
    [InlineData(100, true, 400)]
    public void The_closest_weight_and_matching_slant_are_chosen(int wanted, bool italic, int expected)
    {
        var selection = FontRegistry.FindFamily("Inter", wanted, italic)!;
        Assert.Equal(expected, selection.Face.Weight);
        Assert.Equal(italic, selection.Face.IsItalic);
        Assert.False(selection.SyntheticBold);
        Assert.False(selection.SyntheticItalic);
    }

    [Fact]
    public void Bold_and_italic_are_synthesised_only_when_the_family_lacks_the_face()
    {
        var regular = FontFace.FromData(TestFonts.Read(Path.Combine("Inter", "Inter-Regular.ttf")))!;
        var selection = FontRegistry.Select([regular], 700, true);
        Assert.Same(regular, selection.Face);
        Assert.True(selection.SyntheticBold);
        Assert.True(selection.SyntheticItalic);

        var font = selection.GetFont(12);
        Assert.True(font.Embolden);
        Assert.True(font.SkewX < 0);

        var medium = FontRegistry.Select([regular], 500, false);
        Assert.False(medium.SyntheticBold);
    }

    [Fact]
    public void Unknown_families_fall_through_to_the_next_name_then_to_the_first_registered_family()
    {
        var chain = FontRegistry.ResolveChain(["No Such Family 123", "Literata"], 400, false);
        Assert.Equal("Literata", chain[0].Face.FamilyName);

        var fallback = FontRegistry.ResolveChain(["No Such Family 123"], 400, false);
        Assert.Single(fallback);
        Assert.True(FontRegistry.IsRegistered(fallback[0].Face.FamilyName));
    }

    [Fact]
    public void Characters_missing_from_the_first_family_come_from_the_next()
    {
        // JetBrains Mono has basic Greek but not polytonic Greek (U+1F00); Inter has both.
        var mono = FontRegistry.FindFamily("JetBrains Mono", 400, false)!;
        var inter = FontRegistry.FindFamily("Inter", 400, false)!;
        Assert.True(mono.Face.HasGlyph('α'));
        Assert.False(mono.Face.HasGlyph(0x1F00));
        Assert.True(inter.Face.HasGlyph(0x1F00));

        var options = new ShapingOptions(FontRegistry.ResolveChain(["JetBrains Mono", "Inter"], 400, false), 12, 0, [], 400, false);
        var runs = TextShaper.Shape("aαἀb", options);
        Assert.Equal(3, runs.Count);
        Assert.Equal("JetBrains Mono", runs[0].Font.Face.FamilyName);
        Assert.Equal(0, runs[0].Start);
        Assert.Equal(2, runs[0].End);
        Assert.Equal("Inter", runs[1].Font.Face.FamilyName);
        Assert.Equal("JetBrains Mono", runs[2].Font.Face.FamilyName);
        Assert.All(runs, r => Assert.DoesNotContain(r.Glyphs, g => g.GlyphId == 0));
    }

    [Fact]
    public void Characters_no_family_has_use_the_system_fallback_or_notdef_and_never_throw()
    {
        var options = new ShapingOptions(FontRegistry.ResolveChain(["JetBrains Mono"], 400, false), 12, 0, [], 400, false);
        var runs = TextShaper.Shape("x中y", options);
        Assert.NotEmpty(runs);
        Assert.Equal(3, runs.Sum(r => r.Glyphs.Length));
        // Without a system face for the character (a Linux runner with no CJK font) the whole string stays in one
        // JetBrains Mono run, so look at the glyph of the character itself, not the run's first glyph.
        var middle = runs.First(r => r.Start <= 1 && r.End > 1);
        Assert.True(middle.Font.Face.FamilyName != "JetBrains Mono" || middle.Glyphs[1 - middle.Start].GlyphId == 0);
    }

    [Fact]
    public void Registering_garbage_throws_and_registering_twice_is_harmless()
    {
        Assert.Throws<ArgumentException>(() => FontManager.RegisterFont([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]));
        FontManager.RegisterFont(TestFonts.Read(Path.Combine("Inter", "Inter-Bold.ttf")));
        Assert.Equal(700, FontRegistry.FindFamily("Inter", 700, false)!.Face.Weight);
    }

    [Fact]
    public void Lookups_are_thread_safe()
    {
        Parallel.For(0, 64, i =>
        {
            var chain = FontRegistry.ResolveChain(["Inter", "Literata"], 100 * ((i % 9) + 1), i % 2 == 0);
            var options = new ShapingOptions(chain, 10 + (i % 5), 0, ["liga"], 400, false);
            var runs = TextShaper.Shape("Concurrent shaping ἀβγ " + i, options);
            Assert.NotEmpty(runs);
            Assert.NotNull(runs[0].Font.GetFont(10 + (i % 5)));
        });
    }
}
