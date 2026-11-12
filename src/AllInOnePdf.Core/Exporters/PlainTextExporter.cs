using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Exporters;

/// <summary>Document model → readable plain text.</summary>
public sealed class PlainTextExporter : IExporter
{
    public const string OptHeadings = "headingStyle";
    public const string OptWidth = "lineWidth";
    public const string OptLinks = "showLinks";

    public FileFormat Format => KnownFormats.PlainText;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Choice(OptHeadings, "Headings", "underline",
        [
            new OptionChoice("underline", "Underlined", "Heading text with a line of = or - beneath."),
            new OptionChoice("upper", "UPPERCASE", "Heading text in capitals."),
            new OptionChoice("plain", "Plain", "No decoration."),
        ], null, "Layout"),
        OptionDefinition.Int(OptWidth, "Wrap at", 0, 0, 200, "Wrap paragraphs at this many characters (0 = no wrapping).", "Layout", unit: "chars"),
        OptionDefinition.Bool(OptLinks, "Show link targets", true, "Append the URL after link text in angle brackets.", "Content"),
    ];

    public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        var headingStyle = context.Options.GetString(OptHeadings) ?? "underline";
        var width = context.Options.GetInt(OptWidth);
        var showLinks = context.Options.GetBool(OptLinks, true);

        var doc = context.Document;
        if (!string.IsNullOrWhiteSpace(doc.Metadata.Title) && doc.Headings.FirstOrDefault(h => h.Level == 1)?.ToPlainText() != doc.Metadata.Title)
        {
            sb.AppendLine(doc.Metadata.Title).AppendLine(new string('=', Math.Min(doc.Metadata.Title.Length, 80))).AppendLine();
        }

        foreach (var block in doc.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteBlock(sb, block, 0, headingStyle, width, showLinks);
        }

        if (doc.Footnotes.Count > 0)
        {
            sb.AppendLine("---").AppendLine();
            foreach (var fn in doc.Footnotes)
            {
                sb.Append('[').Append(fn.Label).Append("] ");
                foreach (var b in fn.Blocks)
                {
                    WriteBlock(sb, b, 0, headingStyle, width, showLinks);
                }
            }
        }

        var text = sb.ToString().TrimEnd() + Environment.NewLine;
        return Task.FromResult(ExportResult.Single(context.BaseName + ".txt", Encoding.UTF8.GetBytes(text), Format.MediaType));
    }

    private static void WriteBlock(StringBuilder sb, Block block, int indent, string headingStyle, int width, bool showLinks)
    {
        var pad = new string(' ', indent);
        switch (block)
        {
            case HeadingBlock h:
                {
                    var text = InlinesToText(h.Inlines, showLinks);
                    switch (headingStyle)
                    {
                        case "upper":
                            sb.Append(pad).AppendLine(text.ToUpperInvariant());
                            break;
                        case "plain":
                            sb.Append(pad).AppendLine(text);
                            break;
                        default:
                            sb.Append(pad).AppendLine(text);
                            sb.Append(pad).AppendLine(new string(h.Level == 1 ? '=' : '-', Math.Max(3, Math.Min(text.Length, 80))));
                            break;
                    }

                    sb.AppendLine();
                    break;
                }

            case ParagraphBlock p:
                sb.AppendLine(Wrap(InlinesToText(p.Inlines, showLinks), width, pad)).AppendLine();
                break;

            case ListBlock list:
                {
                    var n = list.Start;
                    foreach (var item in list.Items)
                    {
                        var marker = list.Ordered ? $"{n++}. " : item.Checked switch { true => "[x] ", false => "[ ] ", _ => "- " };
                        var first = true;
                        foreach (var b in item.Blocks)
                        {
                            if (first && b is ParagraphBlock ip)
                            {
                                sb.Append(pad).Append(marker).AppendLine(InlinesToText(ip.Inlines, showLinks));
                                first = false;
                            }
                            else
                            {
                                WriteBlock(sb, b, indent + marker.Length, headingStyle, width, showLinks);
                            }
                        }
                    }

                    sb.AppendLine();
                    break;
                }

            case CodeBlock code:
                foreach (var line in code.Code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                {
                    sb.Append(pad).Append("    ").AppendLine(line);
                }

                sb.AppendLine();
                break;

            case QuoteBlock quote:
                {
                    var inner = new StringBuilder();
                    foreach (var b in quote.Blocks)
                    {
                        WriteBlock(inner, b, 0, headingStyle, width, showLinks);
                    }

                    foreach (var line in inner.ToString().TrimEnd().Split('\n'))
                    {
                        sb.Append(pad).Append("> ").AppendLine(line.TrimEnd('\r'));
                    }

                    if (!string.IsNullOrWhiteSpace(quote.Attribution))
                    {
                        sb.Append(pad).Append("> -- ").AppendLine(quote.Attribution);
                    }

                    sb.AppendLine();
                    break;
                }

            case CalloutBlock callout:
                sb.Append(pad).Append(callout.Kind.ToString().ToUpperInvariant()).Append(": ").AppendLine(callout.Title ?? string.Empty);
                foreach (var b in callout.Blocks)
                {
                    WriteBlock(sb, b, indent + 2, headingStyle, width, showLinks);
                }

                break;

            case TableBlock table:
                {
                    var rows = table.Rows.Select(r => r.Cells.Select(c => c.ToPlainText()).ToList()).ToList();
                    var cols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
                    var widths = new int[cols];
                    foreach (var r in rows)
                    {
                        for (var i = 0; i < r.Count; i++)
                        {
                            widths[i] = Math.Max(widths[i], r[i].Length);
                        }
                    }

                    for (var ri = 0; ri < rows.Count; ri++)
                    {
                        var r = rows[ri];
                        sb.Append(pad).Append("| ");
                        for (var i = 0; i < cols; i++)
                        {
                            sb.Append((i < r.Count ? r[i] : string.Empty).PadRight(widths[i])).Append(" | ");
                        }

                        sb.AppendLine();
                        if (table.Rows[ri].IsHeader)
                        {
                            sb.Append(pad).Append('|');
                            for (var i = 0; i < cols; i++)
                            {
                                sb.Append(new string('-', widths[i] + 2)).Append('|');
                            }

                            sb.AppendLine();
                        }
                    }

                    sb.AppendLine();
                    break;
                }

            case ImageBlock img:
                sb.Append(pad).Append("[Image: ").Append(img.AltText ?? img.Title ?? img.Source).AppendLine("]").AppendLine();
                break;

            case ThematicBreakBlock:
                sb.Append(pad).AppendLine("* * *").AppendLine();
                break;

            case PageBreakBlock:
                sb.AppendLine("\f");
                break;

            case MathBlock math:
                sb.Append(pad).AppendLine(math.Latex).AppendLine();
                break;
        }
    }

    private static string InlinesToText(IEnumerable<Inline> inlines, bool showLinks)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LinkInline link when showLinks && !link.IsInternal:
                    var label = Inline.ToPlainText(link.Inlines);
                    sb.Append(label);
                    if (!string.Equals(label, link.Url, StringComparison.Ordinal))
                    {
                        sb.Append(" <").Append(link.Url).Append('>');
                    }

                    break;
                case LineBreakInline { IsHard: true }:
                    sb.AppendLine();
                    break;
                default:
                    Inline.AppendPlainText([inline], sb);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string Wrap(string text, int width, string pad)
    {
        if (width <= 0)
        {
            return pad + text;
        }

        var sb = new StringBuilder();
        foreach (var paragraphLine in text.Split('\n'))
        {
            var line = new StringBuilder(pad);
            foreach (var word in paragraphLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length + word.Length + 1 > width && line.Length > pad.Length)
                {
                    sb.AppendLine(line.ToString().TrimEnd());
                    line.Clear().Append(pad);
                }

                line.Append(word).Append(' ');
            }

            sb.AppendLine(line.ToString().TrimEnd());
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }
}
