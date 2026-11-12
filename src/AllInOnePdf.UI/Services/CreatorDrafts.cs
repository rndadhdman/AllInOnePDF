using AllInOnePdf.Core.Storage;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Storage;
using AllInOnePdf.Publishing.Templates;
using AllInOnePdf.UI.Pages;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.Extensions.Logging;

namespace AllInOnePdf.UI.Services;

/// <summary>An unsaved Creator draft, as the gallery lists it.</summary>
/// <param name="Key">The draft store key (<c>publication:&lt;route id&gt;</c>).</param>
/// <param name="RouteId">The builder route id: a saved publication's id or <c>new:&lt;templateId&gt;</c>.</param>
/// <param name="Name">The publication's name as it was in the draft.</param>
/// <param name="Written">When the draft was written (UTC).</param>
/// <param name="TemplateId">The template a never-saved publication started from; null for a saved publication's draft.</param>
public sealed record CreatorDraft(string Key, string RouteId, string Name, DateTimeOffset Written, string? TemplateId)
{
    /// <summary>True when the publication was never saved (the route is <c>new:&lt;templateId&gt;</c>).</summary>
    public bool IsNew => TemplateId is not null;

    /// <summary>The saved publication the draft belongs to; null for a never-saved publication.</summary>
    public string? PublicationId => IsNew ? null : RouteId;

    /// <summary>The template's display name, or null when the draft belongs to a saved publication.</summary>
    public string? TemplateName => TemplateId is null
        ? null
        : (BuiltInTemplates.Find(TemplateId) ?? BuiltInTemplates.Find(BuiltInTemplates.BlankId))?.Name;
}

/// <summary>The gallery's split of the drafts against the saved publications (see <see cref="CreatorDrafts.Match"/>).</summary>
/// <param name="Unsaved">Drafts of publications that were never saved, newest first.</param>
/// <param name="BySavedPublication">The newest draft of each saved publication that is newer than its last save, by publication id.</param>
public sealed record CreatorDraftMatch(IReadOnlyList<CreatorDraft> Unsaved, IReadOnlyDictionary<string, CreatorDraft> BySavedPublication);

/// <summary>
/// Lists the Creator's crash-recovery drafts for the gallery (singleton). Only keys that start with
/// <see cref="KeyPrefix"/> are publication drafts; each one is read with <see cref="CreatorState.ReadDraft"/> to learn
/// the publication's name. Unreadable drafts and drafts older than <see cref="DraftAutosaver.MaxDraftAge"/> are left
/// out (the builder's start-up clean-up deletes the old ones).
/// </summary>
public sealed class CreatorDrafts
{
    /// <summary>Prefix of every publication draft key (see <see cref="CreatorState.DraftKeyFor"/>).</summary>
    public const string KeyPrefix = "publication:";

    private readonly IDraftStore _drafts;
    private readonly ILogger<CreatorDrafts>? _logger;

    /// <summary>Creates the listing on top of the shared draft store.</summary>
    public CreatorDrafts(IDraftStore drafts, ILogger<CreatorDrafts>? logger = null)
    {
        _drafts = drafts;
        _logger = logger;
    }

    /// <summary>The route id inside a publication draft key, or false for other keys (themes, empty ids).</summary>
    public static bool TryGetRouteId(string key, out string routeId)
    {
        ArgumentNullException.ThrowIfNull(key);
        routeId = string.Empty;
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal) || key.Length == KeyPrefix.Length)
        {
            return false;
        }

        routeId = key[KeyPrefix.Length..];
        return true;
    }

    /// <summary>
    /// Describes a publication draft from its key and content, or returns null when the key is not a publication
    /// draft or the content cannot be read.
    /// </summary>
    public static CreatorDraft? Describe(string key, Draft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!TryGetRouteId(key, out var routeId))
        {
            return null;
        }

        Publication publication;
        try
        {
            publication = CreatorState.ReadDraft(draft.Content);
        }
        catch (Exception ex) when (CreatorState.IsUnreadableDraft(ex))
        {
            return null;
        }

        var templateId = routeId.StartsWith(CreatorBuilder.NewPrefix, StringComparison.OrdinalIgnoreCase)
            ? routeId[CreatorBuilder.NewPrefix.Length..]
            : null;
        // ReadDraft normalises the publication, so a blank name already reads "Untitled publication".
        return new CreatorDraft(key, routeId, publication.Name, draft.Written, templateId);
    }

    /// <summary>Every readable publication draft written within the last 30 days, newest first.</summary>
    public async Task<IReadOnlyList<CreatorDraft>> ListAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - DraftAutosaver.MaxDraftAge;
        var result = new List<CreatorDraft>();
        foreach (var info in await _drafts.ListAsync(cancellationToken))
        {
            if (info.Written < cutoff || !TryGetRouteId(info.Key, out _))
            {
                continue;
            }

            var draft = await _drafts.TryReadAsync(info.Key, cancellationToken);
            if (draft is null)
            {
                continue;
            }

            var described = await Task.Run(() => Describe(info.Key, draft), cancellationToken);
            if (described is null)
            {
                _logger?.LogWarning("Ignoring the unreadable publication draft {Key}.", info.Key);
                continue;
            }

            result.Add(described);
        }

        return Newest(result);
    }

    /// <summary>The drafts ordered newest first (ties by name, so the order is stable).</summary>
    public static IReadOnlyList<CreatorDraft> Newest(IEnumerable<CreatorDraft> drafts) =>
        [.. drafts.OrderByDescending(d => d.Written).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Splits drafts for the gallery: never-saved publications get their own cards; a saved publication's draft marks
    /// that publication's card, but only when it was written after the last save (the builder deletes older ones). A
    /// draft of a publication that no longer exists is left out, since there is nothing to continue.
    /// </summary>
    public static CreatorDraftMatch Match(IEnumerable<CreatorDraft> drafts, IEnumerable<PublicationSummary> saved)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(saved);
        var modified = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var summary in saved)
        {
            modified[summary.Id] = summary.Modified;
        }

        var unsaved = new List<CreatorDraft>();
        var bySaved = new Dictionary<string, CreatorDraft>(StringComparer.Ordinal);
        foreach (var draft in Newest(drafts))
        {
            if (draft.IsNew)
            {
                unsaved.Add(draft);
            }
            else if (draft.PublicationId is { } id && modified.TryGetValue(id, out var savedAt) && draft.Written > savedAt)
            {
                bySaved.TryAdd(id, draft);
            }
        }

        return new CreatorDraftMatch(unsaved, bySaved);
    }

    /// <summary>Reads the full publication of a draft (for its thumbnail); null when it is gone or unreadable.</summary>
    public async Task<Publication?> ReadPublicationAsync(CreatorDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var stored = await _drafts.TryReadAsync(draft.Key, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        try
        {
            return await Task.Run(() => CreatorState.ReadDraft(stored.Content), cancellationToken);
        }
        catch (Exception ex) when (CreatorState.IsUnreadableDraft(ex))
        {
            return null;
        }
    }

    /// <summary>Deletes a draft (nothing happens when it is already gone).</summary>
    public Task DiscardAsync(CreatorDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return _drafts.DeleteAsync(draft.Key, cancellationToken);
    }

    /// <summary>Deletes the draft of a saved publication (after the publication itself was deleted).</summary>
    public Task DiscardForPublicationAsync(string publicationId, CancellationToken cancellationToken = default) =>
        _drafts.DeleteAsync(CreatorState.DraftKeyFor(publicationId), cancellationToken);
}
