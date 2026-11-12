using System.Globalization;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Exporters.Pptx;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Document = AllInOnePdf.Core.Documents.Document;

namespace AllInOnePdf.Core.Exporters;

/// <summary>
/// Document model → PowerPoint (.pptx) with Open XML SDK. Headings (or page breaks) start slides; long sections
/// continue on "(cont.)" slides; lists keep their bullets and numbering, tables their merged cells; pictures get
/// their own slide. The slide master, layouts and DrawingML theme carry the <see cref="PdfTheme"/> fonts and colours.
/// </summary>
public sealed class PptxExporter : IExporter
{
    public const string OptTheme = PdfExporter.OptTheme;
    public const string OptSplitOn = "splitOn";
    public const string OptTitleSlide = "titleSlide";
    public const string OptMaxLines = "maxLines";
    public const string OptAspect = "aspect";
    public const string OptMetadata = PdfExporter.OptMetadata;

    public FileFormat Format => KnownFormats.Pptx;

    public ConverterStatus Status => ConverterStatus.Ready;

    public bool SupportsTheme => true;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Theme(OptTheme, "Theme", BuiltInThemes.DefaultId, "Fonts and colours for the slides; they also become the presentation's theme.", "Appearance"),
        OptionDefinition.Choice(OptAspect, "Slide size", "16:9",
        [
            new OptionChoice("16:9", "Widescreen (16:9)"),
            new OptionChoice("4:3", "Standard (4:3)"),
        ], "Shape of the slides.", "Appearance"),
        OptionDefinition.Choice(OptSplitOn, "New slide at", "h1",
        [
            new OptionChoice("h1", "Level 1 headings", "Every top-level heading starts a slide."),
            new OptionChoice("h2", "Level 1 and 2 headings", "Smaller sections, more slides."),
            new OptionChoice("pageBreak", "Page breaks and level 1 headings", "Keeps the pages of a paginated source, such as a converted presentation."),
        ], "Which headings start a new slide; the heading becomes the slide title.", "Structure"),
        OptionDefinition.Choice(OptTitleSlide, "Title slide", "yes",
        [
            new OptionChoice("theme", "Use theme setting", "Follow the theme's cover page setting."),
            new OptionChoice("yes", "Always"),
            new OptionChoice("no", "Never"),
        ], "Start with a slide showing the title, subtitle, authors and date.", "Structure"),
        OptionDefinition.Int(OptMaxLines, "Lines per slide", 12, 6, 30, "About how many lines of text fit on a slide before the rest continues on the next one.", "Structure"),
        OptionDefinition.Bool(OptMetadata, "Embed metadata", true, "Write title, author, subject and keywords into the presentation properties.", "Metadata", advanced: true),
    ];

    public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken = default) =>
        Task.Run(() => Export(context, cancellationToken), cancellationToken);

    private ExportResult Export(ExportContext context, CancellationToken ct)
    {
        var theme = context.Theme ?? BuiltInThemes.Clean();
        var doc = context.Document;
        var settings = PptxExportSettings.From(context.Options.WithDefaults(Options));
        var result = new ExportResult();

        var resolvedTitle = theme.Cover.TitleOverride is { Length: > 0 } o ? o.Trim() : doc.ResolveTitle()?.Trim() is { Length: > 0 } t ? t : context.BaseName;
        var overviewTitle = theme.Cover.TitleOverride is { Length: > 0 } ov ? ov.Trim() : !string.IsNullOrWhiteSpace(doc.Metadata.Title) ? doc.Metadata.Title.Trim() : "Overview";

        context.Report(0, "Planning slides");
        var geometry = new PptxGeometry(settings.Widescreen, settings.MaxLines, theme.Typography.BodySizePt);
        var writer = new PptxSlideWriter(theme, geometry, doc, result.Warnings);
        var plans = new PptxSlidePlanner(settings, geometry, theme, writer.CanPlace).Plan(doc, overviewTitle);

        if (settings.TitleSlide ?? theme.Cover.Enabled)
        {
            plans.Insert(0, TitleSlide(doc, theme, resolvedTitle));
        }

        if (plans.Count == 0)
        {
            plans.Add(new PptxSlidePlan { Title = resolvedTitle });
        }

        using var stream = new MemoryStream();
        using (var package = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentationPart = package.AddPresentationPart();
            var builder = new PptxPackageBuilder(theme, geometry, writer);
            builder.Build(presentationPart);

            var slideParts = new List<SlidePart>();
            for (var i = 0; i < plans.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                context.Report(0.05 + 0.85 * i / plans.Count, string.Create(CultureInfo.InvariantCulture, $"Slide {i + 1} of {plans.Count}"));
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.AddPart(plans[i].IsTitleSlide ? builder.TitleLayout : builder.ContentLayout);
                slidePart.Slide = writer.Write(slidePart, plans[i]);
                slideParts.Add(slidePart);
            }

            presentationPart.Presentation = builder.Presentation(presentationPart, slideParts);

            if (settings.Metadata)
            {
                WriteMetadata(package, doc, resolvedTitle);
            }

            context.Report(0.95, "Saving presentation");
        }

        result.Files.Insert(0, new OutputFile(context.BaseName + Format.PrimaryExtension, stream.ToArray(), Format.MediaType));
        context.Report(1, "Presentation written");
        return result;
    }

    private static PptxSlidePlan TitleSlide(Document doc, PdfTheme theme, string title)
    {
        var plan = new PptxSlidePlan { Title = title, IsTitleSlide = true };
        var m = doc.Metadata;
        if (theme.Cover.ShowSubtitle && !string.IsNullOrWhiteSpace(m.Subtitle))
        {
            plan.SubtitleLines.Add(m.Subtitle.Trim());
        }

        if (theme.Cover.ShowAuthor && m.Authors.Count > 0)
        {
            plan.SubtitleLines.Add(string.Join(", ", m.Authors));
        }

        if (theme.Cover.ShowDate && m.Date is { } date)
        {
            plan.SubtitleLines.Add(date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(theme.Cover.Note))
        {
            plan.SubtitleLines.Add(theme.Cover.Note.Trim());
        }

        return plan;
    }

    private static void WriteMetadata(PresentationDocument package, Document doc, string title)
    {
        var m = doc.Metadata;
        var props = package.PackageProperties;
        props.Title = title;
        if (!string.IsNullOrWhiteSpace(m.Subtitle))
        {
            props.Subject = m.Subtitle;
        }

        if (m.Authors.Count > 0)
        {
            props.Creator = string.Join("; ", m.Authors);
        }

        if (m.Keywords.Count > 0)
        {
            props.Keywords = string.Join(", ", m.Keywords);
        }

        if (!string.IsNullOrWhiteSpace(m.Description))
        {
            props.Description = m.Description;
        }

        if (!string.IsNullOrWhiteSpace(m.Language))
        {
            props.Language = m.Language;
        }

        if (m.Date is { } date)
        {
            props.Created = date.UtcDateTime;
        }

        props.Modified = DateTime.UtcNow;
    }
}
