using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Templates;

/// <summary>The free-flow template.</summary>
public static partial class BuiltInTemplates
{
    /// <summary>
    /// A4 portrait, one free-flow section filling the page: a large heading near the top, a picture placeholder, a
    /// text block, a quote overlapping the picture's bottom edge on a higher Z level, and a button at the bottom.
    /// </summary>
    private static Publication Poster()
    {
        var p = New("Poster", "swiss", t =>
        {
            t.Page.Size = PageSizeName.A4;
            t.Page.Orientation = PageOrientation.Portrait;
            t.Header.Left = t.Header.Center = t.Header.Right = null;
            t.Footer.Left = t.Footer.Center = t.Footer.Right = null;
        });
        p.Metadata.Title = "Open studios";
        p.Metadata.Date = new DateTimeOffset(2027, 3, 13, 0, 0, 0, TimeSpan.Zero);
        p.Metadata.Keywords.AddRange(["poster", "event", "art"]);

        // Place everything from the page's real canvas size (content width, body height) so it fits any margins.
        var composer = new ThemedComposer(p.Theme, new Core.Documents.Document());
        var w = Math.Floor(composer.ContentWidthPt);
        var h = Math.Floor(composer.BodyHeightPt);

        var pictureTop = 210.0;
        var pictureHeight = Math.Floor(h * 0.37);
        var pictureBottom = pictureTop + pictureHeight;
        var quoteWidth = Math.Floor(w * 0.52);

        var heading = new HeadingModule
        {
            Text = "Open studios",
            Level = 1,
            Design = new ModuleDesign { FontScale = 1.5 },
        };
        var intro = Text("Twelve artists open their workshops for one weekend. **13 and 14 March 2027**, 10:00 to 18:00, Canal Yard.", scale: 1.15);
        var picture = Placeholder("A painter at work in a sunlit studio");
        var quote = new QuoteModule
        {
            Markdown = "Come for the paintings, stay for the conversations.",
            Attribution = "Canal Yard Artists",
            Design = new ModuleDesign
            {
                Background = "#FFFFFF",
                BorderWidthPt = 1,
                CornerRadiusPt = 6,
                Padding = Spacing.All(12),
            },
        };
        var body = Text("""
            Walk through printmaking, ceramics, textiles and painting studios, watch demonstrations every hour and
            take home something made a few metres from where you bought it.

            Free entry. Step-free access to every ground-floor studio.
            """);
        var button = new ButtonModule { Text = "Plan your visit", Url = "https://example.com/open-studios", CornerRadiusPt = 0 };

        var section = new Section
        {
            Layout = SectionLayout.FreeFlow,
            Items =
            [
                new PlacedModule { Module = heading, XPt = 0, YPt = 0, WidthPt = w, HeightPt = 150 },
                new PlacedModule { Module = intro, XPt = 0, YPt = 150, WidthPt = w, HeightPt = 52 },
                new PlacedModule { Module = picture, XPt = 0, YPt = pictureTop, WidthPt = w, HeightPt = pictureHeight, LockAspect = true },
                new PlacedModule { Module = body, XPt = 0, YPt = pictureBottom + 20, WidthPt = Math.Floor(w - quoteWidth - 24), HeightPt = Math.Floor(h - 60 - (pictureBottom + 20)) },
                new PlacedModule { Module = quote, XPt = w - quoteWidth, YPt = pictureBottom - 48, WidthPt = quoteWidth, HeightPt = 108, Z = 2 },
                new PlacedModule { Module = button, XPt = 0, YPt = h - 44, WidthPt = 200, HeightPt = 40 },
            ],
        };
        p.DefaultLayout = SectionLayout.FreeFlow;
        p.Sections.Add(section);
        return Done(p);
    }
}
