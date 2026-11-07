using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.Messenger;

internal sealed partial class MessagingRouter
{
    private const string AccountsCursor = "accounts:";
    public async Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default)
    {
        if (Accounts is null) return await LegacyChatsAsync(olderThan, cancellationToken);
        await Accounts.LoadAsync(cancellationToken);
        var sources = Accounts.Added.Where(a => a.Google?.Files.SessionSaved == true || a.Messenger?.SessionSaved == true || a.Instagram?.HasSavedSession == true).ToArray();
        if (sources.Length == 0 && olderThan?.StartsWith(AccountsCursor, StringComparison.Ordinal) != true)
            return await LegacyChatsAsync(olderThan, cancellationToken);
        Dictionary<string, string?>? cursors = null;
        if (olderThan is not null)
        {
            if (!olderThan.StartsWith(AccountsCursor, StringComparison.Ordinal)) return await LegacyChatsAsync(olderThan, cancellationToken);
            try
            {
                if (olderThan.Length > 128 * 1024) throw new FormatException();
                cursors = JsonSerializer.Deserialize<Dictionary<string, string?>>(Convert.FromBase64String(olderThan[AccountsCursor.Length..]));
                if (cursors is null || cursors.Keys.Any(key => key != "legacy" && !AccountNamespace.ValidKey(key))) throw new FormatException();
            }
            catch (Exception failure) when (failure is JsonException or FormatException)
            { throw new ArgumentException("That list position is no longer valid. Refresh chats."); }
        }
        var tasks = new List<Task<(string Key, Page<ChatSummary> Page)>>();
        async Task<(string, Page<ChatSummary>)> Read(string key, Func<string?, CancellationToken, Task<Page<ChatSummary>>> read, string scope, string name)
        {
            var cursor = cursors?.GetValueOrDefault(key);
            var page = await PartialChatPages.ReadAsync(() => read(cursor, cancellationToken), scope, name, cursor ?? "retry:first", cancellationToken);
            return (key, page);
        }
        if (cursors is null || cursors.ContainsKey("legacy"))
            tasks.Add(Read("legacy", (cursor, token) => LegacyChatsAsync(cursor == "retry:first" ? null : cursor, token), "legacy:", "Services"));
        foreach (var account in sources)
        {
            var key = account.Registration.Key;
            if (cursors is not null && !cursors.ContainsKey(key)) continue;
            IChatReader reader = (IChatReader?)account.Google ?? (IChatReader?)account.Messenger ?? account.Instagram!;
            var prefix = account.Google is not null ? "google-direct:" : account.Messenger is not null ? "messenger:" : "instagram:";
            tasks.Add(Read(key, (cursor, token) => reader.GetChatsAsync(cursor == "retry:first" ? null : cursor, token), account.Slot.Ids.Qualify(prefix), account.Registration.Service));
        }
        var pages = await Task.WhenAll(tasks);
        var next = pages.Where(p => p.Page.HasMore && p.Page.OldestCursor is not null).ToDictionary(p => p.Key, p => p.Page.OldestCursor);
        return new(pages.SelectMany(p => p.Page.Items).OrderByDescending(c => c.LastActivity).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray(), next.Count > 0,
            next.Count == 0 ? null : AccountsCursor + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(next)), null,
            Warning: string.Join(" ", pages.Select(p => p.Page.Warning).Where(w => !string.IsNullOrEmpty(w))),
            UnavailableScopes: pages.SelectMany(p => p.Page.UnavailableScopes ?? []).Distinct().ToArray());
    }
}
