using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters.Html;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Exporters;

/// <summary>
/// Document model → one standalone, themed HTML page: semantic <c>aio-*</c> markup from <see cref="DocumentHtmlRenderer"/>
/// (with syntax-highlighted code), a single inline stylesheet generated from the <see cref="PdfTheme"/>, optional cover
/// and contents list, print rules matching the theme's page geometry, and images inlined or written to a side folder.
/// </summary>
public sealed partial class HtmlExporter : IExporter
{
    public const string OptTheme = "theme";
    public const string OptCover = "cover";
    public const string OptToc = "toc";
    public const string OptFonts = "fonts";
    public const string OptImages = "images";
    public const string OptRawHtml = "rawHtml";
    public const string OptMinify = "minify";
    public const string OptLineEndings = "lineEndings";

    private static readonly IReadOnlyList<OptionChoice> ThemeOverride =
    [
        new("theme", "Use theme setting"),
        new("yes", "Always"),
        new("no", "Never"),
    ];

    private readonly ISyntaxHighlighter? _highlighter;

    public HtmlExporter()
        : this(null)
    {
    }

    /// <summary>With a highlighter, code blocks carry <c>hl-*</c> spans coloured by the theme's syntax palette.</summary>
    public HtmlExporter(ISyntaxHighlighter? highlighter)
    {
        _highlighter = highlighter;
    }

    public FileFormat Format => KnownFormats.Html;

    public ConverterStatus Status => ConverterStatus.Ready;

