using System.Globalization;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Tests;

public class DocumentDatesTests
{
    private static readonly DateTimeOffset Date = new(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Formats_in_the_documents_language()
    {
        Assert.Equal("20.06.2026", DocumentDates.Short(Date, "de"));
        Assert.Equal("20/06/2026", DocumentDates.Short(Date, "en-GB"));
        Assert.Equal("6/20/2026", DocumentDates.Short(Date, "en-US"));
        Assert.Contains("2026", DocumentDates.Long(Date, "fr"), StringComparison.Ordinal);
        Assert.Contains("juin", DocumentDates.Long(Date, "fr"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xx-YY")]
    [InlineData("not a language")]
    public void Falls_back_to_the_current_culture_for_missing_or_unknown_tags(string? language)
    {
        Assert.Equal(CultureInfo.CurrentCulture, DocumentDates.CultureFor(language));
        Assert.Equal(Date.ToString("d", CultureInfo.CurrentCulture), DocumentDates.Short(Date, language));
    }
}
