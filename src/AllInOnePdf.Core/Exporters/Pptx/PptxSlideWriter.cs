using System.Globalization;
using System.Text;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters.Docx;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace AllInOnePdf.Core.Exporters.Pptx;

/// <summary>Writes one planned slide (title, text boxes, code panels, tables, pictures) with theme colours and fonts.</summary>
internal sealed class PptxSlideWriter
{
    public const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";
    private const long IndentStep = 342900;
    private const long MinTextHeight = 457200;

    private static readonly HashSet<string> SlideImageTypes = new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif", "image/bmp" };

    private readonly PdfTheme _theme;
    private readonly PptxGeometry _geo;
    private readonly Document _document;
    private readonly List<string> _warnings;
    private readonly Dictionary<string, ImagePart> _imageParts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    public PptxSlideWriter(PdfTheme theme, PptxGeometry geometry, Document document, List<string> warnings)
    {
        _theme = theme;
        _geo = geometry;
        _document = document;
        _warnings = warnings;

        TextColor = DocxUnits.Hex(theme.Colors.Text, "1F2937");
        HeadingColor = DocxUnits.Hex(theme.Headings.Color, TextColor);
        AccentColor = DocxUnits.Hex(theme.Colors.Accent, "0F6CBD");
        MutedColor = DocxUnits.Hex(theme.Colors.Muted, "6B7280");
        SurfaceColor = DocxUnits.Hex(theme.Colors.Surface, "F3F4F6");
        BorderColor = DocxUnits.Hex(theme.Colors.Border, "D1D5DB");
        BackgroundColor = DocxUnits.Hex(theme.Page.Background, "FFFFFF");
        LinkColor = DocxUnits.Hex(theme.Links.Color ?? theme.Colors.Accent, AccentColor);
        BulletColor = DocxUnits.Hex(theme.Lists.MarkerColor ?? theme.Colors.Accent, AccentColor);
        CodeBackground = DocxUnits.Hex(theme.Code.Background ?? theme.Colors.Surface, SurfaceColor);
        CodeTextColor = DocxUnits.Hex(theme.Code.TextColor ?? theme.Colors.Text, TextColor);
        CodeBorder = DocxUnits.Hex(theme.Code.BorderColor ?? theme.Colors.Border, BorderColor);
        HeaderFill = DocxUnits.Hex(theme.Tables.HeaderBackground ?? theme.Colors.Surface, SurfaceColor);
        HeaderTextColor = DocxUnits.Hex(theme.Tables.HeaderTextColor ?? theme.Colors.Text, TextColor);
        TableBorder = DocxUnits.Hex(theme.Tables.BorderColor ?? theme.Colors.Border, BorderColor);
        TitleFont = string.IsNullOrWhiteSpace(theme.Headings.Font) ? BodyFont : theme.Headings.Font.Trim();
        TitleBold = theme.Headings.H1.Weight >= FontWeight.SemiBold;
        TitleSizePt = Math.Clamp(theme.Headings.H1.SizePt * 1.4, 32, 40);
    }

    public string TextColor { get; }
    public string HeadingColor { get; }
    public string AccentColor { get; }
    public string MutedColor { get; }
    public string SurfaceColor { get; }
    public string BorderColor { get; }
    public string BackgroundColor { get; }
    public string LinkColor { get; }
    public string BulletColor { get; }
    public string CodeBackground { get; }
    public string CodeTextColor { get; }
    public string CodeBorder { get; }
    public string HeaderFill { get; }
    public string HeaderTextColor { get; }
    public string TableBorder { get; }
    public string BodyFont => string.IsNullOrWhiteSpace(_theme.Typography.BodyFont) ? "Calibri" : _theme.Typography.BodyFont.Trim();
    public string TitleFont { get; }
    public string CodeFont => string.IsNullOrWhiteSpace(_theme.Code.Font) ? "Consolas" : _theme.Code.Font.Trim();
    public bool TitleBold { get; }
    public double TitleSizePt { get; }

    private sealed class SlideState(SlidePart part)
    {
        private uint _nextId = 2;

        public SlidePart Part { get; } = part;

        public Dictionary<string, string> Links { get; } = new(StringComparer.Ordinal);

