using System.Globalization;
using System.Security;
using System.Text;
using AllInOnePdf.Core.Exporters.Docx;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace AllInOnePdf.Core.Exporters.Pptx;

/// <summary>
/// Builds the fixed parts of a deck: one slide master (background, title/body placeholders, text styles), a
/// "Title Slide" and a "Title Only" layout, the DrawingML theme (colour, font and format schemes from the
/// <see cref="PdfTheme"/>), presentation properties and table styles.
/// </summary>
internal sealed class PptxPackageBuilder
{
    public const uint MasterId = 2147483648U;
    private const string Ns = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
    private const string DefaultTableStyle = "{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}";

    private readonly PdfTheme _theme;
    private readonly PptxGeometry _geo;
    private readonly PptxSlideWriter _writer;

    public PptxPackageBuilder(PdfTheme theme, PptxGeometry geometry, PptxSlideWriter writer)
    {
        _theme = theme;
        _geo = geometry;
        _writer = writer;
    }

    public SlideMasterPart Master { get; private set; } = null!;

    public SlideLayoutPart TitleLayout { get; private set; } = null!;

    public SlideLayoutPart ContentLayout { get; private set; } = null!;

    public void Build(PresentationPart presentation)
    {
        Master = presentation.AddNewPart<SlideMasterPart>();
        var themePart = Master.AddNewPart<ThemePart>();
        Feed(themePart, ThemeXml());
        presentation.AddPart(themePart);

        TitleLayout = Master.AddNewPart<SlideLayoutPart>();
        Feed(TitleLayout, TitleLayoutXml());
        TitleLayout.AddPart(Master);

        ContentLayout = Master.AddNewPart<SlideLayoutPart>();
        Feed(ContentLayout, TitleOnlyLayoutXml());
        ContentLayout.AddPart(Master);

        Feed(Master, MasterXml(Master.GetIdOfPart(TitleLayout), Master.GetIdOfPart(ContentLayout)));

        presentation.AddNewPart<PresentationPropertiesPart>().PresentationProperties = new P.PresentationProperties();
        presentation.AddNewPart<TableStylesPart>().TableStyleList = new A.TableStyleList { Default = DefaultTableStyle };
    }

