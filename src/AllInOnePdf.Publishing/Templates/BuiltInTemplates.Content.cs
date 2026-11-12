using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Templates;

/// <summary>The template layouts and their placeholder copy.</summary>
public static partial class BuiltInTemplates
{
    private static Publication Blank()
    {
        var p = New("Blank", "clean");
        p.Sections.Add(Section(Row(One, Col())));
        return Done(p);
    }

    private static Publication Newsletter()
    {
        var p = New("Newsletter", "clean");
        p.Metadata.Title = "The Harbour Street Gazette";
        p.Metadata.Authors.Add("Harbour Street Residents' Association");
        p.Metadata.Date = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        p.Metadata.Keywords.AddRange(["newsletter", "community"]);

        var masthead = Section(
            Row(TwoThirdsThird,
                Col(
                    H(1, "The Harbour Street Gazette"),
                    Text("News, events and good neighbours from the north end of town.", scale: 1.1)),
                Col(
                    Text("**Issue 14**  \nNovember 2026", TextAlignment.Right))),
            Full(new DividerModule { ThicknessPt = 2 }));

        var lead = Section(
            Row(Halves,
                Col(Placeholder("Volunteers planting the first beds", "The first beds went in on a bright Saturday morning.")),
                Col(
                    H(2, "Community garden opens its gates"),
                    Text("""
                        After two years of planning, fundraising and a great deal of digging, the Harbour Street community garden
                        opened on 18 October. Forty raised beds, a tool library and a small orchard now fill the old car park
                        behind the library.

                        Plots are still available for the spring season. Each plot costs **£25 a year**, and bursaries are
                        available for anyone who needs one. Ask at the library desk or email the garden team.
                        """),
                    new QuoteModule { Markdown = "It feels like the whole street finally has a back garden.", Attribution = "Amira Haddad, plot 12" })));

        var shorts = Section(
            Row(Thirds,
                Col(
                    H(3, "Winter fair"),
                    Text("Stalls, mulled apple juice and the school choir on **Saturday 6 December**, 11:00 to 16:00 at St Mark's hall.")),
                Col(
                    H(3, "Road works"),
                    Text("Resurfacing of Quay Lane starts on 17 November and takes about three weeks. The bus stop moves to Mill Road meanwhile.")),
                Col(
                    H(3, "Repair café"),
                    Text("Bring a broken toaster, a torn jacket or a wobbly chair. Volunteers fix what they can, every first Sunday of the month."))));

        var footer = Section(
            Full(
                new DividerModule(),
                Text("Harbour Street Residents' Association · 4 Quay Lane · hello@harbourstreet.example · 01632 960 418", TextAlignment.Center, 0.85)));

        p.Sections.AddRange([masthead, lead, shorts, footer]);
        return Done(p);
    }

    private static Publication Flyer()
    {
        var p = New("Flyer", "swiss");
        p.Metadata.Title = "Summer design workshop";
        p.Metadata.Date = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);

        var hero = Section(
            Full(
                H(1, "Summer design workshop"),
                Text("Two days of type, colour and layout for people who make things. **20 and 21 June 2026**, Riverside Studios.", scale: 1.25),
                new SpacerModule { HeightPt = 12 },
                Placeholder("Participants sketching layouts at long tables")));

        var features = Section(
            Row(Halves,
                Col(
                    H(3, "Learn by doing"),
                    Text("Every session ends with something on paper. You will set a poster, design a small booklet and build a colour palette from a photograph you took that morning."),
                    new CalloutModule { Kind = CalloutKind.Tip, Title = "No experience needed", Markdown = "Bring curiosity and a pencil. We provide paper, laptops and lunch." }),
                Col(
                    H(3, "Small groups"),
                    Text("Twelve places per group and two tutors in every room, so there is time for real feedback on your own work rather than a lecture to a crowd."),
                    new CalloutModule { Kind = CalloutKind.Important, Title = "Early-bird price", Markdown = "**£180** until 1 May, then £240. Student places are £90." })));

