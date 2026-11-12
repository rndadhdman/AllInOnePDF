using System.Text.RegularExpressions;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Exporters.Epub;

/// <summary>How the document is cut into spine items.</summary>
internal enum EpubChapterSplit { H1, H2, None, PageBreaks }

/// <summary>Resolved EPUB export options (see <see cref="EpubExporter.Options"/>).</summary>
internal sealed partial class EpubSettings
{
    public EpubChapterSplit Split { get; init; } = EpubChapterSplit.H1;
    public bool Cover { get; init; }
    public bool CoverImage { get; init; } = true;
    public bool IncludeNav { get; init; } = true;
    public bool EmbedFonts { get; init; }
    public bool RespectReaderTheme { get; init; } = true;
    public bool ApplyPageBackground { get; init; }
    public string Language { get; init; } = "en";
    public string? Identifier { get; init; }
    public bool AllowRawHtml { get; init; }

    public static EpubSettings From(OptionValues options, Document document, PdfTheme theme, List<string> warnings)
    {
        var split = options.GetString(EpubExporter.OptChapterSplit)?.Trim().ToLowerInvariant() switch
        {
            "h2" => EpubChapterSplit.H2,
            "none" => EpubChapterSplit.None,
            "page-breaks" or "pagebreaks" => EpubChapterSplit.PageBreaks,
            _ => EpubChapterSplit.H1,
        };

        var cover = options.GetString(EpubExporter.OptCover)?.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" => true,
            "no" or "false" => false,
            _ => theme.Cover.Enabled,
        };

        var language = options.GetString(EpubExporter.OptLanguage)?.Trim();
        if (string.IsNullOrEmpty(language))
        {
            language = document.Metadata.Language?.Trim();
        }

        if (string.IsNullOrEmpty(language))
        {
            language = "en";
        }
        else if (!LanguageTag().IsMatch(language))
        {
            warnings.Add($"\"{language}\" is not a valid language tag, so the book is marked as English (en).");
            language = "en";
        }

        var identifier = options.GetString(EpubExporter.OptIdentifier)?.Trim();

        return new EpubSettings
        {
            Split = split,
            Cover = cover,
            CoverImage = options.GetBool(EpubExporter.OptCoverImage, true),
            IncludeNav = options.GetBool(EpubExporter.OptIncludeNav, true),
            EmbedFonts = options.GetBool(EpubExporter.OptEmbedFonts, false),
            RespectReaderTheme = options.GetBool(EpubExporter.OptRespectReaderTheme, true),
            ApplyPageBackground = options.GetBool(EpubExporter.OptApplyPageBackground, false),
            Language = language,
            Identifier = string.IsNullOrEmpty(identifier) ? null : identifier,
            AllowRawHtml = options.GetBool(EpubExporter.OptAllowRawHtml, false),
        };
    }

    [GeneratedRegex("^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$")]
    private static partial Regex LanguageTag();
}
