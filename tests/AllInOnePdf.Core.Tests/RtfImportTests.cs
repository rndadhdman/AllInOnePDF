using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static AllInOnePdf.Core.Tests.MarkdownExportTestHelpers;

namespace AllInOnePdf.Core.Tests;

/// <summary>Import of hand-written RTF: Word, WordPad and older writers' constructs, encodings and damaged input.</summary>
public class RtfImportTests
{
    private const string Header = @"{\rtf1\ansi\ansicpg1252\deff0\nouicompat\deflang2057{\fonttbl{\f0\fnil\fcharset0 Calibri;}{\f1\fnil\fcharset2 Symbol;}{\f2\fmodern\fcharset0 Courier New;}}" + "\r\n"
        + @"{\colortbl ;\red255\green0\blue0;}" + "\r\n"
        + @"{\*\generator Riched20 10.0.19041}\viewkind4\uc1" + "\r\n";

    private static async Task<Document> ImportAsync(string rtf, Action<OptionValues>? configure = null)
    {
        var importer = new RtfImporter();
        var options = OptionValues.FromDefaults(importer.Options);
        configure?.Invoke(options);
        using var stream = new MemoryStream(Encoding.Latin1.GetBytes(rtf));
        return await importer.ImportAsync(new ImportContext { Input = stream, FileName = "test.rtf", Options = options });
    }