        var action = Section(
            Full(
                new SpacerModule { HeightPt = 16 },
                new ButtonModule { Text = "Book your place", Url = "https://example.com/workshop", Align = BlockAlign.Center, CornerRadiusPt = 0 },
                Text("riversidestudios.example/workshop · 0161 496 0731", TextAlignment.Center, 0.9)));

        p.Sections.AddRange([hero, features, action]);
        return Done(p);
    }

    private static Publication Report()
    {
        // The title page carries no running header or page number, as in a printed report.
        var p = New("Report", "academic", t =>
        {
            t.Header.ShowOnFirstPage = false;
            t.Footer.ShowOnFirstPage = false;
        });
        p.Metadata.Title = "Street trees and urban heat";
        p.Metadata.Subtitle = "A summer survey of surface temperatures in three neighbourhoods";
        p.Metadata.Authors.Add("Dr. Maya Lindqvist");
        p.Metadata.Date = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        p.Metadata.Keywords.AddRange(["urban heat island", "street trees", "climate adaptation"]);

        var title = Section(
            Full(
                new SpacerModule { HeightPt = 140 },
                H(1, "Street trees and urban heat", TextAlignment.Center),
                Text("*A summer survey of surface temperatures in three neighbourhoods*", TextAlignment.Center, 1.15),
                new SpacerModule { HeightPt = 36 },
                Text("Dr. Maya Lindqvist  \nDepartment of Environmental Science, Northbridge University", TextAlignment.Center),
                Text("5 November 2026", TextAlignment.Center)));
        title.PageBreakAfter = true;

        var contents = Section(Full(new ContentsModule { Depth = 2 }));
        contents.PageBreakAfter = true;

        var intro = Section(
            Full(
                H(1, "Introduction"),
                Text("""
                    Cities are warmer than the countryside around them, and the difference is largest on still summer nights.
                    Paved surfaces store heat during the day and release it slowly after sunset, while trees shade the ground
                    and cool the air by evaporating water through their leaves.

                    This report measures how much difference street trees make at the scale of a single block. It compares
                    surface temperatures on tree-lined and treeless streets in three neighbourhoods of similar age and density.
                    """),
                H(2, "Research questions"),
                Text("""
                    1. How much cooler are pavements under a closed canopy than in full sun?
                    2. Does the effect persist after sunset?
                    3. Which species give the most shade for the space they take?
                    """)));

        var method = Section(
            Full(
                H(1, "Method"),
                Text("""
                    We recorded pavement and air temperatures at 42 sites between 1 July and 31 August 2026, at 14:00 and again
                    at 22:00. Surface readings used a calibrated infrared thermometer held one metre above the ground; air
                    readings came from shaded loggers mounted at two metres.
                    """),
                Table(true, [ColumnAlignment.Left, ColumnAlignment.Right, ColumnAlignment.Right, ColumnAlignment.Right],
                    ["Neighbourhood", "Sites", "Canopy cover", "Readings"],
                    ["Old Town", "14", "38 %", "1,736"],
                    ["Riverside", "15", "22 %", "1,860"],
                    ["Eastfield", "13", "9 %", "1,612"]),
                new CalloutModule
                {
                    Kind = CalloutKind.Note,
                    Markdown = "Eleven readings were discarded because a logger was moved by road works. They are listed in the appendix.",
                }));
        method.Rows[0].Columns[0].Modules.OfType<TableModule>().First().Caption = "Survey sites and readings by neighbourhood";

        var results = Section(
            Full(
                H(1, "Results"),
                Text("""
                    At 14:00, pavements under a closed canopy were on average **11.4 °C cooler** than pavements in full sun.
                    The gap narrowed to 3.1 °C by 22:00 but did not disappear, which suggests that shade during the day reduces
                    the heat that streets give back at night.
                    """),
                H(2, "Species"),
                Text("""
                    London planes and limes gave the widest shade per metre of pavement. Young trees under ten years old made
                    little measurable difference, which matters for planting programmes that are judged on their first years.
                    """)));

        p.Sections.AddRange([title, contents, intro, method, results]);
        return Done(p);
    }

    private static Publication Menu()
    {
        var p = New("Menu", "newsprint", t => t.Tables.Style = TableStyle.Minimal);
        p.Metadata.Title = "Osteria del Ponte";
        p.Metadata.Description = "Autumn menu";

        var header = Section(
            Full(
                H(1, "Osteria del Ponte", TextAlignment.Center),
                Text("*Autumn menu · served Tuesday to Sunday, 18:00 to 22:30*", TextAlignment.Center),
                new DividerModule { ThicknessPt = 1.5, WidthPercent = 60 }));

        var body = Section(
            Course("Antipasti", "To share, or not.",
                ["**Burrata** with roasted cherry tomatoes and basil oil", "€12"],
                ["**Vitello tonnato**, capers and rocket", "€14"],
                ["**Fried artichokes** with lemon mayonnaise", "€10"]),
            Full(new DividerModule { Style = DividerStyle.Dotted }),
            Course("Primi", "Fresh pasta made every morning.",
                ["**Tagliatelle** with slow-cooked beef ragù", "€18"],
                ["**Pumpkin ravioli**, sage butter and amaretti", "€17"],
                ["**Risotto** with porcini and aged parmesan", "€19"]),
            Full(new DividerModule { Style = DividerStyle.Dotted }),
            Course("Secondi", "Served with seasonal vegetables.",
                ["**Sea bass** baked in salt, salsa verde", "€26"],
                ["**Lamb shoulder**, rosemary and white beans", "€24"],
                ["**Aubergine parmigiana** (vegetarian)", "€19"]),
            Full(new DividerModule { Style = DividerStyle.Dotted }),
            Course("Dolci", "Ask about our dessert wines.",
                ["**Tiramisù** made to order", "€8"],
                ["**Panna cotta** with poached pears", "€8"],
                ["**Affogato** with hazelnut gelato", "€7"]));

        var footer = Section(
            Full(
                new DividerModule(),
                Text("Please tell us about any allergies. Via del Ponte 7 · +39 055 010 2233 · osteriadelponte.example", TextAlignment.Center, 0.85)));

        p.Sections.AddRange([header, body, footer]);
        return Done(p);
    }

    private static Row Course(string name, string note, params string[][] dishes) =>
        Row(ThirdTwoThirds,
            Col(H(2, name), Text("*" + note + "*", scale: 0.9)),
            Col(Table(false, [ColumnAlignment.Left, ColumnAlignment.Right], dishes)));

    private static Publication Resume()
    {
        var p = New("Résumé", "compact");
        p.Metadata.Title = "Jordan Avery";
        p.Metadata.Subtitle = "Senior product designer";
        p.Metadata.Authors.Add("Jordan Avery");

        var sidebar = Col(
            H(1, "Jordan Avery"),
            Text("Senior product designer", scale: 1.1),
            new DividerModule(),
            H(3, "Contact"),
            Text("jordan.avery@example.com  \n+44 20 7946 0958  \nLondon, United Kingdom  \n[portfolio.example/jordan](https://portfolio.example/jordan)"),
            H(3, "Skills"),
            Text("""
                - Interaction and visual design
                - Design systems
                - User research and testing
                - Prototyping in Figma
                - HTML, CSS and a little TypeScript
                """),
            H(3, "Languages"),
            Text("English (native)  \nSwedish (fluent)  \nGerman (conversational)"));
        sidebar.Design = new BoxDesign { Background = "#F3F4F6", Padding = Spacing.All(12), CornerRadiusPt = 4 };

        var main = Col(
            H(2, "Profile"),
            Text("Product designer with ten years of experience turning complicated workflows into calm, clear software. I lead small teams, work closely with engineers and like to test ideas with real users early."),
            H(2, "Experience"),
            H(3, "Lead product designer · Northwind Labs"),
            Text("""
                *March 2022 – present · London*

                - Led the redesign of the scheduling product used by 4,000 clinics; support tickets fell by 31 %.
                - Built and maintain the company design system, now used by six product teams.
                - Mentor three designers and run the monthly research share-out.
                """),
            H(3, "Product designer · Fabrikam Travel"),
            Text("""
                *June 2018 – February 2022 · Stockholm*

                - Designed the mobile booking flow that lifted conversion from 2.9 % to 4.1 %.
                - Introduced weekly usability testing with five customers.
                """),
            H(3, "Junior designer · Contoso Agency"),
            Text("*September 2015 – May 2018 · Stockholm*\n\nWebsites and campaigns for retail and public-sector clients."),
            H(2, "Education"),
            H(3, "MA Interaction Design · Umeå Institute of Design"),
            Text("*2013 – 2015*"),
            H(3, "BA Graphic Design · University of the Arts London"),
            Text("*2010 – 2013*"));

        p.Sections.Add(Section(Row(ThirdTwoThirds, sidebar, main)));
        p.Sections[0].Rows[0].GutterPt = 20;
        p.Sections[0].Rows[0].KeepTogether = false;
        return Done(p);
    }

    private static Publication Invoice()
    {
        var p = New("Invoice", "technical", t => t.Headings.NumberHeadings = false);
        p.Metadata.Title = "Invoice INV-2026-0142";
        p.Metadata.Authors.Add("Brightline Studio Ltd.");
        p.Metadata.Date = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        var header = Section(
            Row(Halves,
                Col(Text("**Brightline Studio Ltd.**  \n12 Canal Street  \nManchester M1 3HE  \nUnited Kingdom  \nVAT GB 123 4567 89")),
                Col(
                    H(1, "Invoice", TextAlignment.Right),
                    Text("Invoice no. **INV-2026-0142**  \nIssued: 5 November 2026  \nDue: 5 December 2026", TextAlignment.Right))),
            Full(new SpacerModule { HeightPt = 12 }),
            Row(Halves,
                Col(Text("**Bill to**  \nHarper & Lowe Architects  \nAttn. Priya Shah  \n48 Deansgate  \nManchester M3 2EG")),
                Col(Text("**Project**  \nNew website and brand refresh  \nPO 7781-B", TextAlignment.Right))));

        var lines = Section(
            Full(
                new SpacerModule { HeightPt = 8 },
                Table(true, [ColumnAlignment.Left, ColumnAlignment.Right, ColumnAlignment.Right, ColumnAlignment.Right],
                    ["Description", "Qty", "Unit price", "Amount"],
                    ["Brand identity refresh", "1", "£2,400.00", "£2,400.00"],
                    ["Website design, page templates", "6", "£450.00", "£2,700.00"],
                    ["Front-end development (hours)", "32", "£85.00", "£2,720.00"],
                    ["Copywriting (hours)", "10", "£60.00", "£600.00"],
                    ["Hosting setup, first year", "1", "£180.00", "£180.00"])),
            Row(TwoThirdsThird,
                Col(),
                Col(Table(false, [ColumnAlignment.Left, ColumnAlignment.Right],
                    ["Subtotal", "£8,600.00"],
                    ["VAT 20 %", "£1,720.00"],
                    ["**Total due**", "**£10,320.00**"]))));

        var terms = Section(
            Full(
                new SpacerModule { HeightPt = 12 },
                new CalloutModule
                {
                    Kind = CalloutKind.Note,
                    Title = "Payment terms",
                    Markdown = """
                        Payment is due within 30 days by bank transfer to **Brightline Studio Ltd.**, sort code 04-00-04,
                        account 12345678. Please quote the invoice number as the reference. Late payments may incur interest
                        at 8 % above the Bank of England base rate.
                        """,
                },
                Text("Thank you for your business.", TextAlignment.Center, 0.9)));

        p.Sections.AddRange([header, lines, terms]);
        return Done(p);
    }
}
