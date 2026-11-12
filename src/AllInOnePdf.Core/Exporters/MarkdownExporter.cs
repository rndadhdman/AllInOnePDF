using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Exporters.Markdown;

namespace AllInOnePdf.Core.Exporters;

/// <summary>
/// Document model → GitHub-flavoured Markdown (GFM tables, task lists, alerts, footnotes, math, front matter).
/// Embedded images are written to a side folder as extra output files, inlined as data URLs, or reduced to alt text.
/// </summary>
public sealed class MarkdownExporter : IExporter
{
    public const string OptFrontMatter = "frontMatter";
    public const string OptTitleHeading = "titleHeading";
    public const string OptPageBreaks = "pageBreaks";
    public const string OptFootnotes = "footnotes";
    public const string OptCallouts = "callouts";
    public const string OptMath = "math";
    public const string OptHeadingStyle = "headingStyle";
    public const string OptEmphasisMarker = "emphasisMarker";
    public const string OptBulletMarker = "bulletMarker";
    public const string OptOrderedNumbering = "orderedNumbering";
    public const string OptHardBreak = "hardBreak";
    public const string OptTableAlignment = "tableAlignment";
    public const string OptWrapWidth = "wrapWidth";
    public const string OptCodeFence = "codeFence";
    public const string OptImages = "images";
    public const string OptImageFolder = "imageFolderName";
    public const string OptLinkStyle = "linkStyle";
    public const string OptLineEndings = "lineEndings";

    private const string Content = "Content";
    private const string Style = "Style";
    private const string Images = "Images";
    private const string Links = "Links";
    private const string Output = "Output";

    public FileFormat Format => KnownFormats.Markdown;

    public ConverterStatus Status => ConverterStatus.Ready;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Bool(OptFrontMatter, "YAML front matter", true, "Start the file with a --- block holding the title, subtitle, authors, date, keywords and description.", Content),
        OptionDefinition.Bool(OptTitleHeading, "Title as heading", false, "Also write the document title as a level-1 heading when the document has none.", Content),
        OptionDefinition.Choice(OptPageBreaks, "Page breaks", "html-div",
        [
            new OptionChoice("omit", "Omit", "Drop page breaks."),
            new OptionChoice("html-div", "HTML page-break div", "A <div style=\"page-break-after: always\"> that converts back to a page break."),
            new OptionChoice("horizontal-rule", "Horizontal rule", "A --- line (reads back as a thematic break)."),
        ], "How explicit page breaks from the source are written.", Content),
        OptionDefinition.Bool(OptFootnotes, "Footnotes", true, "Write footnote references as [^1] with the notes at the end. Off writes superscript numbers and plain notes.", Content),
        OptionDefinition.Choice(OptCallouts, "Callouts", "github-alerts",
        [
            new OptionChoice("github-alerts", "GitHub alerts", "> [!NOTE] blocks, rendered as coloured callouts on GitHub."),
            new OptionChoice("blockquote-with-bold-title", "Quote with bold title", "A block quote starting with **Note** (works everywhere)."),
            new OptionChoice("plain-blockquote", "Plain quote", "A plain block quote."),
        ], "How notes, tips and warnings are written.", Content),
        OptionDefinition.Choice(OptMath, "Math", "dollar",
        [
            new OptionChoice("dollar", "Dollar signs", "$inline$ and $$ display $$ (GitHub, Obsidian, Pandoc)."),
            new OptionChoice("codeblock", "Code", "Inline code and ```math blocks."),
        ], "How LaTeX math is written.", Content),

