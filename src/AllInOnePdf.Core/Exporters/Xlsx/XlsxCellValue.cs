using System.Globalization;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Importers.Xlsx;

namespace AllInOnePdf.Core.Exporters.Xlsx;

internal enum XlsxValueKind { Text, Number, Boolean }

/// <summary>
/// A cell's plain text typed for Excel: numbers (with thousands separators, a leading currency sign or a percent
/// suffix), TRUE/FALSE, ISO dates (yyyy-MM-dd); everything else stays text. A number is only typed when its number
/// format displays the same text again (thousands-separated values use the built-in #,##0 / #,##0.00 formats).
/// </summary>
internal readonly partial record struct XlsxCellValue(XlsxValueKind Kind, string Text, double Number = 0, uint NumberFormatId = 0, string? FormatCode = null)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly DateTime Epoch = new(1899, 12, 30);

    private static readonly DateTime FirstSafeDate = new(1900, 3, 1);

    public static XlsxCellValue Parse(string text)
    {
        var s = text.Trim();
        if (s.Length == 0 || s.Length > 40)
        {
            return new XlsxCellValue(XlsxValueKind.Text, text);
        }

        if (s is "TRUE" or "FALSE")
        {
            return new XlsxCellValue(XlsxValueKind.Boolean, s, s == "TRUE" ? 1 : 0);
        }

        if (IsoDate().IsMatch(s)
            && DateTime.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var date)
            && date >= FirstSafeDate)
        {
            return new XlsxCellValue(XlsxValueKind.Number, s, (date - Epoch).TotalDays, 14);
        }

        var m = NumberPattern().Match(s);
        if (!m.Success)
        {
            return new XlsxCellValue(XlsxValueKind.Text, text);
        }

        var integer = m.Groups["int"].Value;
        var decimals = m.Groups["dec"].Value;
        var commas = integer.Contains(',', StringComparison.Ordinal);
        var digits = integer.Replace(",", string.Empty, StringComparison.Ordinal);
        var currency = m.Groups["cur"].Value;
        var percent = m.Groups["pct"].Success;

        // Leading zeros (codes, ZIPs) and more than 15 significant digits (IDs) would not survive as numbers.
        if ((digits.Length > 1 && digits[0] == '0') || (digits.TrimStart('0') + decimals).Length > 15 || (percent && currency.Length > 0))
        {
            return new XlsxCellValue(XlsxValueKind.Text, text);
        }

        var number = double.Parse(digits + (decimals.Length > 0 ? "." + decimals : string.Empty), NumberStyles.Float, Inv);
        if (m.Groups["sign"].Success)
        {
            number = -number;
        }

        var n = decimals.Length;
        var zeros = n > 0 ? "." + new string('0', n) : string.Empty;
        XlsxCellValue value;
        if (percent)
        {
            number /= 100;
            value = n switch
            {
                0 => new(XlsxValueKind.Number, s, number, 9),
                2 => new(XlsxValueKind.Number, s, number, 10),
                _ => new(XlsxValueKind.Number, s, number, FormatCode: "0" + zeros + "%"),
            };
        }
        else if (currency.Length > 0)
        {
            value = new(XlsxValueKind.Number, s, number, FormatCode: "\"" + currency + "\"" + (commas ? "#,##0" : "0") + zeros);
        }
        else if (commas)
        {
            // Thousands-separated values use the built-in formats (1,234.5 is shown as 1,234.50).
            return n switch
            {
                0 => new(XlsxValueKind.Number, s, number, 3),
                1 or 2 => new(XlsxValueKind.Number, s, number, 4),
                _ => new(XlsxValueKind.Number, s, number, FormatCode: "#,##0" + zeros),
            };
        }
        else if (n == 0)
        {
            value = new(XlsxValueKind.Number, s, number, digits.Length > 11 ? 1u : 0u);
        }
        else if (n == 2)
        {
            value = new(XlsxValueKind.Number, s, number, 2);
        }
        else
        {
            value = XlsxNumberFormat.General(number) == s
                ? new(XlsxValueKind.Number, s, number)
                : new(XlsxValueKind.Number, s, number, FormatCode: "0" + zeros);
        }

        var shown = XlsxNumberFormat.Format(value.Number, (int)value.NumberFormatId, value.FormatCode);
        return shown == s ? value : new XlsxCellValue(XlsxValueKind.Text, text);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^(?<sign>-)?(?<cur>[$€£¥])?(?<int>\d{1,3}(?:,\d{3})+|\d+)(?:\.(?<dec>\d+))?(?<pct>%)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
