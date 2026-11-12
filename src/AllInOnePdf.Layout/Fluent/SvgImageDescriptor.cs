using AllInOnePdf.Layout.Elements;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Options of a vector image added with <c>container.Svg(markup)</c>. Every method returns the descriptor.</summary>
public sealed class SvgImageDescriptor
{
    private readonly SvgImage _image;

    internal SvgImageDescriptor(SvgImage image)
    {
        _image = image;
    }

    /// <summary>The default: the available width, height from the SVG's aspect ratio (Wrap when too tall).</summary>
    public SvgImageDescriptor FitWidth() => Scale(ImageScaling.FitWidth);

    /// <summary>The available height, width from the aspect ratio (Wrap when too wide).</summary>
    public SvgImageDescriptor FitHeight() => Scale(ImageScaling.FitHeight);

    /// <summary>The largest size with the aspect ratio inside the available space, at its top-left.</summary>
    public SvgImageDescriptor FitArea() => Scale(ImageScaling.FitArea);

    private SvgImageDescriptor Scale(ImageScaling scaling)
    {
        _image.Scaling = scaling;
        return this;
    }
}
