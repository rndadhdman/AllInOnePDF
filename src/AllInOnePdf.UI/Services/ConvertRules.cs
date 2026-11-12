using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.UI.Services;

/// <summary>
/// Pure rules of the Convert workspace (no state, no I/O), kept apart so they can be tested: which target a new
/// file starts with, what its outputs are called, and how the status line counts files.
/// </summary>
public static class ConvertRules
{
    /// <summary>
    /// The target a newly added file starts with: the target last picked for files of the same source format (shown
    /// on the row as remembered), else PDF, else (for a PDF, which cannot become a PDF again) Markdown, then plain
    /// text, else the first offered target. Planned targets must already be filtered out of
    /// <paramref name="targetIds"/>.
    /// </summary>
    public static string? DefaultTarget(IReadOnlyList<string> targetIds, string? rememberedTargetId)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        string? Find(string? id) => id is null ? null : targetIds.FirstOrDefault(t => string.Equals(t, id, StringComparison.OrdinalIgnoreCase));

        return Find(rememberedTargetId)
            ?? Find(KnownFormats.Pdf.Id)
            ?? Find(KnownFormats.Markdown.Id)
            ?? Find(KnownFormats.PlainText.Id)
            ?? (targetIds.Count > 0 ? targetIds[0] : null);
    }

    /// <summary>
    /// Base name (without extension) of a file's outputs. Normally the source name without its extension; when
    /// another listed file has the same name with a different extension and converts to a file of the same type,
    /// both would be called the same (<c>rt.docx</c> and <c>rt.html</c> → <c>rt.pdf</c>), so the source extension
    /// is added: <c>rt-docx.pdf</c>, <c>rt-html.pdf</c>.
    /// </summary>
    /// <param name="fileName">This file's name with extension.</param>
    /// <param name="outputExtension">Primary extension of this file's target (".pdf"), or null when none.</param>
    /// <param name="others">The other listed files: name and target extension.</param>
    public static string OutputBaseName(string fileName, string? outputExtension, IEnumerable<(string FileName, string? OutputExtension)> others)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(others);
        var stem = ConversionService.SanitizeBaseName(Path.GetFileNameWithoutExtension(fileName));
        var extension = Path.GetExtension(fileName);
        if (outputExtension is null || string.IsNullOrEmpty(extension))
        {
            return stem;
        }

        var collides = others.Any(o =>
            string.Equals(o.OutputExtension, outputExtension, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetExtension(o.FileName), extension, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ConversionService.SanitizeBaseName(Path.GetFileNameWithoutExtension(o.FileName)), stem, StringComparison.OrdinalIgnoreCase));

        return collides ? $"{stem}-{extension.TrimStart('.').ToLowerInvariant()}" : stem;
    }

    /// <summary>Counts for the status line; every file that was converted (or is converting) is in exactly one bucket.</summary>
    public static ConvertBatchSummary Summarize(IEnumerable<ConvertItemState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        int succeeded = 0, failed = 0, cancelled = 0, running = 0, queued = 0;
        foreach (var state in states)
        {
            switch (state)
            {
                case ConvertItemState.Succeeded: succeeded++; break;
                case ConvertItemState.Failed: failed++; break;
                case ConvertItemState.Cancelled: cancelled++; break;
                case ConvertItemState.Running: running++; break;
                case ConvertItemState.Queued: queued++; break;
            }
        }

        return new ConvertBatchSummary(succeeded, failed, cancelled, running, queued);
    }

    /// <summary>
    /// The status line, for example "3 of 5 converted · 1 failed · 1 converting". The parts always add up to the
    /// total: converted + failed + cancelled + converting + waiting.
    /// </summary>
    public static string Describe(ConvertBatchSummary summary)
    {
        var parts = new List<string> { $"{summary.Succeeded} of {summary.Total} converted" };
        if (summary.Failed > 0)
        {
            parts.Add($"{summary.Failed} failed");
        }

        if (summary.Cancelled > 0)
        {
            parts.Add($"{summary.Cancelled} cancelled");
        }

        if (summary.Running > 0)
        {
            parts.Add($"{summary.Running} converting");
        }

        if (summary.Queued > 0)
        {
            parts.Add($"{summary.Queued} waiting");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The error a row shows: the converter's message without a parser's internal detail in brackets (kept in the
    /// row's tooltip), with the next step for an empty, damaged or password-protected file.
    /// </summary>
    public static string? RowError(string? error, long sourceSize)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return error;
        }

        if (sourceSize == 0)
        {
            return EmptyFileError;
        }

        // PdfImporter: "The PDF is encrypted; supply the password in options." / "The PDF password is not correct. …"
        if (error.StartsWith("The PDF is encrypted", StringComparison.Ordinal))
        {
            return "This PDF is password-protected. Open Options for this file, enter the password and convert again.";
        }

        if (error.StartsWith("The PDF password is not correct", StringComparison.Ordinal))
        {
            return "The PDF password is not correct. Open Options for this file, correct the password and convert again.";
        }

        // "The file could not be read as a PDF; it may be damaged or not a PDF (Could not find the version header …)."
        // "The file could not be read as a Word document; it may be damaged (…)."
        const string readPrefix = "The file could not be read as ";
        if (error.StartsWith(readPrefix, StringComparison.Ordinal))
        {
            var end = error.IndexOf(';', StringComparison.Ordinal);
            if (end < 0)
            {
                end = error.IndexOf(" (", StringComparison.Ordinal);
            }

            if (end > readPrefix.Length)
            {
                return error[..end] + ". It may be damaged, or saved in another format under this name.";
            }
        }

        // "The Word document is damaged and could not be read (…)." keeps its sentence, loses the parser detail.
        var open = error.IndexOf(" (", StringComparison.Ordinal);
        if (error.Contains("is damaged and could not be read", StringComparison.Ordinal) && open > 0
            && error.TrimEnd().EndsWith(").", StringComparison.Ordinal))
        {
            return error[..open] + ".";
        }

        return error;
    }

    /// <summary>Shown on a row whose file has no content (before and after converting).</summary>
    public const string EmptyFileError = "The file is empty (0 bytes). Check that it was saved or downloaded completely, then add it again.";

    /// <summary>
    /// The status of a converted row, for example "Converted in 1.2 s · 16 KB · 3 files". After the web download the
    /// result has left memory, which the row says too.
    /// </summary>
    public static string ConvertedText(TimeSpan elapsed, long totalBytes, int fileCount, bool downloaded)
    {
        var parts = new List<string>
        {
            $"Converted in {ConvertFormatting.Duration(elapsed)}",
            ConvertFormatting.Bytes(totalBytes),
        };
        if (fileCount > 1)
        {
            parts.Add($"{fileCount} files");
        }

        if (downloaded)
        {
            parts.Add("downloaded");
        }

        return string.Join(" · ", parts);
    }
}
