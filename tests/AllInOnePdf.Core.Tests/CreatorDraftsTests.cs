using System.Text;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Serialization;
using AllInOnePdf.Publishing.Storage;
using AllInOnePdf.Publishing.Templates;
using AllInOnePdf.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllInOnePdf.Core.Tests;

public sealed class CreatorDraftsTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aio-creator-drafts", Guid.NewGuid().ToString("N"));

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

    // ----------------------------------------------------------- reading a draft

    [Fact]
    public void A_new_publication_draft_gives_its_name_route_and_template()
    {
        var draft = CreatorDrafts.Describe("publication:new:flyer", Package("Summer fair", Now));

        Assert.NotNull(draft);
        Assert.Equal("new:flyer", draft.RouteId);
        Assert.Equal("Summer fair", draft.Name);
        Assert.Equal("flyer", draft.TemplateId);
        Assert.Equal("Flyer", draft.TemplateName);
        Assert.True(draft.IsNew);
        Assert.Null(draft.PublicationId);
        Assert.Equal(Now, draft.Written);
    }

    [Fact]
    public void A_saved_publication_draft_points_at_the_publication()
    {
        var draft = CreatorDrafts.Describe("publication:abc123", Package("Annual report", Now));

        Assert.NotNull(draft);
        Assert.False(draft.IsNew);
        Assert.Equal("abc123", draft.PublicationId);
        Assert.Null(draft.TemplateName);
        Assert.Equal("Annual report", draft.Name);
    }

    [Fact]
    public void A_draft_without_a_name_reads_as_untitled()
    {
        var draft = CreatorDrafts.Describe("publication:new:menu", Package("   ", Now));
        Assert.Equal("Untitled publication", draft?.Name);
        Assert.Equal("Menu", draft?.TemplateName);
    }

    [Theory]
    [InlineData("theme:abc")]
    [InlineData("publication:")]
    [InlineData("Publication:abc")]
    public void Keys_that_are_not_publication_drafts_are_ignored(string key)
    {
        Assert.False(CreatorDrafts.TryGetRouteId(key, out _));
        Assert.Null(CreatorDrafts.Describe(key, Package("X", Now)));
    }

    [Fact]
    public void Unreadable_draft_content_is_ignored()
    {
        Assert.Null(CreatorDrafts.Describe("publication:new:flyer", new Draft(Encoding.UTF8.GetBytes("not a package"), Now)));
    }

    [Fact]
    public void The_draft_key_round_trips_through_the_route_id()
    {
        Assert.True(CreatorDrafts.TryGetRouteId(CreatorState.DraftKeyFor("new:report"), out var route));
        Assert.Equal("new:report", route);
    }

    // ------------------------------------------------------------------- listing

    [Fact]
    public async Task Listing_keeps_readable_publication_drafts_newest_first()
    {
        var store = new MemoryDraftStore();
        store.Put("publication:new:flyer", Package("Old flyer", Now.AddHours(-5)));
        store.Put("publication:new:menu", Package("Lunch menu", Now.AddMinutes(-2)));
        store.Put("publication:p1", Package("Report", Now.AddHours(-1)));
        store.Put("theme:t1", new Draft(Encoding.UTF8.GetBytes("{}"), Now));
        store.Put("publication:new:broken", new Draft([1, 2, 3], Now));
        store.Put("publication:new:resume", Package("Ancient CV", Now.AddDays(-31)));

        var drafts = await new CreatorDrafts(store).ListAsync();

        Assert.Equal(["Lunch menu", "Report", "Old flyer"], drafts.Select(d => d.Name));
    }

    [Fact]
    public void Ordering_is_newest_first()
    {
        var a = new CreatorDraft("publication:a", "a", "A", Now.AddDays(-1), null);
        var b = new CreatorDraft("publication:b", "b", "B", Now, null);
        var c = new CreatorDraft("publication:c", "c", "C", Now.AddHours(-1), null);

        Assert.Equal(["B", "C", "A"], CreatorDrafts.Newest([a, b, c]).Select(d => d.Name));
    }

    [Fact]
    public void Matching_splits_new_drafts_from_saved_publications_with_newer_changes()
    {
        var saved = new[] { Summary("p1", Now.AddHours(-2)), Summary("p2", Now.AddHours(-1)) };
        var drafts = new[]
        {
            new CreatorDraft("publication:new:flyer", "new:flyer", "Flyer", Now.AddMinutes(-3), "flyer"),
            new CreatorDraft("publication:p1", "p1", "Report", Now.AddMinutes(-10), null),   // newer than the save
            new CreatorDraft("publication:p2", "p2", "Menu", Now.AddHours(-3), null),        // older than the save
            new CreatorDraft("publication:gone", "gone", "Deleted", Now, null),              // no such publication
        };

        var match = CreatorDrafts.Match(drafts, saved);

        Assert.Equal(["Flyer"], match.Unsaved.Select(d => d.Name));
        Assert.Equal(["p1"], match.BySavedPublication.Keys);
    }

    [Fact]
    public async Task Discard_deletes_the_draft()
    {
        var store = new MemoryDraftStore();
        store.Put("publication:new:flyer", Package("Flyer", Now));
        var drafts = new CreatorDrafts(store);

        await drafts.DiscardAsync((await drafts.ListAsync())[0]);

        Assert.Empty(await drafts.ListAsync());
    }

    // -------------------------------------------------------------- auto-restore

    [Fact]
    public async Task Without_a_request_the_draft_is_offered()
    {
        var (state, drafts) = NewState();
        state.Load(BuiltInTemplates.Find("flyer")!.Create(), isNew: true, routeId: "new:flyer");
        await drafts.WriteAsync(CreatorState.DraftKeyFor("new:flyer"), ChangedFlyer("Summer fair"));

        await state.CheckForDraftAsync();

        Assert.NotNull(state.PendingDraft);
        Assert.NotEqual("Summer fair", state.Working!.Name);
    }

    [Fact]
    public async Task A_restore_request_restores_the_draft_without_asking()
    {
        var (state, drafts) = NewState();
        await drafts.WriteAsync(CreatorState.DraftKeyFor("new:flyer"), ChangedFlyer("Summer fair"));

        state.RequestDraftRestore("new:flyer");
        Assert.Equal("new:flyer", state.DraftRestoreRequest);
        state.Load(BuiltInTemplates.Find("flyer")!.Create(), isNew: true, routeId: "new:flyer");
        await state.CheckForDraftAsync();

        Assert.Null(state.PendingDraft);
        Assert.Equal("Summer fair", state.Working!.Name);
        Assert.True(state.CanUndo);
        Assert.Null(state.DraftRestoreRequest);
    }

    [Fact]
    public async Task A_restore_request_for_another_route_is_used_up_without_restoring()
    {
        var (state, drafts) = NewState();
        await drafts.WriteAsync(CreatorState.DraftKeyFor("new:flyer"), ChangedFlyer("Summer fair"));

        state.RequestDraftRestore("new:menu");
        state.Load(BuiltInTemplates.Find("flyer")!.Create(), isNew: true, routeId: "new:flyer");
        await state.CheckForDraftAsync();

        Assert.NotNull(state.PendingDraft);
        Assert.Null(state.DraftRestoreRequest);
    }

    [Fact]
    public void A_restore_request_for_the_open_route_forgets_the_session()
    {
        var (state, _) = NewState();
        state.Load(BuiltInTemplates.Find("flyer")!.Create(), isNew: true, routeId: "new:flyer");

        state.RequestDraftRestore("new:flyer");

        Assert.Null(state.LoadedId);
    }

    // ------------------------------------------------------------------- helpers

    private (CreatorState State, IDraftStore Drafts) NewState()
    {
        var publications = new FilePublicationStore(new AppPaths(Path.Combine(_root, "data")), NullLogger<FilePublicationStore>.Instance);
        var thumbnails = new CreatorThumbnailCache(new ServiceCollection().BuildServiceProvider(), new PdfiumRasterizer(), publications);
        var drafts = new FileDraftStore(Path.Combine(_root, "drafts"));
        return (new CreatorState(publications, thumbnails, drafts), drafts);
    }

    private static byte[] ChangedFlyer(string name)
    {
        var publication = BuiltInTemplates.Find("flyer")!.Create();
        publication.Name = name;
        publication.Sections.Add(new Section { Rows = [new Row()] });
        return PublicationPackage.Write(publication);
    }

    private static Draft Package(string name, DateTimeOffset written)
    {
        var publication = BuiltInTemplates.Find(BuiltInTemplates.BlankId)!.Create();
        publication.Name = name;
        return new Draft(PublicationPackage.Write(publication), written);
    }

    private static PublicationSummary Summary(string id, DateTimeOffset modified)
    {
        var publication = BuiltInTemplates.Find(BuiltInTemplates.BlankId)!.Create();
        publication.Id = id;
        publication.Modified = modified;
        return PublicationSummary.From(publication);
    }

    /// <summary>An in-memory draft store with explicit write times.</summary>
    private sealed class MemoryDraftStore : IDraftStore
    {
        private readonly Dictionary<string, Draft> _drafts = new(StringComparer.Ordinal);

        public void Put(string key, Draft draft) => _drafts[key] = draft;

        public Task WriteAsync(string key, byte[] content, CancellationToken ct = default)
        {
            _drafts[key] = new Draft(content, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }

        public Task<Draft?> TryReadAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_drafts.GetValueOrDefault(key));

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            _drafts.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DraftInfo>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DraftInfo>>(
                [.. _drafts.OrderByDescending(d => d.Value.Written).Select(d => new DraftInfo(d.Key, d.Value.Written, d.Value.Content.LongLength))]);
    }
}
