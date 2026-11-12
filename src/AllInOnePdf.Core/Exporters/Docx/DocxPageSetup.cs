using System.Globalization;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Document = AllInOnePdf.Core.Documents.Document;
using Text = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace AllInOnePdf.Core.Exporters.Docx;

/// <summary>Page geometry, header/footer parts, cover page, table of contents field, settings and font table.</summary>
internal static partial class DocxPageSetup
{
    public static (double WidthPt, double HeightPt) ContentSize(PdfTheme theme)
    {
        var (w, h) = theme.Page.GetSizeMm();
        var p = theme.Page;
        return (DocxUnits.MmToPt(w - p.MarginLeftMm - p.MarginRightMm), DocxUnits.MmToPt(h - p.MarginTopMm - p.MarginBottomMm));
    }

    public static SectionProperties Section(MainDocumentPart main, PdfTheme theme, Document doc, string fallbackTitle, bool cover)
    {
        var sect = new SectionProperties();
        var titlePage = (cover && !theme.Header.ShowOnCover) || !theme.Header.ShowOnFirstPage
            || (cover && !theme.Footer.ShowOnCover) || !theme.Footer.ShowOnFirstPage;

        if (theme.Header.HasContent)
        {
            var part = main.AddNewPart<HeaderPart>();
            part.Header = new Header(HeaderFooterParagraph(theme, theme.Header, doc, fallbackTitle, isHeader: true));
            sect.AppendChild(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(part) });
            if (titlePage)
            {
                var first = main.AddNewPart<HeaderPart>();
                var showFirst = theme.Header.ShowOnFirstPage && !(cover && !theme.Header.ShowOnCover);
                first.Header = new Header(showFirst
                    ? HeaderFooterParagraph(theme, theme.Header, doc, fallbackTitle, isHeader: true)
                    : new Paragraph(new ParaFormat { StyleId = DocxStyleIds.Header }.ToParagraphProperties()));
                sect.AppendChild(new HeaderReference { Type = HeaderFooterValues.First, Id = main.GetIdOfPart(first) });
            }
        }

