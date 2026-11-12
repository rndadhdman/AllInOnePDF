using AllInOnePdf.Core.Storage;
using AllInOnePdf.UI.Services;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.JSInterop;

namespace AllInOnePdf.Core.Tests;

/// <summary>The desktop title bar preference, the browser host's window controls and the About links.</summary>
public sealed class TitleBarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aio-titlebar-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_app_title_bar_is_the_default()
    {
        Assert.False(new UserPreferences().UseSystemTitleBar);
    }

    [Fact]
    public async Task The_system_title_bar_choice_survives_a_restart_and_a_reset()
    {
        var paths = new AppPaths(Path.Combine(_root, "data"));
        using (var first = new FilePreferencesService(paths))
        {
            await first.UpdateAsync(p => p.UseSystemTitleBar = true);
        }

        using var second = new FilePreferencesService(paths);
        await second.LoadAsync();
        Assert.True(second.Current.UseSystemTitleBar);

        await second.ResetAsync(paths.DefaultOutputDirectory);
        Assert.False(second.Current.UseSystemTitleBar);
    }

    [Fact]
    public async Task The_browser_host_has_no_window_controls_and_ignores_them()
    {
        var host = new BrowserHostServices(new ThrowingJsRuntime());
        var raised = false;
        host.WindowStateChanged += () => raised = true;

        Assert.False(host.SupportsWindowControls);
        Assert.False(host.IsMaximized);
        Assert.False(await host.BeginWindowDragAsync(WindowDragOperation.Move));
        await host.MinimizeWindowAsync();
        await host.ToggleMaximizeWindowAsync();
        await host.CloseWindowAsync();
        await host.MoveWindowByAsync(10, 10);
        Assert.False(raised);
    }

    [Fact]
    public void About_links_are_absolute_https_urls()
    {
        foreach (var url in new[] { BuildInfo.RepositoryUrl, BuildInfo.ReleasesUrl, BuildInfo.WebsiteUrl })
        {
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri), url);
            Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
        }

        Assert.Equal("therandomadmin.com", new Uri(BuildInfo.WebsiteUrl).Host);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>The window operations must not touch JS in the browser.</summary>
    private sealed class ThrowingJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("No JS expected: " + identifier);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("No JS expected: " + identifier);
    }
}
