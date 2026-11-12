using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

namespace AllInOnePdf.Layout.Svg;

/// <summary>
/// Reads the SVG subset the engine draws: <c>svg</c> (viewBox, width, height, preserveAspectRatio), <c>g</c>,
/// <c>path</c>, <c>rect</c>, <c>circle</c>, <c>ellipse</c>, <c>line</c>, <c>polyline</c>, <c>polygon</c>, with
/// <c>fill</c>, <c>stroke</c>, <c>stroke-width</c>, <c>stroke-linecap</c>, <c>stroke-linejoin</c>,
/// <c>stroke-miterlimit</c>, <c>fill-rule</c>, <c>opacity</c>, <c>fill-opacity</c>, <c>stroke-opacity</c>,
/// <c>transform</c> and <c>display="none"</c>, as attributes or in a <c>style</c> attribute. Other elements are
/// skipped with their content (so <c>defs</c>, <c>title</c>, <c>style</c> and friends never draw); other
/// attributes are ignored. Element names are matched without regard to the XML namespace.
/// </summary>
public static partial class SvgParser
{
    /// <summary>Parses <paramref name="svg"/>; null when it is not well-formed XML or its root is not <c>svg</c>.</summary>
    public static SvgDocument? Parse(string? svg)
    {
        if (string.IsNullOrWhiteSpace(svg))
        {
            return null;
        }

        XElement root;
        try
        {
            var settings = new XmlReaderSettings
            {
                // Editors write DOCTYPEs; skip them, never fetch anything.
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersFromEntities = 1024,
            };
            using var text = new StringReader(svg);
            using var reader = XmlReader.Create(text, settings);
            root = XDocument.Load(reader).Root!;
        }
        catch (XmlException)
        {
            return null;
        }

        if (root is null || root.Name.LocalName != "svg")
        {
            return null;
        }

        var viewBox = ParseViewBox(Attr(root, "viewBox"));
        var width = ParseLength(Attr(root, "width"));
        var height = ParseLength(Attr(root, "height"));
        var aspect = ParseAspectRatio(Attr(root, "preserveAspectRatio"));
        var group = new SvgGroup(ParseStyle(root), SKMatrix.Identity, ParseChildren(root));
        return new SvgDocument(viewBox, width is > 0 ? width : null, height is > 0 ? height : null, aspect, group);
    }

    private static List<SvgNode> ParseChildren(XElement parent)
    {
        var nodes = new List<SvgNode>();
        foreach (var element in parent.Elements())
        {
            if (ParseNode(element) is { } node)
            {
                nodes.Add(node);
            }
        }

        return nodes;
    }

