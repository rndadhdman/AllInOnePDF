using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Validation;

namespace AllInOnePdf.Publishing.Serialization;

/// <summary>
/// The <c>.aiopub</c> file: a zip holding <c>publication.json</c> (the publication without asset bytes),
/// <c>assets/&lt;id&gt;</c> (raw image bytes, id = 32 lowercase hex characters) and an optional <c>thumbnail.png</c>.
/// Everything is read in memory; nothing is ever extracted to disk.
/// </summary>
public static class PublicationPackage
{
    /// <summary>File extension including the dot.</summary>
    public const string Extension = ".aiopub";

    /// <summary>Media type used for downloads.</summary>
    public const string MediaType = "application/vnd.allinonepdf.publication+zip";

    /// <summary>Largest package accepted, compressed or uncompressed: 200 MB.</summary>
    public const long MaxPackageBytes = 200L * 1024 * 1024;

    /// <summary>Entry holding the publication JSON.</summary>
    public const string PublicationEntry = "publication.json";

    /// <summary>Entry holding the optional first-page thumbnail.</summary>
    public const string ThumbnailEntry = "thumbnail.png";

    /// <summary>Folder prefix of the asset entries.</summary>
    public const string AssetsFolder = "assets/";

    /// <summary>
    /// Writes a normalised copy of <paramref name="publication"/> (the argument is not changed) with its assets and an
    /// optional thumbnail. Assets without bytes are left out.
    /// </summary>
    public static byte[] Write(Publication publication, byte[]? thumbnail = null)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var copy = PublicationValidator.Normalize(publication.Clone());
        copy.Assets.RemoveAll(a => a.Data.Length == 0);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, PublicationEntry, Encoding.UTF8.GetBytes(copy.ToJson()), CompressionLevel.Optimal);
            foreach (var asset in copy.Assets)
            {
                WriteEntry(zip, AssetsFolder + asset.Id, asset.Data, CompressionLevel.Fastest);
            }

            if (thumbnail is { Length: > 0 })
            {
                WriteEntry(zip, ThumbnailEntry, thumbnail, CompressionLevel.Fastest);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Reads a package and returns the normalised publication (assets filled in) and the thumbnail, if any.</summary>
    /// <exception cref="InvalidDataException">
    /// The package is larger than 200 MB, is not a zip, has an unknown entry or an invalid asset entry name, or its
    /// <c>publication.json</c> is missing or malformed.
    /// </exception>
    public static (Publication Publication, byte[]? Thumbnail) Read(Stream package)
    {
        ArgumentNullException.ThrowIfNull(package);
        using var zip = Open(package);
        var entries = IndexEntries(zip);

        var publication = Deserialize(entries);
        var assets = new List<PublicationAsset>();
        foreach (var asset in publication.Assets ?? [])
        {
            if (asset is not null && PublicationValidator.IsAssetId(asset.Id) && entries.TryGetValue(AssetsFolder + asset.Id, out var entry))
            {
                asset.Data = ReadEntry(entry);
                assets.Add(asset);
            }
        }

        publication.Assets = assets;
        PublicationValidator.Normalize(publication);

        var thumbnail = entries.TryGetValue(ThumbnailEntry, out var thumb) ? ReadEntry(thumb) : null;
        return (publication, thumbnail);
    }

    /// <summary>Reads only <c>publication.json</c> (asset bytes stay empty), for listings. Validates like <see cref="Read"/>.</summary>
    internal static Publication ReadManifest(Stream package)
    {
        using var zip = Open(package);
        var publication = Deserialize(IndexEntries(zip));
        return PublicationValidator.Normalize(publication);
    }

    /// <summary>Reads only the thumbnail entry, or null when the package has none.</summary>
    internal static byte[]? ReadThumbnail(Stream package)
    {
        using var zip = Open(package);
        return IndexEntries(zip).TryGetValue(ThumbnailEntry, out var entry) ? ReadEntry(entry) : null;
    }

    private static ZipArchive Open(Stream package)
    {
        Stream source = package;
        if (package.CanSeek)
        {
            if (package.Length - package.Position > MaxPackageBytes)
            {
                throw new InvalidDataException("The package is larger than 200 MB.");
            }
        }
        else
        {
            var copy = new MemoryStream();
            CopyBounded(package, copy, MaxPackageBytes, "The package is larger than 200 MB.");
            copy.Position = 0;
            source = copy;
        }

        try
        {
            return new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: ReferenceEquals(source, package));
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The file is not a publication package (it is not a valid zip).", ex);
        }
    }

    private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive zip)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name == AssetsFolder)
            {
                continue; // a directory entry written by some zip tools
            }

            var known = name is PublicationEntry or ThumbnailEntry
                || (name.StartsWith(AssetsFolder, StringComparison.Ordinal) && PublicationValidator.IsAssetId(name[AssetsFolder.Length..]));
            if (!known)
            {
                throw new InvalidDataException($"The package contains an unexpected entry '{name}'.");
            }

            if (!entries.TryAdd(name, entry))
            {
                throw new InvalidDataException($"The package contains the entry '{name}' twice.");
            }

            total += entry.Length;
            if (total > MaxPackageBytes)
            {
                throw new InvalidDataException("The package unpacks to more than 200 MB.");
            }
        }

        return entries;
    }

    private static Publication Deserialize(Dictionary<string, ZipArchiveEntry> entries)
    {
        if (!entries.TryGetValue(PublicationEntry, out var entry))
        {
            throw new InvalidDataException("The package has no publication.json.");
        }

        try
        {
            return JsonSerializer.Deserialize<Publication>(ReadEntry(entry), PublishingJson.Options)
                ?? throw new InvalidDataException("publication.json is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("publication.json could not be read: " + ex.Message, ex);
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream(entry.Length is > 0 and <= MaxPackageBytes ? (int)entry.Length : 0);
        CopyBounded(stream, buffer, MaxPackageBytes, $"The entry '{entry.FullName}' is larger than 200 MB.");
        return buffer.ToArray();
    }

    private static void CopyBounded(Stream source, Stream target, long limit, string message)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException(message);
            }

            target.Write(chunk, 0, read);
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(data);
    }
}
