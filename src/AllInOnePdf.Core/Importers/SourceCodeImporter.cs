using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers.Data;

namespace AllInOnePdf.Core.Importers;

/// <summary>Source files → code blocks with a language hint from the extension, so the renderer highlights them.</summary>
public sealed class SourceCodeImporter : IImporter
{
    public const string OptShowFileName = "showFileName";
    public const string OptTabWidth = "tabWidth";
    public const string OptMaxLines = "maxLines";
    public const string OptSplitLongFiles = "splitLongFiles";

    /// <summary>Lines per code block when <see cref="OptSplitLongFiles"/> is on.</summary>
    public const int SplitLines = 500;

    private const string GroupLayout = "Layout";
    private const string GroupLimits = "Limits";
    private const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Extension → language hint. Data and prose formats with their own importer (.json, .csv, .txt, .md, .html) are
    /// deliberately absent so those importers win.
    /// </summary>
    internal static readonly FrozenDictionary<string, string> LanguageByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".csx"] = "csharp",
        [".js"] = "javascript",
        [".mjs"] = "javascript",
        [".cjs"] = "javascript",
        [".jsx"] = "jsx",
        [".ts"] = "typescript",
        [".mts"] = "typescript",
        [".tsx"] = "tsx",
        [".py"] = "python",
        [".java"] = "java",
        [".go"] = "go",
        [".rs"] = "rust",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".cxx"] = "cpp",
        [".h"] = "cpp",
        [".hpp"] = "cpp",
        [".c"] = "c",
        [".css"] = "css",
        [".scss"] = "scss",
        [".less"] = "less",
        [".xml"] = "xml",
        [".xaml"] = "xml",
        [".csproj"] = "xml",
        [".yaml"] = "yaml",
        [".yml"] = "yaml",
        [".sh"] = "bash",
        [".bash"] = "bash",
        [".zsh"] = "bash",
        [".ps1"] = "powershell",
        [".psm1"] = "powershell",
        [".sql"] = "sql",
        [".rb"] = "ruby",
        [".php"] = "php",
        [".kt"] = "kotlin",
        [".kts"] = "kotlin",
        [".swift"] = "swift",
        [".toml"] = "toml",
        [".ini"] = "ini",
        [".cfg"] = "ini",
        [".diff"] = "diff",
        [".patch"] = "diff",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public FileFormat Format => KnownFormats.SourceCode;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Bool(OptShowFileName, "Show file name", true, "Print the file name above the code.", GroupLayout),
        OptionDefinition.Int(OptTabWidth, "Tab width", 4, 2, 8, "Tabs are replaced by spaces up to the next multiple of this width.", GroupLayout, unit: "spaces"),
        OptionDefinition.Bool(OptSplitLongFiles, "Split long files", false, $"Use one code block per {SplitLines} lines so long files break across pages more evenly.", GroupLayout),
        OptionDefinition.Int(OptMaxLines, "Maximum lines", 20000, 1, 1_000_000, "Lines after this many are left out, with a warning.", GroupLimits, unit: "lines"),
    ];

    public bool CanImport(string fileName) => LanguageByExtension.ContainsKey(Path.GetExtension(fileName));

    /// <summary>Language hint for a file name, or null when the extension is not a known source format.</summary>
    public static string? LanguageFor(string fileName) =>
        LanguageByExtension.TryGetValue(Path.GetExtension(fileName), out var language) ? language : null;

    public async Task<Document> ImportAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options.WithDefaults(Options);
        var bytes = await TextDecoding.ReadAllBytesAsync(context.Input, MaxBytes, "The source file is larger than 64 MB and cannot be converted.", cancellationToken);
        var text = TextDecoding.NormalizeNewlines(TextDecoding.Decode(bytes, out var latin1));
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var document = new Document();
        document.Metadata.SourceFormat = Format.Id;
        document.Metadata.SourcePath = context.SourcePath;
        document.Metadata.Title = context.FileName;
        if (latin1)
        {
            document.ImportWarnings.Add(TextDecoding.Latin1Warning);
        }

        var tabWidth = Math.Clamp(options.GetInt(OptTabWidth, 4), 2, 8);
        var maxLines = Math.Max(1, options.GetInt(OptMaxLines, 20000));
        var showName = options.GetBool(OptShowFileName, true);
        var split = options.GetBool(OptSplitLongFiles);
        var language = LanguageFor(context.FileName);

        var lines = text.TrimEnd('\n').Split('\n');
        if (lines.Length > maxLines)
        {
            document.ImportWarnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"Only the first {maxLines:N0} of {lines.Length:N0} lines were imported. Raise \"Maximum lines\" to include more."));
            lines = lines[..maxLines];
        }

        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = ExpandTabs(lines[i].TrimEnd(), tabWidth);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var chunk = split ? SplitLines : Math.Max(1, lines.Length);
        for (var start = 0; start < lines.Length; start += chunk)
        {
            var count = Math.Min(chunk, lines.Length - start);
            string? caption = null;
            if (showName)
            {
                caption = start == 0 ? context.FileName : string.Create(CultureInfo.InvariantCulture, $"{context.FileName} (lines {start + 1}–{start + count})");
            }

            document.Blocks.Add(new CodeBlock
            {
                Code = string.Join('\n', lines, start, count),
                Language = language,
                Caption = caption,
            });
        }

        context.Report(1, "Done");
        return document;
    }

    /// <summary>Replaces each tab with spaces up to the next tab stop (column-aware, not a fixed count).</summary>
    internal static string ExpandTabs(string line, int tabWidth)
    {
        if (!line.Contains('\t', StringComparison.Ordinal))
        {
            return line;
        }

        var sb = new StringBuilder(line.Length + 16);
        foreach (var c in line)
        {
            if (c == '\t')
            {
                sb.Append(' ', tabWidth - (sb.Length % tabWidth));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
