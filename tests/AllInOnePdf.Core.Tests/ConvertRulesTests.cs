using System.IO.Compression;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.UI.Services;

namespace AllInOnePdf.Core.Tests;

/// <summary>Convert workspace rules from the QA pass of 6 November 2026 (C4, C5, C6, C8, C9, C10, C11).</summary>
public class ConvertRulesTests
{
    // ---- C4: default target ----

    [Fact]
    public void Pdf_source_defaults_to_markdown_not_the_first_target()
    {
        string[] pdfTargets = ["epub", "docx", "html", "markdown", "text", "png"];
        Assert.Equal("markdown", ConvertRules.DefaultTarget(pdfTargets, rememberedTargetId: null));
    }

    [Fact]
    public void Other_sources_default_to_pdf()
    {
        string[] targets = ["epub", "pdf", "markdown"];
        Assert.Equal("pdf", ConvertRules.DefaultTarget(targets, null));
    }

    [Fact]
    public void Remembered_target_wins_when_offered_and_is_ignored_otherwise()
    {
        string[] targets = ["pdf", "webp", "markdown"];
        Assert.Equal("webp", ConvertRules.DefaultTarget(targets, "WEBP"));
        Assert.Equal("pdf", ConvertRules.DefaultTarget(targets, "xlsx"));
        Assert.Null(ConvertRules.DefaultTarget([], "pdf"));
    }

    // ---- C5: output names ----

    [Fact]
    public void Output_name_is_the_source_stem_when_nothing_collides()
    {
        Assert.Equal("rt", ConvertRules.OutputBaseName("rt.docx", ".pdf", [("other.html", ".pdf")]));
        // Same stem but another output type: rt.pdf and rt.md do not collide.
        Assert.Equal("rt", ConvertRules.OutputBaseName("rt.docx", ".pdf", [("rt.html", ".md")]));
    }

    [Fact]
    public void Sources_that_differ_only_by_extension_get_the_extension_in_the_output_name()
    {
        (string, string?)[] others = [("rt.html", ".pdf"), ("RT.xlsx", ".PDF"), ("notes.md", ".pdf")];
        Assert.Equal("rt-docx", ConvertRules.OutputBaseName("rt.docx", ".pdf", others));
        Assert.Equal("rt-page-png", ConvertRules.OutputBaseName("rt-page.png", ".pdf", [("rt-page.webp", ".pdf")]));
    }

    [Fact]
    public void Two_files_with_the_same_name_and_extension_keep_the_plain_name()
    {
        // Nothing to tell them apart by; the browser or the overwrite policy numbers them.
        Assert.Equal("a", ConvertRules.OutputBaseName("a.md", ".pdf", [("a.md", ".pdf")]));
    }

    // ---- C8: progress counts ----

    [Fact]
    public void Summary_counts_add_up_and_split_running_from_waiting()
    {
        var summary = ConvertRules.Summarize(
        [
            ConvertItemState.Succeeded, ConvertItemState.Succeeded, ConvertItemState.Cancelled,
            ConvertItemState.Running, ConvertItemState.Queued, ConvertItemState.Ready, ConvertItemState.Unsupported,
        ]);

        Assert.Equal(5, summary.Total);
        Assert.Equal(summary.Total, summary.Succeeded + summary.Failed + summary.Cancelled + summary.Running + summary.Queued);
        Assert.Equal("2 of 5 converted · 1 cancelled · 1 converting · 1 waiting", ConvertRules.Describe(summary));
        Assert.True(summary.IsRunning);
    }

    [Fact]
    public void Summary_of_finished_files_reads_plainly()
    {
        var summary = ConvertRules.Summarize([ConvertItemState.Succeeded, ConvertItemState.Failed, ConvertItemState.Succeeded]);
        Assert.Equal("2 of 3 converted · 1 failed", ConvertRules.Describe(summary));
        Assert.False(summary.IsRunning);
        Assert.Equal(3, summary.Finished);
    }

    // ---- C9: row errors ----

    [Fact]
    public void Row_error_drops_the_pdf_parser_detail_and_explains_an_empty_file()
    {
        const string raw = "The file could not be read as a PDF; it may be damaged or not a PDF (Could not find the version header comment at the start of the document.).";
        Assert.Equal("The file could not be read as a PDF. It may be damaged, or saved in another format under this name.", ConvertRules.RowError(raw, 120));
        Assert.StartsWith("The file is empty (0 bytes)", ConvertRules.RowError(raw, 0), StringComparison.Ordinal);
        Assert.Equal("Something else (detail).", ConvertRules.RowError("Something else (detail).", 10));
        Assert.Null(ConvertRules.RowError(null, 0));
    }

