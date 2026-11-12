namespace AllInOnePdf.Core.Importers.Html;

/// <summary>How <see cref="HtmlDocumentConverter"/> maps a DOM onto the document model.</summary>
internal sealed class HtmlConversionSettings
{
    /// <summary>Use <c>&lt;main&gt;</c>, a single <c>&lt;article&gt;</c> or the largest content block, and drop navigation chrome.</summary>
    public bool MainContentOnly { get; init; } = true;

    /// <summary>Keep unknown or unsupported elements as <c>HtmlBlock</c>/<c>HtmlInline</c> instead of flattening them to text.</summary>
    public bool KeepRawHtml { get; init; }

    /// <summary>Treat newlines in text as hard line breaks (for HTML that relies on <c>white-space: pre-line</c>).</summary>
    public bool PreserveLineBreaks { get; init; }

    /// <summary>Load local images through <see cref="HtmlImageResolver"/> and embed them as resources.</summary>
    public bool EmbedImages { get; init; } = true;

    public long MaxImageBytes { get; init; } = 25L * 1024 * 1024;
}