    public bool SupportsTheme => true;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Theme(OptTheme, "Theme", BuiltInThemes.DefaultId, "Fonts, colours and spacing for the page. The same themes as PDF output.", "Appearance"),
        OptionDefinition.Choice(OptFonts, "Fonts", "system-stack",
        [
            new OptionChoice("system-stack", "System fonts", "Name the theme's fonts with a generic fallback. Small file; readers without the fonts see a similar one."),
            new OptionChoice("embed", "Embed fonts", "Include the bundled fonts in the file so it looks the same everywhere (adds about 2-5 MB)."),
        ], "How the theme's fonts reach the reader.", "Appearance"),
        OptionDefinition.Choice(OptCover, "Cover", "theme", ThemeOverride, "Show a title block (title, subtitle, author, date) before the content.", "Structure"),
        OptionDefinition.Choice(OptToc, "Table of contents", "theme", ThemeOverride, "Add a linked contents list built from the headings.", "Structure"),
        OptionDefinition.Choice(OptImages, "Images", "data-url",
        [
            new OptionChoice("data-url", "Embed in the file", "Inline images as base64 data URLs: one self-contained file."),
            new OptionChoice("folder", "Save to a folder", "Write images to a <name>_files folder next to the page and link to them."),
            new OptionChoice("omit", "Leave out", "Show only the alt text."),
        ], "What to do with images embedded in the document.", "Images"),
        OptionDefinition.Bool(OptRawHtml, "Keep raw HTML", false, "Pass raw HTML from the source through unchanged. Off shows only its text, which is safer for content you did not write.", "Content", advanced: true),
        OptionDefinition.Bool(OptMinify, "Minify", false, "Write the page without line breaks or indentation.", "Output", advanced: true),
        OptionDefinition.Choice(OptLineEndings, "Line endings", "lf",
        [
            new OptionChoice("lf", "LF (macOS, Linux, Git)"),
            new OptionChoice("crlf", "CRLF (Windows)"),
        ], "Line ending characters in the output file.", "Output", advanced: true),
    ];

    public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options.WithDefaults(Options);
        var theme = context.Theme ?? BuiltInThemes.Find(options.GetString(OptTheme) ?? BuiltInThemes.DefaultId) ?? BuiltInThemes.Clean();
        var doc = context.Document;
        var result = new ExportResult();
        var minify = options.GetBool(OptMinify);

        context.Report(0, "Writing HTML");
        var imageMode = (options.GetString(OptImages) ?? "data-url").ToLowerInvariant() switch
        {
            "folder" or "extract-to-folder" => HtmlImageMode.Folder,
            "omit" => HtmlImageMode.Omit,
            _ => HtmlImageMode.DataUrl,
        };
        var images = new HtmlImageWriter(imageMode, context.BaseName, result);

        var body = RenderBody(doc, theme, images, options.GetBool(OptRawHtml));
        cancellationToken.ThrowIfCancellationRequested();
        context.Report(0.5, "Styling");

        var fontFaces = string.Equals(options.GetString(OptFonts), "embed", StringComparison.OrdinalIgnoreCase)
            ? HtmlFontEmbedder.Build(theme, result, minify)
            : null;
        var stylesheet = ThemeStylesheet.Generate(theme, fontFaces, minify);

        var includeCover = Tri(options.GetString(OptCover)) ?? theme.Cover.Enabled;
        var includeToc = Tri(options.GetString(OptToc)) ?? theme.Toc.Enabled;
        var title = doc.ResolveTitle() ?? context.BaseName;

        var nl = minify ? string.Empty : "\n";
        var sb = new StringBuilder(body.Length + stylesheet.Length + 4096);
        sb.Append("<!doctype html>").Append(nl);
        sb.Append("<html");
        if (!string.IsNullOrWhiteSpace(doc.Metadata.Language))
        {
            sb.Append(" lang=\"").Append(Attr(doc.Metadata.Language.Trim())).Append('"');
        }

        sb.Append('>').Append(nl).Append("<head>").Append(nl);
        sb.Append("<meta charset=\"utf-8\">").Append(nl);
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">").Append(nl);
        sb.Append("<title>").Append(Encode(title)).Append("</title>").Append(nl);
        AppendMeta(sb, "subtitle", doc.Metadata.Subtitle, nl);
        foreach (var author in doc.Metadata.Authors.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            AppendMeta(sb, "author", author, nl);
        }

        AppendMeta(sb, "description", doc.Metadata.Description, nl);
        if (doc.Metadata.Keywords.Count > 0)
        {
            AppendMeta(sb, "keywords", string.Join(", ", doc.Metadata.Keywords), nl);
        }

        if (doc.Metadata.Date is { } date)
        {
            AppendMeta(sb, "date", FormatDate(date), nl);
        }

        AppendMeta(sb, "generator", "AllInOne PDF", nl);
        sb.Append("<style>").Append(nl).Append(stylesheet).Append("</style>").Append(nl);
        sb.Append("</head>").Append(nl).Append("<body>").Append(nl);
        sb.Append("<div class=\"aio-page\">").Append(nl);

        if (includeCover)
        {
            AppendCover(sb, doc, theme, context.BaseName, nl);
        }

        if (includeToc)
        {
            AppendToc(sb, doc, theme, nl);
        }

        sb.Append(minify ? body : Prettify(body)).Append(nl);
        sb.Append("</div>").Append(nl).Append("</body>").Append(nl).Append("</html>").Append(nl);

        var html = sb.ToString();
        if (string.Equals(options.GetString(OptLineEndings), "crlf", StringComparison.OrdinalIgnoreCase))
        {
            html = html.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(html);
        result.Files.Insert(0, new OutputFile(context.BaseName + ".html", bytes, Format.MediaType));
        context.Report(1, "HTML written");
        return Task.FromResult(result);
    }

    private string RenderBody(Document doc, PdfTheme theme, HtmlImageWriter images, bool rawHtml)
    {
        // Theme labels ("Hinweis") replace the default kind name for callouts without a title of their own.
        var relabelled = new List<CalloutBlock>();
        foreach (var callout in doc.EnumerateAllBlocks().Concat(doc.Footnotes.SelectMany(f => Block.EnumerateRecursive(f.Blocks))).OfType<CalloutBlock>())
        {
            if (callout.Title is null && theme.Callouts.Kind(callout.Kind).Label is { Length: > 0 } label)
            {
                callout.Title = label;
                relabelled.Add(callout);
            }
        }

        try
        {
            var renderer = new DocumentHtmlRenderer(_highlighter);
            return renderer.RenderFragment(doc, new HtmlRenderOptions
            {
                HeadingAnchors = true,
                ExternalLinksBlank = false,
                AllowRawHtml = rawHtml,
                ResolveImage = images.Resolve,
            });
        }
        finally
        {
            foreach (var callout in relabelled)
            {
                callout.Title = null;
            }
        }
    }

    private static void AppendCover(StringBuilder sb, Document doc, PdfTheme theme, string baseName, string nl)
    {
        var cv = theme.Cover;
        var meta = doc.Metadata;
        var title = !string.IsNullOrWhiteSpace(cv.TitleOverride) ? cv.TitleOverride.Trim() : doc.ResolveTitle() ?? baseName;
        var style = cv.Style.ToString().ToLowerInvariant();
        sb.Append("<header class=\"aio-cover aio-cover-").Append(style).Append("\">").Append(nl);

        var banner = cv.Style == CoverStyle.Banner;
        if (banner)
        {
            sb.Append("<div class=\"aio-cover-band\">");
        }
        else if (cv.AccentBar && cv.Style != CoverStyle.Split)
        {
            sb.Append("<div class=\"aio-cover-accent\"></div>");
        }

        if (cv.ShowTitle)
        {
            sb.Append("<p class=\"aio-cover-title\">").Append(Encode(title)).Append("</p>");
        }

        if (cv.ShowSubtitle && !string.IsNullOrWhiteSpace(meta.Subtitle))
        {
            sb.Append("<p class=\"aio-cover-subtitle\">").Append(Encode(meta.Subtitle)).Append("</p>");
        }

        if (banner)
        {
            sb.Append("</div>");
        }

        var parts = new List<string>();
        if (cv.ShowAuthor && meta.Authors.Count > 0)
        {
            parts.Add(string.Join(", ", meta.Authors));
        }

        if (cv.ShowDate && meta.Date is { } date)
        {
            parts.Add(date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture));
        }

        if (parts.Count > 0)
        {
            sb.Append("<p class=\"aio-cover-meta\">").Append(Encode(string.Join(" · ", parts))).Append("</p>");
        }

        if (!string.IsNullOrWhiteSpace(cv.Note))
        {
            sb.Append("<p class=\"aio-cover-note\">").Append(Encode(cv.Note)).Append("</p>");
        }

        sb.Append(nl).Append("</header>").Append(nl);
    }

    /// <summary>A linked contents list. Must run after the body was rendered: rendering assigns the heading ids.</summary>
    private static void AppendToc(StringBuilder sb, Document doc, PdfTheme theme, string nl)
    {
        var depth = Math.Clamp(theme.Toc.Depth, 1, 6);
        var headings = doc.Blocks.OfType<HeadingBlock>().Where(h => h.Level <= depth && !string.IsNullOrWhiteSpace(h.Id)).ToList();
        if (headings.Count == 0)
        {
            return;
        }

        var top = headings.Min(h => h.Level);
        sb.Append("<nav class=\"aio-toc\" aria-labelledby=\"aio-toc-title\">").Append(nl);
        sb.Append("<p class=\"aio-toc-title\" id=\"aio-toc-title\">").Append(Encode(string.IsNullOrWhiteSpace(theme.Toc.Title) ? "Contents" : theme.Toc.Title)).Append("</p>").Append(nl);
        sb.Append("<ol class=\"aio-toc-list\">").Append(nl);
        foreach (var h in headings)
        {
            var level = h.Level - top + 1;
            sb.Append("<li class=\"aio-toc-entry aio-toc-l").Append(level.ToString(CultureInfo.InvariantCulture)).Append(level == 1 ? " aio-toc-top" : string.Empty).Append("\">")
              .Append("<a href=\"#").Append(Attr(h.Id)).Append("\">").Append(Encode(h.ToPlainText())).Append("</a></li>").Append(nl);
        }

        sb.Append("</ol>").Append(nl).Append("</nav>").Append(nl);
    }

    private static void AppendMeta(StringBuilder sb, string name, string? content, string nl)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            sb.Append("<meta name=\"").Append(name).Append("\" content=\"").Append(Attr(content.Trim())).Append("\">").Append(nl);
        }
    }

    private static string FormatDate(DateTimeOffset date) =>
        date.TimeOfDay == TimeSpan.Zero && date.Offset == TimeSpan.Zero
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>One block per line for readable source; the content of &lt;pre&gt; is never touched.</summary>
    private static string Prettify(string fragment)
    {
        var sb = new StringBuilder(fragment.Length + fragment.Length / 20);
        var last = 0;
        foreach (Match pre in PreElement().Matches(fragment))
        {
            sb.Append(BlockStart().Replace(fragment[last..pre.Index], "\n"));
            sb.Append(pre.Value);
            last = pre.Index + pre.Length;
        }

        sb.Append(BlockStart().Replace(fragment[last..], "\n"));
        return sb.ToString().TrimStart('\n');
    }

    private static bool? Tri(string? value) => value?.ToLowerInvariant() switch
    {
        "yes" or "true" => true,
        "no" or "false" => false,
        _ => null,
    };

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static string Attr(string? text) => WebUtility.HtmlEncode(text ?? string.Empty).Replace("\"", "&quot;", StringComparison.Ordinal);

    [GeneratedRegex(@"<pre\b[\s\S]*?</pre>", RegexOptions.IgnoreCase)]
    private static partial Regex PreElement();

    [GeneratedRegex(@"(?=<(?:(?:h[1-6]|p|ul|ol|li|figure|blockquote|aside|table|thead|tbody|tr|hr|section|pre)\b|div class=""aio-pagebreak""|/article>))")]
    private static partial Regex BlockStart();
}
