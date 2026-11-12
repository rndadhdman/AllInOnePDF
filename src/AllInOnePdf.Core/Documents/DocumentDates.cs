using System.Globalization;

namespace AllInOnePdf.Core.Documents;

/// <summary>
/// Formats a document's date the way its readers expect: in the document's own language
/// (<see cref="DocumentMetadata.Language"/>, a BCP 47 tag such as <c>de</c> or <c>en-GB</c>) when it names a known
/// culture, otherwise in the machine's current culture. A German document therefore prints <c>20.06.2026</c> on an
/// American machine, and an untagged document follows the user's region.
/// </summary>
public static class DocumentDates
{
    /// <summary>Short date (<c>d</c> pattern) for header and footer tokens.</summary>
    public static string Short(DateTimeOffset date, string? language) => date.ToString("d", CultureFor(language));

    /// <summary>Long date (<c>D</c> pattern) for cover pages and title blocks.</summary>
    public static string Long(DateTimeOffset date, string? language) => date.ToString("D", CultureFor(language));

    /// <summary>The culture for a language tag, or the current culture when the tag is empty or unknown.</summary>
    public static CultureInfo CultureFor(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return CultureInfo.CurrentCulture;
        }

        try
        {
            // predefinedOnly: made-up tags such as "xx-YY" are rejected instead of synthesised into an empty culture.
            var culture = CultureInfo.GetCultureInfo(language.Trim(), predefinedOnly: true);
            return string.IsNullOrEmpty(culture.Name) ? CultureInfo.CurrentCulture : culture;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.CurrentCulture;
        }
    }
}
