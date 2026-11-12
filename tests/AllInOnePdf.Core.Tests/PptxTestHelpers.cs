using System.Globalization;
using System.Security;
using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace AllInOnePdf.Core.Tests;

/// <summary>PowerPoint export/import shortcuts, validation and a small deck builder for importer tests.</summary>
internal static class PptxTestHelpers
{
    public const string Ns = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
    public const long Inch = 914400;

    public static async Task<byte[]> ExportAsync(Document document, PdfTheme? theme = null, params (string Key, object Value)[] options)
    {
        var (bytes, _) = await ExportWithWarningsAsync(document, theme, options);
        return bytes;
    }

    public static async Task<(byte[] Bytes, List<string> Warnings)> ExportWithWarningsAsync(Document document, PdfTheme? theme = null, params (string Key, object Value)[] options)
    {
        var exporter = new PptxExporter();
        var values = new OptionValues();
        foreach (var (key, value) in options)
        {
            values.Set(key, value);
        }

        var result = await exporter.ExportAsync(new ExportContext
        {
            Document = document,
            BaseName = "deck",
            Theme = theme,
            Options = values.WithDefaults(exporter.Options),
        });
        Assert.Equal("deck.pptx", result.Primary!.FileName);
        return (result.Primary.Content, result.Warnings);
    }

    public static async Task<Document> ImportAsync(byte[] pptx, Action<OptionValues>? configure = null, List<string>? warnings = null)
    {
        var importer = new PptxImporter();
        var options = OptionValues.FromDefaults(importer.Options);
        configure?.Invoke(options);
        using var stream = new MemoryStream(pptx);
        var context = new ImportContext { Input = stream, FileName = "test.pptx", Options = options };
        var doc = await importer.ImportAsync(context);
        warnings?.AddRange(context.Warnings);
        return doc;
    }

    /// <summary>Schema and semantic errors reported by the Open XML validator (Office 2019 rules).</summary>
    public static List<string> Validate(byte[] pptx)
    {
        using var stream = new MemoryStream(pptx);
        using var package = PresentationDocument.Open(stream, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Office2019);
        return validator.Validate(package)
            .Select(e => $"{e.ErrorType} {e.Part?.Uri} {e.Path?.XPath}: {e.Description}")
            .ToList();
    }

    public static string X(string text) => SecurityElement.Escape(text) ?? string.Empty;

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    // ---- shape XML snippets ------------------------------------------------------------------------------

    public static string Run(string text, string attrs = "", string inner = "") =>
        $"<a:r><a:rPr lang=\"en-US\" {attrs}>{inner}</a:rPr><a:t>{X(text)}</a:t></a:r>";

    public static string Para(string runs, string pPr = "") => $"<a:p>{(pPr.Length > 0 ? pPr : string.Empty)}{runs}</a:p>";

    public static string P(string text, int level = 0, string bullet = "", string attrs = "") =>
        Para(Run(text), level > 0 || bullet.Length > 0 || attrs.Length > 0 ? $"<a:pPr lvl=\"{level}\" {attrs}>{bullet}</a:pPr>" : string.Empty);

    private static string Xfrm(long x, long y, long cx, long cy) =>
        $"<a:xfrm><a:off x=\"{N(x)}\" y=\"{N(y)}\"/><a:ext cx=\"{N(cx)}\" cy=\"{N(cy)}\"/></a:xfrm>";

    public static string Title(int id, string text, string type = "title") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Title {id}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"{type}\"/></p:nvPr></p:nvSpPr><p:spPr/>"
        + $"<p:txBody><a:bodyPr/><a:lstStyle/>{P(text)}</p:txBody></p:sp>";

    /// <summary>A body placeholder (idx 1) without its own position: it inherits the layout's.</summary>
    public static string Body(int id, string paragraphs, string ph = "<p:ph idx=\"1\"/>") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Content {id}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{ph}</p:nvPr></p:nvSpPr><p:spPr/>"
        + $"<p:txBody><a:bodyPr/><a:lstStyle/>{paragraphs}</p:txBody></p:sp>";

