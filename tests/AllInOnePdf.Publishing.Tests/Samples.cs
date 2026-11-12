using AllInOnePdf.Core.Documents;
using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Tests;

internal static class Samples
{
    public static readonly byte[] FakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5];

    /// <summary>One section with a ½ ½ row holding every module type, plus a second section after a page break.</summary>
    public static Publication EveryModule()
    {
        var asset = new PublicationAsset { FileName = "photo.png", MediaType = "image/png", Data = FakePng, PixelWidth = 640, PixelHeight = 480 };
        var left = new Column
        {
            Modules =
            [
                new HeadingModule { Text = "Intro **bold**", Level = 2 },
                new TextModule { Markdown = "First paragraph.\n\n- one\n- two" },
                new ImageModule { AssetId = asset.Id, Alt = "Photo", Caption = "A caption", WidthPercent = 60 },
                new DividerModule { Style = DividerStyle.Dashed },
                new SpacerModule { HeightPt = 30 },
                new QuoteModule { Markdown = "Quoted words.", Attribution = "Someone" },
            ],
        };
        var right = new Column
        {
            Modules =
            [
                new CalloutModule { Kind = CalloutKind.Warning, Title = "Careful", Markdown = "Mind the gap." },
                new CodeModule { Code = "var x = 1;", Language = "csharp", Caption = "Example" },
                new TableModule { Cells = [["Name", "Price"], ["Tea", "€3"]], Alignments = [ColumnAlignment.Left, ColumnAlignment.Right], Caption = "Prices" },
                new ButtonModule { Text = "Go", Url = "https://example.com", Align = BlockAlign.Center },
                new ContentsModule { Depth = 3 },
                new PageBreakModule(),
            ],
        };

        var publication = new Publication { Name = "Sample", Assets = [asset] };
        publication.Sections.Add(new Section { Rows = [new Row { Fractions = [0.5, 0.5], Columns = [left, right] }] });
        publication.Sections.Add(new Section
        {
            PageBreakBefore = true,
            Rows = [new Row { Columns = [new Column { Modules = [new HeadingModule { Text = "Second", Level = 1 }] }] }],
        });
        return publication;
    }
}
