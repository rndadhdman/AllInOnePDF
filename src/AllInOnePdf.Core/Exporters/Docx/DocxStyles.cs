using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using TableStyle = AllInOnePdf.Core.Themes.TableStyle;
using ThemeFontWeight = AllInOnePdf.Core.Themes.FontWeight;

namespace AllInOnePdf.Core.Exporters.Docx;

/// <summary>
/// Word styles derived from a <see cref="PdfTheme"/>, mirroring the mapping of the PDF renderer: body text,
/// six heading levels, title page, quotes, code, callouts, links, captions, footnotes, lists, header/footer and a
/// table style per <see cref="TableStyle"/>.
/// </summary>
internal static class DocxStyles
{
    public static Styles Build(PdfTheme theme, int? headingNumId, string? language)
    {
        var t = theme;
        var body = t.Typography;
        var colors = t.Colors;
        var headingFont = t.Headings.Font ?? body.BodyFont;
        var styles = new Styles();

        styles.AppendChild(new DocDefaults(
            new RunPropertiesDefault(new RunFormat
            {
                Font = body.BodyFont,
                Color = colors.Text,
                SizePt = body.BodySizePt,
                Language = string.IsNullOrWhiteSpace(language) ? "en-US" : language,
            }.AppendTo(new RunPropertiesBaseStyle())),
            new ParagraphPropertiesDefault(new ParaFormat
            {
                SpaceAfterPt = body.ParagraphSpacingPt,
                LineHeight = body.LineHeight,
            }.AppendTo(new ParagraphPropertiesBaseStyle()))));

        // Normal: body font, size, line spacing, space after, alignment, first-line indent, colour.
        styles.AppendChild(Paragraph(DocxStyleIds.Normal, "Normal", null, null, primary: true, isDefault: true,
            new ParaFormat
            {
                SpaceBeforePt = 0,
                SpaceAfterPt = body.ParagraphSpacingPt,
                LineHeight = body.LineHeight,
                FirstLinePt = body.FirstLineIndentPt > 0 ? body.FirstLineIndentPt : null,
                Justification = body.Alignment switch
                {
                    TextAlign.Justify => JustificationValues.Both,
                    TextAlign.Center => JustificationValues.Center,
                    TextAlign.Right => JustificationValues.Right,
                    _ => null,
                },
                WidowControl = body.OrphanControl,
            },
            new RunFormat
            {
                Font = body.BodyFont,
                Color = colors.Text,
                SizePt = body.BodySizePt,
                SpacingPt = body.LetterSpacingEm * body.BodySizePt,
            }));

        styles.AppendChild(new Style(new StyleName { Val = "Default Paragraph Font" }, new UIPriority { Val = 1 }, new SemiHidden(), new UnhideWhenUsed())
        {
            Type = StyleValues.Character,
            StyleId = "DefaultParagraphFont",
            Default = true,
        });

        for (var level = 1; level <= 6; level++)
        {
            var h = t.Headings.Level(level);
            var bold = h.Weight is ThemeFontWeight.SemiBold or ThemeFontWeight.Bold;
            var para = new ParaFormat
            {
                KeepNext = t.Headings.KeepWithNext,
                KeepLines = true,
                PageBreakBefore = level == 1 && t.Headings.PageBreakBeforeH1,
                Numbering = t.Headings.NumberHeadings && headingNumId is { } hn ? (hn, level - 1) : null,
                SpaceBeforePt = h.SpaceBeforePt,
                SpaceAfterPt = h.SpaceAfterPt,
                LineHeight = t.Headings.LineHeight,
                FirstLinePt = 0,
                Justification = h.Alignment switch
                {
                    TextAlign.Center => JustificationValues.Center,
                    TextAlign.Right => JustificationValues.Right,
                    _ => JustificationValues.Left,
                },
                OutlineLevel = level - 1,
            };
            if (h.Underline)
            {
                para.BorderBottom = new BorderSpec(h.RuleColor ?? colors.Accent, Math.Max(0.25, h.RuleWidthPt), 3);
            }

            styles.AppendChild(Paragraph(DocxStyleIds.Heading(level), "heading " + level, DocxStyleIds.Normal, DocxStyleIds.Normal, primary: true, isDefault: false,
                para,
                new RunFormat
                {
                    Font = headingFont,
                    Bold = bold,
                    Italic = h.Italic,
                    Caps = h.Uppercase,
                    Color = h.Color ?? t.Headings.Color,
                    SizePt = h.SizePt,
                    SpacingPt = h.LetterSpacingEm * h.SizePt,
                }));
        }

        var cover = t.Cover;
        styles.AppendChild(Paragraph(DocxStyleIds.Title, "Title", DocxStyleIds.Normal, DocxStyleIds.Normal, primary: true, isDefault: false,
            new ParaFormat
            {
                SpaceBeforePt = 0,
                SpaceAfterPt = 12,
                LineHeight = 1.1,
                FirstLinePt = 0,
                Justification = cover.Style is CoverStyle.Centered ? JustificationValues.Center : JustificationValues.Left,
                BorderBottom = cover.AccentBar ? new BorderSpec(colors.Accent, 3, 6) : null,
            },
            new RunFormat { Font = headingFont, Bold = true, Color = cover.TitleColor ?? t.Headings.Color, SizePt = cover.TitleSizePt }));
        styles.AppendChild(Paragraph(DocxStyleIds.Subtitle, "Subtitle", DocxStyleIds.Normal, DocxStyleIds.Normal, primary: true, isDefault: false,
            new ParaFormat
            {
                SpaceAfterPt = 18,
                LineHeight = 1.2,
                FirstLinePt = 0,
                Justification = cover.Style is CoverStyle.Centered ? JustificationValues.Center : JustificationValues.Left,
            },
            new RunFormat { Font = headingFont, Color = colors.Muted, SizePt = Math.Max(body.BodySizePt * 1.5, 14) }));
        styles.AppendChild(Paragraph(DocxStyleIds.CoverMeta, "Cover Details", DocxStyleIds.Normal, DocxStyleIds.Normal, primary: false, isDefault: false,
            new ParaFormat
            {
                SpaceAfterPt = 4,
                FirstLinePt = 0,
                Justification = cover.Style is CoverStyle.Centered ? JustificationValues.Center : JustificationValues.Left,
            },
            new RunFormat { Color = colors.Muted, SizePt = body.BodySizePt * 1.1 }));

        // Quote: left border, shading, italic.
        var q = t.Quotes;
        styles.AppendChild(Paragraph(DocxStyleIds.Quote, "Quote", DocxStyleIds.Normal, DocxStyleIds.Quote, primary: true, isDefault: false,
            new ParaFormat
            {
                BorderLeft = q.BorderWidthPt > 0 ? new BorderSpec(q.BorderColor ?? colors.Accent, q.BorderWidthPt, q.PaddingPt) : null,
                Shading = q.Background,
                IndentLeftPt = q.PaddingPt + q.BorderWidthPt,
                IndentRightPt = q.Background is null ? 0 : q.PaddingPt,
                FirstLinePt = 0,
                Justification = JustificationValues.Left,
            },
            new RunFormat { Italic = q.Italic, Color = q.TextColor ?? colors.Text, SizePt = body.BodySizePt * q.FontScale }));
        styles.AppendChild(Paragraph(DocxStyleIds.QuoteAttribution, "Quote Attribution", DocxStyleIds.Quote, DocxStyleIds.Normal, primary: false, isDefault: false,
            new ParaFormat { Justification = JustificationValues.Right },
            new RunFormat { Italic = false, Color = colors.Muted, SizePt = body.BodySizePt * 0.9 }));

        // Code: mono font, shading, border; CodeChar for inline code.
        var c = t.Code;
        var codeBg = c.Background ?? colors.Surface;
        var codeBorder = c.BorderWidthPt > 0 ? new BorderSpec(c.BorderColor ?? colors.Border, c.BorderWidthPt, Math.Min(c.PaddingPt, 31)) : (BorderSpec?)null;
        var codePad = Math.Min(c.PaddingPt, 31);
        styles.AppendChild(Paragraph(DocxStyleIds.Code, "Code", DocxStyleIds.Normal, DocxStyleIds.Normal, primary: true, isDefault: false,
            new ParaFormat
            {
                BorderTop = codeBorder ?? new BorderSpec(codeBg, 0.25, codePad),
                BorderLeft = codeBorder ?? new BorderSpec(codeBg, 0.25, codePad),
                BorderBottom = codeBorder ?? new BorderSpec(codeBg, 0.25, codePad),
                BorderRight = codeBorder ?? new BorderSpec(codeBg, 0.25, codePad),
                Shading = codeBg,
                SpaceBeforePt = codePad,
                SpaceAfterPt = body.ParagraphSpacingPt + codePad,
                LineHeight = c.LineHeight,
                IndentLeftPt = codePad,
                IndentRightPt = codePad,
                FirstLinePt = 0,
                Justification = JustificationValues.Left,
                WidowControl = false,
            },
            new RunFormat { Font = c.Font, Color = c.TextColor ?? colors.Text, SizePt = c.SizePt, NoProof = true }));
        styles.AppendChild(Paragraph(DocxStyleIds.CodeLabel, "Code Label", DocxStyleIds.Normal, DocxStyleIds.Code, primary: false, isDefault: false,
            new ParaFormat { KeepNext = true, SpaceAfterPt = 2, FirstLinePt = 0, Justification = JustificationValues.Left },
            new RunFormat { Font = c.Font, Color = colors.Muted, SizePt = Math.Max(6, c.SizePt * 0.85), NoProof = true }));
        styles.AppendChild(Character(DocxStyleIds.CodeChar, "Code Char",
            new RunFormat
            {
                Font = c.Font,
                Color = c.InlineTextColor ?? c.TextColor ?? colors.Text,
                SizePt = body.BodySizePt * c.InlineScale,
                Shading = c.InlineBackground ?? c.Background ?? colors.Surface,
                NoProof = true,
            }));

        styles.AppendChild(Paragraph(DocxStyleIds.MathBlock, "Math Block", DocxStyleIds.Normal, DocxStyleIds.Normal, primary: false, isDefault: false,
            new ParaFormat { Justification = JustificationValues.Center, FirstLinePt = 0 },
            new RunFormat { Font = c.Font, SizePt = c.SizePt, NoProof = true }));
        styles.AppendChild(Character(DocxStyleIds.MathChar, "Math Char", new RunFormat { Font = c.Font, NoProof = true }));
        styles.AppendChild(Paragraph(DocxStyleIds.HtmlSource, "HTML Source", DocxStyleIds.Normal, DocxStyleIds.Normal, primary: false, isDefault: false,
            new ParaFormat { FirstLinePt = 0, Justification = JustificationValues.Left },
            new RunFormat { Font = c.Font, Color = colors.Muted, SizePt = c.SizePt, NoProof = true }));

        // Callouts: shaded, left border in the kind's accent, bold label run.
        var cs = t.Callouts;
        foreach (var kind in Enum.GetValues<CalloutKind>())
        {
            var k = cs.Kind(kind);
            var accent = k.Accent ?? colors.Accent;
            var pad = Math.Min(cs.PaddingPt, 31);
            styles.AppendChild(Paragraph(DocxStyleIds.Callout(kind), "Callout " + kind, DocxStyleIds.Normal, DocxStyleIds.Callout(kind), primary: false, isDefault: false,
                new ParaFormat
                {
                    BorderLeft = cs.BorderWidthPt > 0 ? new BorderSpec(accent, cs.BorderWidthPt, pad) : null,
                    Shading = k.Background ?? cs.Background ?? colors.Surface,
                    IndentLeftPt = pad + cs.BorderWidthPt,
                    IndentRightPt = pad,
                    FirstLinePt = 0,
                    Justification = JustificationValues.Left,
                },
                new RunFormat()));
        }

        styles.AppendChild(Character(DocxStyleIds.CalloutLabel, "Callout Label", new RunFormat { Bold = true, SizePt = body.BodySizePt * 0.95 }));

        var link = t.Links;
        styles.AppendChild(Character(DocxStyleIds.Hyperlink, "Hyperlink", new RunFormat
        {
            Bold = link.Weight is ThemeFontWeight.SemiBold or ThemeFontWeight.Bold ? true : null,
            Color = link.Color ?? colors.Accent,
            Underline = link.Underline,
        }, uiPriority: 99));

        styles.AppendChild(Paragraph(DocxStyleIds.FootnoteText, "footnote text", DocxStyleIds.Normal, DocxStyleIds.FootnoteText, primary: false, isDefault: false,
            new ParaFormat { SpaceAfterPt = 2, LineHeight = 1.2, FirstLinePt = 0, Justification = JustificationValues.Left },
            new RunFormat { SizePt = Math.Max(7, body.BodySizePt * 0.82) }));
        styles.AppendChild(Character(DocxStyleIds.FootnoteReference, "footnote reference", new RunFormat { Superscript = true }));

        var img = t.Images;
        styles.AppendChild(Paragraph(DocxStyleIds.Figure, "Figure", DocxStyleIds.Normal, DocxStyleIds.Normal, primary: false, isDefault: false,
            new ParaFormat
            {
                KeepNext = img.ShowCaptions,
                SpaceBeforePt = img.SpacingPt,
                SpaceAfterPt = img.SpacingPt,
                LineHeight = 1,
                FirstLinePt = 0,
                Justification = img.Align switch
                {
                    ImageAlign.Left => JustificationValues.Left,
                    ImageAlign.Right => JustificationValues.Right,
                    _ => JustificationValues.Center,
                },
            },
            new RunFormat()));
        styles.AppendChild(Paragraph(DocxStyleIds.Caption, "caption", DocxStyleIds.Normal, DocxStyleIds.Normal, primary: true, isDefault: false,
            new ParaFormat
            {
                SpaceAfterPt = Math.Max(body.ParagraphSpacingPt, 6),
                FirstLinePt = 0,
                Justification = img.Align switch
                {
                    ImageAlign.Left => JustificationValues.Left,
                    ImageAlign.Right => JustificationValues.Right,
                    _ => JustificationValues.Center,
                },
            },
            new RunFormat { Italic = true, Color = colors.Muted, SizePt = img.CaptionSizePt }));

        var lists = t.Lists;
        styles.AppendChild(Paragraph(DocxStyleIds.ListParagraph, "List Paragraph", DocxStyleIds.Normal, DocxStyleIds.ListParagraph, primary: true, isDefault: false,
            new ParaFormat { SpaceAfterPt = lists.ItemSpacingPt, FirstLinePt = 0, ContextualSpacing = false },
            new RunFormat()));
        styles.AppendChild(Paragraph(DocxStyleIds.ListContinue, "List Continue", DocxStyleIds.Normal, DocxStyleIds.ListContinue, primary: false, isDefault: false,
            new ParaFormat { SpaceAfterPt = lists.ItemSpacingPt, FirstLinePt = 0, IndentLeftPt = lists.IndentPt },
            new RunFormat()));

        styles.AppendChild(Paragraph(DocxStyleIds.HorizontalRule, "Horizontal Rule", DocxStyleIds.Normal, DocxStyleIds.Normal, primary: false, isDefault: false,
            new ParaFormat
            {
                BorderBottom = new BorderSpec(colors.Border, 0.75, 1),
                SpaceBeforePt = body.ParagraphSpacingPt,
                SpaceAfterPt = body.ParagraphSpacingPt * 2,
                LineHeight = 1,
                FirstLinePt = 0,
            },
            new RunFormat { SizePt = 4 }));

        var toc = t.Toc;
        styles.AppendChild(Paragraph(DocxStyleIds.TocHeading, "TOC Heading", DocxStyleIds.Heading(1), DocxStyleIds.Normal, primary: true, isDefault: false,
            new ParaFormat { PageBreakBefore = false, OutlineLevel = 9 },
            new RunFormat()));
        for (var level = 1; level <= 6; level++)
        {
            styles.AppendChild(Paragraph(DocxStyleIds.Toc(level), "toc " + level, DocxStyleIds.Normal, DocxStyleIds.Normal, primary: false, isDefault: false,
                new ParaFormat
                {
                    SpaceAfterPt = 3,
                    FirstLinePt = 0,
                    IndentLeftPt = toc.IndentLevels ? (level - 1) * 12 : 0,
                    Justification = JustificationValues.Left,
                },
                new RunFormat { Bold = level == 1 ? true : null }));
        }

        styles.AppendChild(HeaderFooterStyle(DocxStyleIds.Header, "header", t.Header, colors));
        styles.AppendChild(HeaderFooterStyle(DocxStyleIds.Footer, "footer", t.Footer, colors));

        var tableNormal = new Style { Type = StyleValues.Table, StyleId = "TableNormal", Default = true };
        tableNormal.Append(
            new StyleName { Val = "Normal Table" },
            new UIPriority { Val = 99 },
            new SemiHidden(),
            new UnhideWhenUsed(),
            new StyleTableProperties(
                new TableIndentation { Width = 0, Type = TableWidthUnitValues.Dxa },
                new TableCellMarginDefault(
                    new TopMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 108, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 108, Type = TableWidthValues.Dxa })));
        styles.AppendChild(tableNormal);
        styles.AppendChild(TableStyleFor(t));
        return styles;
    }

    private static Style Paragraph(string id, string name, string? basedOn, string? next, bool primary, bool isDefault, ParaFormat para, RunFormat run)
    {
        var style = new Style { Type = StyleValues.Paragraph, StyleId = id };
        if (isDefault)
        {
            style.Default = true;
        }

        style.AppendChild(new StyleName { Val = name });
        if (basedOn is not null)
        {
            style.AppendChild(new BasedOn { Val = basedOn });
        }

        if (next is not null)
        {
            style.AppendChild(new NextParagraphStyle { Val = next });
        }

        style.AppendChild(new UIPriority { Val = primary ? 10 : 20 });
        if (primary)
        {
            style.AppendChild(new PrimaryStyle());
        }

        style.AppendChild(para.AppendTo(new StyleParagraphProperties()));
        if (!run.IsEmpty)
        {
            style.AppendChild(run.AppendTo(new StyleRunProperties()));
        }

        return style;
    }

    private static Style Character(string id, string name, RunFormat run, int uiPriority = 20)
    {
        var style = new Style { Type = StyleValues.Character, StyleId = id };
        style.AppendChild(new StyleName { Val = name });
        style.AppendChild(new BasedOn { Val = "DefaultParagraphFont" });
        style.AppendChild(new UIPriority { Val = uiPriority });
        if (uiPriority == 99)
        {
            style.AppendChild(new UnhideWhenUsed());
        }

        style.AppendChild(run.AppendTo(new StyleRunProperties()));
        return style;
    }

    private static Style HeaderFooterStyle(string id, string name, HeaderFooterSettings hf, ColorSettings colors) =>
        Paragraph(id, name, DocxStyleIds.Normal, null, primary: false, isDefault: false,
            new ParaFormat { SpaceBeforePt = 0, SpaceAfterPt = 0, LineHeight = 1.15, FirstLinePt = 0, Justification = JustificationValues.Left },
            new RunFormat { Color = hf.Color ?? colors.Muted, SizePt = hf.SizePt });

    /// <summary>One table style per theme: borders per <see cref="TableStyle"/>, header row shading, stripes, padding and font scale.</summary>
    private static Style TableStyleFor(PdfTheme t)
    {
        var tb = t.Tables;
        var colors = t.Colors;
        var border = DocxUnits.Hex(tb.BorderColor ?? colors.Border, "D1D5DB");
        var size = DocxUnits.Eighths(tb.BorderWidthPt);
        var headerBg = tb.HeaderBackground ?? (tb.Style is TableStyle.Grid or TableStyle.Striped ? colors.Surface : null);
        var headerBold = tb.HeaderWeight is ThemeFontWeight.SemiBold or ThemeFontWeight.Bold;
        var pad = DocxUnits.Twips(tb.CellPaddingPt);

        T B<T>()
            where T : BorderType, new() => new() { Val = BorderValues.Single, Size = size, Space = 0, Color = border };

        var borders = new TableBorders();
        switch (tb.Style)
        {
            case TableStyle.Grid:
                borders.Append(B<TopBorder>(), B<LeftBorder>(), B<BottomBorder>(), B<RightBorder>(), B<InsideHorizontalBorder>(), B<InsideVerticalBorder>());
                break;
            case TableStyle.Lines:
                borders.Append(B<TopBorder>(), B<BottomBorder>(), B<InsideHorizontalBorder>());
                break;
            case TableStyle.Striped:
                borders.Append(B<BottomBorder>());
                break;
        }

        var tblPr = new StyleTableProperties(
            new TableStyleRowBandSize { Val = 1 },
            new TableStyleColumnBandSize { Val = 1 },
            new TableIndentation { Width = 0, Type = TableWidthUnitValues.Dxa });
        if (borders.HasChildren)
        {
            tblPr.AppendChild(borders);
        }

        tblPr.AppendChild(new TableCellMarginDefault(
            new TopMargin { Width = (pad / 2).ToString(System.Globalization.CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
            new TableCellLeftMargin { Width = (short)pad, Type = TableWidthValues.Dxa },
            new BottomMargin { Width = (pad / 2).ToString(System.Globalization.CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
            new TableCellRightMargin { Width = (short)pad, Type = TableWidthValues.Dxa }));

        var style = new Style { Type = StyleValues.Table, StyleId = DocxStyleIds.Table };
        style.AppendChild(new StyleName { Val = "AllInOne Table" });
        style.AppendChild(new BasedOn { Val = "TableNormal" });
        style.AppendChild(new UIPriority { Val = 39 });
        style.AppendChild(new ParaFormat { SpaceBeforePt = 0, SpaceAfterPt = 0, LineHeight = 1.2, FirstLinePt = 0, Justification = JustificationValues.Left }
            .AppendTo(new StyleParagraphProperties()));
        style.AppendChild(new RunFormat { SizePt = t.Typography.BodySizePt * tb.FontScale }.AppendTo(new StyleRunProperties()));
        style.AppendChild(tblPr);

        // Header row: shading, colour, weight; Minimal and Striped draw a rule under it.
        var header = new TableStyleProperties { Type = TableStyleOverrideValues.FirstRow };
        header.AppendChild(new RunFormat { Bold = headerBold, Color = tb.HeaderTextColor ?? colors.Text }.AppendTo(new RunPropertiesBaseStyle()));
        header.AppendChild(new TableStyleConditionalFormattingTableRowProperties(new TableHeader()));
        var headerCell = new TableStyleConditionalFormattingTableCellProperties();
        if (tb.Style is TableStyle.Minimal or TableStyle.Striped or TableStyle.Lines)
        {
            headerCell.AppendChild(new TableCellBorders(new BottomBorder { Val = BorderValues.Single, Size = Math.Max(size, 8u), Space = 0, Color = border }));
        }

        if (DocxUnits.Hex(headerBg) is { } hb)
        {
            headerCell.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = hb });
        }

        if (headerCell.HasChildren)
        {
            header.AppendChild(headerCell);
        }

        style.AppendChild(header);

        if (tb.Style is TableStyle.Striped)
        {
            var band = new TableStyleProperties { Type = TableStyleOverrideValues.Band1Horizontal };
            band.AppendChild(new TableStyleConditionalFormattingTableCellProperties(
                new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = DocxUnits.Hex(tb.StripeColor ?? colors.Surface, "F3F4F6") }));
            style.AppendChild(band);
        }

        return style;
    }
}
