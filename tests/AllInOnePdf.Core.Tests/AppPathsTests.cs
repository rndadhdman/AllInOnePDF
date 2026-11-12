using AllInOnePdf.Core.Storage;

namespace AllInOnePdf.Core.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aio-paths", Guid.NewGuid().ToString("N"));

    public AppPathsTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Environment_override_wins()
    {
        var data = AppPaths.ResolveDataRoot(_root, Path.Combine(_root, "elsewhere"), out var portable);

        Assert.Equal(Path.Combine(_root, "elsewhere"), data);
        Assert.False(portable);
    }

    [Fact]
    public void Marker_file_switches_to_portable_data_folder_beside_executable()
    {
        File.WriteAllText(Path.Combine(_root, AppPaths.PortableMarkerFileName), string.Empty);

        var data = AppPaths.ResolveDataRoot(_root, null, out var portable);

        Assert.True(portable);
        Assert.Equal(Path.Combine(_root, "Data"), data);
        Assert.Equal(Path.Combine(data, "Output"), new AppPaths(data, portable).DefaultOutputDirectory);
    }

    [Fact]
    public void Marker_file_can_point_at_a_relative_folder()
    {
        File.WriteAllText(Path.Combine(_root, AppPaths.PortableMarkerFileName), "# comment\n..\\MySettings\n");

        var data = AppPaths.ResolveDataRoot(_root, null, out var portable);

        Assert.True(portable);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "..", "MySettings")), data);
    }

    [Fact]
    public void PortableApps_layout_uses_sibling_data_folder()
    {
        var exeDir = Path.Combine(_root, "App", "AllInOnePdf");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(Path.Combine(_root, "App", "AppInfo"));

        var data = AppPaths.ResolveDataRoot(exeDir, null, out var portable);

        Assert.True(portable);
        Assert.Equal(Path.Combine(_root, "Data"), data);
    }

    [Fact]
    public void EnsureCreated_creates_the_publications_folder()
    {
        var paths = new AppPaths(Path.Combine(_root, "data"));

        paths.EnsureCreated();

        Assert.Equal(Path.Combine(_root, "data", "publications"), paths.PublicationsDirectory);
        Assert.True(Directory.Exists(paths.PublicationsDirectory));
        Assert.True(Directory.Exists(paths.ThemesDirectory));
    }

    [Fact]
    public void Plain_folder_uses_per_user_application_data()
    {
        var data = AppPaths.ResolveDataRoot(_root, null, out var portable);

        Assert.False(portable);
        Assert.EndsWith(AppPaths.AppFolderName, data);
        Assert.DoesNotContain(_root, data);
    }
}
