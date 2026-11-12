using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Tests;

public class ContractTests
{
    [Fact]
    public void Colors_parse_and_print_hex()
    {
        Assert.Equal(new Color(0x1F, 0x29, 0x37), Color.FromHex("#1F2937"));
        Assert.Equal(new Color(0xAA, 0xBB, 0xCC), Color.FromHex("abc"));
        Assert.Equal(new Color(2, 3, 4, 1), Color.FromHex("#01020304"));
        Assert.Equal("#1F2937", Color.FromHex("#1f2937").ToString());
        Assert.False(Color.TryFromHex("bogus", out _));
        Assert.Throws<FormatException>(() => Color.FromHex("#12"));
    }

    [Fact]
    public void Text_styles_inherit_unset_properties_and_accumulate_disabled_features()
    {
        var parent = TextStyle.Default.FontSize(11).DisableFontFeature("calt");
        var child = TextStyle.Inherit.Bold().DisableFontFeature("liga").Apply(parent);

        Assert.Equal(11, child.Size);
        Assert.Equal(FontWeight.Bold, child.FontWeight);
        Assert.Equal(["Inter"], child.FontFamilies);
        Assert.Equal(["calt", "liga"], child.DisabledFontFeatures);
        Assert.Equal(FontWeight.Normal, parent.FontWeight);
        Assert.Equal(1.5f, TextStyle.Inherit.LineHeight(1.5f).LetterSpacing(0.02f).BackgroundColor(Color.White).Weight(FontWeight.Medium).LineHeightFactor);
    }

    [Fact]
    public void Units_convert_to_points()
    {
        Assert.Equal(72f, 25.4f.ToPoints(Unit.Millimetre), 3);
        Assert.Equal(72f, 1f.ToPoints(Unit.Inch), 3);
        Assert.Equal(10f, 10f.ToPoints(Unit.Point), 3);
    }

    [Fact]
    public void Page_context_tracks_passes_sections_and_captures()
    {
        var context = new PageContext();
        context.BeginFirstPass();
        context.BeginPage();
        context.BeginPage();
        context.SetSectionPage("intro", 2);
        context.SetSectionPage("intro", 3);
        context.RegisterCapture("h", new CapturedPosition(2, 10, 20));

        Assert.True(context.IsFirstPass);
        Assert.Equal(2, context.CurrentPage);
        Assert.Equal(2, context.GetSectionPage("intro"));
        Assert.Null(context.GetSectionPage("missing"));
        Assert.Single(context.GetCaptures("h"));

        context.BeginSecondPass(2);
        Assert.False(context.IsFirstPass);
        Assert.Equal(2, context.PageCount);
        Assert.Equal(0, context.CurrentPage);
        Assert.Equal(2, context.GetSectionPage("intro"));
    }

    [Fact]
    public void Container_chaining_hangs_children()
    {
        var root = new Probe();
        var inner = root.Element(new Probe());
        Assert.Same(inner, root.Child);
        Assert.Equal(SpacePlanType.Empty, root.Measure(new Size(100, 100)).Type);
    }

    private sealed class Probe : ContainerElement;
}
