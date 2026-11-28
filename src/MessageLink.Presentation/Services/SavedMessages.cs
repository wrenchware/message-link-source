using System.Security.Cryptography;
using System.Text.Json;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

// An explicit local bookmark retains the words and attachment metadata the user chose to keep.
// It does not archive contact lists, receipts or provider credentials. Its photos' originals are kept beside it, apart from
// this store (SavedPhotos, 2026-10-04): queued as it is saved, let go as it is unsaved.
// RemindAt (appended): "Remind me about this message" keeps the message here with a time, when one notification is raised
// (MessageReminders). RemindedAt is when that last happened, so the list can say so; an older entry has neither.
// RaisedAt (appended, shared rows only, never written when empty so the in-process row is the one it was): when a device actually raised the reminder, which is later than
// RemindedAt (the time it was due) when it was raised late. Another device uses it to tell whether it was running for the reminder (MessageReminders).
public sealed record SavedMessage(string Scope, ChatSummary Chat, ChatMessage Message, DateTimeOffset SavedAt,
    DateTimeOffset? RemindAt = null, DateTimeOffset? RemindedAt = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RaisedAt = null);

// A client of a Messages Server keeps these rows in its replica of the server's shared state, where other devices write them too (store is
// IConditionalSharedRows): saving is a create-only write (a message already saved, there or here, counts as done, as on one device), unsaving deletes, a
// reminder's time is written as it is set (last writer wins), and the mark "raised" only while the row is as it was read, so a stale mark never undoes a time
// set meanwhile. The server keeps the photos' originals itself (ServerSavedPhotos), so a client of one that does keeps none.
// The in-process app's and the loopback's state.db shares nothing, so none of this runs there and every call below is the code it was.
public sealed class SavedMessages : IDisposable
{
    private readonly IPrivateStateStore store;
    private readonly TimeProvider? clock;
    private readonly SavedPhotos? photos;
    private readonly IConditionalSharedRows? shared;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SavedMessages(IPrivateStateStore store, TimeProvider? clock = null, SavedPhotos? photos = null)
    {
        this.store = store; this.clock = clock; this.photos = photos;
        shared = store as IConditionalSharedRows;
        if (shared is not null) shared.ServerChanged += OnServerChanged;
    }

    private sealed record Envelope(int Version, SavedMessage Value);
    private static string Prefix(string scope) => "saved:" + NotificationIdentity.Hash(scope) + ":";
    private static string Key(string scope, string chat, string message) => Prefix(scope) +
        NotificationIdentity.Hash(JsonSerializer.Serialize(new[] { chat, message }));
    // Raised after anything here changes, so reminders can look at their times again.
    public event EventHandler? Changed;

    /// <summary>
    /// Whether these are the rows of a server's shared state, which other devices write as well (a client of a server), and not this device's alone. A reminder
    /// then also fires for a time another device raised first (MessageReminders).
    /// </summary>
    public bool IsShared => shared is not null;

    // The server's rows changed a saved message on another device, or this device's write lost to another's: Changed, off the replica's thread, as for a change here.
    private void OnServerChanged(object? sender, IReadOnlyList<string> keys)
    {
        if (!keys.Any(key => key.StartsWith("saved:", StringComparison.Ordinal))) return;
        try { Changed?.Invoke(this, EventArgs.Empty); } catch (Exception) { /* a listener that fails must not stop the replica */ }
    }

    public void Dispose()
    {
        if (shared is not null) shared.ServerChanged -= OnServerChanged;
    }

    // Whether the server keeps this row's photos itself: the server is of a version that shares the saved messages (ServerSavedPhotos keeps them from its rows).
    private bool ServerKeepsPhotos(string key) => shared?.Shares(key) == true;

    public async Task<bool> ContainsAsync(string scope, string chatId, string messageId, CancellationToken token = default)
        => await ReadAsync(Key(scope, chatId, messageId), scope, token) is not null;

    public Task<SavedMessage?> GetAsync(string scope, string chatId, string messageId, CancellationToken token = default)
        => ReadAsync(Key(scope, chatId, messageId), scope, token);

