using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Storage;
using AllInOnePdf.Publishing.Templates;
using AllInOnePdf.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GalleryPage = AllInOnePdf.UI.Pages.Themes;

namespace AllInOnePdf.Core.Tests;

/// <summary>Themes gallery search, the import repair report, header/footer template checks and the canvas header/footer.</summary>
public sealed class ThemeGalleryAndImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aio-gallery-" + Guid.NewGuid().ToString("N"));

    // ------------------------------------------------------------------ search

    [Fact]
    public void Search_dark_finds_every_dark_built_in_theme()
    {
        var hits = BuiltInThemes.All().Where(t => GalleryPage.Matches(t, "dark")).Select(t => t.Id).ToList();

        Assert.Contains("nord", hits);
        Assert.Contains("everforest", hits);
        foreach (var theme in BuiltInThemes.All().Where(t => t.Tags.Contains("dark")))
        {
            Assert.Contains(theme.Id, hits);
        }
    }

    [Fact]
    public void Every_built_in_theme_is_tagged_light_or_dark_to_match_its_page()
    {
        foreach (var theme in BuiltInThemes.All())
        {
            var dark = ThemeBuilderColor.IsDark(theme.Page.Background);
            Assert.True(theme.Tags.Contains(dark ? "dark" : "light"), theme.Id);
            Assert.False(theme.Tags.Contains(dark ? "light" : "dark"), theme.Id);
        }
    }

    [Fact]
    public void Settings_search_filters_the_sections_by_title_and_keyword()
    {
        Assert.Equal(15, AllInOnePdf.UI.Components.ThemeBuilder.SettingsPanel.Visible(null).Count);
        var margins = AllInOnePdf.UI.Components.ThemeBuilder.SettingsPanel.Visible("margins");
        Assert.Equal("page", Assert.Single(margins).Key);
        Assert.Empty(AllInOnePdf.UI.Components.ThemeBuilder.SettingsPanel.Visible("zzz"));
    }

    [Fact]
    public void Search_matches_name_description_and_tags_and_every_word_must_hit()
    {
        var clean = BuiltInThemes.Clean();
        Assert.True(GalleryPage.Matches(clean, "CLEAN"));
        Assert.True(GalleryPage.Matches(clean, "notes"));          // description
        Assert.True(GalleryPage.Matches(clean, "sans"));           // tag
        Assert.True(GalleryPage.Matches(clean, "clean sans"));
        Assert.False(GalleryPage.Matches(clean, "clean dark"));
        Assert.True(GalleryPage.Matches(clean, "  "));
        Assert.False(GalleryPage.Matches(clean, "default"));        // the misleading tag is gone
    }

    [Fact]
    public void Search_finds_an_untagged_user_theme_by_its_page_colour()
    {
        var mine = new PdfTheme { Name = "Mine", Tags = [] };
        mine.Page.Background = "#101010";
        Assert.True(GalleryPage.Matches(mine, "dark"));
        Assert.False(GalleryPage.Matches(mine, "light"));

        mine.Page.Background = "#FFFFFF";
        Assert.True(GalleryPage.Matches(mine, "light"));
        Assert.False(GalleryPage.Matches(mine, "dark"));
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public void Classify_tells_malformed_json_from_json_that_is_not_a_theme()
    {
        Assert.Equal(ThemeJsonKind.Malformed, ThemeValidator.Classify("{ \"name\": "));
        Assert.Equal(ThemeJsonKind.Malformed, ThemeValidator.Classify(""));
        Assert.Equal(ThemeJsonKind.NotATheme, ThemeValidator.Classify("[1,2,3]"));
        Assert.Equal(ThemeJsonKind.NotATheme, ThemeValidator.Classify("42"));
        Assert.Equal(ThemeJsonKind.NotATheme, ThemeValidator.Classify("{\"foo\": 1}"));
        Assert.Equal(ThemeJsonKind.Theme, ThemeValidator.Classify("{\"name\": \"Mine\"}"));
        Assert.Equal(ThemeJsonKind.Theme, ThemeValidator.Classify(BuiltInThemes.Clean().ToJson()));
    }

    [Fact]
    public void Describe_repairs_lists_clamped_numbers_and_bad_colours()
    {
        var theme = BuiltInThemes.Clean().Clone();
        theme.Page.MarginTopMm = 9999;
        theme.Page.Background = "notacolor";
        theme.Links.Color = "blue";
        var repairs = ThemeValidator.DescribeRepairs(theme.ToJson());

        Assert.Equal(3, repairs.Count);
        Assert.Contains(repairs, r => r.StartsWith("Top margin 9999 mm was changed to 138.5 mm", StringComparison.Ordinal));
        Assert.Contains(repairs, r => r.Contains("Page background \"notacolor\"", StringComparison.Ordinal) && r.Contains("#FFFFFF", StringComparison.Ordinal));
        Assert.Contains(repairs, r => r.StartsWith("Link colour \"blue\"", StringComparison.Ordinal) && r.EndsWith("cleared.", StringComparison.Ordinal));

        // The import itself still repairs the same way.
        var imported = PdfTheme.FromJson(theme.ToJson());
        Assert.Equal(138.5, imported.Page.MarginTopMm);
        Assert.Equal("#FFFFFF", imported.Page.Background);
        Assert.Null(imported.Links.Color);
    }

    [Fact]
    public void Built_in_themes_and_missing_values_need_no_repairs()
    {
        foreach (var theme in BuiltInThemes.All())
        {
            Assert.Empty(ThemeValidator.DescribeRepairs(theme.ToJson()));
        }

        Assert.Empty(ThemeValidator.DescribeRepairs("{\"name\": \"Sparse\"}"));
        Assert.Empty(ThemeValidator.DescribeRepairs("[1,2,3]"));
    }

    [Fact]
    public void Repairs_message_shows_the_first_three_and_counts_the_rest()
    {
        Assert.Equal("Imported \"T\". One value was changed so the app can use it: A.", GalleryPage.RepairsMessage("T", ["A."]));
        var message = GalleryPage.RepairsMessage("T", ["A.", "B.", "C.", "D.", "E."]);
        Assert.Equal("Imported \"T\". 5 values were changed so the app can use them: A. B. C. 2 more not listed.", message);
    }

    // ------------------------------------------------------------- templates

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Page {page} of {pages}")]
    [InlineData("{TITLE} · {date}")]
    [InlineData("Plain text")]
    public void Valid_templates_have_no_message(string? template) => Assert.Null(ThemeBuilderValidation.Template(template));

    [Fact]
    public void Unclosed_and_unknown_tokens_are_flagged()
    {
        var unclosed = ThemeBuilderValidation.Template("Page {page} of {pages} {bogus");
        Assert.NotNull(unclosed);
        Assert.Contains("\"{bogus\" is not closed", unclosed, StringComparison.Ordinal);

        var unknown = ThemeBuilderValidation.Template("{page} {chapter}");
        Assert.NotNull(unknown);
        Assert.StartsWith("{chapter} is not a token", unknown, StringComparison.Ordinal);

        Assert.NotNull(ThemeBuilderValidation.Template("{page {pages}"));

        var theme = BuiltInThemes.Clean().Clone();
        theme.Footer.Enabled = true;
        theme.Footer.Right = "{bogus";
        Assert.Contains(ThemeBuilderValidation.All(theme), w => w.StartsWith("Footer: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Fast_preview_pages_token_uses_the_sheet_count_before_an_exact_render()
    {
        var theme = BuiltInThemes.Clean().Clone();
        theme.Cover.Enabled = false;
        theme.Toc.Enabled = false;
        theme.Footer.Enabled = true;
        theme.Footer.Center = "Page {page} of {pages}";
        var fragment = "<article class=\"aio-doc\"><p class=\"aio-p\" data-block=\"0\">One</p><div class=\"aio-pagebreak\" data-block=\"1\"></div><p class=\"aio-p\" data-block=\"2\">Two</p></article>";

        var html = ThemeBuilderPreview.Compose(new Document(), fragment, theme, totalPages: null);
        Assert.Contains("Page 1 of 2", html, StringComparison.Ordinal);
        Assert.Contains("Page 2 of 2", html, StringComparison.Ordinal);
        Assert.DoesNotContain("of n", html, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0001', html);

        Assert.Contains("Page 1 of 7", ThemeBuilderPreview.Compose(new Document(), fragment, theme, totalPages: 7), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- creator

    [Fact]
    public void Canvas_footer_shows_the_page_count_and_skips_page_one_when_the_theme_says_so()
    {
        var blank = BuiltInTemplates.Find(BuiltInTemplates.BlankId)!.Create();
        blank.Theme.Footer.Enabled = true;
        blank.Theme.Footer.Center = "{page} / {pages}";
        blank.Theme.Footer.ShowOnFirstPage = true;
        Assert.Contains("1 / 3", CreatorCanvasHtml.HeaderFooterHtml(blank, header: false, page: 1, pages: 3), StringComparison.Ordinal);
        Assert.Contains("1 / n", CreatorCanvasHtml.HeaderFooterHtml(blank, header: false, page: 1), StringComparison.Ordinal);

        blank.Theme.Footer.ShowOnFirstPage = false;
        Assert.Equal(string.Empty, CreatorCanvasHtml.HeaderFooterHtml(blank, header: false, page: 1, pages: 3));
        Assert.Contains("2 / 3", CreatorCanvasHtml.HeaderFooterHtml(blank, header: false, page: 2, pages: 3), StringComparison.Ordinal);
    }

    [Fact]
    public void Report_template_has_no_header_or_page_number_on_its_title_page()
    {
        var report = BuiltInTemplates.Find("report")!.Create();
        Assert.False(report.Theme.Footer.ShowOnFirstPage);
        Assert.False(report.Theme.Header.ShowOnFirstPage);
    }

    [Fact]
    public void An_untouched_template_has_no_edits_but_a_change_does()
    {
        var state = NewState();
        state.Load(BuiltInTemplates.Find("report")!.Create(), isNew: true, routeId: "new:report");
        Assert.True(state.IsDirty);
        Assert.False(state.HasEdits);

        state.Rename("Renamed");
        Assert.True(state.HasEdits);
    }

    private CreatorState NewState()
    {
        var publications = new FilePublicationStore(new AppPaths(Path.Combine(_root, "data")), NullLogger<FilePublicationStore>.Instance);
        var thumbnails = new CreatorThumbnailCache(new ServiceCollection().BuildServiceProvider(), new PdfiumRasterizer(), publications);
        return new CreatorState(publications, thumbnails);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
