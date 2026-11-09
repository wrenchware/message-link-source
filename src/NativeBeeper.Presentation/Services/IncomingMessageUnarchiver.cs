using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// Who returns which archived conversation to the inbox in the client modes (SWITCHBOARD.md stage 6, slices 9 and 5). A server's host takes group
/// conversations (protocol 4), once for all its clients, since a group is never in a merge (<see cref="MergedConversations.NeverMember"/>); from protocol 5,
/// when the merges are a row of its shared state it can read, it also takes the one-to-one conversations outside every merge. Each client takes the rest:
/// its merges always (a merge's archive is the merges row's, which each client changes itself), and what its host does not take through <c>chats.set</c>, as
/// before. Against a host below 4 a client takes everything, as before.
/// </summary>
public static class IncomingUnarchiveSplit
{
    /// <summary>
    /// Whether a client takes <paramref name="chat"/>, which none of its merges holds, given whether its host takes groups and whether it takes one-to-one
    /// conversations outside the merges too.
    /// </summary>
    public static bool ClientTakes(bool hostTakesGroups, bool hostTakesChats, ChatSummary chat) =>
        MergedConversations.NeverMember(chat) ? !hostTakesGroups : !hostTakesChats;

    /// <summary>
    /// Whether the host takes <paramref name="chat"/>: a group always, and, when it takes one-to-one conversations (<paramref name="takesChats"/>), one the
    /// shared merges do not hold (<paramref name="inMerge"/>).
    /// </summary>
    public static bool HostTakes(ChatSummary chat, bool takesChats = false, bool inMerge = true) => MergedConversations.NeverMember(chat) || takesChats && !inMerge;
}

/// <summary>What one event came to.</summary>
/// <param name="Unarchived">This unarchiver returned the conversation to the inbox.</param>
/// <param name="LeftToHost">An arrival in a conversation archived on its service that this unarchiver does not take (<see cref="IncomingMessageUnarchiver.Takes"/>):
/// the host returns it to the inbox for this arrival, a moment later, so the arrival's notification is judged as the inbox will show it.</param>
public readonly record struct UnarchiveOutcome(bool Unarchived, bool LeftToHost);

