using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Importers;

namespace AllInOnePdf.Core.Tests;

/// <summary>Import/export shortcuts and a structural document dump used to compare documents.</summary>
internal static partial class MarkdownExportTestHelpers
{
    public static async Task<Document> ImportAsync(string markdown)
    {
        var importer = new MarkdownImporter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
        return await importer.ImportAsync(new ImportContext
        {
            Input = stream,
            FileName = "test.md",
            Options = OptionValues.FromDefaults(importer.Options),
        });
    }

    public static async Task<ExportResult> ExportAsync(Document document, string baseName = "doc", params (string Key, object Value)[] options)
    {
        var exporter = new MarkdownExporter();
        var values = new OptionValues();
        foreach (var (key, value) in options)
        {
            values.Set(key, value);
        }

        return await exporter.ExportAsync(new ExportContext
        {
            Document = document,
            BaseName = baseName,
            Options = values.WithDefaults(exporter.Options),
        });
    }

    public static async Task<string> ToMarkdownAsync(Document document, params (string Key, object Value)[] options)
    {
        var result = await ExportAsync(document, "doc", options);
        return Encoding.UTF8.GetString(result.Primary!.Content);
    }

    /// <summary>Markdown → document → Markdown.</summary>
    public static async Task<string> ReformatAsync(string markdown, params (string Key, object Value)[] options) =>
        await ToMarkdownAsync(await ImportAsync(markdown), options);

    public static Document Doc(params Block[] blocks)
    {
        var doc = new Document();
        doc.Blocks.AddRange(blocks);
        return doc;
    }

    public static ParagraphBlock Para(params Inline[] inlines)
    {
        var p = new ParagraphBlock();
        p.Inlines.AddRange(inlines);
        return p;
    }

    public static string FindSample(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Sample not found", name);
    }

