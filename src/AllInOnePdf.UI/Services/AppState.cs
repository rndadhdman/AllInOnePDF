using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.UI.Services;

/// <summary>
/// Per-session UI state shared across pages: the document currently open in the Reader and the last used output
/// folder. Raise <see cref="Changed"/> so other components re-render. The default PDF theme is not kept here:
/// <c>IThemeStore.GetDefaultThemeIdAsync</c> is its only source of truth.
/// </summary>
public sealed class AppState
{
    private Document? _document;
    private string? _documentPath;

    public event Action? Changed;

    /// <summary>Document open in the Reader (from a file or a conversion result).</summary>
    public Document? CurrentDocument => _document;

    /// <summary>Source path of <see cref="CurrentDocument"/>, when it came from disk (null for uploads and drops).</summary>
    public string? CurrentDocumentPath => _documentPath;

    /// <summary>Last folder outputs were written to (so the next conversion defaults to it).</summary>
    public string? LastOutputDirectory { get; set; }

    public void SetDocument(Document? document, string? path = null)
    {
        _document = document;
        _documentPath = path;
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();
}
