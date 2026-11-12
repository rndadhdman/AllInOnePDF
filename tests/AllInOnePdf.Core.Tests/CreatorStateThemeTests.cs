using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Storage;
using AllInOnePdf.Publishing.Templates;
using AllInOnePdf.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllInOnePdf.Core.Tests;

/// <summary>Changing a publication's theme changes its look, not its page settings or what its header and footer say.</summary>
public sealed class CreatorStateThemeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aio-theme-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Set_theme_keeps_page_geometry_and_header_text_but_takes_the_new_look()
    {
        var state = NewState();
        state.Load(BuiltInTemplates.Find("flyer")!.Create(), isNew: true, routeId: "new:flyer");
        var working = state.Working!;
        working.Theme.Page.Orientation = PageOrientation.Landscape;
        working.Theme.Page.Size = PageSizeName.A5;
        working.Theme.Page.MarginTopMm = 7;
        working.Theme.Header.Enabled = true;
        working.Theme.Header.Left = "My own header";
        working.Theme.Footer.Center = "Page {page}";
        working.Theme.Footer.ShowOnFirstPage = false;
        state.Commit();

        var night = BuiltInThemes.Find("night")!;
        state.SetTheme(night);

        var theme = state.Working!.Theme;
        Assert.Equal("night", state.Working.ThemeId);
        Assert.Equal(PageOrientation.Landscape, theme.Page.Orientation);
        Assert.Equal(PageSizeName.A5, theme.Page.Size);
        Assert.Equal(7, theme.Page.MarginTopMm);
        Assert.True(theme.Header.Enabled);
        Assert.Equal("My own header", theme.Header.Left);
        Assert.Equal("Page {page}", theme.Footer.Center);
        Assert.False(theme.Footer.ShowOnFirstPage);

        // The look comes from the new theme: a dark page and its text colours.
        Assert.Equal(night.Page.Background, theme.Page.Background);
        Assert.Equal(night.Colors.Text, theme.Colors.Text);
        Assert.Equal(night.Header.Color, theme.Header.Color);
        Assert.NotEqual("#FFFFFF", theme.Page.Background);
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
