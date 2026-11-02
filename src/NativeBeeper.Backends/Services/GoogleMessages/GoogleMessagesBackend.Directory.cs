using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend : ISearchBackend, ISearchScopeInfo, IContactBackend
{
    private readonly SemaphoreSlim directoryGate = new(1);
    private readonly SemaphoreSlim contactsGate = new(1);
    private GoogleContact[] contacts = [];
    private DateTimeOffset contactsFetched;
    private Task<GoogleContact[]>? contactLoad;
    private object? contactConnection;
    private async Task<GoogleContact[]> LoadContactsAsync()
    {
        var result = await ReadAsync("ui.contacts",null,CancellationToken.None);
        var (list, tries) = GoogleDirectory.ReadContacts(result, Json);
        GoogleMessagesLog.Write($"contacts returned={list.Length} tries={tries}");
        if (list.Length > 0 && archive.Profile is { } profile) archive.SaveContacts(profile, list);
        return list;
    }
    private int chatLimit = 100;
    private readonly HashSet<string> listedChatIds = [];
    public string SearchScopeDescription
    {
        get
        {
            if (!archive.IsLoaded) return "Messages and conversations loaded in this session";
            var (done, known) = archive.CrawlProgress();
            var saved = $"{archive.MessageCount:N0} messages saved";
            return IsIndexing ? $"{saved} · indexing older history ({done:N0} of {known:N0} conversations)"
                : known > 0 && done >= known ? $"{saved} · full history indexed" : saved;
        }
    }

    // The last whole list, for a resync moments later on the same connection (GoogleDirectory.CanReuseList).
    private sealed record ListHeld(int Connection, int Count, long At, string[] Ids, HashSet<string> Known, string? Next);
    private ListHeld? lastList;
    // The phone's own last whole list, by the count asked for, for the next to say what joined and left it (GoogleDirectory.ListChanges).
    private (int Count, string[] Ids)? lastListed;

    public async Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken);
        var connection = Volatile.Read(ref connectionNumber);
        // A refresh behind the list holds the gate through the phone's read, about 6 s, which every list after the first then waited out, and with it the other
        // services' rows: the launch's second caller, each time (audit, 2026-11-01). The phone is being asked already, and what it says differently goes out as row changes.
        if (olderThan is null && Volatile.Read(ref refreshingList) == 1 && Volatile.Read(ref lastList) is { } during &&
            GoogleDirectory.CanAnswerHeld(false, during.Connection, connection, during.Count, chatLimit, System.Diagnostics.Stopwatch.GetElapsedTime(during.At)))
        {
            var kept = GoogleDirectory.HeldList(during.Ids, chats, during.Known);
            GoogleMessagesLog.Write($"conversations reused visible={kept.Length} during=refresh");
            return new(kept.Select(Summary).ToArray(), during.Next is not null, during.Next, null);
        }
        await directoryGate.WaitAsync(cancellationToken);
        try
        {
            var count = chatLimit;
            if (olderThan is not null)
            {
                if (!int.TryParse(olderThan, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 100 || count > GoogleDirectory.MaximumConversationCount)
                    throw new BackendException(BackendFailure.Unsupported, "Couldn't load more conversations. Refresh and try again.");
                count = Math.Max(count, chatLimit);
            }
            if (lastList is { } held && GoogleDirectory.CanAnswerHeld(olderThan is not null, held.Connection, connection, held.Count, count,
                System.Diagnostics.Stopwatch.GetElapsedTime(held.At)))
            {
                var age = System.Diagnostics.Stopwatch.GetElapsedTime(held.At);
                var kept = GoogleDirectory.HeldList(held.Ids, chats, held.Known);
                // Past ListReuse the phone is asked again behind this answer, and what it says differently goes out as row changes.
                var behind = age >= GoogleDirectory.ListReuse && RefreshListBehind(connection, count);
                GoogleMessagesLog.Write($"conversations reused visible={kept.Length} age_ms={(long)age.TotalMilliseconds}{(behind ? " refresh=behind" : "")}");
                return new(kept.Select(Summary).ToArray(), held.Next is not null, held.Next, null);
            }
            var (list, next) = await ReadListLiveAsync(count, olderThan is null, connection, cancellationToken);
            return new(list.Select(Summary).ToArray(), next is not null, next, null);
        }
        finally { directoryGate.Release(); }
    }

    private int refreshingList;
    // When each conversation was last written or removed on its own, by Stopwatch timestamp: a whole list asked for before then does not undo it
    // (GoogleDirectory.KeepNewer).
    private readonly ConcurrentDictionary<string, long> chatWritten = new(StringComparer.Ordinal);
    private void SetChat(GoogleChat chat) { chats[chat.Id] = chat; chatWritten[chat.Id] = System.Diagnostics.Stopwatch.GetTimestamp(); }
    private bool RemoveChat(string id, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out GoogleChat removed)
    {
        chatWritten[id] = System.Diagnostics.Stopwatch.GetTimestamp();
        return chats.TryRemove(id, out removed);
    }

    // The held list answered (2026-10-19: a phone opening waited 4-6 s on this read every time); the phone is read now, once at a time, and any
    // conversation whose row it gives differently from the held one, or that is new to it, is announced as a change so clients read it again.
    private bool RefreshListBehind(int connection, int count)
    {
        if (Interlocked.Exchange(ref refreshingList, 1) == 1) return false;
        _ = Task.Run(async () =>
        {
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await directoryGate.WaitAsync();
                try
                {
                    if (connection != Volatile.Read(ref connectionNumber)) return;
                    var before = chats.ToDictionary(pair => pair.Key, pair => JsonSerializer.Serialize(pair.Value, Json), StringComparer.Ordinal);
                    var (list, _) = await ReadListLiveAsync(count, firstPage: true, connection, CancellationToken.None);
                    var changed = list.Where(chat => !before.TryGetValue(chat.Id, out var was) || was != JsonSerializer.Serialize(chat, Json)).Select(chat => chat.Id).ToArray();
                    foreach (var id in changed) Publish(new(RealtimeKind.ChatChanged, GoogleModels.Qualify(id)));
                    GoogleMessagesLog.Write($"conversations refreshed behind changed={changed.Length} ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds}");
                }
                finally { directoryGate.Release(); }
            }
            catch (Exception failure)
            {
                GoogleMessagesLog.Write($"conversations refreshed behind outcome=failed reason={GoogleMessagesLog.Reason(failure)}");
            }
            finally { Volatile.Write(ref refreshingList, 0); }
        });
        return true;
    }

    // Both folders read from the phone, held as the last whole list when they are the newest end of it on the connection they began on. Under directoryGate.
    private async Task<(GoogleChat[] List, string? Next)> ReadListLiveAsync(int count, bool firstPage, int connection, CancellationToken cancellationToken)
    {
        {
            var asked = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = await ReadAsync("ui.chatList", new { count }, cancellationToken);
            var inbox = result.Deserialize<GoogleChat[]>(Json) ?? [];
            GoogleChat[] archivedChats = [];
            var archiveRead = false;
            // Both folders expose expanding prefixes. Advance them together so an inbox with fewer rows than the
            // requested count cannot make an archive with more than the old 300-row prefix look exhausted.
            try
            {
                var folder = await ReadAsync("ui.chatList", new { count, folder = "archive" }, cancellationToken);
                archivedChats = [.. (folder.Deserialize<GoogleChat[]>(Json) ?? []).Select(c => c with { Archived = true })];
                archiveRead = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not BackendException { Failure: BackendFailure.AuthenticationRequired })
            { /* The inbox still loads; archived conversations appear on a later refresh. */ }
            var merged = GoogleDirectory.MergeConversations(inbox, archivedChats);
            var phoneListed = merged;
            merged = GoogleDirectory.KeepNewer(merged, asked, chatWritten, chats);
            GoogleChat[] list;
            bool advanced;
            // Locked as well as under directoryGate: a realtime event reads it (MessageMoved).
            lock (listedChatIds)
            {
                list = firstPage ? GoogleDirectory.KeepUnlisted(merged, chats.Values, listedChatIds) : merged;
                advanced = firstPage || merged.Any(c => !listedChatIds.Contains(c.Id));
                foreach (var chat in merged) { chats[chat.Id] = chat; listedChatIds.Add(chat.Id); LiftPlaceholder(chat.Id); }
            }
            GoogleMessagesLog.Write($"conversations inbox={inbox.Length} archived={archivedChats.Length} visible={list.Length} requested={count}{(list.Length > merged.Length ? $" unlisted_kept={list.Length - merged.Length}" : "")}");
            // 2026-10-26: the phone's own list gained a row and lost it again, more than once, and a placeholder thread came back
            // with it; which conversation it was could not be told. Each whole list says, by tag, what came and went since the last.
            if (firstPage && archiveRead)
            {
                if (lastListed is { } before && before.Count == count &&
                    GoogleDirectory.ListChanges(before.Ids, phoneListed, messages.Values) is { Length: > 0 } changes)
                    GoogleMessagesLog.Write($"conversations changed {changes}");
                lastListed = (count, [.. phoneListed.Select(chat => chat.Id)]);
            }
            // A list from an earlier connection says nothing of what this one missed while it was down.
            if (connection == Volatile.Read(ref connectionNumber))
            {
                foreach (var chat in list) headsAtConnect.TryAdd(chat.Id, chat.At);
                StartPriorityCatchUp(connection, list);
            }
            ArchiveChats(list);
            // A fresh list is when a renumbering on the phone can be seen (GoogleMessagesBackend.Renumbering).
            await ObserveIdentitiesAsync(list);
            chatLimit = count; // Reconnect/refresh preserves the expanded range.
            var next = GoogleDirectory.NextConversationCount(count, inbox.Length, archivedChats.Length, advanced);
            var nextCursor = next?.ToString(CultureInfo.InvariantCulture);
            // Only a whole list from the newest end, both folders read, on the connection it began on.
            lastList = firstPage && archiveRead && connection == Volatile.Read(ref connectionNumber)
                ? new(connection, count, System.Diagnostics.Stopwatch.GetTimestamp(), [.. list.Select(c => c.Id)], chats.Keys.ToHashSet(StringComparer.Ordinal), nextCursor)
                : null;
            // The public wrapper has no cursor parameter. Request larger prefixes and merge by identity.
            return (list, nextCursor);
        }
    }

    // Live chat data is newest; archived chats cover conversations outside the loaded list.
    private Dictionary<string, GoogleChat> KnownChats()
    {
        var known = archive.ChatsSnapshot().Where(c => !c.Hidden).ToDictionary(c => c.Id);
        foreach (var chat in chats.Values) if (chat.Hidden) known.Remove(chat.Id); else known[chat.Id] = chat;
        return known;
    }

    // The merge picker can use the protected directory already loaded for search, beyond the inbox prefix.
    // No connection or provider read is made here, and hidden/deleted rows are excluded by KnownChats.
    internal IReadOnlyList<ChatSummary> KnownConversationSummaries => KnownChats().Values.Select(Summary).ToArray();

    public async Task<Page<SearchHit>> SearchAsync(SearchRequest request, string? cursor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.CanSearch) return new([], false, null, null);
        if (request.ChatId is not null && !GoogleModels.Owns(request.ChatId)) return new([], false, null, null);
        if (request.Filters?.Service is { Length: > 0 } service && service != "Google Messages") return new([], false, null, null);
        var offset = 0;
        if (cursor is not null && (!int.TryParse(cursor, out offset) || offset < 0))
            throw new BackendException(BackendFailure.Unsupported, "Couldn't load more results. Search again.");
        if (request.Kind == SearchKind.Messages) await WaitForArchiveAsync(cancellationToken);
        var known = KnownChats().Where(pair => MessageSearch.MatchesChat(GoogleModels.Map(pair.Value), request))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (request.Kind != SearchKind.Messages || !archive.IsLoaded || archive.Profile is not { } profile)
        {
            var hits = GoogleDirectory.Search(known.Values.ToArray(), messages.Values.ToArray(), request);
            var slice = hits.Skip(offset).Take(30).ToArray();
            var hasMore = hits.Count > offset + slice.Length;
            return new(slice, hasMore, hasMore ? (offset + slice.Length).ToString(CultureInfo.InvariantCulture) : null, null);
        }
        // Encrypted archive: word index in memory, bodies read back only for this page.
        var nativeChat = request.ChatId is null ? null : GoogleModels.Native(request.ChatId);
        var found = await Task.Run(() => archive.Search(request.Query, nativeChat, request.Filters, cancellationToken)
            .Where(hit => known.ContainsKey(hit.ChatId)).ToArray(), cancellationToken);
        var page = new List<SearchHit>();
        var index = offset;
        for (; index < found.Length && page.Count < 30; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = found[index].MessageId;
            var message = messages.TryGetValue(id, out var live) ? live : await archive.ReadMessageAsync(profile, id, cancellationToken);
            if (message is null || !known.TryGetValue(message.ChatId, out var chat)) continue;
            var mapped = GoogleModels.Map(message, chat);
            if (MessageSearch.Matches(GoogleModels.Map(chat), mapped, request)) page.Add(new(GoogleModels.Map(chat), mapped));
        }
        var more = index < found.Length;
        return new(page, more, more ? index.ToString(CultureInfo.InvariantCulture) : null, null);
    }

    public async Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (accountId != GoogleModels.Account) return new(false, false, "Choose your Google Messages account.");
        await ConnectAsync(cancellationToken);
        return new(true, true, "Choose a contact or enter a phone number.");
    }

    public async Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken = default)
    {
        if (accountId != GoogleModels.Account || string.IsNullOrWhiteSpace(query) || query.Length > 1000) return [];
        await ConnectAsync(cancellationToken);
        await contactsGate.WaitAsync(cancellationToken);
        try
        {
            if (contactConnection != helper) { contacts=[]; contactsFetched=default; contactLoad=null; contactConnection=helper; }
            if (contactLoad is { IsCompletedSuccessfully: true } finished) { contacts=finished.Result; contactsFetched=DateTimeOffset.UtcNow; contactLoad=null; }
            // Saved contacts answer immediately; the phone list refreshes in the background.
            if (contacts.Length == 0 && archive.Contacts is { Contacts.Length: > 0 } saved) { contacts=saved.Contacts; contactsFetched=saved.WholeList ? saved.SavedAt : default; }
            var age = DateTimeOffset.UtcNow - contactsFetched;
            if (contacts.Length > 0 ? age > TimeSpan.FromHours(6) : age > TimeSpan.FromMinutes(5))
            {
                if (contactLoad is null || contactLoad.IsFaulted || contactLoad.IsCanceled) contactLoad=LoadContactsAsync();
                if (contacts.Length == 0)
                {
                    try
                    {
                        contacts = await contactLoad.WaitAsync(cancellationToken);
                        if (contactConnection != helper) { contacts=[]; throw new BackendException(BackendFailure.Unavailable,"The connection changed. Try the contact lookup again."); }
                        contactsFetched = DateTimeOffset.UtcNow;
                        contactLoad=null;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch when (GoogleDirectory.Phone(query) is not null || KnownChats().Values.Any(c=>c.Participants.Any(p=>p.Name.Contains(query,StringComparison.OrdinalIgnoreCase)))) { /* Known participants and explicit numbers remain usable. */ }
                }
            }
            var known = KnownChats().Values;
            var recent = new Dictionary<string, DateTimeOffset>();
            foreach (var chat in known)
                foreach (var person in chat.Participants.Where(p => !p.Self && GoogleDirectory.Phone(p.Id) is not null))
                {
                    var key = GoogleDirectory.PhoneKey(person.Id);
                    if (!recent.TryGetValue(key, out var at) || chat.At > at) recent[key] = chat.At;
                }
            var participants=known.SelectMany(c=>c.Participants).Where(p=>!p.Self && GoogleDirectory.Phone(p.Id) is not null)
                .Select(p=>new GoogleContact(p.Id,p.Name,GoogleDirectory.Phone(p.Id)!));
            return GoogleDirectory.Find(contacts.Concat(participants), query.Trim(), recent);
        }
        finally { contactsGate.Release(); }
    }

    public async Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken = default)
    {
        var phone = GoogleDirectory.Phone(contact.Phone);
        if (contact.AccountId != GoogleModels.Account || contact.CannotMessage || phone is null)
            throw new SendException(false, "Choose a contact with a valid phone number.");
        await ConnectAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        GoogleChat chat;
        try
        {
            var result = await MediaCallAsync("ui.startDirect", new { phone }, cancellationToken);
            if (result.GetProperty("outcome").GetString() != "accepted" ||
                result.GetProperty("chat").Deserialize<GoogleChat>(Json) is not { Id.Length: > 0 } created)
                throw new SendException(true, "The chat may have been created. Check your conversations before trying again.");
            // The person chose exactly who this goes to, so a conversation that had left the list comes back for it.
            chat = created with { Hidden = false };
            LiftPlaceholder(chat.Id);
        }
        catch (SendException) { throw; }
        catch { throw new SendException(true, "The chat may have been created. Check your conversations before trying again."); }
        SetChat(chat);
        ArchiveChats([chat]);
        Publish(new(RealtimeKind.ChatChanged, GoogleModels.Qualify(chat.Id)));
        return GoogleModels.Map(chat);
    }
}
