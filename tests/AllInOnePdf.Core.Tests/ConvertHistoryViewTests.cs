using AllInOnePdf.Core.Storage;
using AllInOnePdf.UI.Services;

namespace AllInOnePdf.Core.Tests;

/// <summary>Filtering, sorting and the count line of the History page.</summary>
public class ConvertHistoryViewTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    private static HistoryEntry Entry(string name, int minutes, string target = "pdf", bool success = true, double seconds = 1, long bytes = 100, string? output = null) =>
        new()
        {
            SourceFileName = name,
            Timestamp = Start.AddMinutes(minutes),
            TargetFormatId = target,
            Success = success,
            ElapsedSeconds = seconds,
            OutputBytes = bytes,
            OutputPaths = output is null ? [] : [output],
        };

    private static readonly HistoryEntry[] Entries =
    [
        Entry("b.md", 1, "pdf", seconds: 3, bytes: 300),
        Entry("a.docx", 2, "markdown", success: false, seconds: 1),
        Entry("c.csv", 3, "xlsx", seconds: 2, bytes: 50, output: @"C:\out\report.xlsx"),
    ];

    private static string[] Names(IReadOnlyList<HistoryEntry> list) => list.Select(e => e.SourceFileName).ToArray();

    [Fact]
    public void Default_order_is_newest_first_and_flips_to_oldest_first()
    {
        Assert.Equal(["c.csv", "a.docx", "b.md"], Names(ConvertHistoryView.Apply(Entries, null, "all", "all", HistorySortColumn.When, descending: true)));
        Assert.Equal(["b.md", "a.docx", "c.csv"], Names(ConvertHistoryView.Apply(Entries, null, "all", "all", HistorySortColumn.When, descending: false)));
    }

    [Fact]
    public void Search_matches_source_and_output_names_and_filters_combine()
    {
        Assert.Equal(["c.csv"], Names(ConvertHistoryView.Apply(Entries, " REPORT ", "all", "all", HistorySortColumn.When, true)));
        Assert.Equal(["a.docx"], Names(ConvertHistoryView.Apply(Entries, null, ConvertHistoryView.FailedOnly, "all", HistorySortColumn.When, true)));
        Assert.Equal(["b.md"], Names(ConvertHistoryView.Apply(Entries, null, ConvertHistoryView.ConvertedOnly, "PDF", HistorySortColumn.When, true)));
    }

    [Fact]
    public void Columns_sort_by_their_values_and_failed_sizes_sort_last()
    {
        Assert.Equal(["a.docx", "b.md", "c.csv"], Names(ConvertHistoryView.Apply(Entries, null, "all", "all", HistorySortColumn.File, false)));
        Assert.Equal(["b.md", "c.csv", "a.docx"], Names(ConvertHistoryView.Apply(Entries, null, "all", "all", HistorySortColumn.Duration, true)));
        Assert.Equal(["b.md", "c.csv", "a.docx"], Names(ConvertHistoryView.Apply(Entries, null, "all", "all", HistorySortColumn.Size, true)));
        Assert.Equal("a.docx", Names(ConvertHistoryView.Apply(Entries, null, "all", "all", HistorySortColumn.Result, false))[0]);
    }

    [Fact]
    public void Header_click_flips_the_same_column_and_starts_new_columns_in_their_natural_order()
    {
        Assert.Equal((HistorySortColumn.When, false), ConvertHistoryView.Toggle(HistorySortColumn.When, true, HistorySortColumn.When));
        Assert.Equal((HistorySortColumn.File, false), ConvertHistoryView.Toggle(HistorySortColumn.When, true, HistorySortColumn.File));
        Assert.Equal((HistorySortColumn.Size, true), ConvertHistoryView.Toggle(HistorySortColumn.File, false, HistorySortColumn.Size));
    }

    [Fact]
    public void Count_line_names_the_total_and_what_a_filter_hides()
    {
        Assert.Equal("1 conversion", ConvertHistoryView.CountText(1, 1));
        Assert.Equal("5 conversions", ConvertHistoryView.CountText(5, 5));
        Assert.Equal("2 of 5 conversions", ConvertHistoryView.CountText(2, 5));
    }
}
