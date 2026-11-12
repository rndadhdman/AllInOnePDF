using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Elements;

/// <summary>How a picture (raster or SVG) is sized inside the space its parent offers.</summary>
internal enum ImageScaling
{
    /// <summary>Takes the available width; the height follows the aspect ratio (Wrap when that is too tall).</summary>
    FitWidth,

    /// <summary>Takes the available height; the width follows the aspect ratio (Wrap when that is too wide).</summary>
    FitHeight,

    /// <summary>The largest size with the aspect ratio that fits both dimensions, at the top-left of the box.</summary>
    FitArea,

    /// <summary>Stretches to the whole available space, ignoring the aspect ratio.</summary>
    FitUnproportionally,
}

/// <summary>The sizing rules shared by <see cref="Image"/> and <see cref="SvgImage"/> (QuestPDF's AspectRatio element).</summary>
internal static class ImageFit
{
    private const float Epsilon = 0.001f;

    /// <summary>The size the picture takes in <paramref name="available"/>; <paramref name="ratio"/> is width / height.</summary>
    public static Size TargetSize(ImageScaling scaling, float ratio, Size available)
    {
        var width = Math.Max(0, available.Width);
        var height = Math.Max(0, available.Height);
        if (!(ratio > 0) || !float.IsFinite(ratio))
        {
            ratio = 1;
        }

        switch (scaling)
        {
            case ImageScaling.FitHeight:
                return new Size(height * ratio, height);

            case ImageScaling.FitArea:
                // Narrower than the space: the height decides; otherwise the width does.
                return height > 0 && ratio < width / height
                    ? new Size(height * ratio, height)
                    : new Size(width, width / ratio);

            case ImageScaling.FitUnproportionally:
                // An unconstrained dimension (Size.Infinity) has no meaningful "fill"; take nothing there.
                return new Size(width >= Size.Infinity ? 0 : width, height >= Size.Infinity ? 0 : height);

            default:
                return new Size(width, width / ratio);
        }
    }

    /// <summary>FullRender at the target size, or Wrap when the target does not fit.</summary>
    public static SpacePlan Measure(ImageScaling scaling, float ratio, Size available)
    {
        if (available.Width < -Epsilon || available.Height < -Epsilon)
        {
            return SpacePlan.Wrap();
        }

        var target = TargetSize(scaling, ratio, available);
        if (target.Width > available.Width + Epsilon || target.Height > available.Height + Epsilon)
        {
            return SpacePlan.Wrap();
        }

        return SpacePlan.FullRender(target);
    }
}
