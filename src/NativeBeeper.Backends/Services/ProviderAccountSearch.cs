using System.Text.Json;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services;

internal sealed class ProviderAccountSearch(ProviderAccounts accounts, ISearchBackend legacy) : ISearchBackend
{
    private sealed record Position(string Key, string? Cursor);
    public async Task<Page<SearchHit>> SearchAsync(SearchRequest request, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (request.ChatId is { } other && !other.StartsWith("google-direct:", StringComparison.Ordinal)) return new([], false, null, null);
        if (request.ChatId is { } chat) return await accounts.Google(chat).SearchAsync(request, cursor, cancellationToken);
        var sources = new List<(string Key, ISearchBackend Search)> { ("legacy", legacy) };
        sources.AddRange(accounts.Added.Where(a => a.Google is not null && a.Google.Files.SessionSaved).Select(a => (a.Registration.Key, (ISearchBackend)a.Google!)));
        var position = new Position("legacy", null);
        if (cursor is not null)
        {
            try { if (cursor.Length > 8192) throw new FormatException(); position = JsonSerializer.Deserialize<Position>(Convert.FromBase64String(cursor)) ?? throw new FormatException(); }
            catch (Exception e) when (e is FormatException or JsonException) { throw new ArgumentException("Search again to load more results."); }
        }
        var index = sources.FindIndex(s => s.Key == position.Key);
        if (index < 0) throw new ArgumentException("That account was removed. Search again.");
        for (; index < sources.Count; index++)
        {
            var source = sources[index];
            var page = await source.Search.SearchAsync(request, source.Key == position.Key ? position.Cursor : null, cancellationToken);
            var next = page.HasMore && page.OldestCursor is { } more ? new Position(source.Key, more) : index + 1 < sources.Count ? new Position(sources[index + 1].Key, null) : null;
            if (page.Items.Count == 0 && !page.HasMore && next is not null) continue;
            return page with { HasMore = next is not null, OldestCursor = next is null ? null : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(next)) };
        }
        return new([], false, null, null);
    }
}

internal sealed class ProviderAccountKeptHistory(ProviderAccounts accounts, IKeptHistorySearch? legacy, IHelperCellUnprotector cells, Action<string> log) : IKeptHistorySearch
{
    private readonly Dictionary<string, IKeptHistorySearch> readers = [];
    public string Covers => "Messenger";
    private IReadOnlyList<(AccountNamespace Ids, IKeptHistorySearch Reader)> Sources()
    {
        lock (readers)
        {
            var active = accounts.Added.Where(a => a.Messenger is not null).ToArray();
            foreach (var key in readers.Keys.Except(active.Select(a => a.Registration.Key)).ToArray()) readers.Remove(key);
            var result = new List<(AccountNamespace, IKeptHistorySearch)>();
            if (legacy is not null) result.Add((new(), legacy));
            foreach (var account in active)
            {
                if (!readers.TryGetValue(account.Registration.Key, out var reader))
                {
                    reader = new MessengerKeptHistory(() =>
                    {
                        if (!accounts.Added.Any(a => a.Registration.Key == account.Registration.Key) || account.Messenger!.SelfId is not { } self) return null;
                        var privateRoot = account.Slot.HelperEnvironment.GetValueOrDefault(MessengerKeptAccount.PrivateRootVariable);
                        return privateRoot is not null ? MessengerKeptAccount.In(privateRoot, self) :
                            new(self, account.Slot.PathFor("Messenger", "accounts", MessengerKeptAccount.StoreKey(self), "messages.db"), MessengerKeptAccount.StoreKey(self));
                    }, _ => null, log, cells);
                    readers.Add(account.Registration.Key, reader);
                }
                result.Add((account.Slot.Ids, reader));
            }
            return result;
        }
    }
    public void Warm() { foreach (var source in Sources()) source.Reader.Warm(); }
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var hits = new List<SearchHit>();
        foreach (var source in Sources())
        {
            if (request.ChatId is { } chat && AccountNamespace.KeyOf(chat) != source.Ids.Key) continue;
            var links = request.Filters?.LinkedChatIds?.Where(id => AccountNamespace.KeyOf(id) == source.Ids.Key).Select(source.Ids.Native).ToArray();
            var local = request with { ChatId = request.ChatId is { } selected ? source.Ids.Native(selected) : null,
                Filters = request.Filters is { } filters ? filters with { LinkedChatIds = links } : null };
            foreach (var hit in await source.Reader.SearchAsync(local, cancellationToken))
                hits.Add(new(source.Ids.Map(hit.Chat), hit.Message is null ? null : source.Ids.Map(hit.Message)));
        }
        return hits;
    }
}
