using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters.Docx;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Exporters.Xlsx;

/// <summary>Where a cell sits in a table, which decides its theme style.</summary>
internal readonly record struct XlsxCellRole(
    bool Header,
    int DataRowIndex,
    ColumnAlignment Alignment,
    bool Bold = false,
    bool Link = false,
    bool Wrap = false,
    uint NumberFormatId = 0);

/// <summary>
/// Turns a <see cref="PdfTheme"/>'s table settings into cell formats: header fill, text colour and weight, borders by
/// table style (grid, lines, minimal, striped) and stripes. Text colours are picked for contrast with the fill, so dark
/// themes stay readable in Excel's white grid (data rows of a dark theme get the page background as their fill).
/// </summary>
internal sealed class XlsxThemeStyles
{
    private readonly PdfTheme _theme;
    private readonly string _family;
    private readonly double _size;
    private readonly string _border;
    private readonly string _headerFill;
    private readonly string _headerText;
    private readonly bool _headerBold;
    private readonly string? _dataFill;
    private readonly string? _stripeFill;
    private readonly string? _dataText;
    private readonly string? _stripeText;
    private readonly string _linkColor;

    public XlsxThemeStyles(PdfTheme theme)
    {
        _theme = theme;
        var tables = theme.Tables;
        var colors = theme.Colors;
        _family = string.IsNullOrWhiteSpace(theme.Typography.BodyFont) ? "Calibri" : theme.Typography.BodyFont;
        _size = XlsxStyleBook.RoundHalf(theme.Typography.BodySizePt * tables.FontScale);
        _border = DocxUnits.Hex(tables.BorderColor) ?? DocxUnits.Hex(colors.Border, "#D1D5DB");
        _headerFill = DocxUnits.Hex(tables.HeaderBackground) ?? DocxUnits.Hex(colors.Surface, "#F3F4F6");
        _headerText = Contrast(DocxUnits.Hex(tables.HeaderTextColor) ?? DocxUnits.Hex(colors.Text, "#1F2937"), _headerFill);
        _headerBold = tables.HeaderWeight is FontWeight.SemiBold or FontWeight.Bold;

        var page = DocxUnits.Hex(theme.Page.Background, "#FFFFFF");
        _dataFill = DocxUnits.IsDark(page) ? page : null;
        _dataText = TextOn(_dataFill);
        if (tables.Style == TableStyle.Striped)
        {
            _stripeFill = DocxUnits.Hex(tables.StripeColor) ?? DocxUnits.Hex(colors.Surface, "#F3F4F6");
            _stripeText = TextOn(_stripeFill);
        }

        var link = DocxUnits.Hex(theme.Links.Color) ?? DocxUnits.Hex(colors.Accent, "#0F6CBD");
        _linkColor = Contrast(link, _dataFill ?? "FFFFFF", null) ?? (_dataFill is null ? "0563C1" : "9CC4FF");

        Book = new XlsxStyleBook(new XlsxFontKey(_family, _size, Color: _dataText));
    }

    public XlsxStyleBook Book { get; }

    public double FontSizePt => _size;

    public uint Cell(XlsxCellRole role)
    {
        var style = _theme.Tables.Style;
        XlsxBorderKey? border = style switch
        {
            TableStyle.Grid => XlsxBorderKey.All(_border),
            TableStyle.Lines or TableStyle.Striped => XlsxBorderKey.BottomOnly(_border),
            TableStyle.Minimal when role.Header => XlsxBorderKey.BottomOnly(_border),
            _ => null,
        };

        string? fill;
        string? text;
        bool bold;
        if (role.Header)
        {
            fill = _headerFill;
            text = _headerText;
            bold = _headerBold || role.Bold;
        }
        else
        {
            var stripe = _stripeFill is not null && role.DataRowIndex % 2 == 1;
            fill = stripe ? _stripeFill : _dataFill;
            text = stripe ? _stripeText : _dataText;
            bold = role.Bold;
        }

        var font = Book.Font(new XlsxFontKey(_family, _size, bold, role.Link && !role.Header ? _linkColor : text, role.Link));
        return Book.Style(new XlsxStyleKey(
            role.NumberFormatId,
            font,
            Book.Fill(fill),
            Book.Border(border),
            Horizontal(role.Alignment),
            role.Wrap,
            Top: role.Wrap));
    }

    /// <summary>Bold, larger title row above a table (the heading that precedes it).</summary>
    public uint Title()
    {
        var color = Contrast(DocxUnits.Hex(_theme.Headings.Color, "#111827"), _dataFill ?? "FFFFFF", null);
        var font = Book.Font(new XlsxFontKey(_family, XlsxStyleBook.RoundHalf(_size * 1.3), true, color));
        return Book.Style(new XlsxStyleKey(FontId: font, FillId: Book.Fill(_dataFill)));
    }

    /// <summary>Styles for the single-column sheet written for documents without tables.</summary>
    public uint Prose(XlsxProseKind kind)
    {
        var body = XlsxStyleBook.RoundHalf(_theme.Typography.BodySizePt);
        var headingFamily = string.IsNullOrWhiteSpace(_theme.Headings.Font) ? _family : _theme.Headings.Font;
        var headingColor = Contrast(DocxUnits.Hex(_theme.Headings.Color, "#111827"), _dataFill ?? "FFFFFF", null);
        var font = kind switch
        {
            XlsxProseKind.Heading1 => new XlsxFontKey(headingFamily, XlsxStyleBook.RoundHalf(body * 1.6), true, headingColor),
            XlsxProseKind.Heading2 => new XlsxFontKey(headingFamily, XlsxStyleBook.RoundHalf(body * 1.3), true, headingColor),
            XlsxProseKind.Heading => new XlsxFontKey(headingFamily, body, true, headingColor),
            XlsxProseKind.Code => new XlsxFontKey(string.IsNullOrWhiteSpace(_theme.Code.Font) ? "Consolas" : _theme.Code.Font, XlsxStyleBook.RoundHalf(_theme.Code.SizePt), false, _dataText),
            _ => new XlsxFontKey(_family, body, false, _dataText),
        };

        return Book.Style(new XlsxStyleKey(FontId: Book.Font(font), FillId: Book.Fill(_dataFill), Wrap: kind != XlsxProseKind.Code, Top: true));
    }

    private static XlsxHorizontal Horizontal(ColumnAlignment alignment) => alignment switch
    {
        ColumnAlignment.Left => XlsxHorizontal.Left,
        ColumnAlignment.Center => XlsxHorizontal.Center,
        ColumnAlignment.Right => XlsxHorizontal.Right,
        _ => XlsxHorizontal.None,
    };

    /// <summary>Text colour for a fill: the theme's text colour when it contrasts, else black or white; null on no fill with dark theme text.</summary>
    private string? TextOn(string? fill)
    {
        var text = DocxUnits.Hex(_theme.Colors.Text, "#1F2937");
        if (fill is null)
        {
            return DocxUnits.IsDark(text) ? text : null;
        }

        return Contrast(text, fill);
    }

    private static string Contrast(string color, string fill) => Contrast(color, fill, null) ?? (DocxUnits.IsDark(fill) ? "FFFFFF" : "000000");

    private static string? Contrast(string color, string fill, string? fallback) =>
        DocxUnits.IsDark(color) != DocxUnits.IsDark(fill) ? color : fallback;
}

internal enum XlsxProseKind { Body, Heading1, Heading2, Heading, Code }