    [Fact]
    public void Row_error_for_other_damaged_files_keeps_the_type_and_drops_the_detail()
    {
        Assert.Equal(
            "The file could not be read as a Word document. It may be damaged, or saved in another format under this name.",
            ConvertRules.RowError("The file could not be read as a Word document; it may be damaged (End of central directory not found.).", 10));
        Assert.Equal(
            "The workbook is damaged and could not be read.",
            ConvertRules.RowError("The workbook is damaged and could not be read (Unexpected end of stream.).", 10));
    }

    [Fact]
    public void Row_error_for_a_password_protected_pdf_says_where_the_password_goes()
    {
        var missing = ConvertRules.RowError("The PDF is encrypted; supply the password in options.", 10)!;
        Assert.StartsWith("This PDF is password-protected.", missing, StringComparison.Ordinal);
        Assert.Contains("Options", missing, StringComparison.Ordinal);

        var wrong = ConvertRules.RowError("The PDF password is not correct. Check the password option and try again.", 10)!;
        Assert.StartsWith("The PDF password is not correct.", wrong, StringComparison.Ordinal);
        Assert.Contains("Options", wrong, StringComparison.Ordinal);
    }

    [Fact]
    public void Converted_status_lists_time_size_extra_files_and_download()
    {
        var one = ConvertRules.ConvertedText(TimeSpan.FromMilliseconds(250), 2048, 1, downloaded: false);
        Assert.StartsWith("Converted in ", one, StringComparison.Ordinal);
        Assert.Equal(2, one.Split(" · ").Length);

        var many = ConvertRules.ConvertedText(TimeSpan.FromSeconds(2), 4096, 3, downloaded: true);
        Assert.EndsWith(" · 3 files · downloaded", many, StringComparison.Ordinal);
    }

    // ---- C6 and C11: downloads ----

    [Fact]
    public void Markdown_with_an_images_folder_downloads_as_one_zip_keeping_the_folder()
    {
        OutputFile[] files =
        [
            new("rt-page.md", "![](rt-page_files/rt-page.png)"u8.ToArray(), "text/markdown"),
            new("rt-page_files/rt-page.png", [1, 2, 3], "image/png", IsPrimary: false),
        ];

        Assert.True(ConvertDownloads.NeedsZip(files));
        Assert.Equal("Download zip", ConvertDownloads.Label(files));
        var prepared = Assert.Single(ConvertDownloads.Prepare(files));
        Assert.Equal("rt-page.zip", prepared.FileName);

        using var zip = new ZipArchive(new MemoryStream(prepared.Content), ZipArchiveMode.Read);
        Assert.Equal(["rt-page.md", "rt-page_files/rt-page.png"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Page_images_download_one_by_one_and_the_label_says_how_many()
    {
        OutputFile[] files =
        [
            new("doc-p1.png", [1], "image/png"),
            new("doc-p2.png", [2], "image/png", IsPrimary: false),
            new("doc-p3.png", [3], "image/png", IsPrimary: false),
        ];

        Assert.False(ConvertDownloads.NeedsZip(files));
        Assert.Equal("Download 3 files", ConvertDownloads.Label(files));
        Assert.Equal(3, ConvertDownloads.Prepare(files).Count);
        Assert.Equal("Download", ConvertDownloads.Label([files[0]]));
    }

    [Fact]
    public void Zip_entries_never_escape_the_archive_root()
    {
        OutputFile[] files =
        [
            new("a.md", [1], "text/markdown"),
            new("../../evil/x.png", [2], "image/png", IsPrimary: false),
        ];

        using var zip = new ZipArchive(new MemoryStream(ConvertDownloads.Zip(files).Content), ZipArchiveMode.Read);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("..", StringComparison.Ordinal));
    }

    // ---- C10: option presentation ----

    [Fact]
    public void Pdf_password_is_never_hidden_under_advanced_and_is_masked()
    {
        var password = new PdfImporter().Options.Single(o => o.Key == PdfImporter.OptPassword);
        Assert.False(ConvertOptionRules.IsAdvanced(password));
        Assert.True(ConvertOptionRules.IsPassword(password));

        var keep = new PdfImporter().Options.Single(o => o.Key == PdfImporter.OptKeepSourcePdf);
        var help = ConvertOptionRules.HelpText(keep)!;
        Assert.DoesNotContain("PDF → PDF", help, StringComparison.Ordinal);
        Assert.EndsWith(".", help, StringComparison.Ordinal);
    }
}
