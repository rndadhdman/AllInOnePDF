using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers.Html;
using AngleSharp.Dom;
using Document = AllInOnePdf.Core.Documents.Document;

namespace AllInOnePdf.Core.Importers;

/// <summary>
/// HTML / XHTML → document model, using AngleSharp. Works offline: remote images keep their URL and are never fetched;
/// local images are embedded when they are small enough. Scripts, styles and embedded media are dropped.
/// </summary>
public sealed class HtmlImporter : IImporter
{
    public const string OptMainContentOnly = "mainContentOnly";
    public const string OptEmbedImages = "embedLocalImages";
    public const string OptMaxImageMb = "maxImageMb";
    public const string OptKeepRawHtml = "keepRawHtml";
    public const string OptPreserveLineBreaks = "preserveLineBreaks";

    /// <summary>Pages larger than this are rejected rather than parsed (the DOM would need several times this in memory).</summary>
    private const long MaxInputBytes = 200L * 1024 * 1024;

    public FileFormat Format => KnownFormats.Html;

    public ConverterStatus Status => ConverterStatus.Ready;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Bool(OptMainContentOnly, "Main content only", true, "Use the page's <main> or <article> (or its largest content block) and leave out navigation, headers, footers and sidebars.", "Content"),
        OptionDefinition.Bool(OptEmbedImages, "Embed local images", true, "Load images referenced by a file path into the document so output is self-contained. Web images are never downloaded.", "Images"),
        OptionDefinition.Int(OptMaxImageMb, "Largest image", 25, 1, 200, "Images larger than this are left as references.", "Images", unit: "MB", advanced: true),
        OptionDefinition.Bool(OptKeepRawHtml, "Keep raw HTML", false, "Keep unsupported elements (video, forms, frames, custom tags) as raw HTML instead of reducing them to their text.", "Parsing", advanced: true),
        OptionDefinition.Bool(OptPreserveLineBreaks, "Keep line breaks", false, "Treat every newline in the text as a line break (for pages that rely on CSS to show them).", "Parsing", advanced: true),
    ];

    public async Task<Document> ImportAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options.WithDefaults(Options);
        context.Report(0, "Reading HTML");

        using var buffer = new MemoryStream();
        await CopyLimitedAsync(context.Input, buffer, MaxInputBytes, cancellationToken);
        buffer.Position = 0;

        var parser = HtmlDocumentConverter.CreateParser();
        IDocument dom;
        if (IsXhtml(context.FileName, buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 512))))
        {
            using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken);
            dom = await parser.ParseDocumentAsync(HtmlDocumentConverter.FixSelfClosingTags(text), cancellationToken);
        }
        else
        {
            dom = await parser.ParseDocumentAsync(buffer, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        context.Report(0.4, "Building document");

        var document = new Document();
        var settings = new HtmlConversionSettings
        {
            MainContentOnly = options.GetBool(OptMainContentOnly, true),
            EmbedImages = options.GetBool(OptEmbedImages, true),
            MaxImageBytes = Math.Max(1, options.GetInt(OptMaxImageMb, 25)) * 1024L * 1024L,
            KeepRawHtml = options.GetBool(OptKeepRawHtml),
            PreserveLineBreaks = options.GetBool(OptPreserveLineBreaks),
        };

        var converter = new HtmlDocumentConverter(document, settings);
        converter.ReadMetadata(dom);
        document.Blocks.AddRange(converter.Convert(dom, LocalImageResolver.ForDirectory(context.SourceDirectory)));
        converter.AddWarnings(context.Warnings);

        if (string.IsNullOrWhiteSpace(document.Metadata.Title))
        {
            document.Metadata.Title = document.Headings.FirstOrDefault(h => h.Level == 1)?.ToPlainText();
        }

        document.Metadata.SourceFormat = Format.Id;
        context.Report(1, "HTML parsed");
        return document;
    }

    private static bool IsXhtml(string fileName, ReadOnlySpan<byte> head)
    {
        if (string.Equals(Path.GetExtension(fileName), ".xhtml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var text = Encoding.UTF8.GetString(head).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task CopyLimitedAsync(Stream input, Stream output, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException($"The file is larger than {limit / (1024 * 1024)} MB and cannot be read.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
