using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllInOnePdf.Core.Storage;

/// <summary>An autosaved draft: the bytes the editor wrote and when it wrote them.</summary>
/// <param name="Content">The draft content (a theme's JSON, a publication package, …).</param>
/// <param name="Written">When the draft was written (UTC).</param>
public sealed record Draft(byte[] Content, DateTimeOffset Written);

/// <summary>A draft in a listing.</summary>
/// <param name="Key">The key the draft was written under.</param>
/// <param name="Written">When the draft was written (UTC).</param>
/// <param name="Length">Size of the draft content in bytes.</param>
public sealed record DraftInfo(string Key, DateTimeOffset Written, long Length);

/// <summary>
/// Crash-recovery drafts for the editors: unsaved work is written here shortly after each change and offered back
/// when the same item is opened again. Keys are free-form strings such as <c>theme:&lt;id&gt;</c>. Implementations are
/// shared by every window and circuit, never throw for a missing or unreadable draft, and write atomically.
/// </summary>
public interface IDraftStore
{
    /// <summary>Writes (or replaces) the draft for <paramref name="key"/>.</summary>
    Task WriteAsync(string key, byte[] content, CancellationToken ct = default);

    /// <summary>Reads the draft for <paramref name="key"/>; null when there is none or it cannot be read.</summary>
    Task<Draft?> TryReadAsync(string key, CancellationToken ct = default);

    /// <summary>Deletes the draft for <paramref name="key"/> (nothing happens when there is none).</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>Lists every readable draft, newest first.</summary>
    Task<IReadOnlyList<DraftInfo>> ListAsync(CancellationToken ct = default);
}

/// <summary>Helpers on top of <see cref="IDraftStore"/>.</summary>
public static class DraftStoreExtensions
{
    /// <summary>Deletes drafts written longer ago than <paramref name="maxAge"/>. Returns how many were deleted.</summary>
    public static async Task<int> DeleteOlderThanAsync(this IDraftStore store, TimeSpan maxAge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var deleted = 0;
        foreach (var draft in await store.ListAsync(ct))
        {
            if (draft.Written < cutoff)
            {
                await store.DeleteAsync(draft.Key, ct);
                deleted++;
            }
        }

        return deleted;
    }
}

/// <summary>
/// <see cref="IDraftStore"/> that keeps one <c>.draft</c> file per key in <see cref="AppPaths.DraftsDirectory"/>.
/// Each file starts with a small header (magic, key, written time, content length) so listings can report the
/// original key and files that are truncated or foreign are recognised and ignored.
/// </summary>
public sealed class FileDraftStore : IDraftStore
{
    /// <summary>File extension of draft files.</summary>
    public const string Extension = ".draft";

    private const int MaxStemLength = 64;
    private static readonly byte[] Magic = "AIODRAFT"u8.ToArray();
    private const byte FormatVersion = 1;

    private readonly string _directory;
    private readonly ILogger<FileDraftStore> _logger;

    /// <summary>Creates the store in the app's drafts folder.</summary>
    public FileDraftStore(AppPaths paths, ILogger<FileDraftStore>? logger = null)
        : this(paths.DraftsDirectory, logger)
    {
    }

    /// <summary>Creates the store in <paramref name="directory"/> (tests).</summary>
    public FileDraftStore(string directory, ILogger<FileDraftStore>? logger = null)
    {
        _directory = directory;
        _logger = logger ?? NullLogger<FileDraftStore>.Instance;
    }

    /// <summary>The folder the drafts live in.</summary>
    public string Directory => _directory;

    /// <summary>
    /// The file name used for <paramref name="key"/>: the key reduced to letters, digits, <c>-</c>, <c>_</c> and
    /// <c>.</c> (at most 64 characters), plus a short hash of the full key so different keys never share a file.
    /// </summary>
    public static string FileNameFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var stem = new StringBuilder(Math.Min(key.Length, MaxStemLength));
        foreach (var c in key.Trim())
        {
            if (stem.Length == MaxStemLength)
            {
                break;
            }

            stem.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        var cleaned = stem.ToString().Trim('.', ' ');
        if (cleaned.Length == 0)
        {
            cleaned = "draft";
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 4);
        return $"{cleaned}-{hash}{Extension}";
    }

