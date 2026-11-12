using AllInOnePdf.UI.Services;
using AllInOnePdf.UI.Services.Shared;

namespace AllInOnePdf.Core.Tests;

public class AboutAndHelpTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3", null)]
    [InlineData("1.2.3+abc1234def5678", "1.2.3", "abc1234")]
    [InlineData("0.1.0-beta.2+25e50f5c0ffee", "0.1.0-beta.2", "25e50f5")]
    [InlineData("1.0.0+build.42", "1.0.0", null)]
    public void Informational_version_splits_into_version_and_short_commit(string raw, string version, string? commit)
    {
        var (v, c) = BuildInfo.Split(raw);
        Assert.Equal(version, v);
        Assert.Equal(commit, c);
    }

    [Fact]
    public void Display_version_is_never_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.DisplayVersion));
        Assert.DoesNotContain("+", BuildInfo.DisplayVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void Notices_are_embedded_and_render_without_links_or_raw_html()
    {
        var markdown = ThirdPartyNotices.Markdown;
        Assert.NotNull(markdown);
        Assert.Contains("HarfBuzzSharp", markdown, StringComparison.Ordinal);

        var html = ThirdPartyNotices.Html!;
        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<pre>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<a ", html, StringComparison.Ordinal);

        // Inside the app: no repeated title, no "How to see this in the app" line, licence texts collapsed.
        Assert.DoesNotContain("<h1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("How to see this in the app", html, StringComparison.Ordinal);
        Assert.Contains("<details class=\"aio-notice\"><summary>Markdig</summary>", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Notes</h2>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Notices_rendering_collapses_every_section_after_the_first()
    {
        var html = ThirdPartyNotices.ToHtml("# Title\n\nHow to see this in the app: open Settings.\n\nIntro.\n\n## Notes\n\nA note.\n\n## Lib\n\nText.\n\n## Other\n\nMore.\n");
        Assert.DoesNotContain("Title", html, StringComparison.Ordinal);
        Assert.DoesNotContain("How to see", html, StringComparison.Ordinal);
        Assert.Contains("<p>Intro.</p>", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Notes</h2>", html, StringComparison.Ordinal);
        var compact = html.Replace("\n", "", StringComparison.Ordinal);
        Assert.Contains("<details class=\"aio-notice\"><summary>Lib</summary><p>Text.</p></details>", compact, StringComparison.Ordinal);
        Assert.Contains("<details class=\"aio-notice\"><summary>Other</summary><p>More.</p></details>", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void Notices_rendering_encodes_html_and_flattens_links()
    {
        var html = ThirdPartyNotices.ToHtml("Hello <script>alert(1)</script> [site](https://example.com) <https://example.org>\n\n<div>block</div>");
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<div>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<a ", html, StringComparison.Ordinal);
        Assert.Contains("site", html, StringComparison.Ordinal);
        Assert.Contains("https://example.org", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_include_the_newest_crash_log_head()
    {
        var root = Path.Combine(Path.GetTempPath(), "aio-diag-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, DiagnosticsReport.LogsFolderName);
        Directory.CreateDirectory(logs);
        try
        {
            var older = Path.Combine(logs, "crash-20260101-000000.log");
            File.WriteAllText(older, "old crash");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));
            var newer = Path.Combine(logs, "crash-20260102-000000.log");
            File.WriteAllLines(newer, Enumerable.Range(1, 60).Select(i => $"line {i}"));

            var report = DiagnosticsReport.Build(HostKind.Desktop, root, isPortable: true, "clean");

            Assert.Contains("crash-20260102-000000.log", report, StringComparison.Ordinal);
            Assert.Contains("line 40", report, StringComparison.Ordinal);
            Assert.DoesNotContain("line 41", report, StringComparison.Ordinal);
            Assert.DoesNotContain("old crash", report, StringComparison.Ordinal);
            Assert.Contains("Portable:      yes", report, StringComparison.Ordinal);
            Assert.Contains("Default theme: clean", report, StringComparison.Ordinal);
            Assert.Contains("Desktop app", report, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Diagnostics_without_logs_say_so()
    {
        var root = Path.Combine(Path.GetTempPath(), "aio-diag-" + Guid.NewGuid().ToString("N"));
        var report = DiagnosticsReport.Build(HostKind.Browser, root, isPortable: false, null);
        Assert.Contains("Crash log:     none", report, StringComparison.Ordinal);
        Assert.Contains("Web app (browser)", report, StringComparison.Ordinal);
    }
}