        public uint NextId() => _nextId++;
    }

    private readonly record struct RunFormat(double SizePt, string Color, string Font, InlineStyle Style, string? LinkId, bool Code);

    public P.Slide Write(SlidePart part, PptxSlidePlan plan)
    {
        var state = new SlideState(part);
        var tree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup(
                new A.Offset { X = 0L, Y = 0L },
                new A.Extents { Cx = 0L, Cy = 0L },
                new A.ChildOffset { X = 0L, Y = 0L },
                new A.ChildExtents { Cx = 0L, Cy = 0L })));

        if (plan.IsTitleSlide)
        {
            WriteTitleSlide(state, tree, plan);
        }
        else
        {
            tree.Append(TitleShape(state, plan.Title));
            WriteItems(state, tree, plan.Items);
        }

        return new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping()));
    }

    private void WriteTitleSlide(SlideState state, P.ShapeTree tree, PptxSlidePlan plan)
    {
        var top = (long)(_geo.SlideHeight * 0.26);
        const long titleHeight = 1554480;
        var titleSize = plan.Title.Length > 40 ? 32 : Math.Clamp(_theme.Cover.TitleSizePt, 32, 54);
        var titleColor = DocxUnits.Hex(_theme.Cover.TitleColor, HeadingColor);
        var title = new A.Paragraph(new A.ParagraphProperties(new A.NoBullet()) { Alignment = A.TextAlignmentTypeValues.Center });
        AppendText(title, plan.Title, new RunFormat(titleSize, titleColor, TitleFont, TitleBold ? InlineStyle.Bold : InlineStyle.None, null, false));
        tree.Append(PlaceholderShape(state, "Title 1", P.PlaceholderValues.CenteredTitle, null,
            PptxGeometry.MarginX, top, _geo.ContentWidth, titleHeight, A.TextAnchoringTypeValues.Bottom, [title]));

        var y = top + titleHeight + 91440;
        if (_theme.Cover.AccentBar)
        {
            const long barWidth = 1371600;
            tree.Append(Rectangle(state, "Accent bar", (_geo.SlideWidth - barWidth) / 2, y, barWidth, 45720, AccentColor));
            y += 137160;
        }

        if (plan.SubtitleLines.Count > 0)
        {
            var paragraphs = plan.SubtitleLines.Select((line, i) =>
            {
                var p = new A.Paragraph(new A.ParagraphProperties(new A.NoBullet()) { Alignment = A.TextAlignmentTypeValues.Center });
                AppendText(p, line, new RunFormat(i == 0 ? _geo.BodyPt + 2 : _geo.BodyPt, i == 0 ? TextColor : MutedColor, BodyFont, InlineStyle.None, null, false));
                return p;
            }).ToList();
            tree.Append(PlaceholderShape(state, "Subtitle 2", P.PlaceholderValues.SubTitle, 1U,
                PptxGeometry.MarginX, y, _geo.ContentWidth, Math.Max(MinTextHeight, _geo.ContentBottom - y - 457200), A.TextAnchoringTypeValues.Top, paragraphs));
        }
    }

    private P.Shape TitleShape(SlideState state, string title)
    {
        var size = title.Length > 40 ? 28 : TitleSizePt;
        var p = new A.Paragraph(new A.ParagraphProperties(new A.NoBullet()));
        AppendText(p, title, new RunFormat(size, HeadingColor, TitleFont, TitleBold ? InlineStyle.Bold : InlineStyle.None, null, false));
        return PlaceholderShape(state, "Title 1", P.PlaceholderValues.Title, null,
            PptxGeometry.MarginX, PptxGeometry.TitleTop, _geo.ContentWidth, PptxGeometry.TitleHeight, A.TextAnchoringTypeValues.Bottom, [p]);
    }

    private void WriteItems(SlideState state, P.ShapeTree tree, List<PptxSlideItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var available = _geo.ContentHeight;
        var heights = items.Select(Estimate).ToList();
        var gaps = PptxGeometry.Gap * (items.Count - 1);
        var hasImage = items.Any(i => i is PptxImageItem);
        var fixedSum = heights.Where(h => h is not null).Sum(h => h!.Value) + gaps;
        var room = available - (hasImage ? 1371600 : 0);
        if (fixedSum > room && fixedSum > 0)
        {
            var scale = (double)Math.Max(room - gaps, 1) / Math.Max(fixedSum - gaps, 1);
            heights = heights.Select(h => h is null ? (long?)null : Math.Max(228600, (long)(h.Value * scale))).ToList();
            fixedSum = heights.Where(h => h is not null).Sum(h => h!.Value) + gaps;
        }

        var imageHeight = Math.Max(1371600, available - fixedSum);
        var y = PptxGeometry.ContentTop;
        for (var i = 0; i < items.Count; i++)
        {
            var height = heights[i] ?? imageHeight;
            if (i == items.Count - 1 && items[i] is PptxTextItem && !hasImage)
            {
                height = Math.Max(height, _geo.ContentBottom - y);
            }

            switch (items[i])
            {
                case PptxTextItem text:
                    tree.Append(TextBox(state, PptxGeometry.MarginX, y, _geo.ContentWidth, height, Paragraphs(state, text.Paragraphs)));
                    break;
                case PptxCodeItem code:
                    tree.Append(CodeShape(state, y, height, code.Code));
                    break;
                case PptxTableItem table:
                    if (TableFrame(state, y, height, table.Table) is { } frame)
                    {
                        tree.Append(frame);
                    }

                    break;
                case PptxImageItem image:
                    foreach (var element in Picture(state, y, height, image.Image))
                    {
                        tree.Append(element);
                    }

                    break;
            }

            y += height + PptxGeometry.Gap;
        }
    }

    private long? Estimate(PptxSlideItem item) => item switch
    {
        PptxTextItem t => Math.Max(MinTextHeight, (long)(t.Lines * _geo.LineEmu) + 91440),
        PptxCodeItem c => Math.Max(MinTextHeight, (long)(c.Lines * _geo.LineEmu)),
        PptxTableItem t => Math.Max(MinTextHeight / 2, (long)(t.Lines * _geo.LineEmu)),
        _ => null,
    };

    // ---- shapes ---------------------------------------------------------------------------------------------

    private static A.Transform2D Transform(long x, long y, long cx, long cy) =>
        new(new A.Offset { X = x, Y = y }, new A.Extents { Cx = Math.Max(1, cx), Cy = Math.Max(1, cy) });

    private static A.PresetGeometry Rect() => new(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle };

    private static A.SolidFill Fill(string hex) => new(new A.RgbColorModelHex { Val = hex });

    private static P.Shape PlaceholderShape(SlideState state, string name, P.PlaceholderValues type, uint? index, long x, long y, long cx, long cy, A.TextAnchoringTypeValues anchor, IEnumerable<A.Paragraph> paragraphs)
    {
        var ph = new P.PlaceholderShape { Type = type };
        if (index is { } idx)
        {
            ph.Index = idx;
        }

        var body = new P.TextBody(
            new A.BodyProperties(new A.NormalAutoFit()) { Wrap = A.TextWrappingValues.Square, Anchor = anchor },
            new A.ListStyle());
        body.Append(paragraphs);
        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = state.NextId(), Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties(ph)),
            new P.ShapeProperties(Transform(x, y, cx, cy)),
            body);
    }

    private static P.Shape TextBox(SlideState state, long x, long y, long cx, long cy, IEnumerable<A.Paragraph> paragraphs, string? fill = null, string? outline = null, string namePrefix = "TextBox")
    {
        var id = state.NextId();
        var spPr = new P.ShapeProperties(Transform(x, y, cx, cy), Rect());
        if (fill is null)
        {
            spPr.Append(new A.NoFill());
        }
        else
        {
            spPr.Append(Fill(fill));
        }

        if (outline is not null)
        {
            spPr.Append(new A.Outline(Fill(outline)) { Width = 9525 });
        }

        var bodyPr = new A.BodyProperties(new A.NormalAutoFit()) { Wrap = A.TextWrappingValues.Square, RightToLeftColumns = false, Anchor = A.TextAnchoringTypeValues.Top };
        if (fill is not null)
        {
            bodyPr.LeftInset = 137160;
            bodyPr.RightInset = 137160;
            bodyPr.TopInset = 91440;
            bodyPr.BottomInset = 91440;
        }

        var body = new P.TextBody(bodyPr, new A.ListStyle());
        body.Append(paragraphs);
        if (!body.Elements<A.Paragraph>().Any())
        {
            body.Append(new A.Paragraph());
        }

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = namePrefix + " " + id.ToString(CultureInfo.InvariantCulture) },
                new P.NonVisualShapeDrawingProperties { TextBox = true },
                new P.ApplicationNonVisualDrawingProperties()),
            spPr,
            body);
    }

    private static P.Shape Rectangle(SlideState state, string name, long x, long y, long cx, long cy, string fill)
    {
        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = state.NextId(), Name = name },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(Transform(x, y, cx, cy), Rect(), Fill(fill), new A.Outline(new A.NoFill())));
    }

    private P.Shape CodeShape(SlideState state, long y, long height, string code)
    {
        var paragraphs = new List<A.Paragraph>();
        foreach (var line in code.Split('\n'))
        {
            var p = new A.Paragraph(new A.ParagraphProperties(new A.NoBullet()));
            var text = line.TrimEnd('\r').Replace("\t", "    ", StringComparison.Ordinal);
            AppendText(p, text, new RunFormat(_geo.CodePt, CodeTextColor, CodeFont, InlineStyle.None, null, true));
            p.Append(EndRun(_geo.CodePt));
            paragraphs.Add(p);
        }

        return TextBox(state, PptxGeometry.MarginX, y, _geo.ContentWidth, height, paragraphs, CodeBackground, CodeBorder, "Code");
    }

    // ---- text -----------------------------------------------------------------------------------------------

    private List<A.Paragraph> Paragraphs(SlideState state, IEnumerable<PptxPara> paras)
    {
        var result = new List<A.Paragraph>();
        foreach (var para in paras)
        {
            result.Add(Paragraph(state, para));
        }

        return result;
    }

    private A.Paragraph Paragraph(SlideState state, PptxPara para)
    {
        // In-slide headings: 4 pt above the body for level 2, one point less per deeper level (at least 2 pt), so
        // the PowerPoint importer can tell the levels apart again.
        var size = para.Kind == PptxParaKind.Subheading ? _geo.BodyPt + Math.Clamp(6 - para.HeadingLevel, 2, 4) : _geo.BodyPt;
        var color = para.Muted ? MutedColor : para.Kind == PptxParaKind.Subheading ? HeadingColor : TextColor;
        var font = para.Monospace ? CodeFont : para.Kind == PptxParaKind.Subheading ? TitleFont : BodyFont;
        var style = para.Extra | (para.Kind == PptxParaKind.Subheading ? InlineStyle.Bold : InlineStyle.None);
        var level = Math.Clamp(para.Level, 0, 8);

        var pPr = new A.ParagraphProperties();
        pPr.Append(new A.SpaceBefore(new A.SpacingPoints { Val = para.Kind == PptxParaKind.Subheading ? 1200 : 600 }));
        switch (para.Kind)
        {
            case PptxParaKind.Bullet:
                pPr.LeftMargin = (int)(IndentStep * (level + 1));
                pPr.Indent = (int)-IndentStep;
                pPr.Level = level;
                pPr.Append(new A.BulletColor(new A.RgbColorModelHex { Val = BulletColor }));
                pPr.Append(new A.BulletFont { Typeface = "Arial" });
                pPr.Append(new A.CharacterBullet { Char = BulletChar(level) });
                break;
            case PptxParaKind.Numbered:
                pPr.LeftMargin = (int)(IndentStep * (level + 1));
                pPr.Indent = (int)-IndentStep;
                pPr.Level = level;
                pPr.Append(new A.BulletColor(new A.RgbColorModelHex { Val = BulletColor }));
                var auto = new A.AutoNumberedBullet { Type = NumberScheme() };
                if (para.Number != 1)
                {
                    auto.StartAt = Math.Clamp(para.Number, 1, 32767);
                }

                pPr.Append(auto);
                break;
            case PptxParaKind.Continuation:
                pPr.LeftMargin = (int)(IndentStep * (level + 1));
                pPr.Indent = 0;
                pPr.Level = level;
                pPr.Append(new A.NoBullet());
                break;
            default:
                pPr.LeftMargin = 0;
                pPr.Indent = 0;
                pPr.Append(new A.NoBullet());
                break;
        }

        if (para.Alignment is { } align && para.Kind is PptxParaKind.Plain)
        {
            pPr.Alignment = align switch
            {
                TextAlignment.Center => A.TextAlignmentTypeValues.Center,
                TextAlignment.Right => A.TextAlignmentTypeValues.Right,
                TextAlignment.Justify => A.TextAlignmentTypeValues.Justified,
                _ => A.TextAlignmentTypeValues.Left,
            };
        }

        var p = new A.Paragraph(pPr);
        var format = new RunFormat(size, color, font, style, null, para.Monospace);
        if (!string.IsNullOrEmpty(para.Prefix))
        {
            var prefixStyle = para.Kind is PptxParaKind.Bullet or PptxParaKind.Numbered ? style : style | InlineStyle.Bold;
            AppendText(p, para.Prefix, format with { Style = prefixStyle });
        }

        AppendInlines(state, p, para.Inlines, format);
        p.Append(EndRun(size));
        return p;
    }

    private static A.EndParagraphRunProperties EndRun(double sizePt) => new() { Language = "en-US", FontSize = Hundredths(sizePt), Dirty = false };

    private string BulletChar(int level)
    {
        var first = _theme.Lists.Bullet switch
        {
            BulletStyle.Circle => "◦",
            BulletStyle.Square => "▪",
            BulletStyle.Dash => "–",
            BulletStyle.Arrow => "›",
            _ => "•",
        };
        return (level % 3) switch
        {
            0 => first,
            1 => first == "–" ? "•" : "–",
            _ => first == "▪" ? "•" : "▪",
        };
    }

    private A.TextAutoNumberSchemeValues NumberScheme() => _theme.Lists.Numbering switch
    {
        NumberStyle.DecimalParen => A.TextAutoNumberSchemeValues.ArabicParenR,
        NumberStyle.LowerAlpha => A.TextAutoNumberSchemeValues.AlphaLowerCharacterPeriod,
        NumberStyle.UpperAlpha => A.TextAutoNumberSchemeValues.AlphaUpperCharacterPeriod,
        NumberStyle.LowerRoman => A.TextAutoNumberSchemeValues.RomanLowerCharacterPeriod,
        NumberStyle.UpperRoman => A.TextAutoNumberSchemeValues.RomanUpperCharacterPeriod,
        _ => A.TextAutoNumberSchemeValues.ArabicPeriod,
    };

    private void AppendInlines(SlideState state, A.Paragraph p, IEnumerable<Inline> inlines, RunFormat format)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline t:
                    AppendText(p, t.Text, format);
                    break;
                case StyledInline s:
                    AppendInlines(state, p, s.Inlines, format with { Style = format.Style | s.Style });
                    break;
                case CodeInline c:
                    AppendText(p, c.Code, format with { Font = CodeFont, Code = true });
                    break;
                case LinkInline l:
                    var id = LinkId(state, l.Url);
                    var linkFormat = id is null ? format : format with
                    {
                        LinkId = id,
                        Color = LinkColor,
                        Style = _theme.Links.Underline ? format.Style | InlineStyle.Underline : format.Style,
                    };
                    AppendInlines(state, p, l.Inlines.Count > 0 ? l.Inlines : [new TextInline(l.Url)], linkFormat);
                    break;
                case LineBreakInline { IsHard: true }:
                    p.Append(new A.Break(RunProperties(format)));
                    break;
                case LineBreakInline:
                    AppendText(p, " ", format);
                    break;
                case ImageInline img:
                    if (!string.IsNullOrWhiteSpace(img.AltText))
                    {
                        AppendText(p, img.AltText, format);
                    }

                    break;
                case FootnoteReferenceInline f:
                    AppendText(p, "[" + f.Label + "]", format with { Style = format.Style | InlineStyle.Superscript });
                    break;
                case MathInline m:
                    AppendText(p, m.Latex, format);
                    break;
                case HtmlInline:
                    break;
                default:
                    AppendInlines(state, p, inline.Children, format);
                    break;
            }
        }
    }

    private static string? LinkId(SlideState state, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#'))
        {
            return null;
        }

        var trimmed = url.Trim();
        if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!state.Links.TryGetValue(trimmed, out var id))
        {
            id = state.Part.AddHyperlinkRelationship(uri, true).Id;
            state.Links[trimmed] = id;
        }

        return id;
    }

    private void AppendText(A.Paragraph p, string? text, RunFormat format)
    {
        var clean = Sanitize(text);
        if (clean.Length == 0)
        {
            return;
        }

        p.Append(new A.Run(RunProperties(format), new A.Text(clean)));
    }

    private A.RunProperties RunProperties(RunFormat format)
    {
        var rPr = new A.RunProperties { Language = "en-US", FontSize = Hundredths(format.SizePt), Dirty = false };
        var style = format.Style;
        if (style.HasFlag(InlineStyle.Bold))
        {
            rPr.Bold = true;
        }

        if (style.HasFlag(InlineStyle.Italic))
        {
            rPr.Italic = true;
        }

        if (style.HasFlag(InlineStyle.Underline))
        {
            rPr.Underline = A.TextUnderlineValues.Single;
        }

        if (style.HasFlag(InlineStyle.Strikethrough))
        {
            rPr.Strike = A.TextStrikeValues.SingleStrike;
        }

        if (style.HasFlag(InlineStyle.Superscript))
        {
            rPr.Baseline = 30000;
        }
        else if (style.HasFlag(InlineStyle.Subscript))
        {
            rPr.Baseline = -25000;
        }

        if (style.HasFlag(InlineStyle.SmallCaps))
        {
            rPr.Capital = A.TextCapsValues.Small;
        }

        if (style.HasFlag(InlineStyle.Highlight))
        {
            rPr.Append(Fill(format.Color));
            rPr.Append(new A.Highlight(new A.RgbColorModelHex { Val = "FFF59D" }));
        }
        else
        {
            rPr.Append(Fill(format.Code && format.LinkId is null ? CodeTextColor : format.Color));
        }

        rPr.Append(new A.LatinFont { Typeface = format.Code ? CodeFont : format.Font });
        if (format.LinkId is not null)
        {
            rPr.Append(new A.HyperlinkOnClick { Id = format.LinkId });
        }

        return rPr;
    }

    private static int Hundredths(double pt) => (int)Math.Clamp(Math.Round(pt * 100), 100, 400000);

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '\v')
            {
                sb.Append(' ');
            }
            else if (c == '\t' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF && !char.IsSurrogate(c)))
            {
                sb.Append(c);
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[i + 1]);
                i++;
            }
        }

        return sb.ToString();
    }

    // ---- tables ---------------------------------------------------------------------------------------------

    private P.GraphicFrame? TableFrame(SlideState state, long y, long height, TableBlock table)
    {
        var grid = PptxTableGrid.Layout(table);
        if (grid.Columns == 0 || grid.Rows == 0)
        {
            return null;
        }

        var width = _geo.ContentWidth;
        var colWidth = width / grid.Columns;
        var tblGrid = new A.TableGrid();
        for (var c = 0; c < grid.Columns; c++)
        {
            tblGrid.Append(new A.GridColumn { Width = c == grid.Columns - 1 ? width - colWidth * (grid.Columns - 1) : colWidth });
        }

        var hasHeader = table.Rows[0].IsHeader;
        var tblPr = new A.TableProperties { BandRow = true };
        if (hasHeader)
        {
            tblPr.FirstRow = true;
        }

        var tbl = new A.Table(tblPr, tblGrid);
        var rowHeight = Math.Max(228600, height / grid.Rows);
        for (var r = 0; r < grid.Rows; r++)
        {
            var isHeader = table.Rows[r].IsHeader;
            var tr = new A.TableRow { Height = rowHeight };
            for (var c = 0; c < grid.Columns; c++)
            {
                var owner = grid.Owner(r, c);
                A.TableCell tc;
                if (owner is { } o && o.Row == r && o.Col == c)
                {
                    tc = Cell(state, o.Cell.Blocks, isHeader, table.GetAlignment(c));
                    var colSpan = grid.ColumnSpan(r, c);
                    var rowSpan = grid.RowSpan(r, c);
                    if (colSpan > 1)
                    {
                        tc.GridSpan = colSpan;
                    }

                    if (rowSpan > 1)
                    {
                        tc.RowSpan = rowSpan;
                    }
                }
                else
                {
                    tc = Cell(state, [], isHeader, ColumnAlignment.Default);
                    if (owner is { } covered)
                    {
                        if (covered.Col != c)
                        {
                            tc.HorizontalMerge = true;
                        }

                        if (covered.Row != r)
                        {
                            tc.VerticalMerge = true;
                        }
                    }
                }

                tr.Append(tc);
            }

            tbl.Append(tr);
        }

        var id = state.NextId();
        return new P.GraphicFrame(
            new P.NonVisualGraphicFrameProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Table " + id.ToString(CultureInfo.InvariantCulture) },
                new P.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.Transform(new A.Offset { X = PptxGeometry.MarginX, Y = y }, new A.Extents { Cx = width, Cy = rowHeight * grid.Rows }),
            new A.Graphic(new A.GraphicData(tbl) { Uri = TableUri }));
    }

    private A.TableCell Cell(SlideState state, IEnumerable<Block> blocks, bool header, ColumnAlignment alignment)
    {
        var size = _geo.TablePt;
        var color = header ? HeaderTextColor : TextColor;
        var style = header && _theme.Tables.HeaderWeight >= FontWeight.SemiBold ? InlineStyle.Bold : InlineStyle.None;
        var format = new RunFormat(size, color, BodyFont, style, null, false);
        var body = new A.TextBody(new A.BodyProperties(), new A.ListStyle());

        A.Paragraph NewParagraph()
        {
            var pPr = new A.ParagraphProperties(new A.NoBullet());
            if (alignment is ColumnAlignment.Center or ColumnAlignment.Right)
            {
                pPr.Alignment = alignment == ColumnAlignment.Center ? A.TextAlignmentTypeValues.Center : A.TextAlignmentTypeValues.Right;
            }

            return new A.Paragraph(pPr);
        }

        foreach (var block in blocks)
        {
            switch (block)
            {
                case InlineContainerBlock icb:
                {
                    var p = NewParagraph();
                    AppendInlines(state, p, icb.Inlines, format);
                    p.Append(EndRun(size));
                    body.Append(p);
                    break;
                }

                case CodeBlock code:
                    foreach (var line in code.Code.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n'))
                    {
                        var p = NewParagraph();
                        AppendText(p, line, format with { Code = true });
                        p.Append(EndRun(size));
                        body.Append(p);
                    }

                    break;
                default:
                    foreach (var inner in Block.EnumerateRecursive([block]).OfType<InlineContainerBlock>())
                    {
                        var p = NewParagraph();
                        AppendInlines(state, p, inner.Inlines, format);
                        p.Append(EndRun(size));
                        body.Append(p);
                    }

                    break;
            }
        }

        if (!body.Elements<A.Paragraph>().Any())
        {
            var empty = NewParagraph();
            empty.Append(EndRun(size));
            body.Append(empty);
        }

        var border = TableBorder;
        var tcPr = new A.TableCellProperties(
            new A.LeftBorderLineProperties(Fill(border)) { Width = 9525 },
            new A.RightBorderLineProperties(Fill(border)) { Width = 9525 },
            new A.TopBorderLineProperties(Fill(border)) { Width = 9525 },
            new A.BottomBorderLineProperties(Fill(border)) { Width = 9525 });
        tcPr.Append(header ? Fill(HeaderFill) : new A.NoFill());
        return new A.TableCell(body, tcPr);
    }

    // ---- pictures -------------------------------------------------------------------------------------------

    /// <summary>True when the picture is embedded in a format slides can show; otherwise warns (once per reason).</summary>
    public bool CanPlace(ImageBlock image) => Resolve(image) is not null;

    private (DocumentResource Resource, string MediaType)? Resolve(ImageBlock image)
    {
        var resource = _document.FindResource(image.Source);
        if (resource is null)
        {
            Warn("Pictures that are not embedded in the document (links to files or web addresses) were left out.");
            return null;
        }

        var mediaType = resource.MediaType.ToLowerInvariant() switch
        {
            "image/jpg" or "image/pjpeg" => "image/jpeg",
            "image/x-png" => "image/png",
            var other => other,
        };
        if (!SlideImageTypes.Contains(mediaType))
        {
            Warn($"Pictures of type {mediaType} cannot be placed on slides and were left out.");
            return null;
        }

        return (resource, mediaType);
    }

    private List<OpenXmlElement> Picture(SlideState state, long y, long height, ImageBlock image)
    {
        var result = new List<OpenXmlElement>();
        var resolved = Resolve(image);
        if (resolved is null)
        {
            return result;
        }

        var (resource, mediaType) = resolved.Value;

        if (!_imageParts.TryGetValue(resource.Id, out var part))
        {
            part = state.Part.AddImagePart(mediaType);
            using (var stream = new MemoryStream(resource.Data, writable: false))
            {
                part.FeedData(stream);
            }

            _imageParts[resource.Id] = part;
        }
        else if (!state.Part.Parts.Any(p => ReferenceEquals(p.OpenXmlPart, part)))
        {
            state.Part.AddPart(part);
        }

        var relId = state.Part.GetIdOfPart(part);

        var caption = _theme.Images.ShowCaptions ? image.ResolveCaption() : null;
        var captionHeight = caption is null ? 0 : Math.Min(548640, (long)(_geo.CaptionPt * 2.2 * 12700));
        var boxHeight = Math.Max(457200, height - captionHeight);
        var size = image.PixelWidth is > 0 && image.PixelHeight is > 0
            ? (image.PixelWidth.Value, image.PixelHeight.Value)
            : ImageDimensions.Read(resource.Data) ?? (640, 480);
        double w = Math.Max(1, size.Item1) * DocxUnits.EmuPerPixel;
        double h = Math.Max(1, size.Item2) * DocxUnits.EmuPerPixel;
        var boxWidth = (double)_geo.ContentWidth * Math.Clamp(_theme.Images.MaxWidthPercent, 5, 100) / 100;
        double scale;
        if (image.WidthPercent is > 0 and var percent)
        {
            var target = _geo.ContentWidth * Math.Min(100, percent) / 100;
            scale = Math.Min(target / w, boxHeight / h);
        }
        else
        {
            var fit = Math.Min(boxWidth / w, boxHeight / h);
            var minWidth = Math.Min(boxWidth, 1828800);
            scale = Math.Min(fit, Math.Max(1, minWidth / w));
        }

        var cx = Math.Max(1, (long)(w * scale));
        var cy = Math.Max(1, (long)(h * scale));
        var x = _theme.Images.Align switch
        {
            ImageAlign.Left => PptxGeometry.MarginX,
            ImageAlign.Right => PptxGeometry.MarginX + _geo.ContentWidth - cx,
            _ => (_geo.SlideWidth - cx) / 2,
        };

        var id = state.NextId();
        var nvPr = new P.NonVisualDrawingProperties { Id = id, Name = resource.FileName ?? "Picture " + id.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(image.AltText))
        {
            nvPr.Description = image.AltText;
        }

        if (!string.IsNullOrWhiteSpace(image.Title))
        {
            nvPr.Title = image.Title;
        }

        result.Add(new P.Picture(
            new P.NonVisualPictureProperties(
                nvPr,
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(Transform(x, y, cx, cy), Rect())));

        if (caption is not null)
        {
            var p = new A.Paragraph(new A.ParagraphProperties(new A.NoBullet()) { Alignment = A.TextAlignmentTypeValues.Center });
            AppendText(p, caption, new RunFormat(_geo.CaptionPt, MutedColor, BodyFont, InlineStyle.Italic, null, false));
            result.Add(TextBox(state, PptxGeometry.MarginX, y + cy + 45720, _geo.ContentWidth, Math.Max(274320, captionHeight), [p], namePrefix: "Caption"));
        }

        return result;
    }

    private void Warn(string message)
    {
        if (_warned.Add(message))
        {
            _warnings.Add(message);
        }
    }
}
