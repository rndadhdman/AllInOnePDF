using AllInOnePdf.Layout.Drawing;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Entry point of the fluent API.</summary>
public static class Document
{
    /// <summary>
    /// Describes a document. <paramref name="handler"/> runs when the PDF is generated (once per
    /// <see cref="IDocument.GeneratePdf()"/> call) and adds the page descriptions.
    /// </summary>
    /// <param name="handler">Adds pages to the document.</param>
    public static IDocument Create(Action<IDocumentContainer> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new LayoutDocument(handler);
    }
}

/// <summary>A described document that can be written as PDF.</summary>
public interface IDocument
{
    /// <summary>Sets the PDF information dictionary values.</summary>
    /// <param name="metadata">Title, author and friends.</param>
    IDocument WithMetadata(DocumentMetadata metadata);

    /// <summary>Sets the output settings (image quality, raster resolution).</summary>
    /// <param name="settings">Output settings.</param>
    IDocument WithSettings(DocumentSettings settings);

    /// <summary>Lays the document out and returns the PDF.</summary>
    /// <exception cref="DocumentComposeException">A compose callback threw.</exception>
    /// <exception cref="DocumentLayoutException">Content cannot be laid out.</exception>
    byte[] GeneratePdf();

    /// <summary>Lays the document out and returns the PDF bytes, reporting the page count and the pages of every element wrapped with <c>TrackPages</c>.</summary>
    /// <param name="info">What the layout learnt.</param>
    byte[] GeneratePdf(out DocumentLayoutInfo info);

    /// <summary>Lays the document out and writes the PDF to <paramref name="stream"/> (nothing is written on failure).</summary>
    /// <param name="stream">Target stream.</param>
    void GeneratePdf(Stream stream);
}

/// <summary>Receives the page descriptions of a document.</summary>
public interface IDocumentContainer
{
    /// <summary>Adds a page description; it produces as many pages as its content needs.</summary>
    /// <param name="handler">Configures the page.</param>
    void Page(Action<PageDescriptor> handler);
}

/// <summary>The document behind <see cref="Document.Create"/>.</summary>
internal sealed class LayoutDocument(Action<IDocumentContainer> handler) : IDocument
{
    private DocumentMetadata _metadata = new();
    private DocumentSettings _settings = new();

    public IDocument WithMetadata(DocumentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = metadata;
        return this;
    }

    public IDocument WithSettings(DocumentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        return this;
    }

    public byte[] GeneratePdf() => GeneratePdf(out _);

    public byte[] GeneratePdf(out DocumentLayoutInfo info)
    {
        var pages = Compose();
        using var buffer = new MemoryStream();
        info = PageLayout.Generate(pages, _metadata, _settings, buffer);
        return buffer.ToArray();
    }

    public void GeneratePdf(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = GeneratePdf();
        stream.Write(bytes);
    }

    /// <summary>Runs the compose callbacks; anything they throw surfaces as <see cref="DocumentComposeException"/>.</summary>
    private List<PageDescriptor> Compose()
    {
        var container = new DocumentContainer();
        try
        {
            handler(container);
        }
        catch (Exception ex) when (ex is not DocumentComposeException and not DocumentLayoutException)
        {
            throw new DocumentComposeException($"Composing the document failed: {ex.Message}", ex);
        }

        return container.Pages;
    }

    private sealed class DocumentContainer : IDocumentContainer
    {
        public List<PageDescriptor> Pages { get; } = [];

        public void Page(Action<PageDescriptor> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            var page = new PageDescriptor();
            handler(page);
            Pages.Add(page);
        }
    }
}
