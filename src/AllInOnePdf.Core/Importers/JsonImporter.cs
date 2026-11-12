using System.Text.Json;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers.Data;

namespace AllInOnePdf.Core.Importers;

/// <summary>
/// JSON → one pretty-printed, highlighted code block. Invalid JSON is kept as-is with a warning naming the line and
/// position of the first error.
/// </summary>
public sealed class JsonImporter : IImporter
{
    public const string OptIndent = "indent";
    public const string OptSortKeys = "sortKeys";
    public const string OptMaxMb = "maxMb";
    public const int MaxDepth = 256;

    private const string GroupLayout = "Layout";
    private const string GroupLimits = "Limits";

    public FileFormat Format => KnownFormats.Json;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Choice(OptIndent, "Indentation", "2",
        [
            new OptionChoice("2", "2 spaces"),
            new OptionChoice("4", "4 spaces"),
        ], "Spaces per nesting level in the formatted output.", GroupLayout),
        OptionDefinition.Bool(OptSortKeys, "Sort keys", false, "List the properties of every object alphabetically.", GroupLayout),
        OptionDefinition.Int(OptMaxMb, "Maximum file size", 20, 1, 512, "Larger files are refused; formatted JSON of that size runs to thousands of pages.", GroupLimits, unit: "MB", advanced: true),
    ];

    public async Task<Document> ImportAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options.WithDefaults(Options);
        var maxMb = Math.Max(1, options.GetInt(OptMaxMb, 20));
        var bytes = await TextDecoding.ReadAllBytesAsync(context.Input, maxMb * 1024L * 1024L,
            $"The JSON file is larger than {maxMb} MB. Raise \"Maximum file size\" in the advanced options to convert it.", cancellationToken);
        var text = TextDecoding.Decode(bytes, out var latin1);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var document = new Document();
        document.Metadata.SourceFormat = Format.Id;
        document.Metadata.SourcePath = context.SourcePath;
        document.Metadata.Title = Path.GetFileNameWithoutExtension(context.FileName);
        if (latin1)
        {
            document.ImportWarnings.Add(TextDecoding.Latin1Warning);
        }

        var indent = options.GetString(OptIndent) == "4" ? 4 : 2;
        var sortKeys = options.GetBool(OptSortKeys);
        context.Report(0.2, "Parsing");

        string code;
        try
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                MaxDepth = MaxDepth,
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            cancellationToken.ThrowIfCancellationRequested();
            code = JsonPrettyPrinter.Format(json.RootElement, indent, sortKeys);
        }
        catch (JsonException ex)
        {
            code = TextDecoding.NormalizeNewlines(text).TrimEnd('\n');
            var where = ex.LineNumber is { } line
                ? $" at line {line + 1}, position {(ex.BytePositionInLine ?? 0) + 1}"
                : string.Empty;
            document.ImportWarnings.Add($"The file is not valid JSON{where}: {FirstSentence(ex.Message)} It is shown unformatted.");
        }

        document.Blocks.Add(new CodeBlock { Code = code, Language = "json" });
        context.Report(1, "Done");
        return document;
    }

    /// <summary>System.Text.Json appends "Path: … | LineNumber: … | BytePositionInLine: …"; the position is reported separately.</summary>
    private static string FirstSentence(string message)
    {
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut < 0)
        {
            cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        }

        var head = (cut > 0 ? message[..cut] : message).Trim();
        return head.EndsWith('.') ? head : head + ".";
    }
}