    public async Task SaveAsync(string scope, ChatSummary source, ChatMessage message, CancellationToken token = default)
    {
        Validate(scope, source, message);
        await gate.WaitAsync(token);
        try
        {
            var key = Key(scope, source.Id, message.Id);
            // Saving an already saved item is idempotent: its original snapshot and ordering remain stable.
            if (await ReadAsync(key, scope, token) is not null) return;
            var saved = Snapshot(scope, source, message);
            // Create only on the server's rows: if another device saved it first, its row stands and the replica takes it (ServerChanged).
            if (shared is not null) await WriteIfAsync(key, saved, 0, null, token);
            else await WriteAsync(key, saved, token);
            // Inside the turn, so an unsave that follows at once always finds the photos' work to stop.
            if (!ServerKeepsPhotos(key)) photos?.Keep(saved);
        }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Remind me: the message is kept here (saved now if it was not) with the moment its notification is due. A null
    /// moment ends the reminder and leaves the message saved.
    /// </summary>
    public async Task SetReminderAsync(string scope, ChatSummary source, ChatMessage message, DateTimeOffset? at, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var key = Key(scope, source.Id, message.Id);
            var existing = await ReadAsync(key, scope, token);
            if (existing is null && at is null) return;
            if (existing is null) Validate(scope, source, message);
            var value = (existing ?? Snapshot(scope, source, message)) with
            { RemindAt = at, RemindedAt = at is null ? existing?.RemindedAt : null, RaisedAt = at is null ? existing?.RaisedAt : null };
            await WriteAsync(key, value, token);
            if (existing is null && !ServerKeepsPhotos(key)) photos?.Keep(value);
        }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ends a reminder once its notification has been raised. False when it changed or went meanwhile.</summary>
    public async Task<bool> MarkRemindedAsync(string scope, string chatId, string messageId, DateTimeOffset at, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var key = Key(scope, chatId, messageId);
            // On the server's rows the mark goes only while the row is still as it was read: another device may have set a new time since, or marked this one
            // already, and then its row is taken instead and nothing is lost. A row with no rev from the server yet (made here, not taken yet) is marked against
            // the rev its own write gets, once that is known (WriteAfterPendingAsync), never unconditionally.
            long rev = 0;
            string? epoch = null;
            SavedMessage? current;
            if (shared is null) current = await ReadAsync(key, scope, token);
            else
            {
                var (bytes, held, heldEpoch) = await shared.ReadWithRevAsync(key, token);
                rev = held;
                epoch = heldEpoch;
                current = bytes is null ? null : Parse(bytes, key, scope);
            }
            if (current is not { } existing || existing.RemindAt != at) return false;
            // On the server's rows the moment it was raised goes with the mark.
            var marked = existing with { RemindAt = null, RemindedAt = at, RaisedAt = shared is null ? null : (clock ?? TimeProvider.System).GetUtcNow() };
            if (shared is not null && rev > 0) await WriteIfAsync(key, marked, rev, epoch, token);
            else if (shared is not null) await WriteAfterPendingAsync(key, marked, token);
            else await WriteAsync(key, marked, token);
        }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task RemoveAsync(string scope, string chatId, string messageId, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            await store.DeleteAsync(Key(scope, chatId, messageId), token);
            // Its kept photos go with it, unless another saved message shows one. A copy that could not be removed now is
            // not a failed unsave: it goes with Clear saved photos or the account's sign-out.
            if (photos is not null)
            {
                try { await photos.ReleaseAsync(scope, chatId, messageId); }
                catch (Exception) { }
            }
        }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<SavedMessage>> ListAsync(IEnumerable<string> scopes, CancellationToken token = default)
    {
        var result = new List<SavedMessage>();
        foreach (var scope in scopes.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal))
        foreach (var key in await store.KeysAsync(Prefix(scope), token))
        {
            token.ThrowIfCancellationRequested();
            if (await ReadAsync(key, scope, token) is { } value) result.Add(value);
        }
        return result.OrderByDescending(s => s.SavedAt).ThenBy(s => s.Message.Id, StringComparer.Ordinal).ToArray();
    }

