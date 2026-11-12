using System.Globalization;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.UI.Services.Shared;

namespace AllInOnePdf.UI.Services;

/// <summary>Validation and visibility rules shared by the generic options renderer and the Convert workspace.</summary>
public static partial class ConvertOptionRules
{
    /// <summary>True when the option should be shown given the current values (honours <see cref="OptionDefinition.VisibleWhen"/>).</summary>
    public static bool IsVisible(OptionDefinition definition, OptionValues values, IReadOnlyList<OptionDefinition> all)
    {
        if (string.IsNullOrEmpty(definition.VisibleWhen))
        {
            return true;
        }

        var controller = all.FirstOrDefault(d => string.Equals(d.Key, definition.VisibleWhen, StringComparison.OrdinalIgnoreCase));
        var fallback = controller?.DefaultValue is bool b && b;
        return values.GetBool(definition.VisibleWhen, fallback);
    }

    /// <summary>Returns a user-facing error for an invalid value, or null when the value is fine.</summary>
    public static string? Validate(OptionDefinition definition, OptionValues values)
    {
        switch (definition.Type)
        {
            case OptionType.PageRange:
                var range = values.GetString(definition.Key);
                return string.IsNullOrWhiteSpace(range) || PageRangePattern().IsMatch(range)
                    ? null
                    : "Use page numbers and ranges, for example 1-3, 7, 10-";
            case OptionType.Color:
                var color = values.GetString(definition.Key);
                return string.IsNullOrWhiteSpace(color) || HexColor.IsValid(color)
                    ? null
                    : "Use a hex colour such as #1F6FEB";
            case OptionType.Integer:
            case OptionType.Number:
                var raw = values.Get(definition.Key);
                if (raw is null)
                {
                    return null;
                }

                var d = values.GetDouble(definition.Key, double.NaN);
                if (double.IsNaN(d))
                {
                    return "Enter a number";
                }

                if (definition.Min is { } min && d < min)
                {
                    return string.Create(CultureInfo.CurrentCulture, $"Must be at least {min}");
                }

                if (definition.Max is { } max && d > max)
                {
                    return string.Create(CultureInfo.CurrentCulture, $"Must be at most {max}");
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>All visible options with invalid values.</summary>
    public static IReadOnlyList<(OptionDefinition Definition, string Error)> Errors(IReadOnlyList<OptionDefinition> definitions, OptionValues values) =>
        definitions
            .Where(d => IsVisible(d, values, definitions))
            .Select(d => (Definition: d, Error: Validate(d, values)))
            .Where(x => x.Error is not null)
            .Select(x => (x.Definition, x.Error!))
            .ToList();

    /// <summary>
    /// Whether the options panel puts this option under "Show advanced". The PDF password is always shown: Help
    /// points users to it and an encrypted PDF cannot be read without it, whatever the importer marks.
    /// </summary>
    public static bool IsAdvanced(OptionDefinition definition) => definition.Advanced && !IsPassword(definition);

    /// <summary>A secret the user types (masked in the field).</summary>
    public static bool IsPassword(OptionDefinition definition) =>
        definition.Type == OptionType.Text && string.Equals(definition.Key, PdfImporter.OptPassword, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The help text shown under an option: the definition's own text, ending with a full stop. Corrects the PDF
    /// importer's "Keep source PDF" text, which names PDF → PDF conversions that the target list does not offer.
    /// </summary>
    public static string? HelpText(OptionDefinition definition)
    {
        var text = definition.Description?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (string.Equals(definition.Key, PdfImporter.OptKeepSourcePdf, StringComparison.OrdinalIgnoreCase)
            && text.Contains("PDF → PDF", StringComparison.Ordinal))
        {
            text = "Keep the original PDF inside the document, so converting it to images renders its pages unchanged.";
        }

        return text[^1] is '.' or '!' or '?' or ')' or ':' ? text : text + ".";
    }

    /// <summary>Strict <c>#RGB</c>/<c>#RRGGBB</c> check (see <see cref="HexColor.IsValid"/>).</summary>
    public static bool IsValidHexColor(string? value) => HexColor.IsValid(value);

    [GeneratedRegex(@"^\s*\d+\s*(-\s*\d*\s*)?(,\s*\d+\s*(-\s*\d*\s*)?)*$")]
    private static partial Regex PageRangePattern();
}
