using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Core.Themes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllInOnePdf.Core.Tests;

public class ThemeAndOptionsTests
{
    [Fact]
    public void Theme_round_trips_through_json()
    {
        var theme = BuiltInThemes.Technical();
        theme.Headings.H1.SizePt = 31.5;
        theme.Code.ShowLineNumbers = true;

        var json = theme.ToJson();
        var back = PdfTheme.FromJson(json);

        Assert.Equal(theme.Id, back.Id);
        Assert.Equal(31.5, back.Headings.H1.SizePt);
        Assert.True(back.Code.ShowLineNumbers);
        Assert.Equal(PageSizeName.Letter, back.Page.Size);
        Assert.False(back.IsBuiltIn);
    }

    [Fact]
    public void Built_in_themes_have_unique_ids_and_pass_validation_unchanged()
    {
        var all = BuiltInThemes.All();
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(all.Count, all.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.NotNull(BuiltInThemes.Find(BuiltInThemes.DefaultId));

        foreach (var theme in all)
        {
            Assert.True(theme.IsBuiltIn, theme.Id);
            Assert.False(string.IsNullOrWhiteSpace(theme.Description), theme.Id);
            Assert.NotEmpty(theme.Tags);

            // Every value a built-in ships is already inside the ranges the validator enforces on imported themes.
            var before = theme.ToJson();
            var after = ThemeValidator.Normalize(theme.Clone()).ToJson();
            Assert.True(before == after, $"{theme.Id} is changed by ThemeValidator.Normalize");
        }
    }

    [Fact]
    public void Duplicate_creates_editable_copy()
    {
        var copy = BuiltInThemes.Manuscript().Duplicate("Mine");

        Assert.NotEqual("manuscript", copy.Id);
        Assert.Equal("Mine", copy.Name);
        Assert.False(copy.IsBuiltIn);
        Assert.Equal("manuscript", copy.BasedOn);
        Assert.Equal("Literata", copy.Typography.BodyFont);
    }

    [Fact]
    public void Option_values_serialize_with_primitive_types()
    {
        var values = new OptionValues().Set("flag", true).Set("count", 3).Set("ratio", 1.5).Set("name", "x");
        var json = values.ToJson();
        var back = OptionValues.FromJson(json);

        Assert.True(back.GetBool("flag"));
        Assert.Equal(3, back.GetInt("count"));
        Assert.Equal(1.5, back.GetDouble("ratio"));
        Assert.Equal("x", back.GetString("name"));
        Assert.Equal(7, back.GetInt("missing", 7));
    }

    [Fact]
    public void Defaults_fill_missing_keys_only()
    {
        var defs = new[] { OptionDefinition.Bool("a", "A", true), OptionDefinition.Int("b", "B", 5, 0, 10) };
        var values = new OptionValues().Set("b", 9).WithDefaults(defs);

        Assert.True(values.GetBool("a"));
        Assert.Equal(9, values.GetInt("b"));
    }

    [Fact]
    public async Task File_theme_store_saves_lists_and_deletes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aio-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dir);
        paths.EnsureCreated();
        var store = new FileThemeStore(paths, NullLogger<FileThemeStore>.Instance);

        var mine = await store.DuplicateAsync("night", "My night");
        var all = await store.GetAllAsync();

        Assert.Contains(all, t => t.Id == mine.Id && !t.IsBuiltIn);
        Assert.Equal(BuiltInThemes.All().Count + 1, all.Count);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(BuiltInThemes.Clean()));
        Assert.True(await store.DeleteAsync(mine.Id));
        Assert.False(await store.DeleteAsync("clean"));

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Output_paths_cannot_escape_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aio-tests-out");
        Directory.CreateDirectory(dir);

        var safe = ConversionService.ResolveOutputPath(dir, "..\\..\\evil.pdf", overwrite: true);

        Assert.StartsWith(Path.GetFullPath(dir), safe);
        Assert.EndsWith("evil.pdf", safe);
        Assert.Equal(Path.GetFullPath(dir), Path.GetDirectoryName(safe));

        var nested = ConversionService.ResolveOutputPath(dir, "notes_files/image-001.png", overwrite: true);
        Assert.Equal(Path.Combine(Path.GetFullPath(dir), "notes_files", "image-001.png"), nested);

        var rooted = ConversionService.ResolveOutputPath(dir, Path.Combine(Path.GetTempPath(), "elsewhere", "x.png"), overwrite: true);
        Assert.Equal(Path.GetFullPath(dir), Path.GetDirectoryName(rooted));
    }
}
