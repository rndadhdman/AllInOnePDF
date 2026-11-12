using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using Markdig;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Figures;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = AllInOnePdf.Core.Documents.Block;
using CodeBlock = AllInOnePdf.Core.Documents.CodeBlock;
using HeadingBlock = AllInOnePdf.Core.Documents.HeadingBlock;
using Inline = AllInOnePdf.Core.Documents.Inline;
using ListBlock = AllInOnePdf.Core.Documents.ListBlock;
using MdBlock = Markdig.Syntax.Block;
using MdCodeBlock = Markdig.Syntax.CodeBlock;
using MdFootnote = Markdig.Extensions.Footnotes.Footnote;
using MdHeading = Markdig.Syntax.HeadingBlock;
using MdHtmlBlock = Markdig.Syntax.HtmlBlock;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdLinkInline = Markdig.Syntax.Inlines.LinkInline;
using MdList = Markdig.Syntax.ListBlock;
using MdMathBlock = Markdig.Extensions.Mathematics.MathBlock;
using MdMathInline = Markdig.Extensions.Mathematics.MathInline;
using MdParagraph = Markdig.Syntax.ParagraphBlock;
using MdQuote = Markdig.Syntax.QuoteBlock;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdThematicBreak = Markdig.Syntax.ThematicBreakBlock;
using ParagraphBlock = AllInOnePdf.Core.Documents.ParagraphBlock;
using QuoteBlock = AllInOnePdf.Core.Documents.QuoteBlock;
using ThematicBreakBlock = AllInOnePdf.Core.Documents.ThematicBreakBlock;

namespace AllInOnePdf.Core.Importers;

/// <summary>Markdown (CommonMark + GitHub extensions) → document model, using Markdig.</summary>
public sealed partial class MarkdownImporter : IImporter
{
    public const string OptEmbedImages = "embedLocalImages";
    public const string OptFrontMatter = "frontMatter";
    public const string OptHardBreaks = "hardLineBreaks";
    public const string OptSmartyPants = "smartQuotes";
    public const string OptTitleFromHeading = "titleFromHeading";
    public const string OptMaxImageMb = "maxImageMb";