        OptionDefinition.Choice(OptHeadingStyle, "Headings", "atx",
        [
            new OptionChoice("atx", "# Hash marks", "# Heading for every level."),
            new OptionChoice("setext", "Underlined", "Underline levels 1 and 2 with === and ---; hash marks for the rest."),
        ], "Heading syntax.", Style),
        OptionDefinition.Choice(OptEmphasisMarker, "Emphasis marker", "*",
        [
            new OptionChoice("*", "Asterisk", "*italic* and **bold**."),
            new OptionChoice("_", "Underscore", "_italic_ and __bold__ (asterisks are still used inside words)."),
        ], "Character used for italic and bold.", Style),
        OptionDefinition.Choice(OptBulletMarker, "Bullet marker", "-",
        [
            new OptionChoice("-", "Hyphen (-)"),
            new OptionChoice("*", "Asterisk (*)"),
            new OptionChoice("+", "Plus (+)"),
        ], "Character that starts bullet list items.", Style),
        OptionDefinition.Choice(OptOrderedNumbering, "Numbered lists", "incrementing",
        [
            new OptionChoice("incrementing", "1. 2. 3.", "Number items in sequence."),
            new OptionChoice("all-ones", "1. 1. 1.", "Repeat the first number so reordering items never needs renumbering."),
        ], "How numbered list items are numbered.", Style),
        OptionDefinition.Choice(OptHardBreak, "Line breaks", "two-spaces",
        [
            new OptionChoice("two-spaces", "Two trailing spaces", "Invisible, but the most widely supported."),
            new OptionChoice("backslash", "Backslash", "A visible \\ at the end of the line."),
        ], "How forced line breaks inside a paragraph are written.", Style),
        OptionDefinition.Bool(OptTableAlignment, "Align table columns", true, "Pad table cells so the pipes line up in a text editor.", Style),
        OptionDefinition.Int(OptWrapWidth, "Wrap at", 0, 0, 200, "Wrap paragraphs at this many characters (0 = one line per paragraph). Code, links and tables are never wrapped.", Style, unit: "chars"),
        OptionDefinition.Choice(OptCodeFence, "Code fences", "backticks",
        [
            new OptionChoice("backticks", "Backticks (```)"),
            new OptionChoice("tildes", "Tildes (~~~)"),
        ], "Fence characters around code blocks.", Style),

        OptionDefinition.Choice(OptImages, "Images", "extract-to-folder",
        [
            new OptionChoice("extract-to-folder", "Save to a folder", "Write embedded images next to the Markdown file and link to them."),
            new OptionChoice("data-url", "Embed in the file", "Inline images as base64 data URLs (one self-contained file, but large)."),
            new OptionChoice("omit", "Leave out", "Write only the alt text."),
        ], "What to do with images embedded in the document.", Images),
        OptionDefinition.Text(OptImageFolder, "Image folder", "{name}_files", "Folder for extracted images; {name} is replaced with the output file name.", Images, advanced: true),

        OptionDefinition.Choice(OptLinkStyle, "Links", "inline",
        [
            new OptionChoice("inline", "Inline", "[text](https://example.com)"),
            new OptionChoice("reference-at-end", "References at the end", "[text][1] with the URLs listed at the bottom."),
        ], "Where link targets are written.", Links),

        OptionDefinition.Choice(OptLineEndings, "Line endings", "lf",
        [
            new OptionChoice("lf", "LF (macOS, Linux, Git)"),
            new OptionChoice("crlf", "CRLF (Windows)"),
        ], "Line ending characters in the output file.", Output, advanced: true),
    ];

    public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken = default)
    {
        var settings = MarkdownExportSettings.From(context.Options.WithDefaults(Options));
        var result = new ExportResult();
        var images = new MarkdownImageResolver(context.Document, settings, context.BaseName, result);
        var writer = new MarkdownWriter(context.Document, settings, images);

        context.Report(0, "Writing Markdown");
        var markdown = writer.Write(f => context.Report(f * 0.95), cancellationToken);
        if (settings.CrLf)
        {
            markdown = markdown.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(markdown);
        result.Files.Insert(0, new OutputFile(context.BaseName + ".md", bytes, Format.MediaType));
        context.Report(1, "Markdown written");
        return Task.FromResult(result);
    }
}
