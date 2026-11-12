using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Core.Themes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AllInOnePdf.Core.Tests;

public sealed class ConversionPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aio-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public ConversionPipelineTests()
    {
        Directory.CreateDirectory(_dir);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddAllInOnePdfCore(Path.Combine(_dir, "data"));
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Registry_resolves_markdown_to_pdf_and_lists_targets()
    {
        var registry = _services.GetRequiredService<FormatRegistry>();

        var path = registry.Resolve("notes.md", "pdf");
        Assert.NotNull(path);
        Assert.True(path!.IsAvailable);
        Assert.IsType<MarkdownImporter>(path.Importer);
        Assert.IsType<PdfExporter>(path.Exporter);

        var targets = registry.GetTargetsFor("notes.md");
        Assert.Equal("pdf", targets[0].Format.Id);
        Assert.DoesNotContain(targets, t => t.Format.Id == "markdown");
        Assert.Contains(".md", registry.ImportableExtensions);
        Assert.True(registry.CanOpen("file.txt"));
        Assert.False(registry.CanOpen("file.xyz"));
    }

    [Fact]
    public async Task Converts_markdown_file_to_pdf_on_disk_with_theme()
    {
        var source = Path.Combine(_dir, "notes.md");
        await File.WriteAllTextAsync(source, "# Hello\n\nSome **text**.\n\n- a\n- b\n");
        var service = _services.GetRequiredService<IConversionService>();
        var progress = new List<ConversionProgress>();

        var result = await service.ConvertAsync(new ConversionRequest
        {
            SourcePath = source,
            TargetFormatId = "pdf",
            ThemeId = "technical",
            OutputDirectory = Path.Combine(_dir, "out"),
        }, new Progress<ConversionProgress>(progress.Add));

        Assert.True(result.Success, result.Error);
        var written = Assert.Single(result.WrittenPaths);
        Assert.EndsWith("notes.pdf", written);
        Assert.True(new FileInfo(written).Length > 1000);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(await File.ReadAllBytesAsync(written), 0, 4));
        Assert.Equal("Hello", result.Document!.Metadata.Title);
        Assert.Equal("markdown", result.Document.Metadata.SourceFormat);
    }

    [Fact]
    public async Task Converts_in_memory_content_and_dedupes_output_names()
    {
        var service = _services.GetRequiredService<IConversionService>();
        var outDir = Path.Combine(_dir, "out2");
        var request = new ConversionRequest
        {
            SourceContent = Encoding.UTF8.GetBytes("PROJECT NOTES\n\nFirst paragraph\nstill first.\n\nSecond paragraph.\n"),
            SourceFileName = "notes.txt",
            TargetFormatId = "text",
            OutputDirectory = outDir,
            ExportOptions = new OptionValues().Set(PlainTextExporter.OptHeadings, "upper"),
        };

        var first = await service.ConvertAsync(request);
        var second = await service.ConvertAsync(request);

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.EndsWith("notes.txt", first.WrittenPaths[0]);
        Assert.EndsWith("notes (2).txt", second.WrittenPaths[0]);
        var text = await File.ReadAllTextAsync(first.WrittenPaths[0]);
        Assert.Contains("PROJECT NOTES", text);
        Assert.Contains("First paragraph still first.", text);
    }

    [Fact]
    public async Task Planned_exporter_fails_gracefully()
    {
        var registry = new FormatRegistry([new MarkdownImporter()], [new PlannedExporter()]);
        var themes = _services.GetRequiredService<IThemeStore>();
        var service = new ConversionService(registry, themes, _services.GetRequiredService<ILogger<ConversionService>>());

        var result = await service.ConvertAsync(new ConversionRequest
        {
            SourceContent = Encoding.UTF8.GetBytes("# x"),
            SourceFileName = "x.md",
            TargetFormatId = PlannedExporter.FormatId,
        });

        Assert.False(result.Success);
        Assert.Contains("not ready", result.Error);
        Assert.False(registry.Resolve("x.md", PlannedExporter.FormatId)!.IsAvailable);
    }

    private sealed class PlannedExporter : IExporter
    {
        public const string FormatId = "planned-test";

        public FileFormat Format { get; } = new(FormatId, "Planned", [".planned"], "application/octet-stream", FormatCategory.Other);

        public ConverterStatus Status => ConverterStatus.Planned;

        public string? StatusMessage => "This exporter is not ready yet.";

        public IReadOnlyList<OptionDefinition> Options { get; } = [];

        public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(StatusMessage);
    }

    [Fact]
    public async Task Queue_runs_jobs_and_raises_changes()
    {
        var queue = _services.GetRequiredService<ConversionQueue>();
        var source = Path.Combine(_dir, "q.md");
        await File.WriteAllTextAsync(source, "# Queue\n\nbody\n");
        var changes = 0;
        var done = new TaskCompletionSource();
        queue.Changed += job =>
        {
            Interlocked.Increment(ref changes);
            if (job.IsFinished)
            {
                done.TrySetResult();
            }
        };

        var job = queue.Enqueue(new ConversionRequest { SourcePath = source, TargetFormatId = "pdf", OutputDirectory = Path.Combine(_dir, "qout") });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.NotNull(job.Result);
        Assert.True(changes >= 3);
        Assert.True(job.Progress.Overall >= 1);

        var history = _services.GetRequiredService<IConversionHistory>();
        await history.AddAsync(FileConversionHistory.FromJob(job, "markdown"));
        var entries = await history.GetAsync();
        Assert.Single(entries);
        Assert.True(entries[0].Success);
        Assert.EndsWith("q.pdf", entries[0].PrimaryOutput);
    }

    [Fact]
    public async Task Plain_text_importer_detects_headings()
    {
        var importer = new PlainTextImporter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Title\n=====\n\nAGENDA\n\nBody line one\nline two\n"));
        var doc = await importer.ImportAsync(new ImportContext { Input = stream, FileName = "a.txt", Options = OptionValues.FromDefaults(importer.Options) });

        Assert.Collection(doc.Blocks,
            b => Assert.Equal(1, Assert.IsType<HeadingBlock>(b).Level),
            b => Assert.Equal("AGENDA", Assert.IsType<HeadingBlock>(b).ToPlainText()),
            b => Assert.Equal("Body line one line two", Assert.IsType<ParagraphBlock>(b).ToPlainText()));
    }

    [Fact]
    public async Task Pdf_exporter_honours_cover_and_toc_overrides()
    {
        var doc = await SampleDocuments.CreateWelcomeAsync();
        var exporter = new PdfExporter(new PdfRenderer());
        var raster = new PdfiumRasterizer();

        var plain = await exporter.ExportAsync(new ExportContext { Document = doc, BaseName = "w", Theme = BuiltInThemes.Clean(), Options = OptionValues.FromDefaults(exporter.Options) });
        var withExtras = await exporter.ExportAsync(new ExportContext
        {
            Document = doc,
            BaseName = "w",
            Theme = BuiltInThemes.Clean(),
            Options = OptionValues.FromDefaults(exporter.Options).Set(PdfExporter.OptCover, "yes").Set(PdfExporter.OptToc, "yes"),
        });

        var plainPages = raster.GetPageCount(plain.Primary!.Content);
        var extraPages = raster.GetPageCount(withExtras.Primary!.Content);
        Assert.True(extraPages >= plainPages + 2, $"expected cover and contents pages ({plainPages} vs {extraPages})");
    }
}
