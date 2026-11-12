using AllInOnePdf.Layout.Elements;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Options of a raster image added with <c>container.Image(bytes)</c>. Every method returns the descriptor.</summary>
public sealed class ImageDescriptor
{
    private readonly Image _image;

    internal ImageDescriptor(Image image)
    {
        _image = image;
    }

    /// <summary>The default: the available width, height from the aspect ratio (Wrap when too tall).</summary>
    public ImageDescriptor FitWidth() => Scale(ImageScaling.FitWidth);

    /// <summary>The available height, width from the aspect ratio (Wrap when too wide).</summary>
    public ImageDescriptor FitHeight() => Scale(ImageScaling.FitHeight);

    /// <summary>The largest size with the aspect ratio inside the available space, at its top-left.</summary>
    public ImageDescriptor FitArea() => Scale(ImageScaling.FitArea);

    /// <summary>Stretches the image over the available space.</summary>
    public ImageDescriptor FitUnproportionally() => Scale(ImageScaling.FitUnproportionally);

    /// <summary>Hands the decoded image to the PDF untouched (no per-image re-encoding even when a quality was set).</summary>
    public ImageDescriptor UseOriginalImage()
    {
        _image.UseOriginalImage = true;
        return this;
    }

    /// <summary>Re-encodes an opaque image as JPEG at this quality instead of the document's; transparent images stay lossless.</summary>
    public ImageDescriptor WithCompressionQuality(ImageCompressionQuality quality)
    {
        _image.CompressionQuality = quality;
        return this;
    }

    private ImageDescriptor Scale(ImageScaling scaling)
    {
        _image.Scaling = scaling;
        return this;
    }
}
