using System.Globalization;
using AllInOnePdf.Core;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.UI.Services;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace AllInOnePdf.Core.Tests;

/// <summary>The Reader start page: file types grouped by what people have, and the Recent rows.</summary>
public sealed class ReaderStartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aio-reader-start-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public ReaderStartTests()
    {
        _services = new ServiceCollection().AddLogging().AddAllInOnePdfCore(_dir).BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private FormatRegistry Registry => _services.GetRequiredService<FormatRegistry>();

    [Fact]
    public void Readable_types_are_grouped_by_what_people_have()
    {
        var groups = FormatCatalog.ReadableGroups(Registry);

        Assert.Equal(
            ["Documents", "Spreadsheets and presentations", "Web pages", "E-books", "Images", "Data and code"],
            groups.Select(g => g.Name));
        Assert.Equal(["pdf", "docx", "rtf", "text", "markdown"], Ids(groups, "Documents"));
        Assert.Equal(["xlsx", "pptx"], Ids(groups, "Spreadsheets and presentations"));
        Assert.Equal(["html"], Ids(groups, "Web pages"));
        Assert.Equal(["epub"], Ids(groups, "E-books"));
        Assert.Equal(["csv", "json", "code"], Ids(groups, "Data and code"));
    }

    [Fact]
    public void Every_readable_type_appears_once_and_planned_ones_do_not()
    {
        var listed = FormatCatalog.ReadableGroups(Registry).SelectMany(g => g.Formats).Select(e => e.Format.Id).ToList();
        var readable = Registry.Importers
            .Where(i => i.Status != ConverterStatus.Planned)
            .Select(i => i.Format.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(listed.Count, listed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(readable.OrderBy(x => x), listed.OrderBy(x => x));
    }

    [Fact]
    public void A_type_the_table_does_not_know_lands_in_other()
    {
        var unknown = new FileFormat("zzz", "Zed file", [".zzz"], "application/x-zzz", FormatCategory.Other);
        var registry = new FormatRegistry([new FakeImporter(KnownFormats.Pdf), new FakeImporter(unknown)], []);

        var groups = FormatCatalog.ReadableGroups(registry);

        Assert.Equal(["Documents", "Other"], groups.Select(g => g.Name));
        Assert.Equal("zzz", Assert.Single(groups[1].Formats).Format.Id);
    }

    [Fact]
    public void Long_extension_lists_are_summarised_for_a_row()
    {
        var code = new FormatEntry(KnownFormats.SourceCode, new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.Code());
        var pdf = new FormatEntry(KnownFormats.Pdf, new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.Document());

        Assert.Equal(".pdf", pdf.ExtensionSummary());
        Assert.Equal($".cs, .js, .mjs and {KnownFormats.SourceCode.Extensions.Count - 3} more", code.ExtensionSummary(3));
        Assert.Equal(6, KnownFormats.Image.Extensions.Count); // the image row shows all six without "more"
        Assert.Equal(string.Join(", ", KnownFormats.Image.Extensions), new FormatEntry(KnownFormats.Image, pdf.Icon).ExtensionSummary());
    }

    [Fact]
    public void Recent_row_shows_the_folder_not_the_file_and_nothing_without_a_path()
    {
        var folder = Path.Combine(Path.GetTempPath(), "reports");
        var onDisk = new ReaderRecentItem("q3.pdf", Path.Combine(folder, "q3.pdf"), DateTimeOffset.Now, null);
        var upload = new ReaderRecentItem("q3.pdf", null, DateTimeOffset.Now, null);

        Assert.Equal(folder, onDisk.Folder);
        Assert.Null(upload.Folder);
    }

    [Fact]
    public void Recent_time_is_the_time_today_and_date_and_time_otherwise()
    {
        // Mid-June: no daylight-saving change between the two days in any common time zone.
        var culture = CultureInfo.InvariantCulture;
        var local = new DateTime(2026, 6, 11, 15, 0, 0, DateTimeKind.Unspecified);
        var now = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        var today = new ReaderRecentItem("a.md", null, now.AddHours(-2), null);
        var yesterday = new ReaderRecentItem("b.md", null, now.AddDays(-1), null);

        Assert.Equal("13:00", today.OpenedLabel(now, culture));
        Assert.Equal("06/10/2026 15:00", yesterday.OpenedLabel(now, culture));
    }

    private static IEnumerable<string> Ids(IReadOnlyList<FormatGroup> groups, string name) =>
        groups.Single(g => g.Name == name).Formats.Select(e => e.Format.Id);

    private sealed class FakeImporter(FileFormat format) : IImporter
    {
        public FileFormat Format => format;

        public IReadOnlyList<OptionDefinition> Options => [];

        public Task<Documents.Document> ImportAsync(ImportContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
