using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Primitives;

namespace AllInOnePdf.Desktop;

/// <summary>
/// Serves the app's wwwroot plus Razor Class Library / package static web assets (<c>_content/…</c>) by reading the
/// <c>{AssemblyName}.staticwebassets.runtime.json</c> manifest the SDK emits for non-published builds.
/// Published output copies every asset into wwwroot, so the manifest is simply absent there and the physical provider suffices.
/// </summary>
public sealed class StaticWebAssetsFileProvider : IFileProvider
{
    private readonly PhysicalFileProvider? _physical;
    private readonly string[] _contentRoots;
    private readonly Node _root;

    private StaticWebAssetsFileProvider(PhysicalFileProvider? physical, string[] contentRoots, Node root)
    {
        _physical = physical;
        _contentRoots = contentRoots;
        _root = root;
    }

    public static IFileProvider Create(string baseDirectory, string assemblyName)
    {
        var wwwroot = Path.Combine(baseDirectory, "wwwroot");
        var physical = Directory.Exists(wwwroot) ? new PhysicalFileProvider(wwwroot) : null;

        var manifestPath = Path.Combine(baseDirectory, assemblyName + ".staticwebassets.runtime.json");
        if (!File.Exists(manifestPath))
        {
            return (IFileProvider?)physical ?? new NullFileProvider();
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var roots = doc.RootElement.GetProperty("ContentRoots").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
            var root = Node.Parse(doc.RootElement.GetProperty("Root"));
            return new StaticWebAssetsFileProvider(physical, roots, root);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (IFileProvider?)physical ?? new NullFileProvider();
        }
    }

    public IFileInfo GetFileInfo(string subpath)
    {
        var physical = _physical?.GetFileInfo(subpath);
        if (physical is { Exists: true })
        {
            return physical;
        }

        var resolved = Resolve(subpath);
        if (resolved is not null && File.Exists(resolved))
        {
            return new PhysicalFileInfo(new FileInfo(resolved));
        }

        return physical ?? new NotFoundFileInfo(subpath);
    }

    public IDirectoryContents GetDirectoryContents(string subpath) =>
        _physical?.GetDirectoryContents(subpath) ?? NotFoundDirectoryContents.Singleton;

    public IChangeToken Watch(string filter) => _physical?.Watch(filter) ?? NullChangeToken.Singleton;

    private string? Resolve(string subpath)
    {
        var segments = subpath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var node = _root;
        (int RootIndex, int Depth)? pattern = node.PatternRoot is { } pr ? (pr, 0) : null;

        for (var i = 0; i < segments.Length; i++)
        {
            if (node.Children is null || !node.Children.TryGetValue(segments[i], out var child))
            {
                node = null;
                break;
            }

            node = child;
            if (node.PatternRoot is { } idx)
            {
                pattern = (idx, i + 1);
            }
        }

        if (segments.Any(s => s is "." or ".."))
        {
            return null;
        }

        if (node?.Asset is { } asset && asset.RootIndex < _contentRoots.Length)
        {
            return Contain(_contentRoots[asset.RootIndex], asset.SubPath.Replace('/', Path.DirectorySeparatorChar));
        }

        if (pattern is { } p && p.RootIndex < _contentRoots.Length)
        {
            var remainder = string.Join(Path.DirectorySeparatorChar, segments.Skip(p.Depth));
            return Contain(_contentRoots[p.RootIndex], remainder);
        }

        return null;
    }

    /// <summary>Joins and verifies the result still lives under the content root.</summary>
    private static string? Contain(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private sealed class Node
    {
        public Dictionary<string, Node>? Children { get; init; }
        public (int RootIndex, string SubPath)? Asset { get; init; }
        /// <summary>Content root index of a "**" pattern declared on this node, if any.</summary>
        public int? PatternRoot { get; init; }

        public static Node Parse(JsonElement element)
        {
            Dictionary<string, Node>? children = null;
            if (element.TryGetProperty(nameof(Children), out var childrenEl) && childrenEl.ValueKind == JsonValueKind.Object)
            {
                children = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in childrenEl.EnumerateObject())
                {
                    children[prop.Name] = Parse(prop.Value);
                }
            }

            (int, string)? asset = null;
            if (element.TryGetProperty(nameof(Asset), out var assetEl) && assetEl.ValueKind == JsonValueKind.Object)
            {
                asset = (assetEl.GetProperty("ContentRootIndex").GetInt32(), assetEl.GetProperty("SubPath").GetString() ?? string.Empty);
            }

            int? patternRoot = null;
            if (element.TryGetProperty("Patterns", out var patternsEl) && patternsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var pattern in patternsEl.EnumerateArray())
                {
                    if (pattern.TryGetProperty("ContentRootIndex", out var idx))
                    {
                        patternRoot = idx.GetInt32();
                        break;
                    }
                }
            }

            return new Node { Children = children, Asset = asset, PatternRoot = patternRoot };
        }
    }
}
