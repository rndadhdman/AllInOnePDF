using System.Text;
using AllInOnePdf.Core.Storage;

namespace AllInOnePdf.Core.Tests;

public sealed class DraftStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aio-drafts", Guid.NewGuid().ToString("N"));
    private readonly FileDraftStore _store;

    public DraftStoreTests()
    {
        _store = new FileDraftStore(_root);
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
    public async Task Write_then_read_returns_the_content_and_a_recent_time()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _store.WriteAsync("theme:abc", Encoding.UTF8.GetBytes("{\"x\":1}"));

        var draft = await _store.TryReadAsync("theme:abc");

        Assert.NotNull(draft);
        Assert.Equal("{\"x\":1}", Encoding.UTF8.GetString(draft.Content));
        Assert.InRange(draft.Written, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Missing_draft_reads_as_null()
    {
        Assert.Null(await _store.TryReadAsync("theme:none"));
        await _store.DeleteAsync("theme:none");
        Assert.Empty(await _store.ListAsync());
    }

    [Fact]
    public async Task Write_replaces_and_delete_removes()
    {
        await _store.WriteAsync("publication:p1", [1, 2, 3]);
        await _store.WriteAsync("publication:p1", [4, 5]);

        Assert.Equal(new byte[] { 4, 5 }, (await _store.TryReadAsync("publication:p1"))!.Content);

        await _store.DeleteAsync("publication:p1");

        Assert.Null(await _store.TryReadAsync("publication:p1"));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task List_reports_original_keys_and_lengths()
    {
        await _store.WriteAsync("theme:new", new byte[10]);
        await _store.WriteAsync("publication:new:magazine", new byte[200_000]);

        var list = await _store.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, d => d is { Key: "theme:new", Length: 10 });
        Assert.Contains(list, d => d is { Key: "publication:new:magazine", Length: 200_000 });
    }

    [Theory]
    [InlineData("theme:abc")]
    [InlineData("../../evil\\path:name")]
    [InlineData("  ")]
    [InlineData("a/b|c*d?e\"f<g>h")]
    public async Task Keys_become_safe_file_names_inside_the_folder(string key)
    {
        var name = FileDraftStore.FileNameFor(key);

        Assert.EndsWith(FileDraftStore.Extension, name, StringComparison.Ordinal);
        Assert.DoesNotContain(name, c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '/' or '\\' or '*' or '?' or '|' or '"' or '<' or '>');
        Assert.False(name.StartsWith('.'));

        await _store.WriteAsync(key, [7]);
        Assert.Equal(_root, Path.GetDirectoryName(Directory.GetFiles(_root).Single()));
        Assert.Equal(new byte[] { 7 }, (await _store.TryReadAsync(key))!.Content);
    }

    [Fact]
    public void Similar_keys_get_different_files()
    {
        Assert.NotEqual(FileDraftStore.FileNameFor("theme:a"), FileDraftStore.FileNameFor("theme_a"));
        Assert.Equal(FileDraftStore.FileNameFor("theme:a"), FileDraftStore.FileNameFor("theme:a"));
    }

    [Fact]
    public async Task Corrupt_and_truncated_files_are_ignored()
    {
        await _store.WriteAsync("theme:good", [1, 2, 3]);
        await _store.WriteAsync("theme:cut", [1, 2, 3, 4, 5]);
        var cut = Path.Combine(_root, FileDraftStore.FileNameFor("theme:cut"));
        var bytes = await File.ReadAllBytesAsync(cut);
        await File.WriteAllBytesAsync(cut, bytes[..^2]);
        await File.WriteAllTextAsync(Path.Combine(_root, FileDraftStore.FileNameFor("theme:junk")), "not a draft");

        Assert.Null(await _store.TryReadAsync("theme:cut"));
        Assert.Null(await _store.TryReadAsync("theme:junk"));
        var list = await _store.ListAsync();
        Assert.Equal("theme:good", Assert.Single(list).Key);
    }

    [Fact]
    public async Task Delete_older_than_keeps_recent_drafts()
    {
        await _store.WriteAsync("theme:recent", [1]);

        Assert.Equal(0, await _store.DeleteOlderThanAsync(TimeSpan.FromDays(30)));
        Assert.Equal(1, await _store.DeleteOlderThanAsync(TimeSpan.FromDays(-1)));
        Assert.Empty(await _store.ListAsync());
    }

    [Fact]
    public void App_paths_create_the_drafts_folder()
    {
        var paths = new AppPaths(Path.Combine(_root, "data"));
        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.DraftsDirectory));
        Assert.Equal(Path.Combine(_root, "data", "drafts"), paths.DraftsDirectory);
    }
}