    public FileFormat Format => KnownFormats.Markdown;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Bool(OptFrontMatter, "Read YAML front matter", true, "Use title, author, date and other keys from a leading --- block as document metadata.", "Content"),
        OptionDefinition.Bool(OptTitleFromHeading, "Title from first heading", true, "When no front-matter title exists, use the first level-1 heading as the document title.", "Content"),
        OptionDefinition.Bool(OptEmbedImages, "Embed local images", true, "Load images referenced by relative path into the document so output is self-contained.", "Images"),
        OptionDefinition.Int(OptMaxImageMb, "Largest image", 25, 1, 200, "Images larger than this are left as references.", "Images", unit: "MB", advanced: true),
        OptionDefinition.Bool(OptHardBreaks, "Newlines are line breaks", false, "Treat every newline inside a paragraph as a hard line break (like chat apps).", "Parsing", advanced: true),
        OptionDefinition.Bool(OptSmartyPants, "Smart quotes and dashes", false, "Convert straight quotes, -- and ... to typographic equivalents.", "Parsing", advanced: true),
    ];

    public async Task<Document> ImportAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(context.Input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var markdown = await reader.ReadToEndAsync(cancellationToken);
        context.Report(0.2, "Parsing Markdown");

        var settings = new WalkSettings(
            FrontMatter: context.Options.GetBool(OptFrontMatter, true),
            EmbedImages: context.Options.GetBool(OptEmbedImages, true),
            MaxImageBytes: Math.Max(1, context.Options.GetInt(OptMaxImageMb, 25)) * 1024L * 1024L,
            SourceDirectory: context.SourceDirectory);
        var document = ParseCore(
            markdown,
            hardBreaks: context.Options.GetBool(OptHardBreaks),
            smartQuotes: context.Options.GetBool(OptSmartyPants),
            titleFromHeading: context.Options.GetBool(OptTitleFromHeading, true),
            settings,
            onParsed: () => context.Report(0.5, "Building document"));

        document.Metadata.SourceFormat = Format.Id;
        context.Report(1, "Markdown parsed");
        return document;
    }

    /// <summary>
    /// Parses a Markdown snippet (an editor field, a template) into a document with the same pipeline and rules as
    /// <see cref="ImportAsync"/>, synchronously. It never touches the file system: image references stay as written
    /// (<c>res:</c>, <c>data:</c> or URLs), YAML front matter is skipped unless <see cref="MarkdownParseOptions.FrontMatter"/>
    /// is set, and <see cref="DocumentMetadata.SourcePath"/> and <see cref="DocumentMetadata.SourceFormat"/> stay unset.
    /// </summary>
    /// <param name="markdown">CommonMark with the GitHub extensions; null is treated as empty.</param>
    /// <param name="options">Parsing switches; null uses the defaults.</param>
    public static Document Parse(string markdown, MarkdownParseOptions? options = null)
    {
        options ??= new MarkdownParseOptions();
        var settings = new WalkSettings(FrontMatter: options.FrontMatter, EmbedImages: false, MaxImageBytes: 0, SourceDirectory: null);
        return ParseCore(markdown ?? string.Empty, options.HardLineBreaks, options.SmartQuotes, options.TitleFromHeading, settings, onParsed: null);
    }

    private static Document ParseCore(string markdown, bool hardBreaks, bool smartQuotes, bool titleFromHeading, WalkSettings settings, Action? onParsed)
    {
        var builder = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseYamlFrontMatter()
            .UseAutoIdentifiers()
            .UseEmphasisExtras()
            .UseAlertBlocks()
            .UseTaskLists();

        if (hardBreaks)
        {
            builder.UseSoftlineBreakAsHardlineBreak();
        }

        if (smartQuotes)
        {
            builder.UseSmartyPants();
        }

        var pipeline = builder.Build();
        var ast = Markdown.Parse(markdown, pipeline);
        onParsed?.Invoke();

        var document = new Document();
        var walker = new Walker(document, settings);
        walker.Walk(ast);
        MergeImageCaptions(document.Blocks);

        if (titleFromHeading && string.IsNullOrWhiteSpace(document.Metadata.Title))
        {
            document.Metadata.Title = document.Headings.FirstOrDefault(h => h.Level == 1)?.ToPlainText();
        }

        return document;
    }

    /// <summary>What the walker may do: read front matter, embed local images (from where, up to what size).</summary>
    private sealed record WalkSettings(bool FrontMatter, bool EmbedImages, long MaxImageBytes, string? SourceDirectory);

    private sealed class Walker
    {
        private readonly Document _doc;
        private readonly WalkSettings _settings;

        public Walker(Document doc, WalkSettings settings)
        {
            _doc = doc;
            _settings = settings;
        }

        public void Walk(MarkdownDocument ast)
        {
            foreach (var block in ast)
            {
                if (block is YamlFrontMatterBlock yaml)
                {
                    if (_settings.FrontMatter)
                    {
                        ParseFrontMatter(yaml.Lines.ToString());
                    }

                    continue;
                }

                if (block is FootnoteGroup group)
                {
                    foreach (var fn in group.OfType<MdFootnote>())
                    {
                        var footnote = new Documents.Footnote { Label = fn.Order.ToString(CultureInfo.InvariantCulture) };
                        footnote.Blocks.AddRange(ConvertBlocks(fn));
                        _doc.Footnotes.Add(footnote);
                    }

                    continue;
                }

                _doc.Blocks.AddRange(ConvertBlock(block));
            }
        }

        private IEnumerable<Block> ConvertBlocks(ContainerBlock container) => container.SelectMany(ConvertBlock);

        private IEnumerable<Block> ConvertBlock(MdBlock block)
        {
            switch (block)
            {
                case MdHeading h:
                    {
                        var heading = new HeadingBlock { Level = h.Level, Id = h.GetAttributes().Id };
                        heading.Inlines.AddRange(ConvertInlines(h.Inline));
                        yield return heading;
                        break;
                    }

                case MdParagraph p:
                    {
                        var inlines = ConvertInlines(p.Inline).ToList();
                        // A paragraph that is only an image becomes a block-level figure.
                        var meaningful = inlines.Where(i => i is not TextInline { Text: var t } || !string.IsNullOrWhiteSpace(t)).ToList();
                        if (meaningful.Count == 1 && meaningful[0] is ImageInline img)
                        {
                            yield return new ImageBlock { Source = img.Source, AltText = img.AltText, Title = img.Title };
                            break;
                        }

                        var para = new ParagraphBlock();
                        para.Inlines.AddRange(inlines);
                        yield return para;
                        break;
                    }

                case MdList list:
                    {
                        var ours = new ListBlock
                        {
                            Ordered = list.IsOrdered,
                            Tight = !list.IsLoose,
                            Start = int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 1,
                        };
                        foreach (var itemBlock in list.OfType<ListItemBlock>())
                        {
                            var item = new ListItem();
                            var children = ConvertBlocks(itemBlock).ToList();
                            // Task list marker lives as the first inline of the first paragraph.
                            if (itemBlock.FirstOrDefault() is MdParagraph fp && fp.Inline?.FirstChild is TaskList task)
                            {
                                item.Checked = task.Checked;
                                if (children.FirstOrDefault() is ParagraphBlock firstPara && firstPara.Inlines.Count > 0 && firstPara.Inlines[0] is TextInline { Text: var tt } && tt.TrimStart().Length == 0)
                                {
                                    firstPara.Inlines.RemoveAt(0);
                                }
                                else if (children.FirstOrDefault() is ParagraphBlock fp2 && fp2.Inlines.Count > 0 && fp2.Inlines[0] is TextInline ti)
                                {
                                    ti.Text = ti.Text.TrimStart();
                                }
                            }

                            item.Blocks.AddRange(children);
                            ours.Items.Add(item);
                        }

                        yield return ours;
                        break;
                    }

                case MdMathBlock math:
                    yield return new Documents.MathBlock { Latex = math.Lines.ToString() };
                    break;

                case FencedCodeBlock fenced:
                    yield return new CodeBlock
                    {
                        Code = fenced.Lines.ToString(),
                        Language = string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info.Trim(),
                        Caption = string.IsNullOrWhiteSpace(fenced.Arguments) ? null : fenced.Arguments.Trim(),
                    };
                    break;

                case MdCodeBlock code:
                    yield return new CodeBlock { Code = code.Lines.ToString() };
                    break;

                case Markdig.Extensions.Alerts.AlertBlock alert:
                    {
                        var callout = new CalloutBlock { Kind = ParseCalloutKind(alert.Kind.ToString()) ?? CalloutKind.Note };
                        callout.Blocks.AddRange(ConvertBlocks(alert));
                        yield return callout;
                        break;
                    }

                case MdQuote quote:
                    {
                        var children = ConvertBlocks(quote).ToList();
                        if (TryExtractCallout(children, out var callout))
                        {
                            yield return callout;
                            break;
                        }

                        var ours = new QuoteBlock();
                        ours.Blocks.AddRange(children);
                        yield return ours;
                        break;
                    }

                case CustomContainer container:
                    {
                        var kind = ParseCalloutKind(container.Info) ?? CalloutKind.Note;
                        var callout = new CalloutBlock { Kind = kind, Title = string.IsNullOrWhiteSpace(container.Arguments) ? null : container.Arguments.Trim() };
                        callout.Blocks.AddRange(ConvertBlocks(container));
                        yield return callout;
                        break;
                    }

                case MdTable table:
                    yield return ConvertTable(table);
                    break;

                case Figure figure:
                    {
                        var caption = figure.OfType<FigureCaption>().FirstOrDefault();
                        var captionText = caption is null ? null : Inline.ToPlainText(ConvertInlines(caption.Inline));
                        var emitted = false;
                        foreach (var inner in figure.Where(b => b is not FigureCaption))
                        {
                            foreach (var converted in ConvertBlock(inner))
                            {
                                if (converted is ImageBlock ib && captionText is not null)
                                {
                                    ib.Title = captionText;
                                }

                                emitted = true;
                                yield return converted;
                            }
                        }

                        if (!emitted && captionText is not null)
                        {
                            yield return new ParagraphBlock(captionText);
                        }

                        break;
                    }

                case DefinitionList defs:
                    {
                        foreach (var item in defs.OfType<DefinitionItem>())
                        {
                            foreach (var child in item)
                            {
                                if (child is DefinitionTerm term)
                                {
                                    var p = new ParagraphBlock();
                                    p.Inlines.Add(new StyledInline(InlineStyle.Bold, ConvertInlines(term.Inline).ToArray()));
                                    yield return p;
                                }
                                else
                                {
                                    var quote = new QuoteBlock();
                                    quote.Blocks.AddRange(ConvertBlock(child));
                                    yield return quote;
                                }
                            }
                        }

                        break;
                    }

                case MdThematicBreak:
                    yield return new ThematicBreakBlock();
                    break;

                case MdHtmlBlock html:
                    {
                        var text = html.Lines.ToString();
                        if (PageBreakHtml().IsMatch(text))
                        {
                            yield return new PageBreakBlock();
                        }
                        else
                        {
                            yield return new Documents.HtmlBlock { Html = text };
                        }

                        break;
                    }

                case LinkReferenceDefinitionGroup:
                case LinkReferenceDefinition:
                    break;

                case ContainerBlock other:
                    foreach (var b in ConvertBlocks(other))
                    {
                        yield return b;
                    }

                    break;

                case LeafBlock leaf when leaf.Inline is not null:
                    {
                        var p = new ParagraphBlock();
                        p.Inlines.AddRange(ConvertInlines(leaf.Inline));
                        yield return p;
                        break;
                    }
            }
        }

        private TableBlock ConvertTable(MdTable table)
        {
            var ours = new TableBlock();
            if (table.ColumnDefinitions is not null)
            {
                foreach (var def in table.ColumnDefinitions)
                {
                    ours.Alignments.Add(def.Alignment switch
                    {
                        TableColumnAlign.Left => ColumnAlignment.Left,
                        TableColumnAlign.Center => ColumnAlignment.Center,
                        TableColumnAlign.Right => ColumnAlignment.Right,
                        _ => ColumnAlignment.Default,
                    });
                }
            }

            foreach (var row in table.OfType<MdTableRow>())
            {
                var ourRow = new Documents.TableRow { IsHeader = row.IsHeader };
                foreach (var cell in row.OfType<MdTableCell>())
                {
                    var ourCell = new Documents.TableCell
                    {
                        ColumnSpan = Math.Max(1, cell.ColumnSpan),
                        RowSpan = Math.Max(1, cell.RowSpan),
                    };
                    ourCell.Blocks.AddRange(ConvertBlocks(cell));
                    if (ourCell.Blocks.Count == 0)
                    {
                        ourCell.Blocks.Add(new ParagraphBlock());
                    }

                    ourRow.Cells.Add(ourCell);
                }

                ours.Rows.Add(ourRow);
            }

            return ours;
        }

        /// <summary>Fallback for "> [!KIND]" quotes when the alert extension did not claim them. Markdig splits the marker into several literals ("[", "!NOTE", "]"), so leading text inlines are joined first.</summary>
        private static bool TryExtractCallout(List<Block> children, out CalloutBlock callout)
        {
            callout = null!;
            if (children.Count == 0 || children[0] is not ParagraphBlock first)
            {
                return false;
            }

            var leading = new List<TextInline>();
            foreach (var inline in first.Inlines)
            {
                if (inline is TextInline t)
                {
                    leading.Add(t);
                }
                else
                {
                    break;
                }
            }

            if (leading.Count == 0)
            {
                return false;
            }

            var joined = string.Concat(leading.Select(t => t.Text));
            var m = CalloutMarker().Match(joined);
            if (!m.Success || ParseCalloutKind(m.Groups["kind"].Value) is not { } kind)
            {
                return false;
            }

            var remaining = m.Length;
            foreach (var t in leading)
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (t.Text.Length <= remaining)
                {
                    remaining -= t.Text.Length;
                    t.Text = string.Empty;
                }
                else
                {
                    t.Text = t.Text[remaining..];
                    remaining = 0;
                }
            }

            first.Inlines.RemoveAll(i => i is TextInline { Text.Length: 0 });
            if (first.Inlines.Count > 0 && first.Inlines[0] is Documents.LineBreakInline)
            {
                first.Inlines.RemoveAt(0);
            }

            if (first.Inlines.Count > 0 && first.Inlines[0] is TextInline lead)
            {
                lead.Text = lead.Text.TrimStart();
            }

            callout = new CalloutBlock { Kind = kind };
            if (first.Inlines.Count == 0)
            {
                children.RemoveAt(0);
            }

            callout.Blocks.AddRange(children);
            return true;
        }

        private static CalloutKind? ParseCalloutKind(string? value) => value?.Trim().ToUpperInvariant() switch
        {
            "NOTE" or "INFO" => CalloutKind.Note,
            "TIP" or "HINT" or "SUCCESS" => CalloutKind.Tip,
            "IMPORTANT" => CalloutKind.Important,
            "WARNING" or "WARN" or "ATTENTION" => CalloutKind.Warning,
            "CAUTION" or "DANGER" or "ERROR" => CalloutKind.Caution,
            _ => null,
        };

        private List<Inline> ConvertInlines(ContainerInline? container) =>
            container is null ? [] : FoldHtmlStyles(container.SelectMany(ConvertInline).ToList());

        /// <summary>Turns sibling pairs such as <c>&lt;u&gt;…&lt;/u&gt;</c> or <c>&lt;sup&gt;…&lt;/sup&gt;</c> into styled runs (Markdown exporters write underline this way).</summary>
        private static List<Inline> FoldHtmlStyles(List<Inline> inlines)
        {
            if (!inlines.Any(i => i is Documents.HtmlInline))
            {
                return inlines;
            }

            var result = new List<Inline>(inlines.Count);
            for (var i = 0; i < inlines.Count; i++)
            {
                if (inlines[i] is Documents.HtmlInline open && ParseStyleTag(open.Html) is { Closing: false } tag)
                {
                    var depth = 0;
                    var close = -1;
                    for (var j = i + 1; j < inlines.Count && close < 0; j++)
                    {
                        if (inlines[j] is Documents.HtmlInline h && ParseStyleTag(h.Html) is { } other && other.Name == tag.Name)
                        {
                            if (!other.Closing)
                            {
                                depth++;
                            }
                            else if (depth-- == 0)
                            {
                                close = j;
                            }
                        }
                    }

                    if (close > 0)
                    {
                        var styled = new StyledInline { Style = tag.Style };
                        styled.Inlines.AddRange(FoldHtmlStyles(inlines.GetRange(i + 1, close - i - 1)));
                        result.Add(styled);
                        i = close;
                        continue;
                    }
                }

                result.Add(inlines[i]);
            }

            return result;
        }

        private static (string Name, bool Closing, InlineStyle Style)? ParseStyleTag(string html)
        {
            var m = StyleTag().Match(html);
            if (!m.Success)
            {
                return null;
            }

            var name = m.Groups["name"].Value.ToLowerInvariant();
            var style = name switch
            {
                "u" or "ins" => InlineStyle.Underline,
                "sup" => InlineStyle.Superscript,
                "sub" => InlineStyle.Subscript,
                "mark" => InlineStyle.Highlight,
                "s" or "del" or "strike" => InlineStyle.Strikethrough,
                "b" or "strong" => InlineStyle.Bold,
                "i" or "em" => InlineStyle.Italic,
                _ => InlineStyle.None,
            };
            return (name, m.Groups["close"].Length > 0, style);
        }

        private IEnumerable<Inline> ConvertInline(MdInline inline)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    yield return new TextInline(literal.Content.ToString());
                    break;

                case EmphasisInline em:
                    {
                        var style = em.DelimiterChar switch
                        {
                            '*' or '_' => em.DelimiterCount >= 2 ? InlineStyle.Bold : InlineStyle.Italic,
                            '~' => em.DelimiterCount >= 2 ? InlineStyle.Strikethrough : InlineStyle.Subscript,
                            '^' => InlineStyle.Superscript,
                            '=' => InlineStyle.Highlight,
                            '+' => InlineStyle.Underline,
                            _ => InlineStyle.None,
                        };
                        var styled = new StyledInline { Style = style };
                        styled.Inlines.AddRange(ConvertInlines(em));
                        yield return styled;
                        break;
                    }

                case Markdig.Syntax.Inlines.CodeInline code:
                    yield return new Documents.CodeInline(code.Content);
                    break;

                case MdLinkInline link:
                    {
                        var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? string.Empty;
                        if (link.IsImage)
                        {
                            var alt = Inline.ToPlainText(ConvertInlines(link));
                            yield return new ImageInline { Source = ResolveImageSource(url), AltText = string.IsNullOrEmpty(alt) ? null : alt, Title = string.IsNullOrEmpty(link.Title) ? null : link.Title };
                        }
                        else
                        {
                            var ours = new Documents.LinkInline { Url = url, Title = string.IsNullOrEmpty(link.Title) ? null : link.Title };
                            ours.Inlines.AddRange(ConvertInlines(link));
                            yield return ours;
                        }

                        break;
                    }

                case AutolinkInline auto:
                    {
                        var url = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
                        var ours = new Documents.LinkInline { Url = url };
                        ours.Inlines.Add(new TextInline(auto.Url));
                        yield return ours;
                        break;
                    }

                case Markdig.Syntax.Inlines.LineBreakInline br:
                    yield return new Documents.LineBreakInline { IsHard = br.IsHard };
                    break;

                case HtmlEntityInline entity:
                    yield return new TextInline(entity.Transcoded.ToString());
                    break;

                case Markdig.Syntax.Inlines.HtmlInline html:
                    {
                        var tag = html.Tag.Trim();
                        if (tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
                        {
                            yield return new Documents.LineBreakInline { IsHard = true };
                        }
                        else
                        {
                            yield return new Documents.HtmlInline { Html = tag };
                        }

                        break;
                    }

                case FootnoteLink fnLink:
                    if (!fnLink.IsBackLink)
                    {
                        yield return new FootnoteReferenceInline { Label = fnLink.Footnote.Order.ToString(CultureInfo.InvariantCulture) };
                    }

                    break;

                case MdMathInline math:
                    yield return new Documents.MathInline { Latex = math.Content.ToString() };
                    break;

                case TaskList:
                    // handled at list-item level
                    break;

                case ContainerInline container:
                    foreach (var c in ConvertInlines(container))
                    {
                        yield return c;
                    }

                    break;

                case LeafInline leaf:
                    yield return new TextInline(leaf.ToString() ?? string.Empty);
                    break;
            }
        }

        /// <summary>Embeds local images as resources when allowed; otherwise returns the source unchanged.</summary>
        private string ResolveImageSource(string url)
        {
            if (!_settings.EmbedImages || string.IsNullOrWhiteSpace(url) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && !abs.IsFile)
            {
                return url;
            }

            var path = abs is { IsFile: true } ? abs.LocalPath : Uri.UnescapeDataString(url);
            if (!Path.IsPathRooted(path))
            {
                if (_settings.SourceDirectory is null)
                {
                    return url;
                }

                path = Path.GetFullPath(Path.Combine(_settings.SourceDirectory, path));
            }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > _settings.MaxImageBytes)
                {
                    return url;
                }

                var mediaType = MediaTypeFor(info.Extension);
                if (mediaType is null)
                {
                    return url;
                }

                var resource = _doc.AddResource(mediaType, File.ReadAllBytes(path), info.Name);
                return resource.Reference;
            }
            catch (IOException)
            {
                return url;
            }
            catch (UnauthorizedAccessException)
            {
                return url;
            }
        }

        private static string? MediaTypeFor(string extension) => extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => null,
        };

        private void ParseFrontMatter(string yaml)
        {
            foreach (var rawLine in yaml.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line == "---")
                {
                    continue;
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                if (value.Length == 0)
                {
                    continue;
                }

                switch (key)
                {
                    case "title":
                        _doc.Metadata.Title = value;
                        break;
                    case "subtitle":
                        _doc.Metadata.Subtitle = value;
                        break;
                    case "author":
                    case "authors":
                        foreach (var a in value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            _doc.Metadata.Authors.Add(a.Trim('"', '\''));
                        }

                        break;
                    case "date":
                        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
                        {
                            _doc.Metadata.Date = date;
                        }

                        break;
                    case "lang":
                    case "language":
                        _doc.Metadata.Language = value;
                        break;
                    case "description":
                    case "summary":
                    case "abstract":
                        _doc.Metadata.Description = value;
                        break;
                    case "keywords":
                    case "tags":
                        foreach (var k in value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            _doc.Metadata.Keywords.Add(k.Trim('"', '\''));
                        }

                        break;
                    default:
                        _doc.Metadata.Custom[key] = value;
                        break;
                }
            }
        }
    }

    [GeneratedRegex(@"^\s*\[!(?<kind>[A-Za-z]+)\]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex CalloutMarker();

    [GeneratedRegex(@"page-break|pagebreak|<div\s+class=""?break""?", RegexOptions.IgnoreCase)]
    private static partial Regex PageBreakHtml();

    [GeneratedRegex(@"^<(?<close>/?)(?<name>u|ins|sup|sub|mark|s|del|strike|b|strong|i|em)\s*>$", RegexOptions.IgnoreCase)]
    private static partial Regex StyleTag();

    /// <summary>
    /// Drops an italic paragraph that directly follows an image block and repeats its title: that is how
    /// Markdown exporters write a visible caption, and the title already carries it.
    /// </summary>
    private static void MergeImageCaptions(List<Block> blocks)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case QuoteBlock q:
                    MergeImageCaptions(q.Blocks);
                    break;
                case CalloutBlock c:
                    MergeImageCaptions(c.Blocks);
                    break;
                case ListBlock l:
                    l.Items.ForEach(item => MergeImageCaptions(item.Blocks));
                    break;
                case ImageBlock { Title: { Length: > 0 } title } when i + 1 < blocks.Count &&
                    blocks[i + 1] is ParagraphBlock { Inlines: [StyledInline { Style: InlineStyle.Italic } caption] } &&
                    string.Equals(Inline.ToPlainText(caption.Inlines).Trim(), title.Trim(), StringComparison.Ordinal):
                    blocks.RemoveAt(i + 1);
                    break;
            }
        }
    }
}
