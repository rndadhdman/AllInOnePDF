using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Storage;

/// <summary>Persists publications as <c>.aiopub</c> packages.</summary>
public interface IPublicationStore
{
    /// <summary>Every readable publication, most recently modified first. Unreadable files are skipped.</summary>
    Task<IReadOnlyList<PublicationSummary>> ListAsync(CancellationToken ct = default);

    /// <summary>The publication with its assets, or null when it does not exist or cannot be read.</summary>
    Task<Publication?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Normalises the publication, sets <see cref="Publication.Modified"/> and writes it atomically (temp file + move).</summary>
    Task SaveAsync(Publication publication, byte[]? thumbnailPng = null, CancellationToken ct = default);

    /// <summary>Deletes the publication; false when it did not exist.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Saves a copy with a new id and the name <paramref name="newName"/> (default: "&lt;name&gt; copy").</summary>
    /// <exception cref="KeyNotFoundException">No publication has that id.</exception>
    Task<Publication> DuplicateAsync(string id, string? newName = null, CancellationToken ct = default);

    /// <summary>
    /// Imports a package. A fresh id is assigned when the id is already taken, and the name gets " (imported)" when
    /// another publication already has it.
    /// </summary>
    /// <exception cref="InvalidDataException">The package is invalid.</exception>
    Task<Publication> ImportAsync(Stream package, CancellationToken ct = default);

    /// <summary>The saved thumbnail PNG, or null.</summary>
    Task<byte[]?> GetThumbnailAsync(string id, CancellationToken ct = default);

    /// <summary>Raised after a save, delete or import.</summary>
    event Action? Changed;
}

/// <summary>What the gallery shows for a saved publication.</summary>
/// <param name="Id">Publication id.</param>
/// <param name="Name">Publication name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="Modified">Last save.</param>
/// <param name="ThemeName">Name of the theme snapshot.</param>
/// <param name="PageSize">Page size of the theme snapshot.</param>
/// <param name="Orientation">Page orientation of the theme snapshot.</param>
/// <param name="SectionCount">Number of sections.</param>
/// <param name="Tags">Publication tags.</param>
public sealed record PublicationSummary(
    string Id,
    string Name,
    string? Description,
    DateTimeOffset Modified,
    string ThemeName,
    PageSizeName PageSize,
    PageOrientation Orientation,
    int SectionCount,
    IReadOnlyList<string> Tags)
{
    /// <summary>Builds the summary of a publication.</summary>
    public static PublicationSummary From(Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return new(
            publication.Id,
            publication.Name,
            publication.Description,
            publication.Modified,
            publication.Theme.Name,
            publication.Theme.Page.Size,
            publication.Theme.Page.Orientation,
            publication.Sections.Count,
            [.. publication.Tags]);
    }
}
