using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AllInOnePdf.Core.Exporters.Xlsx;

internal readonly record struct XlsxFontKey(string Family, double SizePt, bool Bold = false, string? Color = null, bool Underline = false);

internal readonly record struct XlsxBorderKey(bool Left, bool Right, bool Top, bool Bottom, string Color)
{
    public static XlsxBorderKey All(string color) => new(true, true, true, true, color);

    public static XlsxBorderKey BottomOnly(string color) => new(false, false, false, true, color);
}

internal enum XlsxHorizontal { None, Left, Center, Right }

internal readonly record struct XlsxStyleKey(
    uint NumberFormatId = 0,
    uint FontId = 0,
    uint FillId = 0,
    uint BorderId = 0,
    XlsxHorizontal Horizontal = XlsxHorizontal.None,
    bool Wrap = false,
    bool Top = false);

/// <summary>
/// Collects fonts, fills, borders, number formats and cell formats with de-duplication and builds the
/// <see cref="Stylesheet"/>. Index 0 of each list is the default; fills 0 and 1 are the mandatory none and gray125.
/// </summary>
internal sealed class XlsxStyleBook
{
    /// <summary>First id Excel leaves for custom number formats.</summary>
    private const uint FirstCustomFormatId = 164;

    private readonly List<XlsxFontKey> _fonts = [];
    private readonly List<string?> _fills = [null, null];
    private readonly List<XlsxBorderKey?> _borders = [null];
    private readonly List<XlsxStyleKey> _styles = [];
    private readonly Dictionary<string, uint> _formats = new(StringComparer.Ordinal);

    public XlsxStyleBook(XlsxFontKey defaultFont)
    {
        _fonts.Add(defaultFont);
        _styles.Add(default);
    }

    public uint Font(XlsxFontKey key) => IndexOf(_fonts, key);

    /// <summary>Solid fill in "RRGGBB"; null is no fill (index 0).</summary>
    public uint Fill(string? hex)
    {
        if (hex is null)
        {
            return 0;
        }

        var i = _fills.FindIndex(2, f => string.Equals(f, hex, StringComparison.Ordinal));
        if (i >= 0)
        {
            return (uint)i;
        }

        _fills.Add(hex);
        return (uint)(_fills.Count - 1);
    }

    public uint Border(XlsxBorderKey? key) => key is null ? 0 : IndexOf(_borders, key);

    /// <summary>Id of a custom number format code (registered on first use).</summary>
    public uint NumberFormat(string code)
    {
        if (!_formats.TryGetValue(code, out var id))
        {
            id = FirstCustomFormatId + (uint)_formats.Count;
            _formats[code] = id;
        }

        return id;
    }

    public uint Style(XlsxStyleKey key) => IndexOf(_styles, key);

    public Stylesheet Build()
    {
        var stylesheet = new Stylesheet();
        if (_formats.Count > 0)
        {
            var numFmts = new NumberingFormats { Count = (uint)_formats.Count };
            foreach (var (code, id) in _formats.OrderBy(f => f.Value))
            {
                numFmts.Append(new NumberingFormat { NumberFormatId = id, FormatCode = code });
            }

            stylesheet.Append(numFmts);
        }

        var fonts = new DocumentFormat.OpenXml.Spreadsheet.Fonts { Count = (uint)_fonts.Count };
        foreach (var f in _fonts)
        {
            var font = new Font();
            if (f.Bold)
            {
                font.Append(new Bold());
            }

            if (f.Underline)
            {
                font.Append(new Underline());
            }

            font.Append(new FontSize { Val = f.SizePt });
            if (f.Color is { } color)
            {
                font.Append(new Color { Rgb = "FF" + color });
            }

            font.Append(new FontName { Val = f.Family });
            font.Append(new FontFamilyNumbering { Val = 2 });
            fonts.Append(font);
        }

        stylesheet.Append(fonts);

        var fills = new Fills { Count = (uint)_fills.Count };
        fills.Append(new Fill(new PatternFill { PatternType = PatternValues.None }));
        fills.Append(new Fill(new PatternFill { PatternType = PatternValues.Gray125 }));
        foreach (var hex in _fills.Skip(2))
        {
            fills.Append(new Fill(new PatternFill(
                new ForegroundColor { Rgb = "FF" + hex },
                new BackgroundColor { Indexed = 64 })
            { PatternType = PatternValues.Solid }));
        }

        stylesheet.Append(fills);

        var borders = new Borders { Count = (uint)_borders.Count };
        foreach (var b in _borders)
        {
            borders.Append(BuildBorder(b));
        }

        stylesheet.Append(borders);

        stylesheet.Append(new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }) { Count = 1 });

        var xfs = new CellFormats { Count = (uint)_styles.Count };
        foreach (var s in _styles)
        {
            var xf = new CellFormat
            {
                NumberFormatId = s.NumberFormatId,
                FontId = s.FontId,
                FillId = s.FillId,
                BorderId = s.BorderId,
                FormatId = 0,
            };
            if (s.NumberFormatId != 0)
            {
                xf.ApplyNumberFormat = true;
            }

            if (s.FontId != 0)
            {
                xf.ApplyFont = true;
            }

            if (s.FillId != 0)
            {
                xf.ApplyFill = true;
            }

            if (s.BorderId != 0)
            {
                xf.ApplyBorder = true;
            }

            if (s.Horizontal != XlsxHorizontal.None || s.Wrap || s.Top)
            {
                xf.ApplyAlignment = true;
                var alignment = new Alignment();
                if (s.Horizontal != XlsxHorizontal.None)
                {
                    alignment.Horizontal = s.Horizontal switch
                    {
                        XlsxHorizontal.Center => HorizontalAlignmentValues.Center,
                        XlsxHorizontal.Right => HorizontalAlignmentValues.Right,
                        _ => HorizontalAlignmentValues.Left,
                    };
                }

                if (s.Top)
                {
                    alignment.Vertical = VerticalAlignmentValues.Top;
                }

                if (s.Wrap)
                {
                    alignment.WrapText = true;
                }

                xf.Append(alignment);
            }

            xfs.Append(xf);
        }

        stylesheet.Append(xfs);
        stylesheet.Append(new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 });
        return stylesheet;
    }

    private static Border BuildBorder(XlsxBorderKey? key)
    {
        BorderPropertiesType Side(BorderPropertiesType side, bool on)
        {
            if (on && key is { } k)
            {
                side.Style = BorderStyleValues.Thin;
                side.Append(new Color { Rgb = "FF" + k.Color });
            }

            return side;
        }

        var b = key ?? default;
        return new Border(
            Side(new LeftBorder(), b.Left),
            Side(new RightBorder(), b.Right),
            Side(new TopBorder(), b.Top),
            Side(new BottomBorder(), b.Bottom),
            new DiagonalBorder());
    }

    private static uint IndexOf<T>(List<T> list, T key)
    {
        var i = list.IndexOf(key);
        if (i >= 0)
        {
            return (uint)i;
        }

        list.Add(key);
        return (uint)(list.Count - 1);
    }

    public static double RoundHalf(double pt) => Math.Max(1, Math.Round(pt * 2, MidpointRounding.AwayFromZero) / 2);

    public static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
}