        if (theme.Footer.HasContent)
        {
            var part = main.AddNewPart<FooterPart>();
            part.Footer = new Footer(HeaderFooterParagraph(theme, theme.Footer, doc, fallbackTitle, isHeader: false));
            sect.AppendChild(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(part) });
            if (titlePage)
            {
                var first = main.AddNewPart<FooterPart>();
                var showFirst = theme.Footer.ShowOnFirstPage && !(cover && !theme.Footer.ShowOnCover);
                first.Footer = new Footer(showFirst
                    ? HeaderFooterParagraph(theme, theme.Footer, doc, fallbackTitle, isHeader: false)
                    : new Paragraph(new ParaFormat { StyleId = DocxStyleIds.Footer }.ToParagraphProperties()));
                sect.AppendChild(new FooterReference { Type = HeaderFooterValues.First, Id = main.GetIdOfPart(first) });
            }
        }

        var (wMm, hMm) = theme.Page.GetSizeMm();
        var page = theme.Page;
        var landscape = wMm > hMm;
        var size = new PageSize { Width = DocxUnits.MmToTwips(wMm), Height = DocxUnits.MmToTwips(hMm) };
        if (landscape)
        {
            size.Orient = PageOrientationValues.Landscape;
        }

        sect.AppendChild(size);
        sect.AppendChild(new PageMargin
        {
            Top = (int)DocxUnits.MmToTwips(page.MarginTopMm),
            Bottom = (int)DocxUnits.MmToTwips(page.MarginBottomMm),
            Left = DocxUnits.MmToTwips(page.MarginLeftMm),
            Right = DocxUnits.MmToTwips(page.MarginRightMm),
            Header = DocxUnits.MmToTwips(Math.Max(5, page.MarginTopMm / 2)),
            Footer = DocxUnits.MmToTwips(Math.Max(5, page.MarginBottomMm / 2)),
            Gutter = 0,
        });
        if (titlePage && (theme.Header.HasContent || theme.Footer.HasContent))
        {
            sect.AppendChild(new TitlePage());
        }

        return sect;
    }

    /// <summary>Left, centre and right templates on one line, separated by centre and right tab stops.</summary>
    private static Paragraph HeaderFooterParagraph(PdfTheme theme, HeaderFooterSettings hf, Document doc, string fallbackTitle, bool isHeader)
    {
        var width = ContentSize(theme).WidthPt;
        var format = new ParaFormat { StyleId = isHeader ? DocxStyleIds.Header : DocxStyleIds.Footer };
        format.Tabs.Add((width / 2, TabStopValues.Center));
        format.Tabs.Add((width, TabStopValues.Right));
        if (hf.Rule)
        {
            var rule = new BorderSpec(theme.Colors.Border, 0.5, Math.Min(hf.SpacingPt / 2, 8));
            if (isHeader)
            {
                format.BorderBottom = rule;
            }
            else
            {
                format.BorderTop = rule;
            }
        }

        var p = new Paragraph(format.ToParagraphProperties());
        var parts = new[] { hf.Left, hf.Center, hf.Right };
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0 && (!string.IsNullOrWhiteSpace(parts[i]) || parts.Skip(i + 1).Any(s => !string.IsNullOrWhiteSpace(s))))
            {
                p.AppendChild(new Run(new TabChar()));
            }

            if (!string.IsNullOrWhiteSpace(parts[i]))
            {
                p.Append(Template(parts[i]!, doc, fallbackTitle));
            }
        }

        return p;
    }

    /// <summary>{page} → PAGE, {pages} → NUMPAGES, {section} → STYLEREF "Heading 1"; the rest are filled in as text.</summary>
    private static IEnumerable<OpenXmlElement> Template(string template, Document doc, string fallbackTitle)
    {
        var last = 0;
        foreach (Match m in Placeholder().Matches(template))
        {
            if (m.Index > last)
            {
                yield return TextRun(template[last..m.Index]);
            }

            last = m.Index + m.Length;
            var key = m.Groups[1].Value.ToLowerInvariant();
            switch (key)
            {
                case "page":
                    yield return Field(" PAGE ", "1");
                    break;
                case "pages":
                    yield return Field(" NUMPAGES ", "1");
                    break;
                case "section":
                    yield return Field(" STYLEREF \"Heading 1\" ", doc.Headings.FirstOrDefault()?.ToPlainText() ?? string.Empty);
                    break;
                case "title":
                    yield return TextRun(doc.ResolveTitle() ?? fallbackTitle);
                    break;
                case "subtitle":
                    yield return TextRun(doc.Metadata.Subtitle ?? string.Empty);
                    break;
                case "author":
                    yield return TextRun(string.Join(", ", doc.Metadata.Authors));
                    break;
                case "date":
                    yield return TextRun(DocumentDates.Short(doc.Metadata.Date ?? DateTimeOffset.Now, doc.Metadata.Language));
                    break;
                case "filename":
                    yield return TextRun(Path.GetFileName(doc.Metadata.SourcePath ?? string.Empty));
                    break;
                default:
                    yield return TextRun(m.Value);
                    break;
            }
        }

        if (last < template.Length)
        {
            yield return TextRun(template[last..]);
        }
    }

    private static Run TextRun(string text) => new(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static SimpleField Field(string instruction, string placeholder) =>
        new(new Run(new RunProperties(new NoProof()), new Text(placeholder) { Space = SpaceProcessingModeValues.Preserve })) { Instruction = instruction };

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    /// <summary>Title, subtitle, author, date and note, then a page break; wrapped in a tagged content control so importers can recognise it.</summary>
    public static SdtBlock Cover(DocxWriter writer, PdfTheme theme, Document doc, string title)
    {
        var cover = theme.Cover;
        var content = new SdtContentBlock();
        var (_, heightPt) = ContentSize(theme);
        var top = cover.Style switch
        {
            CoverStyle.Minimal => heightPt * 0.05,
            CoverStyle.Banner or CoverStyle.Bold => heightPt * 0.12,
            _ => heightPt * 0.28,
        };

        if (cover.ShowTitle)
        {
            content.AppendChild(new Paragraph(new ParaFormat { StyleId = DocxStyleIds.Title, SpaceBeforePt = top }.ToParagraphProperties(), TextRun(title)));
        }

        if (cover.ShowSubtitle && !string.IsNullOrWhiteSpace(doc.Metadata.Subtitle))
        {
            content.AppendChild(new Paragraph(new ParaFormat { StyleId = DocxStyleIds.Subtitle }.ToParagraphProperties(), TextRun(doc.Metadata.Subtitle)));
        }

        if (cover.ShowAuthor && doc.Metadata.Authors.Count > 0)
        {
            content.AppendChild(new Paragraph(new ParaFormat { StyleId = DocxStyleIds.CoverMeta }.ToParagraphProperties(), TextRun(string.Join(", ", doc.Metadata.Authors))));
        }

        if (cover.ShowDate)
        {
            content.AppendChild(new Paragraph(new ParaFormat { StyleId = DocxStyleIds.CoverMeta }.ToParagraphProperties(),
                TextRun(DocumentDates.Long(doc.Metadata.Date ?? DateTimeOffset.Now, doc.Metadata.Language))));
        }

        if (!string.IsNullOrWhiteSpace(cover.Note))
        {
            content.AppendChild(new Paragraph(new ParaFormat { StyleId = DocxStyleIds.CoverMeta, SpaceBeforePt = 24 }.ToParagraphProperties(), TextRun(cover.Note)));
        }

        content.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
        return writer.Sdt(DocxStyleIds.CoverTag, content);
    }

    /// <summary>A TOC field inside a "Table of Contents" content control; Word fills it in when fields are updated on open.</summary>
    public static SdtBlock TableOfContents(PdfTheme theme)
    {
        var toc = theme.Toc;
        var depth = Math.Clamp(toc.Depth, 1, 6).ToString(CultureInfo.InvariantCulture);
        var instruction = $" TOC \\o \"1-{depth}\" \\h \\z \\u ";
        if (!toc.ShowPageNumbers)
        {
            instruction = $" TOC \\o \"1-{depth}\" \\h \\z \\u \\n ";
        }

        var content = new SdtContentBlock(
            new Paragraph(new ParaFormat { StyleId = DocxStyleIds.TocHeading }.ToParagraphProperties(), TextRun(toc.Title)),
            new Paragraph(
                new ParaFormat { StyleId = DocxStyleIds.Toc(1) }.ToParagraphProperties(),
                new Run(new FieldChar { FieldCharType = FieldCharValues.Begin, Dirty = true }),
                new Run(new FieldCode(instruction) { Space = SpaceProcessingModeValues.Preserve }),
                new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
                new Run(new RunProperties(new NoProof()), new Text("Update the field (F9) to build the table of contents.")),
                new Run(new FieldChar { FieldCharType = FieldCharValues.End })));
        if (toc.PageBreakAfter)
        {
            content.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
        }

        return new SdtBlock(
            new SdtProperties(
                new SdtId { Val = 1 },
                new SdtContentDocPartObject(new DocPartGallery { Val = "Table of Contents" }, new DocPartUnique())),
            content);
    }

    public static Settings Settings(PdfTheme theme, bool updateFields)
    {
        var settings = new Settings();
        if (DocxUnits.Hex(theme.Page.Background) is { } bg && bg != "FFFFFF")
        {
            settings.AppendChild(new DisplayBackgroundShape());
        }

        settings.AppendChild(new DefaultTabStop { Val = 720 });
        settings.AppendChild(new CharacterSpacingControl { Val = CharacterSpacingValues.DoNotCompress });
        if (updateFields)
        {
            settings.AppendChild(new UpdateFieldsOnOpen { Val = true });
        }

        settings.AppendChild(new Compatibility(new CompatibilitySetting
        {
            Name = CompatSettingNameValues.CompatibilityMode,
            Uri = "http://schemas.microsoft.com/office/word",
            Val = "15",
        }));
        return settings;
    }

    /// <summary>Font table entries with family hints, so Word substitutes a similar face when a bundled font is not installed.</summary>
    public static DocumentFormat.OpenXml.Wordprocessing.Fonts FontTable(PdfTheme theme)
    {
        var fonts = new DocumentFormat.OpenXml.Wordprocessing.Fonts();
        var names = new[] { theme.Typography.BodyFont, theme.Headings.Font ?? theme.Typography.BodyFont, theme.Code.Font }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var mono = name.Contains("mono", StringComparison.OrdinalIgnoreCase) || name.Contains("code", StringComparison.OrdinalIgnoreCase)
                || name.Contains("courier", StringComparison.OrdinalIgnoreCase) || name.Contains("consolas", StringComparison.OrdinalIgnoreCase);
            var serif = !mono && (name.Contains("serif", StringComparison.OrdinalIgnoreCase) && !name.Contains("sans", StringComparison.OrdinalIgnoreCase)
                || name is "Literata" or "Georgia" or "Times New Roman" or "Cambria" or "Merriweather" or "Garamond");
            fonts.AppendChild(new Font(
                new AltName { Val = mono ? "Consolas" : serif ? "Cambria" : "Calibri" },
                new FontCharSet { Val = "00" },
                new FontFamily { Val = mono ? FontFamilyValues.Modern : serif ? FontFamilyValues.Roman : FontFamilyValues.Swiss },
                new Pitch { Val = mono ? FontPitchValues.Fixed : FontPitchValues.Variable })
            { Name = name });
        }

        return fonts;
    }
}
