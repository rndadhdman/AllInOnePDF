using AllInOnePdf.UI.Services;
using AllInOnePdf.UI.Services.Shared;

namespace AllInOnePdf.Core.Tests;

public class FileFiltersTests
{
    private static readonly string[] ManyExtensions =
    [
        ".md", ".markdown", ".mdown", ".mkd", ".txt", ".text", ".log", ".pdf", ".html", ".htm", ".xhtml", ".epub", ".png",
        ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".csv", ".json", ".cs", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py",
        ".java", ".go", ".rs", ".cpp", ".cc", ".c", ".h", ".hpp", ".css", ".scss", ".xml", ".yaml", ".yml", ".toml",
        ".ini", ".sh", ".ps1", ".sql", ".rb", ".php", ".kt", ".swift", ".diff", ".patch", ".docx", ".xlsx", ".pptx", ".rtf",
    ];

    [Fact]
    public void Long_filter_is_split_into_short_chunks_that_keep_every_extension()
    {
        var filters = FileFilters.Normalize([new FileFilter("Supported files", ManyExtensions)]);

        Assert.True(filters.Count >= 2, "a 55-extension filter must be split");
        foreach (var f in filters)
        {
            var pattern = string.Join(";", f.Extensions.Select(e => "*." + e));
            Assert.True(pattern.Length <= FileFilters.MaxPatternLength, $"{f.Name}: {pattern.Length} chars");
            Assert.StartsWith("Supported files (", f.Name);
        }

        var all = filters.SelectMany(f => f.Extensions).ToList();
        Assert.Equal(ManyExtensions.Length, all.Count);
        Assert.Equal(ManyExtensions.Select(e => e.TrimStart('.')), all);
    }

    [Fact]
    public void Short_filters_are_left_alone_and_cleaned()
    {
        var filters = FileFilters.Normalize([new FileFilter("Markdown", [".md", "*.markdown", "MD", " ", "bad ext"]), new FileFilter("Empty", [])]);

        var only = Assert.Single(filters);
        Assert.Equal("Markdown", only.Name);
        Assert.Equal(new[] { "md", "markdown" }, only.Extensions);
    }

    [Fact]
    public void Null_or_empty_input_gives_no_filters()
    {
        Assert.Empty(FileFilters.Normalize(null));
        Assert.Empty(FileFilters.Normalize([]));
    }
}
