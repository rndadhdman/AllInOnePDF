using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Exporters.Docx;

/// <summary>
/// Style ids and structured-document-tag names the Word exporter writes. The importer recognises them so a
/// Document → DOCX → Document round trip keeps callouts, code languages, quote grouping and the cover page.
/// </summary>
internal static class DocxStyleIds
{
    public const string Normal = "Normal";
    public const string Title = "Title";
    public const string Subtitle = "Subtitle";
    public const string CoverMeta = "CoverMeta";
    public const string Quote = "Quote";
    public const string QuoteAttribution = "QuoteAttribution";
    public const string Code = "Code";
    public const string CodeLabel = "CodeLabel";
    public const string CodeChar = "CodeChar";
    public const string MathBlock = "MathBlock";
    public const string MathChar = "MathChar";
    public const string HtmlSource = "HtmlSource";
    public const string CalloutLabel = "CalloutLabel";
    public const string Hyperlink = "Hyperlink";
    public const string FootnoteText = "FootnoteText";
    public const string FootnoteReference = "FootnoteReference";
    public const string Caption = "Caption";
    public const string Figure = "Figure";
    public const string ListParagraph = "ListParagraph";
    public const string ListContinue = "ListContinue";
    public const string HorizontalRule = "HorizontalRule";
    public const string TocHeading = "TOCHeading";
    public const string Header = "Header";
    public const string Footer = "Footer";
    public const string Table = "AllInOneTable";

    public static string Heading(int level) => "Heading" + Math.Clamp(level, 1, 6).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Toc(int level) => "TOC" + Math.Clamp(level, 1, 9).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Callout(CalloutKind kind) => "Callout" + kind;

    /// <summary>Prefix of the <c>w:tag</c> values on the structured document tags the exporter writes.</summary>
    public const string TagPrefix = "aiop:";
    public const string CoverTag = TagPrefix + "cover";
    public const string QuoteTag = TagPrefix + "quote";
    public const string CalloutTagPrefix = TagPrefix + "callout:";
    public const string CodeTagPrefix = TagPrefix + "code:";
}