    public static string TextBox(int id, long y, string paragraphs, long x = Inch / 2, long cx = 8 * Inch, long cy = Inch) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"TextBox {id}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr>{Xfrm(x, y, cx, cy)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>"
        + $"<p:txBody><a:bodyPr wrap=\"square\"/><a:lstStyle/>{paragraphs}</p:txBody></p:sp>";

    /// <summary>A picture referencing the slide's image (relationship id token <c>{img}</c>).</summary>
    public static string Picture(int id, long cx, long cy = Inch, long y = 2 * Inch, string descr = "A red square") =>
        $"<p:pic><p:nvPicPr><p:cNvPr id=\"{id}\" name=\"Picture {id}\" descr=\"{X(descr)}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>"
        + $"<p:blipFill><a:blip r:embed=\"{{img}}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr>{Xfrm(Inch / 2, y, cx, cy)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>";

    public static string Table(int id, string rows, string grid, bool firstRow = true) =>
        $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Table {id}\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>"
        + $"<p:xfrm><a:off x=\"457200\" y=\"1600200\"/><a:ext cx=\"8229600\" cy=\"1111250\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">"
        + $"<a:tbl><a:tblPr{(firstRow ? " firstRow=\"1\"" : string.Empty)} bandRow=\"1\"/><a:tblGrid>{grid}</a:tblGrid>{rows}</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";

    public static string Cell(string text, string attrs = "", string runAttrs = "") =>
        $"<a:tc {attrs}><a:txBody><a:bodyPr/><a:lstStyle/>{(text.Length == 0 ? "<a:p/>" : Para(Run(text, runAttrs)))}</a:txBody><a:tcPr/></a:tc>";

    public static string Chart(int id) =>
        $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Chart {id}\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
        + "<p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"100\" cy=\"100\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"/></a:graphic></p:graphicFrame>";

    // ---- deck builder ---------------------------------------------------------------------------------------

    internal sealed class TestSlide
    {
        public string Shapes { get; init; } = string.Empty;
        public bool Hidden { get; init; }
        public byte[]? Image { get; init; }
        public string ImageType { get; init; } = "image/png";
        public List<string> Links { get; init; } = [];
        public string? Notes { get; init; }
    }

    /// <summary>Builds a minimal presentation (theme, master, one layout with title/body placeholders) with the given slides.</summary>
    public static byte[] BuildDeck(IEnumerable<TestSlide> slides, Action<PresentationDocument>? configure = null, long slideWidth = 12192000)
    {
        using var ms = new MemoryStream();
        using (var package = PresentationDocument.Create(ms, PresentationDocumentType.Presentation))
        {
            var pp = package.AddPresentationPart();
            var master = pp.AddNewPart<SlideMasterPart>();
            var theme = master.AddNewPart<ThemePart>();
            Feed(theme, ThemeXml);
            pp.AddPart(theme);
            var layout = master.AddNewPart<SlideLayoutPart>();
            Feed(layout, LayoutXml);
            layout.AddPart(master);
            Feed(master, MasterXml(master.GetIdOfPart(layout)));

            var ids = new StringBuilder();
            var n = 0;
            foreach (var slide in slides)
            {
                var part = pp.AddNewPart<SlidePart>();
                part.AddPart(layout);
                var shapes = slide.Shapes;
                if (slide.Image is not null)
                {
                    var image = part.AddImagePart(slide.ImageType);
                    using (var s = new MemoryStream(slide.Image))
                    {
                        image.FeedData(s);
                    }

                    shapes = shapes.Replace("{img}", part.GetIdOfPart(image), StringComparison.Ordinal);
                }

                for (var i = 0; i < slide.Links.Count; i++)
                {
                    var rel = part.AddHyperlinkRelationship(new Uri(slide.Links[i], UriKind.RelativeOrAbsolute), true);
                    shapes = shapes.Replace("{link" + i.ToString(CultureInfo.InvariantCulture) + "}", rel.Id, StringComparison.Ordinal);
                }

                Feed(part, $"<p:sld {Ns}{(slide.Hidden ? " show=\"0\"" : string.Empty)}><p:cSld><p:spTree>{Group}{shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");

                if (slide.Notes is not null)
                {
                    var notes = part.AddNewPart<NotesSlidePart>();
                    Feed(notes, $"<p:notes {Ns}><p:cSld><p:spTree>{Group}"
                        + "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"sldImg\"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>"
                        + $"<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>{slide.Notes}</p:txBody></p:sp>"
                        + "<p:sp><p:nvSpPr><p:cNvPr id=\"4\" name=\"Number\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"sldNum\" idx=\"5\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>7</a:t></a:r></a:p></p:txBody></p:sp>"
                        + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>");
                    notes.AddPart(part);
                }

                ids.Append("<p:sldId id=\"").Append(256 + n).Append("\" r:id=\"").Append(pp.GetIdOfPart(part)).Append("\"/>");
                n++;
            }

            Feed(pp, $"<p:presentation {Ns}><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"{pp.GetIdOfPart(master)}\"/></p:sldMasterIdLst>"
                + $"<p:sldIdLst>{ids}</p:sldIdLst><p:sldSz cx=\"{N(slideWidth)}\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
            configure?.Invoke(package);
        }

        return ms.ToArray();
    }

    public static byte[] BuildDeck(params TestSlide[] slides) => BuildDeck(slides, null);

    private const string Group =
        "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private static void Feed(OpenXmlPart part, string xml)
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        part.FeedData(s);
    }

    private static string MasterXml(string layoutRel) =>
        $"<p:sldMaster {Ns}><p:cSld><p:spTree>{Group}"
        + "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"457200\" y=\"274638\"/><a:ext cx=\"8229600\" cy=\"1143000\"/></a:xfrm></p:spPr></p:sp>"
        + "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Body\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"457200\" y=\"1600200\"/><a:ext cx=\"8229600\" cy=\"4525963\"/></a:xfrm></p:spPr></p:sp>"
        + "</p:spTree></p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>"
        + $"<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"{layoutRel}\"/></p:sldLayoutIdLst></p:sldMaster>";

    private const string LayoutXml =
        "<p:sldLayout " + Ns + " type=\"obj\"><p:cSld name=\"Title and Content\"><p:spTree>" + Group
        + "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>"
        + "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Content\"/><p:cNvSpPr/><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"457200\" y=\"1600200\"/><a:ext cx=\"8229600\" cy=\"4525963\"/></a:xfrm></p:spPr></p:sp>"
        + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private const string ThemeXml =
        "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Test\"><a:themeElements>"
        + "<a:clrScheme name=\"Test\"><a:dk1><a:srgbClr val=\"000000\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"1F497D\"/></a:dk2><a:lt2><a:srgbClr val=\"EEECE1\"/></a:lt2>"
        + "<a:accent1><a:srgbClr val=\"4F81BD\"/></a:accent1><a:accent2><a:srgbClr val=\"C0504D\"/></a:accent2><a:accent3><a:srgbClr val=\"9BBB59\"/></a:accent3><a:accent4><a:srgbClr val=\"8064A2\"/></a:accent4>"
        + "<a:accent5><a:srgbClr val=\"4BACC6\"/></a:accent5><a:accent6><a:srgbClr val=\"F79646\"/></a:accent6><a:hlink><a:srgbClr val=\"0000FF\"/></a:hlink><a:folHlink><a:srgbClr val=\"800080\"/></a:folHlink></a:clrScheme>"
        + "<a:fontScheme name=\"Test\"><a:majorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>"
        + "<a:fmtScheme name=\"Test\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>"
        + "<a:lnStyleLst><a:ln w=\"9525\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"25400\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"38100\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>"
        + "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>"
        + "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme>"
        + "</a:themeElements></a:theme>";

    /// <summary>Plain texts of headings in document order.</summary>
    public static List<string> HeadingTexts(Document doc) => doc.Headings.Select(h => Collapse(h.ToPlainText())).ToList();

    public static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