    private static void Validate(string scope, ChatSummary source, ChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrEmpty(source.AccountId) || source.Id != message.ChatId ||
            MergedConversations.IsMerged(source.Id) || string.IsNullOrEmpty(message.Id))
            throw new ArgumentException("A saved message must name its original account and conversation.");
        if (message.IsDeleted || message.IsHidden) throw new InvalidOperationException("This message is no longer available to save.");
    }

    private SavedMessage Snapshot(string scope, ChatSummary source, ChatMessage message)
    {
        var chat = source with { Preview = null, Participants = [], AvatarUrl = null,
            MemberChatIds = [], DefaultMemberChatId = null, MergedIntoChatId = null };
        var snapshot = message with { Attachments = message.Attachments.ToArray(), Reactions = [],
            Delivery = new(null, null, null, null, new Dictionary<string, bool?>()) };
        return new(scope, chat, snapshot, (clock ?? TimeProvider.System).GetUtcNow());
    }

    private async Task WriteAsync(string key, SavedMessage value, CancellationToken token)
    {
        var bytes = Serialize(value);
        try { await store.WriteAsync(key, bytes, token); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // Only on the server's rows (shared): this write goes up only while the server's row is at `ifRev` (0: while it has none).
    private async Task WriteIfAsync(string key, SavedMessage value, long ifRev, string? epoch, CancellationToken token)
    {
        var bytes = Serialize(value);
        try { await shared!.WriteIfAsync(key, bytes, ifRev, epoch, token); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task WriteAfterPendingAsync(string key, SavedMessage value, CancellationToken token)
    {
        var bytes = Serialize(value);
        try { await shared!.WriteAfterPendingAsync(key, bytes, token); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Serialize(SavedMessage value) => JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, value));

    private async Task<SavedMessage?> ReadAsync(string key, string scope, CancellationToken token)
    {
        try
        {
            if (await store.ReadAsync(key, token) is not { } bytes) return null;
            return Parse(bytes, key, scope);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        // One damaged bookmark must not make the others disappear; preserve it for recovery.
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The saved message a stored row holds, judged as the store of its own scope would judge it, or null when it is damaged or its key is not the one its message
    /// has. For the server, which reads the rows of every device's saved messages to keep their photos (ServerSavedPhotos). Leaves <paramref name="bytes"/> as it is.
    /// </summary>
    public static SavedMessage? FromRow(string key, byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<Envelope>(bytes) is { Version: 1, Value: { } value } && value.Chat.Id == value.Message.ChatId &&
                key == Key(value.Scope, value.Chat.Id, value.Message.Id) ? value : null;
        }
        catch (Exception) { return null; }
    }

    // The saved message a row holds, or null when it is not one of this scope's (or is damaged). Clears the bytes it was given.
    private static SavedMessage? Parse(byte[] bytes, string key, string scope)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<Envelope>(bytes);
            return entry is { Version: 1, Value: { } value } && value.Scope == scope &&
                value.Chat.Id == value.Message.ChatId && key == Key(scope, value.Chat.Id, value.Message.Id)
                ? value : null;
        }
        catch (Exception) { return null; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string AttachmentSummary(SavedMessage saved) => string.Join(" · ", saved.Message.Attachments.Select(a =>
        string.IsNullOrWhiteSpace(a.Name) ? a.IsVoiceNote ? "Voice message" : a.Type switch
        { "img" => "Photo", "video" => "Video", "audio" => "Audio", "link" => "Link", _ => "Attachment" } : a.Name));

    public static bool Matches(SavedMessage saved, string query) =>
        string.Join("\n", saved.Chat.Title, saved.Chat.Network, saved.Message.SenderName, saved.Message.Text,
            AttachmentSummary(saved), string.Join("\n", saved.Message.Attachments.Select(a => a.Link)))
        .Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}
