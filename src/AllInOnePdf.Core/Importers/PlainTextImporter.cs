using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers;

/// <summary>Plain text and log files. Paragraphs are separated by blank lines; optional heading detection.</summary>
public sealed partial class PlainTextImporter : IImporter
{
    public const string OptParagraphs = "paragraphMode";
    public const string OptDetectHeadings = "detectHeadings";
    public const string OptMonospace = "monospace";

    public FileFormat Format => KnownFormats.PlainText;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Choice(OptParagraphs, "Paragraphs", "blankLine",
        [
            new OptionChoice("blankLine", "Separated by blank lines", "Consecutive lines are joined into one paragraph."),
            new OptionChoice("everyLine", "One per line", "Every line becomes its own paragraph."),
        ], "How to split the text into paragraphs.", "Structure"),
        OptionDefinition.Bool(OptDetectHeadings, "Detect headings", true, "Short ALL-CAPS lines or lines underlined with === / --- become headings.", "Structure"),
        OptionDefinition.Bool(OptMonospace, "Keep as monospace", false, "Preserve exact spacing by rendering the whole file as a code block (good for logs and tables).", "Structure"),
    ];

    public async Task<Document> ImportAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(context.Input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        var document = new Document();
        document.Metadata.SourceFormat = Format.Id;

        if (context.Options.GetBool(OptMonospace))
        {
            document.Blocks.Add(new CodeBlock { Code = text });
            return document;
        }

        var everyLine = context.Options.GetString(OptParagraphs) == "everyLine";
        var detectHeadings = context.Options.GetBool(OptDetectHeadings, true);
        var lines = text.Split('\n');
        var paragraph = new StringBuilder();

        void Flush()
        {
            if (paragraph.Length == 0)
            {
                return;
            }

            document.Blocks.Add(new ParagraphBlock(paragraph.ToString().Trim()));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }

            if (detectHeadings)
            {
                // Setext-style underline on the next line.
                if (i + 1 < lines.Length && Underline().IsMatch(lines[i + 1]) && trimmed.Length <= 100)
                {
                    Flush();
                    document.Blocks.Add(new HeadingBlock(lines[i + 1].TrimStart().StartsWith('=') ? 1 : 2, trimmed));
                    i++;
                    continue;
                }

                if (trimmed.Length is >= 3 and <= 80 && AllCaps().IsMatch(trimmed) && paragraph.Length == 0 && (i + 1 >= lines.Length || lines[i + 1].Trim().Length == 0 || everyLine))
                {
                    document.Blocks.Add(new HeadingBlock(2, trimmed));
                    continue;
                }
            }

            if (everyLine)
            {
                document.Blocks.Add(new ParagraphBlock(trimmed));
            }
            else
            {
                if (paragraph.Length > 0)
                {
                    paragraph.Append(' ');
                }

                paragraph.Append(trimmed);
            }
        }

        Flush();
        return document;
    }

    [GeneratedRegex(@"^\s*(={3,}|-{3,})\s*$")]
    private static partial Regex Underline();

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9 \-_:.,!?'""()]*$")]
    private static partial Regex AllCaps();
}
