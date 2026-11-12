using System.Globalization;
using System.Text;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using Document = AllInOnePdf.Core.Documents.Document;
using Footnote = DocumentFormat.OpenXml.Wordprocessing.Footnote;
using TableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;
using TableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using Text = DocumentFormat.OpenXml.Wordprocessing.Text;
using TextAlignment = AllInOnePdf.Core.Documents.TextAlignment;

namespace AllInOnePdf.Core.Exporters.Docx;

/// <summary>Writes the document model's blocks and inlines as WordprocessingML elements using the styles of <see cref="DocxStyles"/>.</summary>
internal sealed class DocxWriter
{
    private static readonly HashSet<string> WordImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/bmp", "image/tiff",
    };

    private readonly Document _doc;
    private readonly PdfTheme _theme;
    private readonly DocxNumbering _numbering;
    private readonly ISyntaxHighlighter? _highlighter;
    private readonly List<string> _warnings;
    private readonly Dictionary<string, int> _footnoteIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _bookmarkNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedBookmarks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(OpenXmlPart, string), string> _imageParts = [];
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private uint _drawingId;
    private int _bookmarkId;
    private int _sdtId = 1000;

    public DocxWriter(Document doc, PdfTheme theme, DocxNumbering numbering, ISyntaxHighlighter? highlighter, List<string> warnings, double contentWidthPt, double contentHeightPt)
    {
        _doc = doc;
        _theme = theme;
        _numbering = numbering;
        _highlighter = highlighter;
        _warnings = warnings;
        ContentWidthPt = contentWidthPt;
        ContentHeightPt = contentHeightPt;

        var next = 1;
        foreach (var fn in doc.Footnotes)
        {
            if (!_footnoteIds.ContainsKey(fn.Label))
            {
                _footnoteIds[fn.Label] = next++;
            }
        }
    }

    public double ContentWidthPt { get; }

    public double ContentHeightPt { get; }

    /// <summary>Writing context: the part that owns relationships (images, links), list depth, paragraph style override, available width.</summary>
    public readonly record struct Ctx(OpenXmlPart Owner, double WidthPt, int ListLevel = -1, string? ParagraphStyle = null, bool InTable = false,
        ColumnAlignment CellAlign = ColumnAlignment.Default, double IndentPt = 0);

    public Ctx Root(OpenXmlPart owner) => new(owner, ContentWidthPt);

    // ----------------------------------------------------------------- blocks

    public List<OpenXmlElement> Blocks(IEnumerable<Block> blocks, Ctx ctx)
    {
        var output = new List<OpenXmlElement>();
        foreach (var block in blocks)
        {
            WriteBlock(block, ctx, output);
        }

        return output;
    }

    public void WriteBlock(Block block, Ctx ctx, List<OpenXmlElement> output)
    {
        switch (block)
        {
            case HeadingBlock h:
                output.Add(Heading(h, ctx));
                break;
            case ParagraphBlock p:
                output.Add(InlineParagraph(p.Inlines, ctx, ctx.ParagraphStyle, p.Alignment));
                break;
            case ListBlock list:
                WriteList(list, ctx, output);
                break;
            case CodeBlock code:
                output.Add(Code(code, ctx));
                break;
            case QuoteBlock quote:
                output.Add(Quote(quote, ctx));
                break;
            case CalloutBlock callout:
                output.Add(Callout(callout, ctx));
                break;
            case TableBlock table:
                WriteTable(table, ctx, output);
                break;
            case ImageBlock image:
                WriteImage(image, ctx, output);
                break;
            case ThematicBreakBlock:
                output.Add(new Paragraph(new ParaFormat { StyleId = DocxStyleIds.HorizontalRule }.ToParagraphProperties()));
                break;
            case PageBreakBlock:
                output.Add(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                break;
            case MathBlock math:
                output.Add(PlainParagraph(math.Latex.Trim('\n', '\r'), DocxStyleIds.MathBlock, ctx));
                break;
            case HtmlBlock html:
                output.Add(PlainParagraph(html.Html.Trim('\n', '\r'), DocxStyleIds.HtmlSource, ctx));
                break;
            default:
                Warn("Some content of type " + block.GetType().Name + " is not supported in Word output and was left out.");
                break;
        }
    }

    private Paragraph Heading(HeadingBlock h, Ctx ctx)
    {
        var p = new Paragraph(Props(new ParaFormat { StyleId = DocxStyleIds.Heading(h.Level) }, ctx));
        var content = Inlines(h.Inlines, ctx);
        if (!string.IsNullOrWhiteSpace(h.Id))
        {
            var id = (_bookmarkId++).ToString(CultureInfo.InvariantCulture);
            p.AppendChild(new BookmarkStart { Name = BookmarkName(h.Id), Id = id });
            p.Append(content);
            p.AppendChild(new BookmarkEnd { Id = id });
        }
        else
        {
            p.Append(content);
        }

        return p;
    }

    private Paragraph InlineParagraph(IEnumerable<Inline> inlines, Ctx ctx, string? style, TextAlignment? alignment, IEnumerable<OpenXmlElement>? prefix = null, (int, int)? numbering = null)
    {
        var format = new ParaFormat { StyleId = style, Numbering = numbering };
        if (alignment is { } a)
        {
            format.Justification = a switch
            {
                TextAlignment.Center => JustificationValues.Center,
                TextAlignment.Right => JustificationValues.Right,
                TextAlignment.Justify => JustificationValues.Both,
                _ => JustificationValues.Left,
            };
        }

        var p = new Paragraph(Props(format, ctx));
        if (prefix is not null)
        {
            p.Append(prefix);
        }

        p.Append(Inlines(inlines, ctx));
        return p;
    }

    /// <summary>Applies the context's table-cell alignment/spacing and list indentation to a paragraph format.</summary>
    private static ParagraphProperties Props(ParaFormat format, Ctx ctx)
    {
        if (ctx.InTable)
        {
            format.SpaceBeforePt ??= 0;
            format.SpaceAfterPt ??= 2;
            format.FirstLinePt ??= 0;
            format.Justification ??= ctx.CellAlign switch
            {
                ColumnAlignment.Left => JustificationValues.Left,
                ColumnAlignment.Center => JustificationValues.Center,
                ColumnAlignment.Right => JustificationValues.Right,
                _ => null,
            };
        }

        if (ctx.IndentPt > 0 && format.IndentLeftPt is null && format.Numbering is null)
        {
            format.IndentLeftPt = ctx.IndentPt;
            format.FirstLinePt ??= 0;
        }

        return format.ToParagraphProperties();
    }

    private Paragraph PlainParagraph(string text, string style, Ctx ctx)
    {
        var p = new Paragraph(Props(new ParaFormat { StyleId = style }, ctx));
        if (text.Length > 0)
        {
            p.AppendChild(TextRun(text, InlineStyle.None, null));
        }

        return p;
    }

    private void WriteList(ListBlock list, Ctx ctx, List<OpenXmlElement> output)
    {
        var level = Math.Clamp(ctx.ListLevel + 1, 0, 8);
        var task = list.Items.Any(i => i.Checked is not null);
        var numId = _numbering.NewList(list.Ordered, task, level, list.Start);
        var indent = Math.Max(9, _theme.Lists.IndentPt) * (level + 1);
        var itemCtx = ctx with { ListLevel = level, IndentPt = indent, WidthPt = Math.Max(72, ctx.WidthPt - Math.Max(9, _theme.Lists.IndentPt)) };

        foreach (var item in list.Items)
        {
            var prefix = item.Checked is { } done ? new[] { TextRun(done ? "☑ " : "☐ ", InlineStyle.None, null) } : null;
            var blocks = item.Blocks;
            var start = 0;
            if (blocks.Count > 0 && blocks[0] is ParagraphBlock first)
            {
                output.Add(InlineParagraph(first.Inlines, ctx with { IndentPt = 0 }, DocxStyleIds.ListParagraph, first.Alignment, prefix, (numId, level)));
                start = 1;
            }
            else
            {
                output.Add(InlineParagraph([], ctx with { IndentPt = 0 }, DocxStyleIds.ListParagraph, null, prefix, (numId, level)));
            }

            for (var i = start; i < blocks.Count; i++)
            {
                switch (blocks[i])
                {
                    case ListBlock nested:
                        WriteList(nested, ctx with { ListLevel = level }, output);
                        break;
                    case ParagraphBlock para:
                        output.Add(InlineParagraph(para.Inlines, itemCtx, DocxStyleIds.ListContinue, para.Alignment));
                        break;
                    default:
                        WriteBlock(blocks[i], itemCtx, output);
                        break;
                }
            }
        }
    }

    private SdtBlock Code(CodeBlock code, Ctx ctx)
    {
        var content = new SdtContentBlock();
        var c = _theme.Code;
        var label = code.Caption ?? (c.ShowLanguageLabel ? code.Language : null);
        if (!string.IsNullOrWhiteSpace(label))
        {
            content.AppendChild(PlainParagraph(label, DocxStyleIds.CodeLabel, ctx));
        }

        var p = new Paragraph(Props(new ParaFormat { StyleId = DocxStyleIds.Code }, ctx));
        var text = code.Code.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        IReadOnlyList<HighlightedRun>? runs = null;
        if (c.SyntaxHighlighting && _highlighter is not null && !string.IsNullOrWhiteSpace(code.Language) && c.Palette != SyntaxPalette.Mono)
        {
            try
            {
                runs = _highlighter.Highlight(text, code.Language, c.Palette, DocxUnits.IsDark(c.Background ?? _theme.Colors.Surface));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                runs = null;
            }
        }

        if (runs is null)
        {
            if (text.Length > 0)
            {
                p.AppendChild(CodeRun(text, null, false, false));
            }
        }
        else
        {
            foreach (var run in runs)
            {
                if (run.Text.Length > 0)
                {
                    p.AppendChild(CodeRun(run.Text, run.ColorHex, run.Bold, run.Italic));
                }
            }
        }

        content.AppendChild(p);
        return Sdt(DocxStyleIds.CodeTagPrefix + (code.Language ?? string.Empty), content);
    }

    private static Run CodeRun(string text, string? color, bool bold, bool italic)
    {
        var format = new RunFormat { Color = color, Bold = bold ? true : null, Italic = italic ? true : null };
        var run = format.IsEmpty ? new Run() : new Run(format.ToRunProperties());
        AppendText(run, text);
        return run;
    }

    private SdtBlock Quote(QuoteBlock quote, Ctx ctx)
    {
        var inner = ctx with { ParagraphStyle = DocxStyleIds.Quote };
        var content = new SdtContentBlock(Blocks(quote.Blocks, inner));
        if (!string.IsNullOrWhiteSpace(quote.Attribution))
        {
            content.AppendChild(PlainParagraph("— " + quote.Attribution.Trim(), DocxStyleIds.QuoteAttribution, ctx));
        }

        EnsureParagraph(content, DocxStyleIds.Quote);
        return Sdt(DocxStyleIds.QuoteTag, content);
    }

    private SdtBlock Callout(CalloutBlock callout, Ctx ctx)
    {
        var cs = _theme.Callouts;
        var kind = cs.Kind(callout.Kind);
        var style = DocxStyleIds.Callout(callout.Kind);
        var content = new SdtContentBlock();
        var label = !string.IsNullOrWhiteSpace(callout.Title) ? callout.Title : cs.ShowLabel ? kind.Label ?? callout.Kind.ToString() : null;
        if (label is not null)
        {
            var run = new Run(new RunFormat { StyleId = DocxStyleIds.CalloutLabel, Color = kind.Accent ?? _theme.Colors.Accent }.ToRunProperties());
            AppendText(run, label);
            content.AppendChild(new Paragraph(Props(new ParaFormat { StyleId = style, KeepNext = true, SpaceAfterPt = 2 }, ctx), run));
        }

        content.Append(Blocks(callout.Blocks, ctx with { ParagraphStyle = style }));
        EnsureParagraph(content, style);
        return Sdt(DocxStyleIds.CalloutTagPrefix + callout.Kind.ToString().ToLowerInvariant(), content);
    }

    private static void EnsureParagraph(OpenXmlCompositeElement content, string style)
    {
        if (!content.HasChildren)
        {
            content.AppendChild(new Paragraph(new ParaFormat { StyleId = style }.ToParagraphProperties()));
        }
    }

    public SdtBlock Sdt(string tag, SdtContentBlock content) =>
        new(new SdtProperties(new Tag { Val = tag }, new SdtId { Val = ++_sdtId }), content);

    // ----------------------------------------------------------------- tables

    private void WriteTable(TableBlock table, Ctx ctx, List<OpenXmlElement> output)
    {
        if (table.Rows.Count == 0)
        {
            return;
        }

        var tb = _theme.Tables;
        if (tb.ShowCaptions && !string.IsNullOrWhiteSpace(table.Caption))
        {
            output.Add(new Paragraph(
                Props(new ParaFormat { StyleId = DocxStyleIds.Caption, KeepNext = true, Justification = JustificationValues.Left }, ctx),
                TextRun(table.Caption.Trim(), InlineStyle.None, null)));
        }

        // Lay the model's cells onto a grid: row spans become vMerge continuation cells in later rows.
        var layout = new List<List<(TableCellModel? Cell, int Column, int Span, bool Continue)>>();
        var pending = new Dictionary<int, (int Remaining, int Span)>();
        var columns = 0;
        foreach (var row in table.Rows)
        {
            var cells = new List<(TableCellModel?, int, int, bool)>();
            var col = 0;
            var queue = new Queue<Documents.TableCell>(row.Cells);
            while (queue.Count > 0 || pending.Keys.Any(k => k >= col))
            {
                if (pending.TryGetValue(col, out var p) && p.Remaining > 0)
                {
                    cells.Add((null, col, p.Span, true));
                    pending[col] = (p.Remaining - 1, p.Span);
                    if (p.Remaining - 1 == 0)
                    {
                        pending.Remove(col);
                    }

                    col += p.Span;
                    continue;
                }

                if (queue.Count == 0)
                {
                    col++;
                    continue;
                }

                var cell = queue.Dequeue();
                var span = Math.Max(1, cell.ColumnSpan);
                cells.Add((new TableCellModel(cell), col, span, false));
                if (cell.RowSpan > 1)
                {
                    pending[col] = (cell.RowSpan - 1, span);
                }

                col += span;
            }

            columns = Math.Max(columns, col);
            layout.Add(cells);
        }

        columns = Math.Max(1, columns);
        var widthPt = ctx.WidthPt - ctx.IndentPt;
        var colTwips = Math.Max(200, DocxUnits.Twips(widthPt / columns));
        var tblPr = new TableProperties(
            new DocumentFormat.OpenXml.Wordprocessing.TableStyle { Val = DocxStyleIds.Table },
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct });
        if (ctx.IndentPt > 0)
        {
            tblPr.AppendChild(new TableIndentation { Width = DocxUnits.Twips(ctx.IndentPt), Type = TableWidthUnitValues.Dxa });
        }

        tblPr.AppendChild(new TableLayout { Type = TableLayoutValues.Autofit });
        var hasHeader = table.Rows[0].IsHeader;
        tblPr.AppendChild(new TableLook
        {
            FirstRow = hasHeader,
            LastRow = false,
            FirstColumn = false,
            LastColumn = false,
            NoHorizontalBand = tb.Style != Themes.TableStyle.Striped,
            NoVerticalBand = true,
        });

        var grid = new TableGrid();
        for (var i = 0; i < columns; i++)
        {
            grid.AppendChild(new GridColumn { Width = colTwips.ToString(CultureInfo.InvariantCulture) });
        }

        var wtable = new Table(tblPr, grid);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var model = table.Rows[r];
            var row = new TableRow();
            var trPr = new TableRowProperties();
            trPr.AppendChild(new CantSplit());
            if (model.IsHeader && (tb.RepeatHeaderOnNewPage || r == 0))
            {
                trPr.AppendChild(new TableHeader());
            }

            row.AppendChild(trPr);
            var used = 0;
            foreach (var (cell, col, span, cont) in layout[r])
            {
                row.AppendChild(Cell(table, cell, col, span, cont, colTwips, ctx, columns));
                used = col + span;
            }

            if (used < columns)
            {
                row.AppendChild(Cell(table, null, used, columns - used, false, colTwips, ctx, columns));
            }

            wtable.AppendChild(row);
        }

        output.Add(wtable);

        // Word needs a paragraph between consecutive tables and after a table at the end of a cell; spacing comes from the theme.
        output.Add(new Paragraph(new ParaFormat { SpaceBeforePt = 0, SpaceAfterPt = 0, LineHeight = 1 }.ToParagraphProperties(),
            new Run(new RunFormat { SizePt = Math.Max(2, _theme.Typography.ParagraphSpacingPt) }.ToRunProperties())));
    }

    private sealed record TableCellModel(Documents.TableCell Cell);

    private TableCell Cell(TableBlock table, TableCellModel? model, int col, int span, bool cont, int colTwips, Ctx ctx, int columns)
    {
        var tcPr = new TableCellProperties(new TableCellWidth { Width = (colTwips * span).ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa });
        if (span > 1)
        {
            tcPr.AppendChild(new GridSpan { Val = span });
        }

        if (cont)
        {
            tcPr.AppendChild(new VerticalMerge());
        }
        else if (model is not null && model.Cell.RowSpan > 1)
        {
            tcPr.AppendChild(new VerticalMerge { Val = MergedCellValues.Restart });
        }

        var tc = new TableCell(tcPr);
        if (model is not null)
        {
            var cellCtx = ctx with
            {
                InTable = true,
                CellAlign = table.GetAlignment(col),
                ParagraphStyle = null,
                IndentPt = 0,
                ListLevel = -1,
                WidthPt = Math.Max(36, (ctx.WidthPt - ctx.IndentPt) * span / columns - 2 * _theme.Tables.CellPaddingPt),
            };
            tc.Append(Blocks(model.Cell.Blocks, cellCtx));
        }

        if (tc.LastChild is not Paragraph)
        {
            tc.AppendChild(new Paragraph(new ParaFormat { SpaceAfterPt = 0, SpaceBeforePt = 0 }.ToParagraphProperties()));
        }

        return tc;
    }

    // ----------------------------------------------------------------- images

    private void WriteImage(ImageBlock image, Ctx ctx, List<OpenXmlElement> output)
    {
        var drawing = ImageDrawing(image.Source, image.AltText, image.Title, image.PixelWidth, image.PixelHeight, image.WidthPercent, ctx, inline: false);
        if (drawing is null)
        {
            var fallback = string.IsNullOrWhiteSpace(image.AltText) ? "[image]" : "[" + image.AltText + "]";
            output.Add(PlainParagraph(fallback, DocxStyleIds.Figure, ctx));
        }
        else
        {
            var p = new Paragraph(Props(new ParaFormat { StyleId = DocxStyleIds.Figure }, ctx with { IndentPt = 0 }), new Run(drawing));
            output.Add(p);
        }

        if (_theme.Images.ShowCaptions && !string.IsNullOrWhiteSpace(image.Title))
        {
            output.Add(PlainParagraph(image.Title.Trim(), DocxStyleIds.Caption, ctx with { IndentPt = 0 }));
        }
    }

    private Drawing? ImageDrawing(string source, string? alt, string? title, int? pixelWidth, int? pixelHeight, double? widthPercent, Ctx ctx, bool inline)
    {
        var (data, mediaType, fileName) = ResolveImage(source);
        if (data is null || mediaType is null)
        {
            return null;
        }

        if (!WordImageTypes.Contains(mediaType))
        {
            Warn($"Images of type {mediaType} cannot be placed in Word documents; their alt text was written instead.");
            return null;
        }

        var size = pixelWidth is > 0 && pixelHeight is > 0 ? (pixelWidth.Value, pixelHeight.Value) : ImageDimensions.Read(data) ?? (480, 320);
        double w = Math.Max(1, size.Item1) * 0.75;
        double h = Math.Max(1, size.Item2) * 0.75;
        var available = Math.Max(36, ctx.WidthPt - ctx.IndentPt);
        double targetW;
        if (inline)
        {
            var lineH = _theme.Typography.BodySizePt * 1.15;
            targetW = w * lineH / h;
        }
        else
        {
            var cap = available * Math.Clamp(_theme.Images.MaxWidthPercent, 5, 100) / 100;
            targetW = widthPercent is > 0 ? available * Math.Min(100, widthPercent.Value) / 100 : Math.Min(w, cap);
            var maxH = ContentHeightPt * 0.9;
            if (targetW * h / w > maxH)
            {
                targetW = maxH * w / h;
            }
        }

        var targetH = targetW * h / w;
        var cx = (long)Math.Round(targetW * DocxUnits.EmuPerPoint);
        var cy = (long)Math.Round(targetH * DocxUnits.EmuPerPoint);

        var key = (ctx.Owner, source);
        if (!_imageParts.TryGetValue(key, out var relId))
        {
            ImagePart part;
            try
            {
                part = ctx.Owner switch
                {
                    MainDocumentPart m => m.AddImagePart(mediaType),
                    FootnotesPart f => f.AddImagePart(mediaType),
                    HeaderPart hp => hp.AddImagePart(mediaType),
                    FooterPart fp => fp.AddImagePart(mediaType),
                    _ => throw new InvalidOperationException("Images are not supported here."),
                };
            }
            catch (InvalidOperationException)
            {
                return null;
            }

            using (var stream = new MemoryStream(data))
            {
                part.FeedData(stream);
            }

            relId = ctx.Owner.GetIdOfPart(part);
            _imageParts[key] = relId;
        }

        var id = ++_drawingId;
        var name = fileName ?? "Picture " + id.ToString(CultureInfo.InvariantCulture);
        var docPr = new DW.DocProperties { Id = id, Name = name };
        if (!string.IsNullOrWhiteSpace(alt))
        {
            docPr.Description = alt;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            docPr.Title = title;
        }

        var spPr = new PIC.ShapeProperties(
            new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle });
        if (_theme.Images.Border && !inline)
        {
            spPr.AppendChild(new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = DocxUnits.Hex(_theme.Colors.Border, "D1D5DB") })) { Width = 9525 });
        }

        var graphic = new A.Graphic(new A.GraphicData(
            new PIC.Picture(
                new PIC.NonVisualPictureProperties(
                    new PIC.NonVisualDrawingProperties { Id = 0, Name = name },
                    new PIC.NonVisualPictureDrawingProperties()),
                new PIC.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                spPr))
        { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" });

        return new Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            docPr,
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            graphic)
        { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 });
    }

    private (byte[]? Data, string? MediaType, string? FileName) ResolveImage(string source)
    {
        var resource = _doc.FindResource(source);
        if (resource is not null)
        {
            return (resource.Data, resource.MediaType, resource.FileName);
        }

        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',', StringComparison.Ordinal);
            var header = comma > 5 ? source[5..comma] : string.Empty;
            if (comma > 0 && header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return (Convert.FromBase64String(source[(comma + 1)..]), header[..^7], null);
                }
                catch (FormatException)
                {
                    return (null, null, null);
                }
            }

            return (null, null, null);
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            Warn("Images on the web are not downloaded (the converter works offline); their alt text was written instead.");
            return (null, null, null);
        }

        try
        {
            var path = source;
            if (!Path.IsPathRooted(path) && _doc.Metadata.SourcePath is { } sourcePath && Path.GetDirectoryName(sourcePath) is { } dir)
            {
                path = Path.Combine(dir, Uri.UnescapeDataString(source));
            }

            if (File.Exists(path))
            {
                var mediaType = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    ".tif" or ".tiff" => "image/tiff",
                    ".svg" => "image/svg+xml",
                    ".webp" => "image/webp",
                    _ => null,
                };
                return (File.ReadAllBytes(path), mediaType, Path.GetFileName(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Fall through to the warning below.
        }

        Warn("Some images could not be found; their alt text was written instead.");
        return (null, null, null);
    }

    // ----------------------------------------------------------------- inlines

    public List<OpenXmlElement> Inlines(IEnumerable<Inline> inlines, Ctx ctx)
    {
        var output = new List<OpenXmlElement>();
        WriteInlines(inlines, InlineStyle.None, null, ctx, output);
        return output;
    }

    private void WriteInlines(IEnumerable<Inline> inlines, InlineStyle style, string? charStyle, Ctx ctx, List<OpenXmlElement> output)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline t:
                    if (t.Text.Length > 0)
                    {
                        output.Add(TextRun(t.Text, style, charStyle));
                    }

                    break;
                case StyledInline s:
                    WriteInlines(s.Inlines, style | s.Style, charStyle, ctx, output);
                    break;
                case CodeInline c:
                    output.Add(TextRun(c.Code, style, DocxStyleIds.CodeChar));
                    break;
                case MathInline m:
                    output.Add(TextRun(m.Latex, style, DocxStyleIds.MathChar));
                    break;
                case HtmlInline h:
                    output.Add(TextRun(h.Html, style, charStyle));
                    break;
                case LineBreakInline br:
                    output.Add(br.IsHard ? new Run(new Break()) : TextRun(" ", style, charStyle));
                    break;
                case FootnoteReferenceInline fr:
                    if (_footnoteIds.TryGetValue(fr.Label, out var fnId) && ctx.Owner is MainDocumentPart)
                    {
                        output.Add(new Run(new RunFormat { StyleId = DocxStyleIds.FootnoteReference }.ToRunProperties(), new FootnoteReference { Id = fnId }));
                    }
                    else
                    {
                        output.Add(TextRun("[" + fr.Label + "]", style | InlineStyle.Superscript, charStyle));
                    }

                    break;
                case ImageInline img:
                    var drawing = ImageDrawing(img.Source, img.AltText, img.Title, null, null, null, ctx, inline: true);
                    output.Add(drawing is null
                        ? TextRun(string.IsNullOrWhiteSpace(img.AltText) ? "[image]" : img.AltText, style, charStyle)
                        : new Run(drawing));
                    break;
                case LinkInline link:
                    WriteLink(link, style, ctx, output);
                    break;
                default:
                    WriteInlines(inline.Children, style, charStyle, ctx, output);
                    break;
            }
        }
    }

    private void WriteLink(LinkInline link, InlineStyle style, Ctx ctx, List<OpenXmlElement> output)
    {
        var inner = new List<OpenXmlElement>();
        WriteInlines(link.Inlines, style, DocxStyleIds.Hyperlink, ctx, inner);
        if (inner.Count == 0)
        {
            inner.Add(TextRun(link.Url, style, DocxStyleIds.Hyperlink));
        }

        var hyperlink = new Hyperlink { History = true };
        if (!string.IsNullOrWhiteSpace(link.Title))
        {
            hyperlink.Tooltip = link.Title;
        }

        var ok = false;
        if (link.IsInternal)
        {
            hyperlink.Anchor = BookmarkName(link.Url[1..]);
            ok = true;
        }
        else
        {
            try
            {
                var uri = Uri.TryCreate(link.Url, UriKind.Absolute, out var abs) ? abs : new Uri(link.Url, UriKind.Relative);
                hyperlink.Id = ctx.Owner.AddHyperlinkRelationship(uri, true).Id;
                ok = true;
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
            {
                ok = false;
            }
        }

        // Hyperlinks may only contain runs; anything else (nested drawings are runs too) stays outside.
        if (ok && inner.All(e => e is Run))
        {
            hyperlink.Append(inner);
            output.Add(hyperlink);
        }
        else
        {
            output.AddRange(inner);
        }

        // Links.ShowUrl ("print the address after the link") is a presentation setting for fixed output: the PDF renderer
        // draws it and HTML / EPUB add it with CSS. A DOCX keeps live hyperlinks, so writing the address as text would
        // change the document's content (and break the round trip through the importer); it is deliberately not applied.
    }

    public static Run TextRun(string text, InlineStyle style, string? charStyle)
    {
        var format = new RunFormat
        {
            StyleId = charStyle,
            Bold = style.HasFlag(InlineStyle.Bold) ? true : null,
            Italic = style.HasFlag(InlineStyle.Italic) ? true : null,
            SmallCaps = style.HasFlag(InlineStyle.SmallCaps),
            Strike = style.HasFlag(InlineStyle.Strikethrough),
            Highlight = style.HasFlag(InlineStyle.Highlight),
            Underline = style.HasFlag(InlineStyle.Underline),
            Superscript = style.HasFlag(InlineStyle.Superscript),
            Subscript = style.HasFlag(InlineStyle.Subscript) && !style.HasFlag(InlineStyle.Superscript),
        };
        var run = format.IsEmpty ? new Run() : new Run(format.ToRunProperties());
        AppendText(run, text);
        return run;
    }

    /// <summary>Appends text, turning <c>\n</c> into line breaks and <c>\t</c> into tabs.</summary>
    public static void AppendText(Run run, string text)
    {
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length > 0)
            {
                run.AppendChild(new Text(sb.ToString()) { Space = SpaceProcessingModeValues.Preserve });
                sb.Clear();
            }
        }

        foreach (var ch in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            switch (ch)
            {
                case '\n':
                    Flush();
                    run.AppendChild(new Break());
                    break;
                case '\t':
                    Flush();
                    run.AppendChild(new TabChar());
                    break;
                case '\r':
                    break;
                default:
                    if (!char.IsControl(ch))
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        Flush();
    }

    // ----------------------------------------------------------------- footnotes

    public Footnotes BuildFootnotes(FootnotesPart part)
    {
        var footnotes = new Footnotes(
            new Footnote(new Paragraph(new ParaFormat { SpaceAfterPt = 0, LineHeight = 1 }.ToParagraphProperties(), new Run(new SeparatorMark())))
            { Type = FootnoteEndnoteValues.Separator, Id = -1 },
            new Footnote(new Paragraph(new ParaFormat { SpaceAfterPt = 0, LineHeight = 1 }.ToParagraphProperties(), new Run(new ContinuationSeparatorMark())))
            { Type = FootnoteEndnoteValues.ContinuationSeparator, Id = 0 });

        var ctx = Root(part) with { ParagraphStyle = DocxStyleIds.FootnoteText };
        foreach (var fn in _doc.Footnotes)
        {
            if (!_footnoteIds.TryGetValue(fn.Label, out var id) || footnotes.Elements<Footnote>().Any(f => f.Id?.Value == id))
            {
                continue;
            }

            var mark = new Run(new RunFormat { StyleId = DocxStyleIds.FootnoteReference }.ToRunProperties(), new FootnoteReferenceMark());
            var space = new Run(new Text(" ") { Space = SpaceProcessingModeValues.Preserve });
            var blocks = Blocks(fn.Blocks, ctx);
            if (blocks.FirstOrDefault() is Paragraph first)
            {
                var props = first.ParagraphProperties;
                if (props is null)
                {
                    first.PrependChild(mark);
                }
                else
                {
                    props.InsertAfterSelf(mark);
                }

                mark.InsertAfterSelf(space);
            }
            else
            {
                blocks.Insert(0, new Paragraph(new ParaFormat { StyleId = DocxStyleIds.FootnoteText }.ToParagraphProperties(), mark, space));
            }

            if (blocks[^1] is not Paragraph)
            {
                blocks.Add(new Paragraph(new ParaFormat { StyleId = DocxStyleIds.FootnoteText }.ToParagraphProperties()));
            }

            footnotes.AppendChild(new Footnote(blocks) { Id = id });
        }

        return footnotes;
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>Word bookmark names: letters, digits and underscores, starting with a letter, at most 40 characters.</summary>
    public string BookmarkName(string anchor)
    {
        if (_bookmarkNames.TryGetValue(anchor, out var existing))
        {
            return existing;
        }

        var sb = new StringBuilder();
        foreach (var ch in anchor)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        }

        var name = sb.Length > 0 && char.IsAsciiLetter(sb[0]) ? sb.ToString() : "h_" + sb;
        if (name.Length > 36)
        {
            name = name[..36];
        }

        var unique = name;
        var n = 2;
        while (!_usedBookmarks.Add(unique))
        {
            unique = name + "_" + n++.ToString(CultureInfo.InvariantCulture);
        }

        _bookmarkNames[anchor] = unique;
        return unique;
    }

    private void Warn(string message)
    {
        if (_warned.Add(message))
        {
            _warnings.Add(message);
        }
    }
}
