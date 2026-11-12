using AllInOnePdf.Core;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Serialization;
using AllInOnePdf.Publishing.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllInOnePdf.Publishing.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aiopdf-pub-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FilePublicationStore _store;

    public StoreTests()
    {
        _store = new FilePublicationStore(new AppPaths(_root), NullLogger<FilePublicationStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task Save_get_list_and_delete()
    {
        var changes = 0;
        _store.Changed += () => changes++;
        Assert.Empty(await _store.ListAsync());

        var p = Samples.EveryModule();
        p.Modified = DateTimeOffset.UnixEpoch;
        await _store.SaveAsync(p, [1, 2, 3]);
        Assert.True(p.Modified > DateTimeOffset.UnixEpoch);
        Assert.True(File.Exists(Path.Combine(_root, "publications", p.Id + ".aiopub")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "publications"), "*.tmp"));

        var loaded = await _store.GetAsync(p.Id);
        Assert.NotNull(loaded);
        Assert.Equal(p.ToJson(), loaded.ToJson());
        Assert.Equal(Samples.FakePng, loaded.Assets[0].Data);
        Assert.Equal([1, 2, 3], await _store.GetThumbnailAsync(p.Id));

        var summary = Assert.Single(await _store.ListAsync());
        Assert.Equal(p.Id, summary.Id);
        Assert.Equal("Sample", summary.Name);
        Assert.Equal("Clean", summary.ThemeName);
        Assert.Equal(2, summary.SectionCount);

        Assert.True(await _store.DeleteAsync(p.Id));
        Assert.False(await _store.DeleteAsync(p.Id));
        Assert.Null(await _store.GetAsync(p.Id));
        Assert.Null(await _store.GetThumbnailAsync(p.Id));
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task List_is_sorted_by_modified_and_skips_unreadable_files()
    {
        var older = new Publication { Name = "Older" };
        await _store.SaveAsync(older);
        await Task.Delay(20);
        var newer = new Publication { Name = "Newer" };
        await _store.SaveAsync(newer);
        await File.WriteAllTextAsync(Path.Combine(_root, "publications", "broken.aiopub"), "not a zip");

        var list = await _store.ListAsync();
        Assert.Equal(["Newer", "Older"], list.Select(s => s.Name));
        Assert.Null(await _store.GetAsync("broken"));
        Assert.Null(await _store.GetAsync("../escape"));
    }

    [Fact]
    public async Task Duplicate_creates_a_copy_with_a_new_id()
    {
        var p = Samples.EveryModule();
        await _store.SaveAsync(p, [7]);

        var copy = await _store.DuplicateAsync(p.Id);
        Assert.NotEqual(p.Id, copy.Id);
        Assert.Equal("Sample copy", copy.Name);
        Assert.Equal([7], await _store.GetThumbnailAsync(copy.Id));
        Assert.Equal(Samples.FakePng, (await _store.GetAsync(copy.Id))!.Assets[0].Data);

        var named = await _store.DuplicateAsync(p.Id, "Renamed");
        Assert.Equal("Renamed", named.Name);
        Assert.Equal(3, (await _store.ListAsync()).Count);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _store.DuplicateAsync("missing"));
    }

    [Fact]
    public async Task Import_assigns_a_fresh_id_when_it_collides()
    {
        var p = Samples.EveryModule();
        var package = PublicationPackage.Write(p, [5]);

        var first = await _store.ImportAsync(new MemoryStream(package));
        Assert.Equal(p.Id, first.Id);
        var second = await _store.ImportAsync(new MemoryStream(package));
        Assert.NotEqual(p.Id, second.Id);
        Assert.Equal([5], await _store.GetThumbnailAsync(second.Id));
        Assert.Equal(2, (await _store.ListAsync()).Count);

        await Assert.ThrowsAsync<InvalidDataException>(() => _store.ImportAsync(new MemoryStream([1, 2, 3])));
    }

    [Fact]
    public async Task Import_marks_a_name_that_is_already_in_the_gallery()
    {
        var p = Samples.EveryModule();
        var package = PublicationPackage.Write(p, null);

        var first = await _store.ImportAsync(new MemoryStream(package));
        var second = await _store.ImportAsync(new MemoryStream(package));
        var third = await _store.ImportAsync(new MemoryStream(package));

        Assert.Equal(p.Name, first.Name);
        Assert.Equal(p.Name + " (imported)", second.Name);
        Assert.Equal(p.Name + " (imported 2)", third.Name);
        Assert.Equal(3, (await _store.ListAsync()).Select(s => s.Name).Distinct().Count());
        Assert.Equal("Fresh", FilePublicationStore.ImportedName(" Fresh ", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Other" }));
    }

    [Fact]
    public void Di_registers_the_store()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAllInOnePdfCore(_root);
        services.AddAllInOnePdfPublishing();
        using var provider = services.BuildServiceProvider();
        var store = Assert.IsType<FilePublicationStore>(provider.GetRequiredService<IPublicationStore>());
        Assert.Equal(Path.Combine(_root, "publications"), store.DirectoryPath);
    }
}
