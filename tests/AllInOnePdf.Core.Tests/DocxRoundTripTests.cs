using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;
using static AllInOnePdf.Core.Tests.DocxTestHelpers;

namespace AllInOnePdf.Core.Tests;

/// <summary>
/// Document → DOCX → <see cref="Importers.DocxImporter"/> → Document. Compared with the structural dump from
/// <see cref="MarkdownExportTestHelpers.Dump"/>; tolerances (see <see cref="DocxTestHelpers.Normalized"/>):
/// resource ids change and lists come back tight. Callouts come back as callouts (a tagged content control keeps
/// the kind and title), code blocks keep their language the same way, and cover pages and the TOC field are not
/// imported as content (the cover restores title and subtitle metadata).
/// </summary>
public class DocxRoundTripTests
{
    public static TheoryData<string, string> SamplesAndThemes() => DocxExportTests.SamplesAndThemes();

    [Theory]
    [MemberData(nameof(SamplesAndThemes))]
    public async Task Sample_round_trips_structurally(string sample, string themeId)
    {
        var first = await LoadSampleAsync(sample);
        var expected = Normalized(first);
        var bytes = await ExportAsync(first, BuiltInThemes.Find(themeId));
        var second = await ImportAsync(bytes);

        Assert.Equal(expected, Normalized(second));
    }

    [Fact]
    public async Task Constructed_document_round_trips()
    {
        var doc = DocxExportTests.EveryBlockDocument();
        var expected = Normalized(doc);
        var back = await ImportAsync(await ExportAsync(doc, BuiltInThemes.Clean()));

        Assert.Equal(expected, Normalized(back));
        Assert.Single(back.Resources);
        Assert.Equal("image/png", back.Resources[0].MediaType);
        var image = back.Blocks.OfType<ImageBlock>().Single();
        Assert.Equal(back.Resources[0].Reference, image.Source);
        Assert.InRange(image.PixelWidth!.Value, 150, 400);

        var link = back.Blocks.OfType<ParagraphBlock>().First().Inlines.OfType<LinkInline>().Single(l => l.IsInternal);
        Assert.Equal("#" + back.Headings.First().Id, link.Url);
        var external = back.Blocks.OfType<ParagraphBlock>().First().Inlines.OfType<LinkInline>().Single(l => !l.IsInternal);
        Assert.Equal("Tip", external.Title);
        Assert.Equal(Documents.TextAlignment.Center, back.Blocks.OfType<ParagraphBlock>().Single(p => p.ToPlainText() == "Centred text").Alignment);
        Assert.Equal("Someone", back.Blocks.OfType<QuoteBlock>().Single().Attribution);
        Assert.Equal("notes.txt", back.Blocks.OfType<CodeBlock>().Last().Caption);
        Assert.Equal(2, back.Footnotes.Count);
    }

    [Fact]
    public async Task Round_trip_through_every_theme_keeps_the_constructed_document()
    {
        var expected = Normalized(DocxExportTests.EveryBlockDocument());
        foreach (var theme in BuiltInThemes.All())
        {
            var back = await ImportAsync(await ExportAsync(DocxExportTests.EveryBlockDocument(), theme));
            Assert.Equal(expected, Normalized(back));
        }
    }

    [Fact]
    public async Task Exporting_the_imported_document_again_is_stable()
    {
        var first = await ImportAsync(await ExportAsync(await LoadSampleAsync("sample-document.md"), BuiltInThemes.Technical()));
        var second = await ImportAsync(await ExportAsync(first, BuiltInThemes.Technical()));
        Assert.Equal(Normalized(first), Normalized(second));
    }
}
