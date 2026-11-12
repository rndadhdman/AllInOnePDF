using System.Text;

namespace AllInOnePdf.Core.Importers.Data;

/// <summary>Reads text files whose encoding is not declared: byte-order mark first, then strict UTF-8, then Latin-1.</summary>
internal static class TextDecoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads the whole stream into memory, refusing inputs larger than <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]> ReadAllBytesAsync(Stream input, long maxBytes, string limitMessage, CancellationToken cancellationToken)
    {
        if (input.CanSeek && input.Length - input.Position > maxBytes)
        {
            throw new InvalidDataException(limitMessage);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                throw new InvalidDataException(limitMessage);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes <paramref name="bytes"/>: UTF-8/UTF-16/UTF-32 when a byte-order mark says so, UTF-8 when the bytes are
    /// valid UTF-8, otherwise Latin-1 (and <paramref name="fellBack"/> is true so the caller can warn).
    /// </summary>
    public static string Decode(byte[] bytes, out bool fellBack)
    {
        fellBack = false;
        if (HasBom(bytes))
        {
            using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            fellBack = true;
            return Encoding.Latin1.GetString(bytes);
        }
    }

    public const string Latin1Warning = "The file is not valid UTF-8; it was read as Latin-1 (Western European). Save it as UTF-8 if characters look wrong.";

    private static bool HasBom(byte[] b) =>
        (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        || (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)))
        || (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF);

    /// <summary>Normalises CRLF and lone CR line endings to LF.</summary>
    public static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