    public P.Presentation Presentation(PresentationPart presentation, IReadOnlyList<SlidePart> slides)
    {
        var slideIds = new P.SlideIdList();
        for (var i = 0; i < slides.Count; i++)
        {
            slideIds.Append(new P.SlideId { Id = (uint)(256 + i), RelationshipId = presentation.GetIdOfPart(slides[i]) });
        }

        var size = new P.SlideSize { Cx = (int)_geo.SlideWidth, Cy = (int)_geo.SlideHeight };
        if (_geo.SlideWidth == 9144000)
        {
            size.Type = P.SlideSizeValues.Screen4x3;
        }

        var defaultText = new P.DefaultTextStyle(
            new A.DefaultParagraphProperties(new A.DefaultRunProperties { Language = "en-US" }),
            new A.Level1ParagraphProperties(
                new A.DefaultRunProperties(
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Text1 }),
                    new A.LatinFont { Typeface = "+mn-lt" },
                    new A.EastAsianFont { Typeface = "+mn-ea" },
                    new A.ComplexScriptFont { Typeface = "+mn-cs" })
                { FontSize = 1800, Kerning = 1200 })
            { LeftMargin = 0, Alignment = A.TextAlignmentTypeValues.Left, DefaultTabSize = 914400, RightToLeft = false, EastAsianLineBreak = true, LatinLineBreak = false, Height = true });

        return new P.Presentation(
            new P.SlideMasterIdList(new P.SlideMasterId { Id = MasterId, RelationshipId = presentation.GetIdOfPart(Master) }),
            slideIds,
            size,
            new P.NotesSize { Cx = 6858000, Cy = 9144000 },
            defaultText)
        { SaveSubsetFonts = true };
    }

    private static void Feed(OpenXmlPart part, string xml)
    {
        using var stream = new MemoryStream(new UTF8Encoding(false).GetBytes(xml), writable: false);
        part.FeedData(stream);
    }

    private static string X(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private bool DarkBackground => DocxUnits.IsDark(_writer.BackgroundColor);

    private string ThemeXml()
    {
        var name = X(string.IsNullOrWhiteSpace(_theme.Name) ? "AllInOne" : _theme.Name);
        string dk1, lt1, dk2, lt2;
        if (DarkBackground)
        {
            dk1 = _writer.BackgroundColor;
            lt1 = _writer.TextColor;
            dk2 = _writer.SurfaceColor;
            lt2 = _writer.HeadingColor;
        }
        else
        {
            dk1 = _writer.TextColor;
            lt1 = _writer.BackgroundColor;
            dk2 = _writer.HeadingColor;
            lt2 = _writer.SurfaceColor;
        }

        var accents = new[]
        {
            _writer.AccentColor,
            _writer.MutedColor,
            DocxUnits.Hex(_theme.Callouts.Tip.Accent, "1E8E3E"),
            DocxUnits.Hex(_theme.Callouts.Important.Accent, "7C3AED"),
            DocxUnits.Hex(_theme.Callouts.Warning.Accent, "B45309"),
            DocxUnits.Hex(_theme.Callouts.Caution.Accent, "B91C1C"),
        };

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
          .Append("<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"").Append(name).Append("\"><a:themeElements>")
          .Append("<a:clrScheme name=\"").Append(name).Append("\">")
          .Append("<a:dk1><a:srgbClr val=\"").Append(dk1).Append("\"/></a:dk1>")
          .Append("<a:lt1><a:srgbClr val=\"").Append(lt1).Append("\"/></a:lt1>")
          .Append("<a:dk2><a:srgbClr val=\"").Append(dk2).Append("\"/></a:dk2>")
          .Append("<a:lt2><a:srgbClr val=\"").Append(lt2).Append("\"/></a:lt2>");
        for (var i = 0; i < accents.Length; i++)
        {
            sb.Append("<a:accent").Append(i + 1).Append("><a:srgbClr val=\"").Append(accents[i]).Append("\"/></a:accent").Append(i + 1).Append('>');
        }

        sb.Append("<a:hlink><a:srgbClr val=\"").Append(_writer.LinkColor).Append("\"/></a:hlink>")
          .Append("<a:folHlink><a:srgbClr val=\"").Append(_writer.MutedColor).Append("\"/></a:folHlink>")
          .Append("</a:clrScheme>")
          .Append("<a:fontScheme name=\"").Append(name).Append("\">")
          .Append("<a:majorFont><a:latin typeface=\"").Append(X(_writer.TitleFont)).Append("\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>")
          .Append("<a:minorFont><a:latin typeface=\"").Append(X(_writer.BodyFont)).Append("\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont>")
          .Append("</a:fontScheme>")
          .Append("<a:fmtScheme name=\"").Append(name).Append("\"><a:fillStyleLst>")
          .Append("<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>")
          .Append("<a:solidFill><a:schemeClr val=\"phClr\"><a:tint val=\"50000\"/></a:schemeClr></a:solidFill>")
          .Append("<a:solidFill><a:schemeClr val=\"phClr\"><a:shade val=\"80000\"/></a:schemeClr></a:solidFill>")
          .Append("</a:fillStyleLst><a:lnStyleLst>");
        foreach (var w in new[] { 6350, 12700, 19050 })
        {
            sb.Append("<a:ln w=\"").Append(w).Append("\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>");
        }

        sb.Append("</a:lnStyleLst><a:effectStyleLst>")
          .Append("<a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle>")
          .Append("</a:effectStyleLst><a:bgFillStyleLst>")
          .Append("<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>")
          .Append("<a:solidFill><a:schemeClr val=\"phClr\"><a:tint val=\"95000\"/></a:schemeClr></a:solidFill>")
          .Append("<a:solidFill><a:schemeClr val=\"phClr\"><a:shade val=\"90000\"/></a:schemeClr></a:solidFill>")
          .Append("</a:bgFillStyleLst></a:fmtScheme>")
          .Append("</a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>");
        return sb.ToString();
    }

    private static string GroupStart() =>
        "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>"
        + "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private static string Placeholder(int id, string name, string ph, long? x, long? y, long? cx, long? cy, string anchor, string prompt, bool bodyLevels = false)
    {
        var sb = new StringBuilder();
        sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"").Append(id).Append("\" name=\"").Append(name).Append("\"/>")
          .Append("<p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>").Append(ph).Append("</p:nvPr></p:nvSpPr>");
        if (x is not null)
        {
            sb.Append("<p:spPr><a:xfrm><a:off x=\"").Append(N(x.Value)).Append("\" y=\"").Append(N(y!.Value)).Append("\"/><a:ext cx=\"")
              .Append(N(cx!.Value)).Append("\" cy=\"").Append(N(cy!.Value)).Append("\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>");
        }
        else
        {
            sb.Append("<p:spPr/>");
        }

        sb.Append("<p:txBody><a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\" anchor=\"").Append(anchor).Append("\"><a:normAutofit/></a:bodyPr><a:lstStyle/>");
        if (bodyLevels)
        {
            var levels = new[] { "Click to edit Master text styles", "Second level", "Third level" };
            for (var i = 0; i < levels.Length; i++)
            {
                sb.Append("<a:p><a:pPr lvl=\"").Append(i).Append("\"/><a:r><a:rPr lang=\"en-US\"/><a:t>").Append(levels[i]).Append("</a:t></a:r></a:p>");
            }
        }
        else
        {
            sb.Append("<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>").Append(prompt).Append("</a:t></a:r><a:endParaRPr lang=\"en-US\"/></a:p>");
        }

        sb.Append("</p:txBody></p:sp>");
        return sb.ToString();
    }

    private string MasterXml(string titleLayoutRel, string contentLayoutRel)
    {
        var contentTop = PptxGeometry.ContentTop;
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
          .Append("<p:sldMaster ").Append(Ns).Append("><p:cSld>")
          .Append("<p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"").Append(_writer.BackgroundColor).Append("\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>")
          .Append("<p:spTree>").Append(GroupStart())
          .Append(Placeholder(2, "Title Placeholder 1", "<p:ph type=\"title\"/>", PptxGeometry.MarginX, PptxGeometry.TitleTop, _geo.ContentWidth, PptxGeometry.TitleHeight, "b", "Click to edit Master title style"))
          .Append(Placeholder(3, "Text Placeholder 2", "<p:ph type=\"body\" idx=\"1\"/>", PptxGeometry.MarginX, contentTop, _geo.ContentWidth, _geo.ContentHeight, "t", string.Empty, bodyLevels: true))
          .Append("</p:spTree></p:cSld>");

        sb.Append(DarkBackground
            ? "<p:clrMap bg1=\"dk1\" tx1=\"lt1\" bg2=\"dk2\" tx2=\"lt2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>"
            : "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>");

        sb.Append("<p:sldLayoutIdLst>")
          .Append("<p:sldLayoutId id=\"").Append(MasterId + 1).Append("\" r:id=\"").Append(titleLayoutRel).Append("\"/>")
          .Append("<p:sldLayoutId id=\"").Append(MasterId + 2).Append("\" r:id=\"").Append(contentLayoutRel).Append("\"/>")
          .Append("</p:sldLayoutIdLst>");

        var titleSize = (int)Math.Round(_writer.TitleSizePt * 100);
        var bodySize = (int)Math.Round(_geo.BodyPt * 100);
        sb.Append("<p:txStyles><p:titleStyle>")
          .Append("<a:lvl1pPr algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:lnSpc><a:spcPct val=\"90000\"/></a:lnSpc><a:spcBef><a:spcPct val=\"0\"/></a:spcBef><a:buNone/>")
          .Append("<a:defRPr sz=\"").Append(titleSize).Append("\" b=\"").Append(_writer.TitleBold ? '1' : '0').Append("\" kern=\"1200\"><a:solidFill><a:srgbClr val=\"").Append(_writer.HeadingColor).Append("\"/></a:solidFill>")
          .Append("<a:latin typeface=\"+mj-lt\"/><a:ea typeface=\"+mj-ea\"/><a:cs typeface=\"+mj-cs\"/></a:defRPr></a:lvl1pPr>")
          .Append("</p:titleStyle><p:bodyStyle>");
        var bullets = new[] { "•", "–", "▪" };
        for (var level = 0; level < 3; level++)
        {
            var marL = 342900 * (level + 1);
            sb.Append("<a:lvl").Append(level + 1).Append("pPr marL=\"").Append(marL).Append("\" indent=\"-342900\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\">")
              .Append("<a:spcBef><a:spcPts val=\"600\"/></a:spcBef><a:buClr><a:srgbClr val=\"").Append(_writer.BulletColor).Append("\"/></a:buClr>")
              .Append("<a:buFont typeface=\"Arial\"/><a:buChar char=\"").Append(bullets[level]).Append("\"/>")
              .Append("<a:defRPr sz=\"").Append(Math.Max(1000, bodySize - 200 * level)).Append("\" kern=\"1200\"><a:solidFill><a:srgbClr val=\"").Append(_writer.TextColor).Append("\"/></a:solidFill>")
              .Append("<a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl").Append(level + 1).Append("pPr>");
        }

        sb.Append("</p:bodyStyle><p:otherStyle><a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr>")
          .Append("<a:lvl1pPr marL=\"0\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:defRPr sz=\"1800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>")
          .Append("<a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr>")
          .Append("</p:otherStyle></p:txStyles></p:sldMaster>");
        return sb.ToString();
    }

    private string TitleLayoutXml()
    {
        var top = (long)(_geo.SlideHeight * 0.26);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
          .Append("<p:sldLayout ").Append(Ns).Append(" type=\"title\" preserve=\"1\"><p:cSld name=\"Title Slide\"><p:spTree>").Append(GroupStart())
          .Append(Placeholder(2, "Title 1", "<p:ph type=\"ctrTitle\"/>", PptxGeometry.MarginX, top, _geo.ContentWidth, 1554480, "b", "Click to edit Master title style"))
          .Append(Placeholder(3, "Subtitle 2", "<p:ph type=\"subTitle\" idx=\"1\"/>", PptxGeometry.MarginX, top + 1783080, _geo.ContentWidth, 1371600, "t", "Click to edit Master subtitle style"))
          .Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        return sb.ToString();
    }

    private static string TitleOnlyLayoutXml()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
          .Append("<p:sldLayout ").Append(Ns).Append(" type=\"titleOnly\" preserve=\"1\"><p:cSld name=\"Title Only\"><p:spTree>").Append(GroupStart())
          .Append(Placeholder(2, "Title 1", "<p:ph type=\"title\"/>", null, null, null, null, "b", "Click to edit Master title style"))
          .Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        return sb.ToString();
    }
}
