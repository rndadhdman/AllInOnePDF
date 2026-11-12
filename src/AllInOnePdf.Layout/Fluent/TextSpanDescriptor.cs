using AllInOnePdf.Layout.Infrastructure;
using AllInOnePdf.Layout.Text;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Styles one span (or page number) of a text block. Every method changes the span and returns this descriptor.</summary>
public sealed class TextSpanDescriptor
{
    private readonly TextBlockItem _item;

    internal TextSpanDescriptor(TextBlockItem item)
    {
        _item = item;
    }

    private TextSpanDescriptor Set(Func<TextStyle, TextStyle> change)
    {
        _item.Style = change(_item.Style);
        return this;
    }

    /// <summary>Font families in order of preference; characters missing from the first come from the next.</summary>
    public TextSpanDescriptor FontFamily(params string[] families) => Set(s => s.FontFamily(families));

    /// <summary>Font size in points.</summary>
    public TextSpanDescriptor FontSize(float size) => Set(s => s.FontSize(size));

    /// <summary>Line height as a multiple of the font size.</summary>
    public TextSpanDescriptor LineHeight(float factor) => Set(s => s.LineHeight(factor));

    /// <summary>Extra space after every character, in em.</summary>
    public TextSpanDescriptor LetterSpacing(float em) => Set(s => s.LetterSpacing(em));

    /// <summary>Text colour.</summary>
    public TextSpanDescriptor FontColor(Color color) => Set(s => s.FontColor(color));

    /// <summary>Colour painted behind the span.</summary>
    public TextSpanDescriptor BackgroundColor(Color color) => Set(s => s.BackgroundColor(color));

    /// <summary>Font weight; the closest face of the family is used (bold is synthesised when the family has none).</summary>
    public TextSpanDescriptor Weight(FontWeight weight) => Set(s => s.Weight(weight));

    /// <summary>Weight 700.</summary>
    public TextSpanDescriptor Bold() => Set(s => s.Bold());

    /// <summary>Weight 600.</summary>
    public TextSpanDescriptor SemiBold() => Set(s => s.SemiBold());

    /// <summary>Weight 500.</summary>
    public TextSpanDescriptor Medium() => Set(s => s.Medium());

    /// <summary>Weight 400.</summary>
    public TextSpanDescriptor NormalWeight() => Set(s => s.NormalWeight());

    /// <summary>Italic (synthesised by skewing when the family has no italic face).</summary>
    public TextSpanDescriptor Italic(bool value = true) => Set(s => s.Italic(value));

    /// <summary>Underlines the span.</summary>
    public TextSpanDescriptor Underline(bool value = true) => Set(s => s.Underline(value));

    /// <summary>Strikes the span through.</summary>
    public TextSpanDescriptor Strikethrough(bool value = true) => Set(s => s.Strikethrough(value));

    /// <summary>Smaller and raised above the baseline.</summary>
    public TextSpanDescriptor Superscript() => Set(s => s.Superscript());

    /// <summary>Smaller and lowered below the baseline.</summary>
    public TextSpanDescriptor Subscript() => Set(s => s.Subscript());

    /// <summary>Turns an OpenType feature off ("liga", "calt").</summary>
    public TextSpanDescriptor DisableFontFeature(string feature) => Set(s => s.DisableFontFeature(feature));

    /// <summary>Allows line breaks between any two characters (code).</summary>
    public TextSpanDescriptor BreakAnywhere() => Set(s => s.BreakAnywhere());
}
