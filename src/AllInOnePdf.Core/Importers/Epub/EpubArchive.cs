using System.IO.Compression;
using System.Text;

namespace AllInOnePdf.Core.Importers.Epub;

/// <summary>
/// Read-only view of an EPUB zip with the safety checks an untrusted archive needs: every entry path is validated
/// (no "..", no absolute or drive paths), the entry count and sizes are capped, and reads are bounded so a
/// zip bomb cannot expand beyond the declared limits.
/// </summary>
internal sealed class EpubArchive : IDisposable
{
    public const int MaxEntries = 10_000;
    public const long MaxEntryBytes = 256L * 1024 * 1024;
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Ratio above which a large entry is treated as a decompression bomb.</summary>
    private const long MaxCompressionRatio = 200;

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZipArchiveEntry> _entriesIgnoreCase = new(StringComparer.OrdinalIgnoreCase);

    private EpubArchive(ZipArchive zip)
    {
        _zip = zip;
    }

    public static EpubArchive Open(Stream stream)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("This file is not a valid EPUB (it is not a ZIP archive).", ex);
        }

        var archive = new EpubArchive(zip);
        try
        {
            archive.Validate();
        }
        catch
        {
            archive.Dispose();
            throw;
        }

        return archive;
    }

    private void Validate()
    {
        if (_zip.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"This EPUB contains more than {MaxEntries:N0} files and was not opened.");
        }

        long total = 0;
        foreach (var entry in _zip.Entries)
        {
            var name = entry.FullName;
            if (!IsSafeEntryName(name))
            {
                throw new InvalidDataException($"This EPUB contains an unsafe file path ('{name}') and was not opened.");
            }

            if (entry.Length > MaxEntryBytes || (entry.Length > 16L * 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxCompressionRatio))
            {
                throw new InvalidDataException($"The EPUB entry '{name}' is too large or too highly compressed to be read safely.");
            }

            total += entry.Length;
            if (total > MaxTotalBytes)
            {
                throw new InvalidDataException("This EPUB expands to more than 2 GB and was not opened.");
            }

            if (name.EndsWith('/'))
            {
                continue;
            }

            _entries.TryAdd(name, entry);
            _entriesIgnoreCase.TryAdd(name, entry);
        }
    }

    /// <summary>Relative, forward-slash paths only: no "..", no leading slash, no drive letters or other colons.</summary>
    public static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\\', StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        if (name.StartsWith('/'))
        {
            return false;
        }

        foreach (var segment in name.Split('/'))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    public bool Contains(string path) => Find(path) is not null;

    public IEnumerable<string> EntryNames => _entries.Keys;

    private ZipArchiveEntry? Find(string path) =>
        _entries.GetValueOrDefault(path) ?? _entriesIgnoreCase.GetValueOrDefault(path);

    /// <summary>Length of an entry as declared in the archive, or null when it does not exist.</summary>
    public long? GetLength(string path) => Find(path)?.Length;

    /// <summary>Reads an entry, refusing to inflate more than <paramref name="maxBytes"/>. Null when missing or too large.</summary>
    public byte[]? ReadBytes(string path, long maxBytes)
    {
        var entry = Find(path);
        if (entry is null || entry.Length > maxBytes)
        {
            return null;
        }

        using var input = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes || total > entry.Length)
            {
                // The entry inflates to more than it declares: a corrupt archive or a bomb.
                throw new InvalidDataException($"The EPUB entry '{path}' is corrupt (it expands beyond its declared size).");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    public string? ReadText(string path, long maxBytes = 64L * 1024 * 1024)
    {
        var bytes = ReadBytes(path, maxBytes);
        if (bytes is null)
        {
            return null;
        }

        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Resolves <paramref name="href"/> relative to the archive file <paramref name="fromPath"/>.
    /// Returns null for URLs with a scheme, empty references and paths that climb above the archive root.
    /// </summary>
    public static string? ResolvePath(string fromPath, string href)
    {
        var value = href.Trim();
        var cut = value.IndexOfAny(['#', '?']);
        if (cut >= 0)
        {
            value = value[..cut];
        }

        if (value.Length == 0 || HasScheme(value))
        {
            return null;
        }

        try
        {
            value = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return null;
        }

        var segments = new List<string>();
        if (!value.StartsWith('/'))
        {
            var slash = fromPath.LastIndexOf('/');
            if (slash > 0)
            {
                segments.AddRange(fromPath[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        foreach (var segment in value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static bool HasScheme(string value)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && value[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    public void Dispose() => _zip.Dispose();
}
