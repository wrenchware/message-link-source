using System.Text.Json;
using MessageLink.App.Services;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>
/// The clients' own rows of a server's shared state, read with <c>state.get</c> for the clients' own readers (LocalPreferenceChatReader, MergedConversations),
/// so the server judges a conversation as a client would (the push notifier): each chat preference as LocalChatPreferenceStore decodes
/// it, each snooze, and any row a <see cref="Presentation.Services.PrivateState"/> reads (the merges). Nothing is written through them. A row, or a read, that
/// fails counts as none, as a client reads a file it cannot: a notification that comes when it should not is noticed, one that never comes is not.
/// </summary>
internal static class SharedRowReads
{
    public static ILocalChatPreferences Preferences(ISharedState? shared) => new PreferenceRows(shared);

    public static IChatSnoozes Snoozes(ISharedState? shared) => new SnoozeRows(shared);

    public static IPrivateStateStore Rows(ISharedState? shared) => new PlainRows(shared);

    private static async Task<IReadOnlyList<SharedRow>> GetAsync(ISharedState? shared, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        // A server with no shared state has no rows: every conversation reads as its service has it.
        if (shared is null) return [];
        try { return (await shared.GetAsync(new StateGetParams(keys), cancellationToken: cancellationToken).ConfigureAwait(false)).Rows; }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return []; }
    }

    private static JsonElement? ValueOf(IReadOnlyList<SharedRow> rows, string key) =>
        rows.FirstOrDefault(row => row.Key == key) is { Deleted: false, Value: { } value } ? value : null;

    private sealed class PreferenceRows(ISharedState? shared) : ILocalChatPreferences
    {
        public async Task<LocalChatPreferences> GetAsync(string profile, string chatId, CancellationToken cancellationToken = default)
        {
            var keys = LocalChatPreferenceStore.Local.Select(setting => SharedRows.ChatPreferenceKey(profile, chatId, setting.ToString())).ToArray();
            var rows = await SharedRowReads.GetAsync(shared, keys, cancellationToken).ConfigureAwait(false);
            var read = keys.Select(key => Decode(ValueOf(rows, key))).ToArray();
            return LocalChatPreferenceStore.Combine(read[0], read[1], read[2], read[3]);
        }

        // As ReplicaChatPreferences reads a client's own row: the file's JSON, with no moment of writing.
        private static LocalChatPreferenceStore.Read Decode(JsonElement? value)
        {
            if (value is not { } element) return default;
            try { return LocalChatPreferenceStore.Decode(element.Deserialize<LocalChatPreferenceStore.Entry>(), written: null); }
            catch (Exception failure) when (failure is JsonException or InvalidDataException or NotSupportedException) { return default; }
        }

        // Never written: the one write a read makes, lifting an Instagram archive kept here for an arrival (LocalPreferenceChatReader.ArchivedHere), is each
        // client's own when it reads that arrival. The server only judges it lifted.
        public Task SetAsync(string profile, string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SnoozeRows(ISharedState? shared) : IChatSnoozes
    {
        public async Task<DateTimeOffset?> GetAsync(string profile, string chatId, CancellationToken cancellationToken = default)
        {
            var key = SharedRows.SnoozeKey(profile, chatId);
            return ValueOf(await SharedRowReads.GetAsync(shared, [key], cancellationToken).ConfigureAwait(false), key) is { } value &&
                SharedRows.ReadSnooze(JsonSerializer.SerializeToUtf8Bytes(value)) is { } snooze ? snooze.Until : null;
        }

        public Task SetAsync(string profile, string chatId, DateTimeOffset? until, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The shared rows are read here, never written.");

        public Task<IReadOnlyList<ChatSnooze>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The shared rows are read here, never written.");
    }

    private sealed class PlainRows(ISharedState? shared) : IPrivateStateStore
    {
        public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            ValueOf(await SharedRowReads.GetAsync(shared, [key], cancellationToken).ConfigureAwait(false), key) is { } value ? JsonSerializer.SerializeToUtf8Bytes(value) : null;

        public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken = default) => throw new NotSupportedException("The shared rows are read here, never written.");

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException("The shared rows are read here, never written.");

        public Task<IReadOnlyList<string>> KeysAsync(string prefix, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
