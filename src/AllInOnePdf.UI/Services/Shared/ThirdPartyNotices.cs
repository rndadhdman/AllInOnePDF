using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AllInOnePdf.UI.Services.Shared;

/// <summary>
/// The repository's <c>THIRD-PARTY-NOTICES.md</c>, embedded in the UI assembly at build time, and its HTML rendering for
/// the notices dialog. Raw HTML in the file is shown as text (Markdig <c>DisableHtml</c>) and links become plain text, so
/// nothing in the dialog can navigate the app's window.
/// </summary>
public static class ThirdPartyNotices
{
    /// <summary>Logical name of the embedded resource (set in the UI project file).</summary>
    public const string ResourceName = "AllInOnePdf.UI.THIRD-PARTY-NOTICES.md";

    private static readonly Lazy<string?> MarkdownText = new(Load);
    private static readonly Lazy<string?> HtmlText = new(() => MarkdownText.Value is { } md ? ToHtml(md) : null);

    /// <summary>The Markdown text, or null when the resource is missing from this build.</summary>
    public static string? Markdown => MarkdownText.Value;

    /// <summary>The rendered HTML, or null when the resource is missing from this build.</summary>
    public static string? Html => HtmlText.Value;

    /// <summary>
    /// Renders notices Markdown to HTML for the in-app dialog: tables, code blocks and headings; HTML encoded; links
    /// as text. The file's own title (the dialog already shows it) and its "How to see this in the app" line are left
    /// out. Every level-2 section after the first (the licence texts, about 6,000 lines) becomes a collapsed
    /// <c>&lt;details&gt;</c>, so the dialog opens at once instead of laying out the whole text first.
    /// </summary>
    public static string ToHtml(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
        var document = Markdig.Markdown.Parse(markdown, pipeline);
        FlattenLinks(document);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);

        var blocks = document.ToList();
        var index = 0;
        if (blocks.Count > 0 && blocks[0] is HeadingBlock { Level: 1 })
        {
            index++;
        }

        var sectionCount = 0;
        var inDetails = false;
        for (; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (block is ParagraphBlock paragraph && IsInAppHint(paragraph))
            {
                continue;
            }

            if (block is HeadingBlock { Level: 2 } heading)
            {
                sectionCount++;
                if (inDetails)
                {
                    writer.Write("</details>\n");
                    inDetails = false;
                }

                // The first section (Notes) stays open; the licence texts are collapsed.
                if (sectionCount > 1)
                {
                    writer.Write("<details class=\"aio-notice\"><summary>");
                    renderer.WriteLeafInline(heading);
                    writer.Write("</summary>\n");
                    inDetails = true;
                    continue;
                }
            }

            renderer.Render(block);
        }

        if (inDetails)
        {
            writer.Write("</details>\n");
        }

        writer.Flush();
        return writer.ToString();
    }

    /// <summary>The file's "How to see this in the app: …" line, which only makes sense outside the app.</summary>
    private static bool IsInAppHint(ParagraphBlock paragraph) =>
        paragraph.Inline?.FirstChild is LiteralInline literal &&
        literal.Content.ToString().StartsWith("How to see this in the app", StringComparison.OrdinalIgnoreCase);

    private static void FlattenLinks(MarkdownDocument document)
    {
        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            autolink.ReplaceBy(new LiteralInline(autolink.Url));
        }

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (link.IsImage)
            {
                link.ReplaceBy(new LiteralInline(string.Empty));
                continue;
            }

            while (link.FirstChild is { } child)
            {
                child.Remove();
                link.InsertBefore(child);
            }

            link.Remove();
        }
    }

    private static string? Load()
    {
        using var stream = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
