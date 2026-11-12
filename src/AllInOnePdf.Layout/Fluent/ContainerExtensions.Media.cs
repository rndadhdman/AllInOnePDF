using AllInOnePdf.Layout.Elements;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Options of a line added with <c>LineHorizontal</c> / <c>LineVertical</c>.</summary>
public interface ILineDescriptor
{
    /// <summary>The line colour (default black).</summary>
    ILineDescriptor LineColor(Color color);

    /// <summary>Alternating on/off lengths; an odd list is repeated so on and off alternate.</summary>
    ILineDescriptor LineDashPattern(float[] dashPattern, Unit unit = Unit.Point);
}

/// <summary>Tables, images, SVG and lines (the media part of the fluent API).</summary>
public static partial class ContainerExtensions
{
    /// <summary>Puts a table in the container; see <see cref="TableDescriptor"/>.</summary>
    public static void Table(this IContainer container, Action<TableDescriptor> handler)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(handler);
        var table = new Table();
        handler(new TableDescriptor(table));
        container.SetChild(table);
    }

    /// <summary>
    /// Puts a raster image (PNG, JPEG, WebP, GIF, BMP, ICO: whatever Skia decodes) in the container, scaled to the
    /// available width by default. Throws <see cref="DocumentComposeException"/> right here when the data cannot be decoded.
    /// </summary>
    public static ImageDescriptor Image(this IContainer container, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(container);
        var image = Elements.Image.FromEncoded(data);
        container.SetChild(image);
        return new ImageDescriptor(image);
    }

    /// <summary>Puts an SVG picture in the container (the subset <see cref="Svg.SvgParser"/> reads); markup that does not parse draws nothing.</summary>
    public static SvgImageDescriptor Svg(this IContainer container, string svg)
    {
        ArgumentNullException.ThrowIfNull(container);
        var image = new SvgImage(svg ?? string.Empty);
        container.SetChild(image);
        return new SvgImageDescriptor(image);
    }

    /// <summary>A horizontal rule across the available width, <paramref name="thickness"/> tall.</summary>
    public static ILineDescriptor LineHorizontal(this IContainer container, float thickness, Unit unit = Unit.Point) =>
        AddLine(container, vertical: false, thickness.ToPoints(unit));

    /// <summary>A vertical rule down the available height, <paramref name="thickness"/> wide.</summary>
    public static ILineDescriptor LineVertical(this IContainer container, float thickness, Unit unit = Unit.Point) =>
        AddLine(container, vertical: true, thickness.ToPoints(unit));

    private static Line AddLine(IContainer container, bool vertical, float thickness)
    {
        ArgumentNullException.ThrowIfNull(container);
        var line = new Line(vertical, thickness);
        container.SetChild(line);
        return line;
    }
}