    private static Task<Document> BodyAsync(string body, Action<OptionValues>? configure = null) =>
        ImportAsync(Header + body + "\r\n}", configure);

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Fact]
    public void Declares_the_documented_options_with_defaults()
    {
        var importer = new RtfImporter();
        Assert.Equal("rtf", importer.Format.Id);
        Assert.Equal(ConverterStatus.Ready, importer.Status);
        var defaults = OptionValues.FromDefaults(importer.Options);
        Assert.True(defaults.GetBool(RtfImporter.OptDetectHeadings));
        Assert.True(defaults.GetBool(RtfImporter.OptDetectLists));
        Assert.True(defaults.GetBool(RtfImporter.OptDetectCode));
        Assert.True(defaults.GetBool(RtfImporter.OptFootnotes));
        Assert.True(defaults.GetBool(RtfImporter.OptExtractImages));
        Assert.Equal("marker", defaults.GetString(RtfImporter.OptPageBreaks));
        Assert.Equal(25, defaults.GetInt(RtfImporter.OptMaxImageMb));
        var maxImage = importer.Options.Single(o => o.Key == RtfImporter.OptMaxImageMb);
        Assert.True(maxImage.Advanced);
        Assert.Equal(RtfImporter.OptExtractImages, maxImage.VisibleWhen);
    }

    [Fact]
    public async Task Word_style_document_with_headings_runs_and_special_characters()
    {
        var doc = await ImportAsync(
            @"{\rtf1\ansi\ansicpg1252\deff0\nouicompat\deflang2057{\fonttbl{\f0\fnil\fcharset0 Calibri;}}" + "\r\n"
            + @"{\colortbl ;\red255\green0\blue0;}{\*\generator Riched20 10.0.19041}" + "\r\n"
            + @"{\stylesheet{\s0 Normal;}{\s1\outlinelevel0 heading 1;}{\s2\sbasedon0 heading 2;}{\*\cs10 Default Paragraph Font;}}" + "\r\n"
            + @"\viewkind4\uc1\pard\s1\outlinelevel0\b\fs32 Main heading\b0\fs22\par" + "\r\n"
            + @"\pard\s2 Second level\par" + "\r\n"
            + @"\pard\sa200 Plain {\b bold} {\i italic} {\ul under}\ulnone  {\strike struck} x{\super 2} H{\sub 2}O {\cf1 red} {\highlight3 marked} {\scaps caps}\par" + "\r\n"
            + @"\pard First line\line second line\par" + "\r\n"
            + @"\pard \ldblquote Quoted\rdblquote  it\rquote s \endash  a \emdash  b non\~breaking\par" + "\r\n"
            + "}");

        var dump = Dump(doc);
        Assert.Equal("rtf", doc.Metadata.SourceFormat);
        Assert.Contains("H1 Main heading\n", dump);
        Assert.Contains("H2 Second level\n", dump);
        Assert.Contains("P Plain {Bold}bold {Italic}italic {Underline}under {Strikethrough}struck x{Superscript}2 H{Subscript}2O red {Highlight}marked {SmallCaps}caps\n", dump);
        Assert.Contains("P First line<br:>second line\n", dump);
        var quotes = Assert.IsType<ParagraphBlock>(doc.Blocks[4]);
        Assert.Equal("“Quoted” it’s – a — b non breaking", quotes.ToPlainText());
        Assert.Empty(doc.ImportWarnings);
    }

    [Fact]
    public async Task Unicode_escapes_skip_their_fallback_characters()
    {
        // A regular string: the compiler would read a backslash-u and four hex digits as a character escape.
        var doc = await BodyAsync("\\pard caf\\u233?\\par \\pard \\u-10179?\\u-8704?\\par \\pard{\\uc2 x\\u8364\\'80\\'80y}\\par \\pard{\\uc0\\u8364 z}\\par \\pard\\u8212\\'97end\\par");

        var texts = doc.Blocks.OfType<ParagraphBlock>().Select(p => p.ToPlainText()).ToList();
        Assert.Equal(["café", "\U0001F600", "x€y", "€z", "—end"], texts);
    }

    [Fact]
    public async Task Code_pages_from_the_document_and_the_font_charset()
    {
        var russian = await ImportAsync(@"{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0\fnil\fcharset204 Arial;}}\f0 \'cf\'f0\'e8\'e2\'e5\'f2\par}");
        Assert.Equal("Привет", Assert.IsType<ParagraphBlock>(Assert.Single(russian.Blocks)).ToPlainText());

        var noFonts = await ImportAsync(@"{\rtf1\ansi\ansicpg1251 \'cf\'f0\'e8\'e2\'e5\'f2\par}");
        Assert.Equal("Привет", Assert.IsType<ParagraphBlock>(Assert.Single(noFonts.Blocks)).ToPlainText());

        var japanese = await ImportAsync(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil\fcharset0 Arial;}{\f1\fnil\fcharset128 MS Mincho;}}\f1 \'82\'a0\'82\'a2\f0  ok\par}");
        Assert.Equal("あい ok", Assert.IsType<ParagraphBlock>(Assert.Single(japanese.Blocks)).ToPlainText());

        var symbol = await BodyAsync(@"\pard{\f1\'b7} dot\par");
        Assert.Equal("• dot", Assert.IsType<ParagraphBlock>(Assert.Single(symbol.Blocks)).ToPlainText());
    }

    [Fact]
    public async Task Upr_uses_the_unicode_branch()
    {
        var doc = await BodyAsync(@"\pard {\upr{Ansi text}{\*\ud{Unicode \u233?}}}\par");
        Assert.Equal("Unicode é", Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks)).ToPlainText());
    }

    [Fact]
    public async Task Unknown_and_ignorable_destinations_are_skipped()
    {
        var doc = await BodyAsync(@"{\*\foobar secret {\b nested}}{\filetbl{\file x.doc}}{\*\themedata 0102}{\header Header text\par}\pard visible{\*\bkmkend x}\par");
        Assert.Equal("P visible\n", Dump(doc)[Dump(doc).IndexOf("P ", StringComparison.Ordinal)..]);
    }

    [Fact]
    public async Task WordPad_legacy_bullets_become_one_list()
    {
        var doc = await BodyAsync(@"\pard Intro\par" + "\r\n"
            + @"\pard{\pntext\f1\'B7\tab}{\*\pn\pnlvlblt\pnf1\pnindent0{\pntxtb\'B7}}\fi-720\li720 One\par" + "\r\n"
            + @"{\pntext\f1\'B7\tab}Two\par" + "\r\n"
            + @"\pard After\par" + "\r\n"
            + @"\pard{\pntext 1.\tab}{\*\pn\pnlvlbody\pndec\pnstart3{\pntxta .}}\fi-360\li720 Third\par" + "\r\n"
            + @"{\pntext 4.\tab}Fourth\par");

        var dump = Dump(doc);
        Assert.Contains("P Intro\nUL tight\n- item checked=none\n    P One\n- item checked=none\n    P Two\nP After\nOL start=3 tight\n- item checked=none\n    P Third\n- item checked=none\n    P Fourth\n", dump);
        Assert.DoesNotContain("•", dump);
    }

    [Fact]
    public async Task Word_lists_from_the_list_table_nest_by_level()
    {
        var doc = await BodyAsync(
            @"{\*\listtable{\list\listtemplateid1{\listlevel\levelnfc23\levelstartat1{\leveltext\'01\u-3913 ?;}{\levelnumbers;}\f1\fi-360\li720}"
            + @"{\listlevel\levelnfc0\levelstartat3{\leveltext\'02\'01.;}{\levelnumbers\'01;}\fi-360\li1440}{\listname ;}\listid100}}" + "\r\n"
            + @"{\*\listoverridetable{\listoverride\listid100\listoverridecount0\ls1}}" + "\r\n"
            + @"\pard\ls1\ilvl0\fi-360\li720 {\listtext\f1\'b7\tab}Bullet a\par" + "\r\n"
            + @"\pard\ls1\ilvl1\fi-360\li1440 {\listtext 3.\tab}Nested c\par" + "\r\n"
            + @"\pard\ls1\ilvl1\fi-360\li1440 {\listtext 4.\tab}Nested d\par" + "\r\n"
            + @"\pard\ls1\ilvl0\fi-360\li720 {\listtext\f1\'b7\tab}Bullet b\par" + "\r\n"
            + @"\pard After\par");

        Assert.Contains(
            "UL tight\n- item checked=none\n    P Bullet a\n    OL start=3 tight\n    - item checked=none\n        P Nested c\n    - item checked=none\n        P Nested d\n- item checked=none\n    P Bullet b\nP After\n",
            Dump(doc));
    }

    [Fact]
    public async Task Typed_bullets_and_numbers_with_a_hanging_indent_become_lists()
    {
        const string body = @"\pard\fi-360\li720 \bullet\tab Alpha\par\pard\fi-360\li720 \bullet\tab Beta\par\pard Gap\par"
            + @"\pard\fi-360\li720 1.\tab First\par\pard\fi-360\li720 2.\tab Second\par\pard No indent - 1. here\par";
        var doc = await BodyAsync(body);
        var dump = Dump(doc);
        Assert.Contains("UL tight\n- item checked=none\n    P Alpha\n- item checked=none\n    P Beta\nP Gap\nOL start=1 tight\n- item checked=none\n    P First\n- item checked=none\n    P Second\nP No indent - 1. here\n", dump);

        var off = await BodyAsync(body, o => o.Set(RtfImporter.OptDetectLists, false));
        Assert.DoesNotContain(off.Blocks, b => b is ListBlock);
    }

    [Fact]
    public async Task Tables_with_header_alignment_and_merged_cells()
    {
        var doc = await BodyAsync(
            @"\pard Before\par" + "\r\n"
            + @"\trowd\trhdr\cellx3000\cellx6000\pard\intbl Name\cell Value\cell\row" + "\r\n"
            + @"\trowd\cellx3000\cellx6000\pard\intbl\qr A\cell\qr 1\cell\row" + "\r\n"
            + @"\trowd\cellx3000\cellx6000\pard\intbl B\cell\qr 2\cell{\trowd\cellx3000\cellx6000\row}" + "\r\n"
            + @"\pard Between\par" + "\r\n"
            + @"\trowd\clmgf\cellx2000\clmrg\cellx4000\cellx6000\pard\intbl Wide\cell\cell C\cell\row" + "\r\n"
            + @"\trowd\clvmgf\cellx2000\cellx4000\cellx6000\pard\intbl Tall\cell X\cell Y\par Y2\cell\row" + "\r\n"
            + @"\trowd\clvmrg\cellx2000\cellx4000\cellx6000\pard\intbl\cell P\cell Q\cell\row" + "\r\n"
            + @"\pard After\par");

        var dump = Dump(doc);
        Assert.Contains("P Before\nTABLE cols=2 align=Default,Right\n  TH | Name | Value\n  TR | A | 1\n  TR | B | 2\nP Between\n", dump);
        var spans = doc.Blocks.OfType<TableBlock>().Last();
        Assert.Equal(3, spans.ColumnCount);
        Assert.Equal(2, spans.Rows[0].Cells.Count);
        Assert.Equal(2, spans.Rows[0].Cells[0].ColumnSpan);
        Assert.Equal("Wide", spans.Rows[0].Cells[0].ToPlainText());
        Assert.Equal(2, spans.Rows[1].Cells[0].RowSpan);
        Assert.Equal("Y Y2", spans.Rows[1].Cells[2].ToPlainText());
        Assert.Equal(["P", "Q"], spans.Rows[2].Cells.Select(c => c.ToPlainText()));
        Assert.IsType<ParagraphBlock>(doc.Blocks[^1]);
    }

    [Fact]
    public async Task Nested_tables_are_flattened_into_the_parent_cell()
    {
        var doc = await BodyAsync(
            @"\trowd\cellx3000\cellx6000\pard\intbl Outer\cell\pard\intbl\itap2 Inner one\nestcell Inner two\nestcell{\*\nesttableprops\trowd\cellx1000\cellx2000\nestrow}{\nonesttables\par}\pard\intbl\itap1\cell\row\pard After\par");

        var table = Assert.IsType<TableBlock>(doc.Blocks[0]);
        Assert.Equal("Outer", table.Rows[0].Cells[0].ToPlainText());
        Assert.Equal("Inner one Inner two", table.Rows[0].Cells[1].ToPlainText());
    }

    [Fact]
    public async Task Hyperlink_fields_become_links_and_bookmarks_become_anchors()
    {
        var doc = await ImportAsync(@"{\rtf1\ansi{\stylesheet{\s1 heading 1;}}"
            + @"\pard\s1{\*\bkmkstart Section_1}Target{\*\bkmkend Section_1}\par"
            + @"\pard See {\field{\*\fldinst{HYPERLINK ""https://example.com/a""}}{\fldrslt{\ul\cf1 Example}}} and "
            + @"{\field{\*\fldinst HYPERLINK \\l ""Section_1""}{\fldrslt Go}}. {\field{\*\fldinst PAGE}{\fldrslt 7}}\par}");

        var dump = Dump(doc);
        Assert.Contains("H1 Target\n", dump);
        Assert.Equal("section-1", doc.Blocks[0].Id);
        Assert.Contains("P See <link:https://example.com/a>{Underline}Example</link:> and <link:#section-1>Go</link:>. 7\n", dump);
    }

    [Fact]
    public async Task Footnotes_are_kept_or_dropped()
    {
        const string body = @"\pard Text{\super\chftn}{\footnote\pard\plain{\super\chftn} The note.} more\par";
        var doc = await BodyAsync(body);
        var dump = Dump(doc);
        Assert.Contains("P Text<fn:1> more\n", dump);
        Assert.Contains("footnote 1\n  P The note.\n", dump);

        var off = await BodyAsync(body, o => o.Set(RtfImporter.OptFootnotes, false));
        Assert.Empty(off.Footnotes);
        Assert.Equal("Text more", Assert.IsType<ParagraphBlock>(Assert.Single(off.Blocks)).ToPlainText());
    }

    [Fact]
    public async Task Png_pictures_become_images_and_metafiles_are_reported()
    {
        var png = DocxTestHelpers.TinyPng();
        var hex = Hex(png);
        var wrapped = string.Join("\r\n", Enumerable.Range(0, (hex.Length + 63) / 64).Select(i => hex.Substring(i * 64, Math.Min(64, hex.Length - (i * 64)))));
        var doc = await BodyAsync(
            @"\pard{\*\shppict{\pict{\*\picprop{\sp{\sn x}}}\pngblip\picw4\pich3\picwgoal4680\pichgoal3510" + "\r\n" + wrapped + @"}}{\nonshppict{\pict\wmetafile8 0102}}\par"
            + @"\pard See {\pict\pngblip " + hex + @"} here\par"
            + @"\pard{\pict\wmetafile8\picw100\pich100 010203}\par"
            + @"\pard{\pict\emfblip 0102}\par");

        var image = Assert.IsType<ImageBlock>(doc.Blocks[0]);
        var resource = doc.FindResource(image.Source);
        Assert.NotNull(resource);
        Assert.Equal("image/png", resource!.MediaType);
        Assert.Equal(png, resource.Data);
        Assert.Equal(50, image.WidthPercent);
        Assert.Equal((4, 3), (image.PixelWidth, image.PixelHeight));

        var inline = Assert.IsType<ParagraphBlock>(doc.Blocks[1]);
        Assert.Contains(inline.Inlines, i => i is ImageInline);
        Assert.Equal(2, doc.Resources.Count);
        Assert.Contains("2 drawings in a format that cannot be converted (WMF/EMF) were left out.", doc.ImportWarnings);

        var off = await BodyAsync(@"\pard{\pict\pngblip " + hex + @"}\par", o => o.Set(RtfImporter.OptExtractImages, false));
        Assert.Empty(off.Resources);
        Assert.Empty(off.Blocks);
    }

    [Fact]
    public async Task Binary_picture_data_is_read()
    {
        var png = DocxTestHelpers.TinyPng();
        var rtf = Encoding.Latin1.GetBytes(@"{\rtf1\ansi\pard{\pict\pngblip\bin" + png.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ")
            .Concat(png).Concat(Encoding.Latin1.GetBytes(@"}\par}")).ToArray();
        var importer = new RtfImporter();
        using var stream = new MemoryStream(rtf);
        var doc = await importer.ImportAsync(new ImportContext { Input = stream, FileName = "b.rtf", Options = OptionValues.FromDefaults(importer.Options) });
        Assert.Equal(png, Assert.Single(doc.Resources).Data);
    }

    [Fact]
    public async Task Page_breaks_follow_the_option()
    {
        const string body = @"\pard A\par\page\pard B\par\pard\pagebb C\par\page";
        var doc = await BodyAsync(body);
        Assert.Equal(["ParagraphBlock", "PageBreakBlock", "ParagraphBlock", "PageBreakBlock", "ParagraphBlock"], doc.Blocks.Select(b => b.GetType().Name));

        var none = await BodyAsync(body, o => o.Set(RtfImporter.OptPageBreaks, "none"));
        Assert.DoesNotContain(none.Blocks, b => b is PageBreakBlock);
    }

    [Fact]
    public async Task Monospace_paragraphs_become_one_code_block()
    {
        var doc = await BodyAsync(
            @"\pard\f0 Intro with {\f2 inline} code\par" + "\r\n"
            + @"\pard\f2 int main()\line \{\par" + "\r\n"
            + @"\pard\f2\tab return 0;\par" + "\r\n"
            + @"\pard\f2\par" + "\r\n"
            + @"\pard\f2 \}\par" + "\r\n"
            + @"\pard\f0 After\par");

        Assert.Contains("P Intro with <code:inline> code\nCODE lang=\nint main()\n{\n\treturn 0;\n\n}\nEND CODE\nP After\n", Dump(doc));

        var off = await BodyAsync(@"\pard\f2 x = 1\par", o => o.Set(RtfImporter.OptDetectCode, false));
        Assert.IsType<ParagraphBlock>(Assert.Single(off.Blocks));
    }

    [Fact]
    public async Task Headings_are_detected_by_size_and_bold_without_styles()
    {
        const string body = @"\pard\fs40 Big title\par"
            + @"\pard\fs32 Section\par"
            + @"\pard\fs24 Body text that is long enough to dominate the size count of the document by far. More text here.\par"
            + @"\pard\fs24 Another body paragraph with plenty of characters in it to keep it dominant.\par"
            + @"\pard\b\fs24 Bold line\b0\par"
            + @"\pard\b\fs24 A bold sentence.\b0\par";
        var doc = await BodyAsync(body);
        var dump = Dump(doc);
        Assert.Contains("H1 Big title\nH2 Section\nP Body", dump);
        Assert.Contains("H3 Bold line\nP {Bold}A bold sentence.\n", dump);

        var off = await BodyAsync(body, o => o.Set(RtfImporter.OptDetectHeadings, false));
        Assert.DoesNotContain(off.Blocks, b => b is HeadingBlock);
    }

    [Fact]
    public async Task Styles_map_to_title_quote_and_code()
    {
        var doc = await ImportAsync(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Calibri;}{\f2\fmodern Consolas;}}"
            + @"{\stylesheet{\s0 Normal;}{\s15 Title;}{\s16 Subtitle;}{\s17 Quote;}{\s18\f2 HTML Preformatted;}}"
            + @"\pard\s15 Doc title\par\pard\s16 The sub\par\pard\s17 Wise words\par\pard\s17 More words\par\pard\s18 a = b\par\pard Body\par}");

        Assert.Equal(
            "H1 Doc title\nP {Italic}The sub\nQUOTE\n  P Wise words\n  P More words\nCODE lang=\na = b\nEND CODE\nP Body\n",
            Dump(doc)[Dump(doc).IndexOf("H1", StringComparison.Ordinal)..]);
    }

    [Fact]
    public async Task Info_group_fills_the_metadata()
    {
        var doc = await BodyAsync(@"{\info{\title My title}{\subject Sub}{\author Ann Author;Bob}{\keywords alpha, beta}{\doccomm A comment}{\creatim\yr2024\mo3\dy5\hr10\min30}{\operator X}{\version2}}\pard Body\par");

        var m = doc.Metadata;
        Assert.Equal("My title", m.Title);
        Assert.Equal("Sub", m.Subtitle);
        Assert.Equal(["Ann Author", "Bob"], m.Authors);
        Assert.Equal(["alpha", "beta"], m.Keywords);
        Assert.Equal("A comment", m.Description);
        Assert.Equal(new DateTime(2024, 3, 5, 10, 30, 0), m.Date!.Value.DateTime);
        Assert.Equal("Body", Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks)).ToPlainText());
    }

    [Fact]
    public async Task TextEdit_style_file_with_flat_font_table_and_backslash_newlines()
    {
        var doc = await ImportAsync("{\\rtf1\\ansi\\ansicpg1252\\cocoartf2639\n"
            + "{\\fonttbl\\f0\\fswiss\\fcharset0 Helvetica;\\f1\\fmodern\\fcharset0 Courier;}\n"
            + "{\\colortbl;\\red255\\green255\\blue255;}\n{\\*\\expandedcolortbl;;}\n"
            + "\\paperw11900\\paperh16840\\pard\\tx566\\pardirnatural\\partightenfactor0\n\n"
            + "\\f0\\fs24 \\cf0 First line\\\nSecond {\\b bold}\\\n"
            + "{\\*\\listtable{\\list\\listtemplateid1\\listhybrid{\\listlevel\\levelnfc23\\levelnfcn23\\leveljc0{\\*\\levelmarker \\{disc\\}}{\\leveltext\\leveltemplateid1\\'01\\uc0\\u8226 ;}{\\levelnumbers;}}{\\listname ;}\\listid1}}\n"
            + "{\\*\\listoverridetable{\\listoverride\\listid1\\listoverridecount0\\ls1}}\n"
            + "\\pard\\ls1\\ilvl0{\\listtext\t\\uc0\\u8226 \t}Item\\\n"
            + "\\pard\\f1 mono()\\\n}");

        Assert.Equal("P First line\nP Second {Bold}bold\nUL tight\n- item checked=none\n    P Item\nCODE lang=\nmono()\nEND CODE\n",
            Dump(doc)[Dump(doc).IndexOf("P ", StringComparison.Ordinal)..]);
    }

    [Fact]
    public async Task Non_rtf_input_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync("Just some text"));
        Assert.Contains("not an RTF document", ex.Message);
    }

    [Fact]
    public async Task Truncated_file_keeps_the_text_and_warns()
    {
        var doc = await ImportAsync(@"{\rtf1\ansi Hello {\b world");
        Assert.Equal("P Hello {Bold}world\n", Dump(doc)[Dump(doc).IndexOf("P ", StringComparison.Ordinal)..]);
        Assert.Contains(doc.ImportWarnings, w => w.Contains("truncated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Objects_are_skipped_with_a_warning()
    {
        var doc = await BodyAsync(@"\pard Chart: {\object\objemb{\*\objclass Excel.Chart.8}{\*\objdata 0102}{\result{\pict\wmetafile8 01}}}\par");
        Assert.Equal("Chart:", Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks)).ToPlainText());
        Assert.Contains("1 embedded object (a chart, equation or other OLE content) was left out.", doc.ImportWarnings);
    }

    [Fact]
    public async Task Converts_to_markdown_and_pdf()
    {
        const string rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Calibri;}}{\stylesheet{\s1 heading 1;}}"
            + @"\pard\s1\outlinelevel0 Main heading\par\pard Some {\b bold} text.\par}";
        var doc = await ImportAsync(rtf);
        var markdown = await ToMarkdownAsync(doc);
        Assert.Contains("# Main heading", markdown);
        Assert.Contains("**bold**", markdown);

        var dir = Path.Combine(Path.GetTempPath(), "aio-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddAllInOnePdfCore(Path.Combine(dir, "data"));
            using var provider = services.BuildServiceProvider();
            var registry = provider.GetRequiredService<FormatRegistry>();
            Assert.IsType<RtfImporter>(registry.Resolve("notes.rtf", "pdf")!.Importer);

            var result = await provider.GetRequiredService<IConversionService>().ConvertAsync(new ConversionRequest
            {
                SourceContent = Encoding.Latin1.GetBytes(rtf),
                SourceFileName = "notes.rtf",
                TargetFormatId = "pdf",
                OutputDirectory = Path.Combine(dir, "out"),
            });

            Assert.True(result.Success, result.Error);
            var written = Assert.Single(result.WrittenPaths);
            Assert.Equal("%PDF", Encoding.ASCII.GetString(await File.ReadAllBytesAsync(written), 0, 4));
            Assert.Equal("rtf", result.Document!.Metadata.SourceFormat);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
