using System.Text.Json;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

// Merge the protected Google archive index with the session-only catalogue, newest first.
// Cursor buffers retain an unconsumed provider page so an uneven service mix cannot lose results.
// History another provider keeps on this PC (Messenger's messages.db) joins the catalogue's side of the merge: it is
// answered whole, like the catalogue, so the same offset cursor pages through both.
public sealed class UniversalSearchBackend(ISearchBackend archive, LocalSearchCatalog catalog, IAccessTokenStore tokens, MergedConversations? merges = null,
    IKeptHistorySearch? kept = null) : ISearchBackend, ISearchScopeInfo
{
    private sealed record Cursor(string Request, int Generation, string? ArchiveNext, bool ArchiveDone, SearchHit[] Buffer, int LocalOffset);
    private static readonly string[] SessionServices = ["Messenger", "Instagram", "Voice"];
    // Instagram's and Voice's recent conversations are in the catalogue from their saved copies too, once the app puts them
    // there (SavedSearchSeeding); Messenger's are its helper's own store.
    public string SearchScopeDescription => kept is null
        ? "Searches Google Messages history saved on this PC, the Messenger, Instagram and Voice messages opened since Messages started, and call records."
        : $"Searches Google Messages and {kept.Covers} history saved on this PC, the " +
          $"{Join([.. SessionServices.Where(service => service != kept.Covers)])} messages opened since Messages started" +
          (catalog.HoldsSavedCopies && kept.Covers == "Messenger" ? " or kept on this PC" : "") + ", and call records.";
    private static string Join(string[] names) => names.Length < 2 ? string.Concat(names) : string.Join(", ", names[..^1]) + " and " + names[^1];
    private const int PageSize = 30;

    public async Task<Page<SearchHit>> SearchAsync(SearchRequest request, string? cursor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.CanSearch) return new([], false, null, null);
        if (merges is not null && request.Filters?.Person is { Length: > 0 } person)
        {
            await merges.EnsureLoadedAsync();
            cancellationToken.ThrowIfCancellationRequested();
            request = request with { Filters = request.Filters with { LinkedChatIds = merges.SearchMemberIds(person) } };
        }
        var generation = catalog.SetProfile(await NotificationIdentity.ProfileAsync(tokens, cancellationToken));
        var signature = NotificationIdentity.Hash(JsonSerializer.Serialize(request));
        Cursor state;
        try
        {
            state = cursor is null ? new(signature, generation, null, false, [], 0) :
                JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(cursor)) ?? throw new FormatException();
            if (state.Request != signature || state.Generation != generation || state.LocalOffset < 0) throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        { throw new BackendException(BackendFailure.Unsupported, "Search changed. Search again to refresh results."); }

        var google = request.ChatId is null || request.ChatId.StartsWith("google-direct:", StringComparison.Ordinal);
        google &= request.Filters?.Service is not { Length: > 0 } service || service == "Google Messages";
        if (!google) state = state with { ArchiveDone = true, Buffer = [] };
        var local = (await Task.Run(() => catalog.Search(request, cancellationToken), cancellationToken))
            .Where(hit => !hit.Chat.Id.StartsWith("google-direct:", StringComparison.Ordinal)).ToArray();
        if (kept is not null && request.Kind == SearchKind.Messages)
        {
            // Answers with nothing when it cannot answer (IKeptHistorySearch); one that throws anyway must not take every other
            // service's results with it.
            IReadOnlyList<SearchHit> saved;
            try { saved = await kept.SearchAsync(request, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { saved = []; }
            // A message this session has loaded is the catalogue's to answer for, matching or not: its copy is the one
            // the conversation shows, and the kept copy can only be the same message or an older state of it.
            if (saved.Count > 0)
            {
                var combined = local.Concat(saved.Where(hit => hit.Message is { } message && !catalog.Holds(MessageIdentity.Key(message)))).ToList();
                combined.Sort(Compare);
                local = [.. combined];
            }
        }
        var hits = new List<SearchHit>();
        string? warning = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (hits.Count < PageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Buffer.Length == 0 && !state.ArchiveDone)
            {
                var previous = state.ArchiveNext;
                var page = await archive.SearchAsync(request, previous, cancellationToken);
                warning ??= page.Warning;
                var stalled = page.HasMore && (page.OldestCursor is null || page.OldestCursor == previous || !seen.Add(page.OldestCursor));
                state = state with { ArchiveNext = page.OldestCursor, ArchiveDone = !page.HasMore || stalled, Buffer = page.Items.ToArray() };
                if (stalled) warning = "One service stopped returning more results. Search again to refresh it.";
                if (state.Buffer.Length == 0 && !state.ArchiveDone) continue;
            }
            var a = state.Buffer.FirstOrDefault();
            var b = state.LocalOffset < local.Length ? local[state.LocalOffset] : null;
            if (a is null && b is null) break;
            var takeArchive = a is not null && (b is null || Compare(a, b) <= 0);
            if (takeArchive) { hits.Add(a!); state = state with { Buffer = state.Buffer[1..] }; }
            else { hits.Add(b!); state = state with { LocalOffset = state.LocalOffset + 1 }; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != catalog.SetProfile(await NotificationIdentity.ProfileAsync(tokens, cancellationToken)))
            throw new BackendException(BackendFailure.NotReady, "The connected accounts changed. Search again.");
        var more = state.Buffer.Length > 0 || !state.ArchiveDone || state.LocalOffset < local.Length;
        return new(hits, more, more ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state)) : null, null, Warning: warning);
    }

    private static int Compare(SearchHit a, SearchHit b)
    {
        var time = Nullable.Compare(b.Message?.Timestamp ?? b.Chat.LastActivity, a.Message?.Timestamp ?? a.Chat.LastActivity);
        if (time != 0) return time;
        var chat = string.Compare(a.Chat.Id, b.Chat.Id, StringComparison.Ordinal);
        return chat != 0 ? chat : string.Compare(a.Message?.Id, b.Message?.Id, StringComparison.Ordinal);
    }
}
