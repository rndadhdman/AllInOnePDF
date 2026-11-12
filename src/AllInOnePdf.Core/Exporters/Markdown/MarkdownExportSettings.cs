using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.Core.Exporters.Markdown;

public enum PageBreakStyle { Omit, HtmlDiv, HorizontalRule }

public enum CalloutStyle { GitHubAlerts, BoldTitle, PlainQuote }

public enum MathStyle { Dollar, CodeBlock }

public enum ImageMode { Extract, DataUrl, Omit }

public enum HardBreakStyle { TwoSpaces, Backslash }

/// <summary>Parsed, validated option values for <see cref="MarkdownWriter"/>. Unknown option strings fall back to the defaults.</summary>
public sealed class MarkdownExportSettings
{
    public bool FrontMatter { get; init; } = true;
    public bool TitleHeading { get; init; }
    public PageBreakStyle PageBreaks { get; init; } = PageBreakStyle.HtmlDiv;
    public bool Footnotes { get; init; } = true;
    public CalloutStyle Callouts { get; init; } = CalloutStyle.GitHubAlerts;
    public MathStyle Math { get; init; } = MathStyle.Dollar;

    public bool SetextHeadings { get; init; }
    public char EmphasisMarker { get; init; } = '*';
    public char BulletMarker { get; init; } = '-';
    public bool OrderedAllOnes { get; init; }
    public HardBreakStyle HardBreak { get; init; } = HardBreakStyle.TwoSpaces;
    public bool AlignTables { get; init; } = true;
    public int WrapWidth { get; init; }
    public char CodeFence { get; init; } = '`';

    public ImageMode Images { get; init; } = ImageMode.Extract;
    public string ImageFolderName { get; init; } = "{name}_files";

    public bool ReferenceLinks { get; init; }
    public bool CrLf { get; init; }

    public static MarkdownExportSettings From(OptionValues options) => new()
    {
        FrontMatter = options.GetBool(MarkdownExporter.OptFrontMatter, true),
        TitleHeading = options.GetBool(MarkdownExporter.OptTitleHeading),
        PageBreaks = options.GetString(MarkdownExporter.OptPageBreaks) switch
        {
            "omit" => PageBreakStyle.Omit,
            "horizontal-rule" => PageBreakStyle.HorizontalRule,
            _ => PageBreakStyle.HtmlDiv,
        },
        Footnotes = options.GetBool(MarkdownExporter.OptFootnotes, true),
        Callouts = options.GetString(MarkdownExporter.OptCallouts) switch
        {
            "blockquote-with-bold-title" => CalloutStyle.BoldTitle,
            "plain-blockquote" => CalloutStyle.PlainQuote,
            _ => CalloutStyle.GitHubAlerts,
        },
        Math = options.GetString(MarkdownExporter.OptMath) == "codeblock" ? MathStyle.CodeBlock : MathStyle.Dollar,
        SetextHeadings = options.GetString(MarkdownExporter.OptHeadingStyle) == "setext",
        EmphasisMarker = options.GetString(MarkdownExporter.OptEmphasisMarker) == "_" ? '_' : '*',
        BulletMarker = options.GetString(MarkdownExporter.OptBulletMarker) switch
        {
            "*" => '*',
            "+" => '+',
            _ => '-',
        },
        OrderedAllOnes = options.GetString(MarkdownExporter.OptOrderedNumbering) == "all-ones",
        HardBreak = options.GetString(MarkdownExporter.OptHardBreak) == "backslash" ? HardBreakStyle.Backslash : HardBreakStyle.TwoSpaces,
        AlignTables = options.GetBool(MarkdownExporter.OptTableAlignment, true),
        WrapWidth = System.Math.Clamp(options.GetInt(MarkdownExporter.OptWrapWidth), 0, 200),
        CodeFence = options.GetString(MarkdownExporter.OptCodeFence) == "tildes" ? '~' : '`',
        Images = options.GetString(MarkdownExporter.OptImages) switch
        {
            "data-url" => ImageMode.DataUrl,
            "omit" => ImageMode.Omit,
            _ => ImageMode.Extract,
        },
        ImageFolderName = options.GetString(MarkdownExporter.OptImageFolder) is { Length: > 0 } folder && !string.IsNullOrWhiteSpace(folder)
            ? folder
            : "{name}_files",
        ReferenceLinks = options.GetString(MarkdownExporter.OptLinkStyle) == "reference-at-end",
        CrLf = options.GetString(MarkdownExporter.OptLineEndings) == "crlf",
    };
}
