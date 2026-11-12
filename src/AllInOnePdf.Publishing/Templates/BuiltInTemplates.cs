using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Validation;

namespace AllInOnePdf.Publishing.Templates;

/// <summary>A starting point in the "New publication" picker.</summary>
/// <param name="Id">Stable id (<c>blank</c>, <c>newsletter</c> …).</param>
/// <param name="Name">Display name; also the name of publications created from it.</param>
/// <param name="Description">One sentence for the picker.</param>
/// <param name="Tags">Tags for filtering.</param>
/// <param name="Create">Builds a fresh, normalised publication with new ids.</param>
public sealed record PublicationTemplate(string Id, string Name, string Description, IReadOnlyList<string> Tags, Func<Publication> Create);

/// <summary>The templates that ship with the app. Placeholder copy is realistic so thumbnails look like finished documents.</summary>
public static partial class BuiltInTemplates
{
    /// <summary>Id of the empty template.</summary>
    public const string BlankId = "blank";

    private static readonly IReadOnlyList<PublicationTemplate> Templates =
    [
        new(BlankId, "Blank", "An empty page in the Clean theme: one section, one row, one column.", ["empty", "start"], Blank),
        new("newsletter", "Newsletter", "Masthead, a lead story with a photo, three short items and a contact footer.", ["news", "columns", "community"], Newsletter),
        new("flyer", "Flyer", "A large headline, a picture, two columns that each end in a callout, and a booking button.", ["event", "marketing", "poster"], Flyer),
        new("report", "Report", "Title page, contents and three chapters with a table and a note, in the Academic theme.", ["academic", "long-form", "contents"], Report),
        new("menu", "Menu", "A restaurant menu with courses, dish descriptions and prices.", ["restaurant", "food", "print"], Menu),
        new("resume", "Résumé", "A two-column CV: contact and skills on the side, experience and education in the main column.", ["cv", "career", "personal"], Resume),
        new("invoice", "Invoice", "Company and invoice details, line items, totals and payment terms.", ["business", "billing", "finance"], Invoice),
        new("poster", "Poster", "A Free flow page: a large headline, a picture with a quote over its bottom edge, a short text and a button.", ["poster", "event", "free flow"], Poster),
    ];

    /// <summary>Every built-in template, in picker order.</summary>
    public static IReadOnlyList<PublicationTemplate> All() => Templates;

    /// <summary>The template with that id (case-insensitive), or null.</summary>
    public static PublicationTemplate? Find(string? id) =>
        id is null ? null : Templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    // ---- Builders -------------------------------------------------------------------------------------------------

    private static Publication New(string name, string themeId, Action<PdfTheme>? tweak = null)
    {
        var theme = (BuiltInThemes.Find(themeId) ?? BuiltInThemes.Clean()).Clone();
        tweak?.Invoke(theme);
        return new Publication { Name = name, ThemeId = theme.Id, Theme = theme };
    }

    private static Publication Done(Publication publication) => PublicationValidator.Normalize(publication);

    private static Section Section(params Row[] rows) => new() { Rows = [.. rows] };

    private static Row Row(double[] fractions, params Column[] columns) => new() { Fractions = [.. fractions], Columns = [.. columns] };

    private static Row Full(params Module[] modules) => Row([1], Col(modules));

    private static Column Col(params Module[] modules) => new() { Modules = [.. modules] };

    private static HeadingModule H(int level, string text, TextAlignment? align = null) =>
        new() { Level = level, Text = text, Design = new ModuleDesign { Alignment = align } };

    private static TextModule Text(string markdown, TextAlignment? align = null, double scale = 1, string? color = null) =>
        new() { Markdown = markdown, Design = new ModuleDesign { Alignment = align, FontScale = scale, TextColor = color } };

    private static ImageModule Placeholder(string alt, string? caption = null) => new() { Alt = alt, Caption = caption };

    private static TableModule Table(bool header, ColumnAlignment[] alignments, params string[][] rows) =>
        new() { HeaderRow = header, Alignments = [.. alignments], Cells = [.. rows.Select(r => r.ToList())] };

    private static readonly double[] One = [1];
    private static readonly double[] Halves = [0.5, 0.5];
    private static readonly double[] Thirds = [1.0 / 3, 1.0 / 3, 1.0 / 3];
    private static readonly double[] ThirdTwoThirds = [1.0 / 3, 2.0 / 3];
    private static readonly double[] TwoThirdsThird = [2.0 / 3, 1.0 / 3];
}
