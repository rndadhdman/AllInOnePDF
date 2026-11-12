using System.IO.Compression;
using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.UI.Services;

/// <summary>
/// How a finished conversion is handed to the browser. One file downloads as itself; several files that live in
/// folders (Markdown or HTML with an images folder) go out as one zip, because a browser cannot create the folder
/// and the links in the main file would break; several flat files (one image per page) download one by one.
/// </summary>
public static class ConvertDownloads
{
    /// <summary>True when the outputs must be zipped to keep their folder layout.</summary>
    public static bool NeedsZip(IReadOnlyList<OutputFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Count > 1 && files.Any(f => f.FileName.Contains('/', StringComparison.Ordinal) || f.FileName.Contains('\\', StringComparison.Ordinal));
    }

    /// <summary>What a row's download button says for these outputs ("Download", "Download zip", "Download 7 files").</summary>
    public static string Label(IReadOnlyList<OutputFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count <= 1)
        {
            return "Download";
        }

        return NeedsZip(files) ? "Download zip" : $"Download {files.Count} files";
    }

    /// <summary>The files to hand to the browser: the outputs themselves, or one zip holding them with their folders.</summary>
    public static IReadOnlyList<OutputFile> Prepare(IReadOnlyList<OutputFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return NeedsZip(files) ? [Zip(files)] : files;
    }

    /// <summary>Zips the outputs, keeping relative folders; named after the primary file ("notes.md" → "notes.zip").</summary>
    public static OutputFile Zip(IReadOnlyList<OutputFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var primary = files.FirstOrDefault(f => f.IsPrimary) ?? files[0];
        var stem = Path.GetFileNameWithoutExtension(primary.FileName.Replace('\\', '/').Split('/')[^1]);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var name = SafeEntryName(file.FileName);
                if (!used.Add(name))
                {
                    continue;
                }

                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(file.Content);
            }
        }

        return new OutputFile($"{(string.IsNullOrEmpty(stem) ? "converted" : stem)}.zip", buffer.ToArray(), "application/zip");
    }

    /// <summary>Relative entry name with forward slashes; rooted paths and ".." segments are reduced to their safe parts.</summary>
    private static string SafeEntryName(string fileName)
    {
        var segments = fileName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s is not "." and not ".." && !s.Contains(':', StringComparison.Ordinal))
            .Select(ConversionService.SanitizeBaseName)
            .ToArray();
        return segments.Length == 0 ? "file" : string.Join('/', segments);
    }
}
