using System.Security.Cryptography;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Exporters.Epub;

/// <summary>A file inside <c>OEBPS/</c> with its manifest entry.</summary>
internal sealed record EpubItem(string Id, string Href, string MediaType, byte[] Data, string? Properties = null);

/// <summary>
/// Turns image sources into package items: <c>res:</c> resources and <c>data:</c> URLs become files under
/// <c>images/</c>; remote URLs, local paths and formats EPUB readers are not required to support are dropped with a
/// warning (the renderer then shows the alt text).
/// </summary>
internal sealed class EpubImageStore
{
    private static readonly Dictionary<string, string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/svg+xml"] = ".svg",
    };

    private readonly Document _document;
    private readonly List<string> _warnings;
    private readonly Dictionary<string, EpubItem> _bySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EpubItem> _byHash = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private int _remote;
    private int _unsupported;
    private int _unresolved;

    public EpubImageStore(Document document, List<string> warnings)
    {
        _document = document;
        _warnings = warnings;
    }

    public List<EpubItem> Items { get; } = [];

    /// <summary>Href of an image relative to a chapter in <c>text/</c>, or null when the image is left out.</summary>
    public string? Resolve(Document document, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        if (_bySource.TryGetValue(source, out var known))
        {
            return "../" + known.Href;
        }

        var resource = _document.FindResource(source) ?? document.FindResource(source);
        if (resource is not null)
        {
            var mediaType = resource.MediaType;
            if (!Supported.ContainsKey(mediaType))
            {
                Once(source, () => _unsupported++);
                return null;
            }

            return Add(source, mediaType, resource.Data, resource.FileName ?? resource.Id);
        }

        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (TryDecodeDataUrl(source, out var mediaType, out var data))
            {
                if (!Supported.ContainsKey(mediaType))
                {
                    Once(source, () => _unsupported++);
                    return null;
                }

                return Add(source, mediaType, data, null);
            }

            Once(source, () => _unresolved++);
            return null;
        }

        if (source.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || source.StartsWith("//", StringComparison.Ordinal))
        {
            Once(source, () => _remote++);
            return null;
        }

        Once(source, () => _unresolved++);
        return null;
    }

    /// <summary>Adds the summary warnings once all chapters are rendered.</summary>
    public void Flush()
    {
        if (_remote > 0)
        {
            _warnings.Add($"{_remote} remote image(s) were left out: the EPUB is built offline and readers may not load web images.");
        }

        if (_unsupported > 0)
        {
            _warnings.Add($"{_unsupported} image(s) were left out because EPUB readers only support PNG, JPEG, GIF, WebP and SVG.");
        }

        if (_unresolved > 0)
        {
            _warnings.Add($"{_unresolved} image(s) could not be found in the document and were replaced by their alt text.");
        }
    }

    private void Once(string source, Action count)
    {
        if (_warned.Add(source))
        {
            count();
        }
    }

    private string Add(string source, string mediaType, byte[] data, string? nameHint)
    {
        var hash = Convert.ToHexString(SHA256.HashData(data));
        if (!_byHash.TryGetValue(hash, out var item))
        {
            var ext = Supported[mediaType];
            var index = Items.Count + 1;
            var stem = EpubXml.Token(Path.GetFileNameWithoutExtension(nameHint ?? string.Empty), "image");
            var href = $"images/img-{index:D3}-{stem}{ext}";
            item = new EpubItem($"img-{index:D3}", href, mediaType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : mediaType.ToLowerInvariant(), data);
            Items.Add(item);
            _byHash[hash] = item;
        }

        _bySource[source] = item;
        return "../" + item.Href;
    }

    private static bool TryDecodeDataUrl(string url, out string mediaType, out byte[] data)
    {
        mediaType = string.Empty;
        data = [];
        var comma = url.IndexOf(',', StringComparison.Ordinal);
        if (comma < 5)
        {
            return false;
        }

        var header = url[5..comma];
        var payload = url[(comma + 1)..];
        var parts = header.Split(';', StringSplitOptions.TrimEntries);
        mediaType = parts[0].ToLowerInvariant();
        var base64 = parts.Skip(1).Any(p => p.Equals("base64", StringComparison.OrdinalIgnoreCase));
        try
        {
            data = base64
                ? Convert.FromBase64String(Uri.UnescapeDataString(payload).Trim())
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            return data.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