    /// <inheritdoc />
    public async Task WriteAsync(string key, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        System.IO.Directory.CreateDirectory(_directory);
        var path = PathFor(key);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await stream.WriteAsync(Header(key, DateTimeOffset.UtcNow, content.LongLength), ct);
                await stream.WriteAsync(content, ct);
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <inheritdoc />
    public async Task<Draft?> TryReadAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (!TryParse(bytes, out var storedKey, out var written, out var offset, out var length) || !string.Equals(storedKey, key, StringComparison.Ordinal))
            {
                _logger.LogWarning("Ignoring the unreadable draft file {Path}.", path);
                return null;
            }

            return new Draft(bytes.AsSpan(offset, checked((int)length)).ToArray(), written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The draft file {Path} could not be read.", path);
            return null;
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var path = PathFor(key);
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The draft file {Path} could not be deleted.", path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DraftInfo>> ListAsync(CancellationToken ct = default)
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return [];
        }

        var result = new List<DraftInfo>();
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*" + Extension))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                var head = new byte[Math.Min(stream.Length, 64 * 1024)];
                var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
                if (TryParseHeader(head.AsSpan(0, read), out var key, out var written, out var offset, out var length)
                    && offset + length == stream.Length)
                {
                    result.Add(new DraftInfo(key, written, length));
                }
                else
                {
                    _logger.LogWarning("Ignoring the unreadable draft file {Path}.", path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The draft file {Path} could not be read.", path);
            }
        }

        result.Sort((a, b) => b.Written.CompareTo(a.Written));
        return result;
    }

    private string PathFor(string key) => Path.Combine(_directory, FileNameFor(key));

    // Layout: magic(8) version(1) keyLength(int32 LE) key(UTF-8) writtenUtcTicks(int64 LE) contentLength(int64 LE) content.
    private static byte[] Header(string key, DateTimeOffset written, long length)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var header = new byte[Magic.Length + 1 + 4 + keyBytes.Length + 8 + 8];
        var span = header.AsSpan();
        Magic.CopyTo(span);
        var at = Magic.Length;
        span[at++] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(span[at..], keyBytes.Length);
        at += 4;
        keyBytes.CopyTo(span[at..]);
        at += keyBytes.Length;
        BinaryPrimitives.WriteInt64LittleEndian(span[at..], written.UtcTicks);
        at += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[at..], length);
        return header;
    }

    private static bool TryParse(ReadOnlySpan<byte> bytes, out string key, out DateTimeOffset written, out int offset, out long length) =>
        TryParseHeader(bytes, out key, out written, out offset, out length) && offset + length == bytes.Length;

    private static bool TryParseHeader(ReadOnlySpan<byte> bytes, out string key, out DateTimeOffset written, out int offset, out long length)
    {
        key = string.Empty;
        written = default;
        offset = 0;
        length = 0;
        var fixedLength = Magic.Length + 1 + 4;
        if (bytes.Length < fixedLength || !bytes[..Magic.Length].SequenceEqual(Magic) || bytes[Magic.Length] != FormatVersion)
        {
            return false;
        }

        var keyLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[(Magic.Length + 1)..]);
        if (keyLength < 0 || keyLength > 4096 || bytes.Length < fixedLength + keyLength + 16)
        {
            return false;
        }

        try
        {
            key = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.Slice(fixedLength, keyLength));
        }
        catch (ArgumentException)
        {
            return false;
        }

        var at = fixedLength + keyLength;
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]);
        length = BinaryPrimitives.ReadInt64LittleEndian(bytes[(at + 8)..]);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks || length < 0 || length > int.MaxValue)
        {
            return false;
        }

        written = new DateTimeOffset(ticks, TimeSpan.Zero);
        offset = at + 16;
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