// Runs serially with notifications, independently of window focus and notification preferences.
// A changed message is not necessarily a new arrival: providers also report edits, receipts and backfill.
public sealed class IncomingMessageUnarchiver(IChatSettingsBackend settings, IMessageSender messages,
    PrivateState state, TimeProvider? clock = null, MergedConversations? merges = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SeenMessages> seen = new(StringComparer.Ordinal);
    private const string Category = "incoming-unarchive";
    private int unnamed;
    private sealed record Attempt(DateTimeOffset Timestamp, HashSet<string> Ids);
    private sealed record SeenMessages(DateTimeOffset Timestamp, HashSet<string> Ids);

    /// <summary>
    /// Which conversations this unarchiver returns to the inbox, asked of the provider's own copy (<c>chats.settings</c>) each arrival. Null takes every one,
    /// as the in-process app and the loopback do. In the client modes it splits them between the host and its clients (<see cref="IncomingUnarchiveSplit"/>).
    /// </summary>
    public Func<ChatSummary, bool>? Takes { get; init; }

    /// <summary>
    /// As <see cref="Takes"/>, for an answer that has to be read: a server's host asks its shared merges whether they hold the conversation (stage 6 slice 5).
    /// Asked after <see cref="Takes"/>, and only when that did not say no.
    /// </summary>
    public Func<ChatSummary, CancellationToken, Task<bool>>? TakesAsync { get; init; }

    /// <summary>Asked before the provider is read: true leaves the conversation without reading it (the host's known one-to-one conversations).</summary>
    public Func<string, bool>? Skips { get; init; }

    /// <summary>
    /// How the provider's unarchive is asked for, given the conversation and the arrival's id (<see cref="ArrivalId"/>, the same for every reader of the event
    /// and for a replay of it). Null asks the settings port; the host passes it through its operation journal under that id, and a client of a server sends
    /// it with that id, so its host's journal makes every ask for one arrival, the clients' and its own, one.
    /// </summary>
    public Func<string, string, CancellationToken, Task<ChatSummary>>? Submit { get; init; }

    /// <summary>Where counts and fixed words go (an arrival left unasked because its event names no usable message).</summary>
    public Action<string>? Log { get; init; }

    public async Task<bool> ObserveAsync(RealtimeEvent change, CancellationToken token) => (await DecideAsync(change, token)).Unarchived;

    public async Task<UnarchiveOutcome> DecideAsync(RealtimeEvent change, CancellationToken token)
    {
        // A client's own link to its host dropped, not a provider's connection (as NotificationObserver reads it). A feed that resumes brings no new
        // Connected, so taking this for one would leave every arrival after it unasked, and unnotified while archived, until the next provider connect.
        if (change.Kind == RealtimeKind.Disconnected && change.Scope is null && change.ClientLink == true) return default;
        if (change.Kind is RealtimeKind.Connected or RealtimeKind.Disconnected or RealtimeKind.AuthenticationRequired or RealtimeKind.ResyncRequired)
        {
            var scope = change.Scope ?? "";
            // Reset this provider/account only. An account scope takes precedence over its provider scope.
            foreach (var key in connections.Keys.Where(key => key.StartsWith(scope, StringComparison.Ordinal)).ToArray()) connections.Remove(key);
            connections[scope] = change.Kind is RealtimeKind.Disconnected or RealtimeKind.AuthenticationRequired
                ? DateTimeOffset.MaxValue : change.ReceivedAt ?? time.GetUtcNow();
            return default;
        }
        if (change.Kind != RealtimeKind.MessageChanged || change.IsNewMessage != true || change.ChatId is not { } chatId || change.Ids is not { Count: > 0 }) return default;
        var since = connections.Where(pair => chatId.StartsWith(pair.Key, StringComparison.Ordinal))
            .OrderByDescending(pair => pair.Key.Length).Select(pair => pair.Value).FirstOrDefault(DateTimeOffset.MaxValue);
        if (since == DateTimeOffset.MaxValue) return default;
        if (Skips?.Invoke(chatId) == true) return default;
        if (merges is not null) await merges.EnsureLoadedAsync();
        var chatSettings = await settings.GetSettingsAsync(chatId, token);
        merges?.Observe([chatSettings.Chat]);
        var group = merges?.Find(merges.DisplayId(chatId));
        if (chatSettings.Chat.IsSpam || chatSettings.Chat.MemberChatIds.Count != 0 || group is null && !chatSettings.CanArchive) return default;
        // A conversation another unarchiver takes is left to it, and said to be, when it is archived on its service: that one returns it for this arrival.
        if (group is null && (Takes?.Invoke(chatSettings.Chat) == false || TakesAsync is not null && !await TakesAsync(chatSettings.Chat, token)))
            return new(false, LeftToHost: chatSettings.Chat.IsArchived);

        ChatMessage? newest = null;
        foreach (var id in change.Ids.Distinct(StringComparer.Ordinal))
        {
            ChatMessage message;
            try { message = await messages.ResolveMessageAsync(chatId, id, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { continue; } // A removed/unavailable message must not hide another arrival in the batch.
            if (message.ChatId != chatId || message.Id != id || message.IsOutgoing || message.IsHidden || message.IsDeleted ||
                message.Type == "system" || message.EditedAt is not null || message.Timestamp < since || message.Timestamp > time.GetUtcNow().AddMinutes(1)) continue;
            if (seen.TryGetValue(chatId, out var previous))
            {
                if (message.Timestamp < previous.Timestamp || (message.Timestamp == previous.Timestamp && previous.Ids.Contains(id))) continue;
                if (message.Timestamp == previous.Timestamp) previous.Ids.Add(id);
                else seen[chatId] = new(message.Timestamp, [id]);
            }
            else seen[chatId] = new(message.Timestamp, [id]);
            if (newest is null || message.Timestamp > newest.Timestamp) newest = message;
        }
        // A manual sending preference also expires when the merged conversation is not open.
        if (newest is not null && group is not null) await merges!.ObserveIncomingMessagesAsync(group.Id, [newest]);
        // Remember arrivals even in the inbox, so a later reaction cannot undo a deliberate archive.
        if (newest is null || !(group?.Archived ?? chatSettings.Chat.IsArchived)) return default;
        var profile = await state.ProfileAsync();
        if (profile is null) return default;
        var attempt = await state.ReadAsync<Attempt>(profile, Category, chatId);
        var ids = new HashSet<string>(seen[chatId].Ids, StringComparer.Ordinal);
        if (attempt is not null)
        {
            if (newest.Timestamp < attempt.Timestamp || (newest.Timestamp == attempt.Timestamp && ids.IsSubsetOf(attempt.Ids))) return default;
            if (newest.Timestamp == attempt.Timestamp) ids.UnionWith(attempt.Ids);
        }
        // An ask under an id (a client of a server, or a server's host) needs the event to name the arrival; one that cannot is not asked for, and its
        // watermark is not written: the host, or the next arrival, returns the conversation.
        string? arrival = null;
        if (group is null && Submit is not null && (arrival = ArrivalId(chatId, change.Ids)) is null)
        {
            var count = Interlocked.Increment(ref unnamed);
            try { Log?.Invoke($"incoming unarchive unnamed n={count}"); } catch (Exception) { /* a logger that fails must not fail the feed */ }
            return default;
        }
        // Persist before submitting. A duplicate or cold-start replay cannot retry an uncertain mutation.
        // One watermark per conversation keeps this bounded by conversations, not message history.
        await state.WriteAsync(profile, Category, chatId, new Attempt(newest.Timestamp, ids));
        token.ThrowIfCancellationRequested();
        if (profile != await state.ProfileAsync()) return default;
        if (group is not null)
        {
            await merges!.SetAsync(group.Id, current => current with { Archived = false });
            return new(true, false);
        }
        var updated = Submit is null ? await settings.SetAsync(chatId, ChatSetting.Archived, false, token)
            : await Submit(chatId, arrival!, token);
        return new(!updated.IsArchived, false);
    }

    // The longest message id that names an arrival; a longer one is not a provider's id.
    private const int MaxArrivalId = 512;

    /// <summary>
    /// The arrival as one digest, from the event exactly as delivered: the conversation and the message ids it names (sorted, so their order does not matter),
    /// or null when it names none usable. Nothing this unarchiver resolved, remembered or wrote goes in, and nothing that differs between the host and its
    /// clients (not the profile, which a server keeps under its own key): every reader of one event, a client or the host, names it the same, so their asks
    /// are one operation in the host's journal. A later event names other messages, and so another arrival. The digest names no conversation.
    /// </summary>
    internal static string? ArrivalId(string chatId, IEnumerable<string> ids)
    {
        var named = ids.Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= MaxArrivalId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return named.Length == 0 ? null : NotificationIdentity.Hash("incoming-unarchive\n" + chatId + "\n" + string.Join("\n", named));
    }
}