    /// <summary>
    /// A canonical text dump of a document's structure: block types, heading levels, list nesting and task state,
    /// code language/text, table rows/cells/alignment, inline style runs, link URLs and image sources.
    /// Whitespace inside text runs is normalised (soft breaks and wrapping are not structural).
    /// </summary>
    public static string Dump(Document document)
    {
        var sb = new StringBuilder();
        var m = document.Metadata;
        sb.Append("title=").Append(m.Title).Append('\n')
          .Append("subtitle=").Append(m.Subtitle).Append('\n')
          .Append("authors=").Append(string.Join("|", m.Authors)).Append('\n')
          .Append("date=").Append(m.Date?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n')
          .Append("keywords=").Append(string.Join("|", m.Keywords)).Append('\n')
          .Append("description=").Append(m.Description).Append('\n');
        DumpBlocks(sb, document.Blocks, 0);
        foreach (var fn in document.Footnotes)
        {
            sb.Append("footnote ").Append(fn.Label).Append('\n');
            DumpBlocks(sb, fn.Blocks, 1);
        }

        return sb.ToString();
    }

    private static void DumpBlocks(StringBuilder sb, IEnumerable<Block> blocks, int depth)
    {
        foreach (var block in blocks)
        {
            DumpBlock(sb, block, depth);
        }
    }

    private static void DumpBlock(StringBuilder sb, Block block, int depth)
    {
        var pad = new string(' ', depth * 2);
        switch (block)
        {
            case HeadingBlock h:
                sb.Append(pad).Append('H').Append(h.Level).Append(' ').Append(Runs(h.Inlines)).Append('\n');
                break;
            case ParagraphBlock p:
                sb.Append(pad).Append("P ").Append(Runs(p.Inlines)).Append('\n');
                break;
            case ListBlock l:
                sb.Append(pad).Append(l.Ordered ? "OL start=" + l.Start : "UL").Append(l.Tight ? " tight" : " loose").Append('\n');
                foreach (var item in l.Items)
                {
                    sb.Append(pad).Append("- item checked=").Append(item.Checked?.ToString() ?? "none").Append('\n');
                    DumpBlocks(sb, item.Blocks, depth + 2);
                }

                break;
            case CodeBlock c:
                sb.Append(pad).Append("CODE lang=").Append(c.Language).Append('\n')
                  .Append(c.Code.Replace("\r\n", "\n").TrimEnd('\n')).Append('\n').Append(pad).Append("END CODE\n");
                break;
            case QuoteBlock q:
                sb.Append(pad).Append("QUOTE\n");
                DumpBlocks(sb, q.Blocks, depth + 1);
                break;
            case CalloutBlock c:
                sb.Append(pad).Append("CALLOUT ").Append(c.Kind).Append(" title=").Append(c.Title).Append('\n');
                DumpBlocks(sb, c.Blocks, depth + 1);
                break;
            case TableBlock t:
                sb.Append(pad).Append("TABLE cols=").Append(t.ColumnCount).Append(" align=")
                  .Append(string.Join(",", Enumerable.Range(0, t.ColumnCount).Select(t.GetAlignment))).Append('\n');
                foreach (var row in t.Rows)
                {
                    sb.Append(pad).Append(row.IsHeader ? "  TH" : "  TR");
                    foreach (var cell in row.Cells)
                    {
                        sb.Append(" | ").Append(string.Join(" / ", cell.Blocks.OfType<InlineContainerBlock>().Select(b => Runs(b.Inlines))));
                    }

                    sb.Append('\n');
                }

                break;
            case ImageBlock i:
                sb.Append(pad).Append("IMG src=").Append(i.Source).Append(" alt=").Append(i.AltText).Append(" title=").Append(i.Title).Append('\n');
                break;
            case MathBlock mb:
                sb.Append(pad).Append("MATH ").Append(mb.Latex.Trim()).Append('\n');
                break;
            case HtmlBlock html:
                sb.Append(pad).Append("HTML ").Append(html.Html.Trim()).Append('\n');
                break;
            default:
                sb.Append(pad).Append(block.GetType().Name).Append('\n');
                break;
        }
    }

    /// <summary>Flattens inlines to runs like <c>{Bold}text</c>, merging adjacent text with equal style.</summary>
    public static string Runs(IEnumerable<Inline> inlines)
    {
        var runs = new List<(InlineStyle Style, string Kind, string Text)>();
        Flatten(inlines, InlineStyle.None, runs);
        var merged = new List<(InlineStyle Style, string Kind, string Text)>();
        foreach (var run in runs)
        {
            if (merged.Count > 0 && run.Kind == "text" && merged[^1].Kind == "text" && merged[^1].Style == run.Style)
            {
                merged[^1] = (run.Style, "text", merged[^1].Text + run.Text);
            }
            else
            {
                merged.Add(run);
            }
        }

        return string.Join("", merged
            .Select(r => r.Kind == "text" ? r with { Text = Whitespace().Replace(r.Text, " ") } : r)
            .Where(r => r.Kind != "text" || r.Text.Length > 0)
            .Select(r => (r.Style == InlineStyle.None ? "" : "{" + r.Style + "}") + (r.Kind == "text" ? r.Text : "<" + r.Kind + ":" + r.Text + ">")))
            .Trim();
    }

    private static void Flatten(IEnumerable<Inline> inlines, InlineStyle style, List<(InlineStyle, string, string)> runs)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline t:
                    runs.Add((style, "text", t.Text));
                    break;
                case StyledInline s:
                    Flatten(s.Inlines, style | s.Style, runs);
                    break;
                case CodeInline c:
                    runs.Add((style, "code", c.Code));
                    break;
                case LinkInline l:
                    runs.Add((style, "link", l.Url + (l.Title is null ? "" : " \"" + l.Title + "\"")));
                    Flatten(l.Inlines, style, runs);
                    runs.Add((style, "/link", ""));
                    break;
                case ImageInline i:
                    runs.Add((style, "img", i.Source + " alt=" + i.AltText));
                    break;
                case LineBreakInline { IsHard: true }:
                    runs.Add((style, "br", ""));
                    break;
                case LineBreakInline:
                    runs.Add((style, "text", " "));
                    break;
                case FootnoteReferenceInline f:
                    runs.Add((style, "fn", f.Label));
                    break;
                case MathInline mi:
                    runs.Add((style, "math", mi.Latex.Trim()));
                    break;
                case HtmlInline h:
                    runs.Add((style, "html", h.Html));
                    break;
            }
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
