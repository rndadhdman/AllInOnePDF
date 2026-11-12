using System.Text.Json;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Serialization;
using AllInOnePdf.Publishing.Validation;
using Microsoft.Extensions.Logging;

namespace AllInOnePdf.Publishing.Storage;

/// <summary>
/// Keeps publications as <c>&lt;data&gt;/publications/&lt;id&gt;.aiopub</c>. Mirrors <c>FileThemeStore</c>: one gate
/// serialises file access, unreadable files are logged and skipped, writes go to a temp file that is then moved over.
/// </summary>
public sealed class FilePublicationStore : IPublicationStore, IDisposable
{
    private readonly string _directory;
    private readonly ILogger<FilePublicationStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a store in the publications folder of <paramref name="paths"/>.</summary>
    public FilePublicationStore(AppPaths paths, ILogger<FilePublicationStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = DirectoryFor(paths);
        _logger = logger;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>The folder holding the packages.</summary>
    public string DirectoryPath => _directory;

    /// <summary>The publications folder of <paramref name="paths"/> (<c>&lt;data&gt;/publications</c>).</summary>
    public static string DirectoryFor(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.PublicationsDirectory;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <inheritdoc />
    public async Task<IReadOnlyList<PublicationSummary>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<PublicationSummary>();
        if (!Directory.Exists(_directory))
        {
            return list;
        }

        await _gate.WaitAsync(ct);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*" + PublicationPackage.Extension))
            {
                ct.ThrowIfCancellationRequested();
                if (!PublicationValidator.IsSafeId(Path.GetFileNameWithoutExtension(file)))
                {
                    continue;
                }

                try
                {
                    await using var stream = OpenRead(file);
                    var publication = PublicationPackage.ReadManifest(stream);
                    publication.Id = Path.GetFileNameWithoutExtension(file);
                    list.Add(PublicationSummary.From(publication));
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    _logger.LogWarning(ex, "Skipping unreadable publication file {File}", file);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return [.. list.OrderByDescending(s => s.Modified).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <inheritdoc />
    public async Task<Publication?> GetAsync(string id, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(id, ct);
        return loaded?.Publication;
    }

    /// <inheritdoc />
    public async Task SaveAsync(Publication publication, byte[]? thumbnailPng = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        PublicationValidator.Normalize(publication);
        publication.Modified = DateTimeOffset.UtcNow;
        var bytes = PublicationPackage.Write(publication, thumbnailPng);

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_directory);
            var target = PathFor(publication.Id)!;
            var tmp = target + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var file = PathFor(id);
        if (file is null)
        {
            return false;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(file))
            {
                return false;
            }

            File.Delete(file);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return true;
    }

    /// <inheritdoc />
    public async Task<Publication> DuplicateAsync(string id, string? newName = null, CancellationToken ct = default)
    {
        var (source, thumbnail) = await LoadAsync(id, ct) ?? throw new KeyNotFoundException($"Publication '{id}' not found.");
        var copy = source.Clone();
        copy.Id = Publication.NewId();
        copy.Name = string.IsNullOrWhiteSpace(newName) ? source.Name + " copy" : newName.Trim();
        copy.Created = DateTimeOffset.UtcNow;
        await SaveAsync(copy, thumbnail, ct);
        return copy;
    }

    /// <inheritdoc />
    public async Task<Publication> ImportAsync(Stream package, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var (publication, thumbnail) = PublicationPackage.Read(package);
        if (File.Exists(PathFor(publication.Id)))
        {
            publication.Id = Publication.NewId();
        }

        // A second card with the same name would be indistinguishable in the gallery: mark the import.
        var names = (await ListAsync(ct)).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        publication.Name = ImportedName(publication.Name, names);

        await SaveAsync(publication, thumbnail, ct);
        return publication;
    }

    /// <summary>
    /// <paramref name="name"/> when no publication has it yet, otherwise "&lt;name&gt; (imported)", then
    /// "(imported 2)", "(imported 3)"… until the name is free.
    /// </summary>
    public static string ImportedName(string name, IReadOnlySet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        name = string.IsNullOrWhiteSpace(name) ? "Imported publication" : name.Trim();
        if (!taken.Contains(name))
        {
            return name;
        }

        var candidate = name + " (imported)";
        for (var i = 2; taken.Contains(candidate) && i < 1000; i++)
        {
            candidate = $"{name} (imported {i})";
        }

        return candidate;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetThumbnailAsync(string id, CancellationToken ct = default)
    {
        var file = PathFor(id);
        if (file is null || !File.Exists(file))
        {
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            await using var stream = OpenRead(file);
            return PublicationPackage.ReadThumbnail(stream);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            _logger.LogWarning(ex, "Thumbnail of publication file {File} could not be read", file);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(Publication Publication, byte[]? Thumbnail)?> LoadAsync(string id, CancellationToken ct)
    {
        var file = PathFor(id);
        if (file is null || !File.Exists(file))
        {
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            await using var stream = OpenRead(file);
            var (publication, thumbnail) = PublicationPackage.Read(stream);
            publication.Id = id;
            return (publication, thumbnail);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            _logger.LogWarning(ex, "Publication file {File} could not be read", file);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static FileStream OpenRead(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: false);

    private static bool IsReadFailure(Exception ex) =>
        ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException;

    /// <summary>The package path for <paramref name="id"/>, or null when the id is not a safe file name.</summary>
    private string? PathFor(string? id) =>
        PublicationValidator.IsSafeId(id) ? Path.Combine(_directory, id + PublicationPackage.Extension) : null;
}