    private static SvgNode? ParseNode(XElement e)
    {
        var name = e.Name.LocalName;
        if (name is not ("g" or "path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"))
        {
            return null;
        }

        if (string.Equals(Property(e, "display"), "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var style = ParseStyle(e);
        var transform = ParseTransform(Attr(e, "transform"));
        float N(string attr) => ParseLength(Attr(e, attr)) ?? 0;

        switch (name)
        {
            case "g":
                return new SvgGroup(style, transform, ParseChildren(e));

            case "path":
                return new SvgShape(SvgShapeKind.Path, style, transform, [], Attr(e, "d"));

            case "rect":
                var rx = ParseLength(Attr(e, "rx"));
                var ry = ParseLength(Attr(e, "ry"));
                rx ??= ry;
                ry ??= rx;
                return new SvgShape(SvgShapeKind.Rect, style, transform, [N("x"), N("y"), N("width"), N("height"), Math.Max(0, rx ?? 0), Math.Max(0, ry ?? 0)], null);

            case "circle":
                return new SvgShape(SvgShapeKind.Circle, style, transform, [N("cx"), N("cy"), N("r")], null);

            case "ellipse":
                return new SvgShape(SvgShapeKind.Ellipse, style, transform, [N("cx"), N("cy"), N("rx"), N("ry")], null);

            case "line":
                return new SvgShape(SvgShapeKind.Line, style, transform, [N("x1"), N("y1"), N("x2"), N("y2")], null);

            default:
                var points = ParseNumbers(Attr(e, "points"));
                if (points.Count % 2 == 1)
                {
                    points.RemoveAt(points.Count - 1);
                }

                return new SvgShape(name == "polygon" ? SvgShapeKind.Polygon : SvgShapeKind.Polyline, style, transform, points, null);
        }
    }

    /// <summary>The element's presentation properties; declarations in <c>style</c> win over attributes.</summary>
    internal static SvgStyle ParseStyle(XElement e)
    {
        var style = SvgStyle.Empty;
        if (Property(e, "fill") is { } fill && SvgColors.TryParsePaint(fill, out var fp))
        {
            style = style with { Fill = fp };
        }

        if (Property(e, "stroke") is { } stroke && SvgColors.TryParsePaint(stroke, out var sp))
        {
            style = style with { Stroke = sp };
        }

        if (ParseLength(Property(e, "stroke-width")) is { } sw && sw >= 0)
        {
            style = style with { StrokeWidth = sw };
        }

        style = Property(e, "stroke-linecap")?.ToLowerInvariant() switch
        {
            "butt" => style with { StrokeLineCap = SKStrokeCap.Butt },
            "round" => style with { StrokeLineCap = SKStrokeCap.Round },
            "square" => style with { StrokeLineCap = SKStrokeCap.Square },
            _ => style,
        };

        style = Property(e, "stroke-linejoin")?.ToLowerInvariant() switch
        {
            "miter" or "miter-clip" or "arcs" => style with { StrokeLineJoin = SKStrokeJoin.Miter },
            "round" => style with { StrokeLineJoin = SKStrokeJoin.Round },
            "bevel" => style with { StrokeLineJoin = SKStrokeJoin.Bevel },
            _ => style,
        };

        if (ParseNumber(Property(e, "stroke-miterlimit")) is { } ml && ml >= 1)
        {
            style = style with { StrokeMiterLimit = ml };
        }

        style = Property(e, "fill-rule")?.ToLowerInvariant() switch
        {
            "evenodd" => style with { FillRule = SKPathFillType.EvenOdd },
            "nonzero" => style with { FillRule = SKPathFillType.Winding },
            _ => style,
        };

        if (ParseOpacity(Property(e, "fill-opacity")) is { } fo)
        {
            style = style with { FillOpacity = fo };
        }

        if (ParseOpacity(Property(e, "stroke-opacity")) is { } so)
        {
            style = style with { StrokeOpacity = so };
        }

        if (ParseOpacity(Property(e, "opacity")) is { } o)
        {
            style = style with { Opacity = o };
        }

        return style;
    }

    /// <summary>A presentation property: the <c>style</c> declaration if present, else the attribute.</summary>
    private static string? Property(XElement e, string name)
    {
        if (Attr(e, "style") is { } css)
        {
            foreach (var declaration in css.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = declaration.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && declaration[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return declaration[(colon + 1)..].Replace("!important", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                }
            }
        }

        return Attr(e, name);
    }

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    /// <summary>Parses a transform list (<c>matrix</c>, <c>translate</c>, <c>scale</c>, <c>rotate</c>, <c>skewX</c>, <c>skewY</c>); identity when absent or invalid.</summary>
    public static SKMatrix ParseTransform(string? value)
    {
        var matrix = SKMatrix.Identity;
        if (string.IsNullOrWhiteSpace(value))
        {
            return matrix;
        }

        foreach (Match m in TransformRegex().Matches(value))
        {
            var a = ParseNumbers(m.Groups[2].Value);
            SKMatrix? op = m.Groups[1].Value switch
            {
                "matrix" when a.Count == 6 => new SKMatrix(a[0], a[2], a[4], a[1], a[3], a[5], 0, 0, 1),
                "translate" when a.Count is 1 or 2 => SKMatrix.CreateTranslation(a[0], a.Count == 2 ? a[1] : 0),
                "scale" when a.Count is 1 or 2 => SKMatrix.CreateScale(a[0], a.Count == 2 ? a[1] : a[0]),
                "rotate" when a.Count == 1 => SKMatrix.CreateRotationDegrees(a[0]),
                "rotate" when a.Count == 3 => SKMatrix.CreateRotationDegrees(a[0], a[1], a[2]),
                "skewX" when a.Count == 1 => SKMatrix.CreateSkew(MathF.Tan(a[0] * MathF.PI / 180), 0),
                "skewY" when a.Count == 1 => SKMatrix.CreateSkew(0, MathF.Tan(a[0] * MathF.PI / 180)),
                _ => null,
            };

            if (op is null)
            {
                // An invalid list makes the whole attribute invalid (SVG's rule).
                return SKMatrix.Identity;
            }

            matrix = matrix.PreConcat(op.Value);
        }

        return matrix;
    }

    private static SKRect? ParseViewBox(string? value)
    {
        var n = ParseNumbers(value);
        return n.Count == 4 && n[2] > 0 && n[3] > 0 ? SKRect.Create(n[0], n[1], n[2], n[3]) : null;
    }

    private static SvgAspectRatio ParseAspectRatio(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SvgAspectRatio.Default;
        }

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var align = parts[0];
        if (align.Equals("none", StringComparison.Ordinal))
        {
            return new SvgAspectRatio(0.5f, 0.5f, false, true);
        }

        if (align.Length != 8 || !align.StartsWith('x') || align[4] != 'Y')
        {
            return SvgAspectRatio.Default;
        }

        static float Align(string s) => s switch { "Min" => 0, "Max" => 1, _ => 0.5f };
        var slice = parts.Length > 1 && parts[1].Equals("slice", StringComparison.Ordinal);
        return new SvgAspectRatio(Align(align[1..4]), Align(align[5..8]), slice, false);
    }

    /// <summary>A length in CSS pixels (<c>px</c>, unitless, <c>pt</c>, <c>pc</c>, <c>mm</c>, <c>cm</c>, <c>in</c>); null for percentages or junk.</summary>
    public static float? ParseLength(string? value)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        var m = NumberRegex().Match(s);
        if (!m.Success || m.Index != 0 || !float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            return null;
        }

        var factor = s[m.Length..].Trim().ToLowerInvariant() switch
        {
            "" or "px" => 1f,
            "pt" => 96f / 72f,
            "pc" => 16f,
            "mm" => 96f / 25.4f,
            "cm" => 96f / 2.54f,
            "in" => 96f,
            _ => float.NaN,
        };

        return float.IsNaN(factor) ? null : n * factor;
    }

    private static float? ParseNumber(string? value) =>
        float.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && float.IsFinite(n) ? n : null;

    /// <summary>0–1, or a percentage; clamped.</summary>
    private static float? ParseOpacity(string? value)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        var percent = s.EndsWith('%');
        return ParseNumber(percent ? s[..^1] : s) is { } n ? Math.Clamp(percent ? n / 100 : n, 0, 1) : null;
    }

    /// <summary>Every number in a comma/space separated list (SVG's compact forms such as <c>1-2</c> and <c>.5.5</c> included).</summary>
    public static List<float> ParseNumbers(string? value)
    {
        var list = new List<float>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return list;
        }

        foreach (Match m in NumberRegex().Matches(value))
        {
            if (float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && float.IsFinite(n))
            {
                list.Add(n);
            }
        }

        return list;
    }

    [GeneratedRegex(@"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex TransformRegex();
}
