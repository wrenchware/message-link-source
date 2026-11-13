using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.Messenger;

// Whose kept history to search: the signed-in Facebook id, the helper's store for it and the key the store must name as
// its owner. DatabasePath is only ever opened read-only.
internal sealed record MessengerKeptAccount(string SelfId, string DatabasePath, string OwnerKey)
{
    // The Linux server's private root for everything the helper writes (storage.go's privateRootVariable). Unset in the
    // Windows app, whose helper keeps the data root's paths.
    public const string PrivateRootVariable = "MESSAGES_HELPER_PRIVATE_ROOT";

    // Where the helper keeps this account's history: messengerAccountFolder in the helper's storage.go, under the same
    // data root the app uses (NATIVEBEEPER_DATA_ROOT for a development copy), or under the private root when one is set.
    public static MessengerKeptAccount For(string selfId) =>
        Environment.GetEnvironmentVariable(PrivateRootVariable) is { Length: > 0 } privateRoot
            ? In(privateRoot, selfId)
            : new(selfId, AppPaths.Combine("Messenger", "accounts", StoreKey(selfId), "messages.db"), StoreKey(selfId));

    // Under a private root the host names itself (the Linux server's keys folder), rather than the one in the environment.
    public static MessengerKeptAccount In(string privateRoot, string selfId)
    {
        var key = StoreKey(selfId);
        return new(selfId, Path.Combine(privateRoot, "accounts", key, "messages.db"), key);
    }

    // messengerAccountKey in the helper: the first 16 bytes of SHA-256("messenger:" + Facebook id), in hex. It names the
    // account folder and is what the store's own store_owner row says.
    public static string StoreKey(string facebookId) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("messenger:" + facebookId)))[..32];
}

/// <summary>
/// Search across everything the Messenger helper keeps in its per-account messages.db, not only what this session loaded.
/// </summary>
/// <remarks>
/// <para>The helper seals every message, name and attachment field with DPAPI (protection_windows.go; on the Linux server
/// with AES-GCM under its helper key, protection_linux.go), so the store cannot answer a text query itself: LIKE or instr
/// over a sealed cell matches nothing. Each cell is opened here instead, in this process, as the same Windows user (or with
/// the server's key, HelperKeyFileCells), and on Windows that costs about 165 µs a cell on this machine (20,000 in 3.3 s one at a time,
/// 0.8 s four at a time). So the words are indexed in memory once, and each later search reopens only the rows whose
/// sealed bytes changed. Nothing is written anywhere: not to the helper's store, and no plaintext copy on disk. Measured on
/// a synthetic store of 50,000 messages: the first read 2.8-3.2 s (begun when the search dialog opens), a search of an
/// unchanged store under 1 ms, one after the helper wrote a row about 90 ms, and about 36 MB held while the account stays
/// signed in; signing out, or another account, drops it.</para>
/// <para>The store keeps SQLite's rollback journal, where a reader holding its shared lock stops the helper committing
/// and a helper write that waits past its five-second busy timeout loses a message Facebook delivers once. So the store is
/// opened read-only, unpooled, with query_only on, and read in short statements of at most a thousand rows, each finished
/// before the next begins; nothing holds the lock across them or while cells are opened. A store that stays busy for a
/// second is skipped for this search, quietly, and the last index answers. Whether anything changed at all is read from
/// the header's change counter while the shared lock is held, so an unchanged store costs one short statement.</para>
/// <para>Encrypted-device key stores (e2ee.db and the rest) are never opened; only messages.db is. Nothing here reaches
/// Messenger or Facebook, and nothing marks anything read. The diagnostic line carries counts and timings only.</para>
/// </remarks>
internal sealed class MessengerKeptHistory(Func<MessengerKeptAccount?> account, Func<string, ChatSummary?> liveChat, Action<string> log, IHelperCellUnprotector cells)
    : IKeptHistorySearch
{
    public string Covers => "Messenger";

    private const int Batch = 1000;
    // SQLite busy-retries for this long (Microsoft.Data.Sqlite's command timeout, whole seconds) before a read gives up.
    private const int BusySeconds = 1;
    private static readonly int Parallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    private sealed record Row(long Thread, long Sender, long At, long EditedAt, string Text, int Print);
    private sealed record Item(string Message, long Size, double Width, double Height, bool Video, bool Animated,
        string Name, string Mime, string Link, string Description, int Print);
    private sealed record Contact(string Name, int Print);
    private sealed record Conversation(string Name, long[] Participants, bool Group, bool Encrypted, bool Archived, bool Deleted,
        bool Unresolved, long DeletedAt, long Activity, int Print);
    // One account's kept history as last read. Never changed once published; the next refresh builds another.
    private sealed record Snapshot(string Owner, string Self, uint Counter, bool CounterTrusted, IReadOnlyDictionary<string, Row> Rows,
        IReadOnlyDictionary<string, Item> Items, IReadOnlyDictionary<string, Item[]> ItemsByMessage,
        IReadOnlyDictionary<long, Conversation> Threads, IReadOnlyDictionary<long, Contact> People, IReadOnlySet<string> Deleted,
        // Rows whose cells would not open, by table, key and sealed bytes: not tried again until the helper rewrites them.
        IReadOnlySet<string> Unopened);
    private sealed record Refresh(string Outcome, int Opened = 0, int Unreadable = 0);

    private readonly object sync = new();
    private volatile Snapshot? current;
    internal int HeldMessages => current?.Rows.Count ?? 0;
    // The words of every kept message, by its id. Only the refresh in flight changes it; a search checks each candidate
    // against its row again, so one reading it mid-refresh cannot answer with words a message no longer has.
    private volatile TextSearchIndex words = new();
    private Task<Refresh>? running;

    // Starts reading the store in the background, so the first search does not wait for the whole of it.
    public void Warm() => _ = Start();

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Kind != SearchKind.Messages || !request.CanSearch) return [];
        if (request.ChatId is { } only && !MessengerModels.Owns(only)) return [];
        if (request.Filters?.Service is { Length: > 0 } service && !string.Equals(service, "Messenger", StringComparison.OrdinalIgnoreCase)) return [];
        var started = Stopwatch.GetTimestamp();
        // A refresh is shared and is not cancelled with the search that started it: typing a new word cancels each search
        // in turn, and a first read cancelled every time would never finish.
        var refresh = await Start().WaitAsync(cancellationToken);
        var waited = Stopwatch.GetElapsedTime(started);
        var snapshot = current;
        // The refresh joined may have begun for the account signed in before (a warm-up just ahead of a sign-out): only the
        // account signed in now answers, and the next refresh lets go of the other's index.
        if (snapshot is not null && (account() is not { } now || snapshot.Owner != now.OwnerKey || snapshot.Self != now.SelfId)) snapshot = null;
        var hits = snapshot is null ? [] : await Task.Run(() => Query(snapshot, request, cancellationToken), cancellationToken);
        log($"kept search outcome={refresh.Outcome} messages={snapshot?.Rows.Count ?? 0} threads={snapshot?.Threads.Count ?? 0} " +
            $"opened={refresh.Opened} unreadable={refresh.Unreadable} refresh_ms={(long)waited.TotalMilliseconds} hits={hits.Count} " +
            $"ms={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
        return hits;
    }

    private Task<Refresh> Start()
    {
        lock (sync)
        {
            if (running is { IsCompleted: false }) return running;
            return running = Task.Run(RefreshNow);
        }
    }

    private void Drop()
    {
        current = null;
        words = new();
    }

    private Refresh RefreshNow()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var who = account();
            if (who is null) { Drop(); return new("no_account"); }
            if (current is { } held && (held.Owner != who.OwnerKey || held.Self != who.SelfId)) Drop();
            if (!File.Exists(who.DatabasePath)) { Drop(); return new("missing"); }
            var result = Read(who, current);
            if (result.Outcome != "unchanged")
                log($"kept history read outcome={result.Outcome} messages={current?.Rows.Count ?? 0} opened={result.Opened} " +
                    $"unreadable={result.Unreadable} ms={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
            return result;
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6 or 8)
        {
            // Busy or locked past the timeout, or a store the helper has yet to recover after a crash (a hot journal a
            // read-only reader may not roll back). The last index answers until a later search reads it again.
            return new("busy");
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Drop();
            return new("unreadable");
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = BusySeconds,
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=1";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private Refresh Read(MessengerKeptAccount who, Snapshot? previous)
    {
        using var connection = Open(who.DatabasePath);
        uint counter; bool trusted;
        // The owner and the change counter under one shared lock, which is all an unchanged store costs.
        using (var peek = connection.BeginTransaction(deferred: true))
        {
            using var command = connection.CreateCommand();
            command.Transaction = peek;
            command.CommandText = "SELECT owner FROM store_owner WHERE slot=1";
            if (command.ExecuteScalar() as string != who.OwnerKey) { Drop(); return new("other_account"); }
            (counter, trusted) = ChangeCounter(who.DatabasePath);
            peek.Rollback();
        }
        if (previous is not null && trusted && previous.CounterTrusted && previous.Counter == counter) return new("unchanged");

        var opener = new List<(byte[] Cell, Action<string?> Take)>();
        var unopened = new HashSet<string>(StringComparer.Ordinal);
        bool Skip(string tag)
        {
            if (previous?.Unopened.Contains(tag) != true) return false;
            unopened.Add(tag);
            return true;
        }
        // Messages: kept as they were when their sealed bytes are the same, reopened otherwise. DPAPI seals the same text
        // differently every time, so a rewritten row is reopened even when its words did not change.
        var rows = new Dictionary<string, Row>(previous?.Rows.Count ?? 0, StringComparer.Ordinal);
        var fresh = new List<(string Id, long Thread, long Sender, long At, long Edited, int Print, string?[] Plain)>();
        foreach (var reader in Batches(connection, "SELECT rowid, id, thread, sender, at, edited_at, body, card_text FROM encrypted_message"))
        {
            var id = reader.GetString(1);
            long thread = reader.GetInt64(2), sender = reader.GetInt64(3), at = reader.GetInt64(4), edited = reader.GetInt64(5);
            var body = Cell(reader, 6); var card = Cell(reader, 7);
            var print = Print(thread, sender, at, edited, body, card);
            if (previous?.Rows.GetValueOrDefault(id) is { } kept && kept.Print == print) { rows[id] = kept; continue; }
            if (Skip($"m:{id}:{print}")) continue;
            var plain = new string?[2];
            fresh.Add((id, thread, sender, at, edited, print, plain));
            opener.Add((body, text => plain[0] = text));
            opener.Add((card, text => plain[1] = text));
        }
        var items = new Dictionary<string, Item>(previous?.Items.Count ?? 0, StringComparer.Ordinal);
        var freshItems = new List<(string Key, string Message, long Size, double Width, double Height, bool Video, bool Animated, int Print, string?[] Plain)>();
        void Attachments(string table, string prefix, bool described)
        {
            var columns = described ? "name, mime, link, description" : "name, mime, link";
            foreach (var reader in Batches(connection, $"SELECT rowid, id, message, size, width, height, video, animated, {columns} FROM {table}"))
            {
                var key = prefix + reader.GetString(1);
                var message = reader.GetString(2);
                long size = reader.GetInt64(3);
                double width = reader.GetDouble(4), height = reader.GetDouble(5);
                bool video = reader.GetInt64(6) != 0, animated = reader.GetInt64(7) != 0;
                var cells = Enumerable.Range(8, described ? 4 : 3).Select(ordinal => Cell(reader, ordinal)).ToArray();
                var print = Print(message.GetHashCode(StringComparison.Ordinal), size, (long)width, (long)height, cells);
                print = HashCode.Combine(print, video, animated);
                if (previous?.Items.GetValueOrDefault(key) is { } kept && kept.Print == print) { items[key] = kept; continue; }
                if (Skip($"{key}:{print}")) continue;
                var plain = new string?[4];
                plain[3] = described ? null : "";
                freshItems.Add((key, message, size, width, height, video, animated, print, plain));
                for (var index = 0; index < cells.Length; index++) { var slot = index; opener.Add((cells[index], text => plain[slot] = text)); }
            }
        }
        Attachments("message_attachment", "a:", true);
        Attachments("encrypted_media", "e:", false);
        var threads = new Dictionary<long, Conversation>();
        var freshThreads = new List<(long Key, int Print, string?[] Plain)>();
        foreach (var reader in Batches(connection, "SELECT thread, thread, identity FROM thread_identity", "thread"))
        {
            var key = reader.GetInt64(1); var cell = Cell(reader, 2);
            var print = Print(key, 0, 0, 0, cell);
            if (previous?.Threads.GetValueOrDefault(key) is { } kept && kept.Print == print) { threads[key] = kept; continue; }
            if (Skip($"t:{key}:{print}")) continue;
            var plain = new string?[1];
            freshThreads.Add((key, print, plain));
            opener.Add((cell, text => plain[0] = text));
        }
        var people = new Dictionary<long, Contact>();
        var freshPeople = new List<(long Id, int Print, string?[] Plain)>();
        foreach (var reader in Batches(connection, "SELECT id, id, name FROM thread_contact", "id"))
        {
            var id = reader.GetInt64(1); var cell = Cell(reader, 2);
            var print = Print(id, 0, 0, 0, cell);
            if (previous?.People.GetValueOrDefault(id) is { } kept && kept.Print == print) { people[id] = kept; continue; }
            if (Skip($"c:{id}:{print}")) continue;
            var plain = new string?[1];
            freshPeople.Add((id, print, plain));
            opener.Add((cell, text => plain[0] = text));
        }
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reader in Batches(connection, "SELECT rowid, id FROM deleted_message")) deleted.Add(reader.GetString(1));
        connection.Close();

        // Every cell that changed, opened with no lock held on the store.
        var opened = new string?[opener.Count];
        Parallel.For(0, opener.Count, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, index => opened[index] = Unseal(opener[index].Cell));
        for (var index = 0; index < opener.Count; index++) opener[index].Take(opened[index]);
        var changedDocuments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in fresh)
        {
            if (row.Plain[0] is not { } body || row.Plain[1] is not { } card) { unopened.Add($"m:{row.Id}:{row.Print}"); continue; }
            // A Meta AI reply can carry its words only on its card; the helper shows the card's text then.
            rows[row.Id] = new(row.Thread, row.Sender, row.At, row.Edited, string.IsNullOrWhiteSpace(body) ? card : body, row.Print);
            changedDocuments.Add(row.Id);
        }
        foreach (var item in freshItems)
        {
            if (item.Plain.Any(value => value is null)) { unopened.Add($"{item.Key}:{item.Print}"); continue; }
            items[item.Key] = new(item.Message, item.Size, item.Width, item.Height, item.Video, item.Animated,
                item.Plain[0]!, item.Plain[1]!, item.Plain[2]!, item.Plain[3]!, item.Print);
            changedDocuments.Add(item.Message);
        }
        foreach (var thread in freshThreads)
        {
            if (thread.Plain[0] is not { } json || ReadThread(json, thread.Print) is not { } parsed) { unopened.Add($"t:{thread.Key}:{thread.Print}"); continue; }
            threads[thread.Key] = parsed;
        }
        foreach (var person in freshPeople)
        {
            if (person.Plain[0] is not { } name) { unopened.Add($"c:{person.Id}:{person.Print}"); continue; }
            people[person.Id] = new(name, person.Print);
        }
        // A message whose attachment went away changed its words too.
        if (previous is not null)
        {
            foreach (var pair in previous.Items) if (!items.ContainsKey(pair.Key)) changedDocuments.Add(pair.Value.Message);
            foreach (var id in previous.Rows.Keys) if (!rows.ContainsKey(id)) words.Remove(id);
        }
        var byMessage = items.Values.GroupBy(item => item.Message, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var snapshot = new Snapshot(who.OwnerKey, who.SelfId, counter, trusted, rows, items, byMessage, threads, people, deleted, unopened);
        foreach (var id in changedDocuments)
            if (rows.TryGetValue(id, out var row)) words.Set(id, MessageSearch.SearchableText(Build(snapshot, id, row)));
        current = snapshot;
        return new("ok", opener.Count, unopened.Count);
    }

    // Reads a table in short statements: each is stepped to its end and closed before the next begins, so the store's
    // shared lock is never held between them. Rows are keyed on rowid (or the named integer key), which only orders one
    // pass; a VACUUM can renumber rowids, which is why nothing is remembered by them.
    private static IEnumerable<SqliteDataReader> Batches(SqliteConnection connection, string select, string key = "rowid")
    {
        long after = long.MinValue;
        while (true)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"{select} WHERE {key} > $after ORDER BY {key} LIMIT {Batch}";
            command.Parameters.AddWithValue("$after", after);
            var count = 0;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    count++;
                    after = reader.GetInt64(0);
                    yield return reader;
                }
            }
            if (count < Batch) yield break;
        }
    }

    private static byte[] Cell(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : reader.GetFieldValue<byte[]>(ordinal);

    private static int Print(long a, long b, long c, long d, params byte[][] cells)
    {
        var hash = new HashCode();
        hash.Add(a); hash.Add(b); hash.Add(c); hash.Add(d);
        foreach (var cell in cells) { hash.Add(cell.Length); hash.AddBytes(cell); }
        return hash.ToHashCode();
    }

    // The header's file change counter, which every committed write in a rollback-journal store moves. A store in WAL mode
    // does not move it, so there it is not trusted and every search reads the store again.
    private static (uint Counter, bool Trusted) ChangeCounter(string path)
    {
        Span<byte> header = stackalloc byte[100];
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None))
            if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) throw new InvalidDataException("Short store header.");
        return (BinaryPrimitives.ReadUInt32BigEndian(header[24..]), header[18] == 1 && header[19] == 1);
    }

    // The helper's archivedFolder (updatechat.go): its own older word, and Messenger's two.
    internal static bool ArchivedFolder(string folder) => folder.Equals("archive", StringComparison.OrdinalIgnoreCase)
        || folder.Equals("archived", StringComparison.OrdinalIgnoreCase) || folder.Equals("e2ee_cutover_archived", StringComparison.OrdinalIgnoreCase);

    // savedThread in the helper's identity.go, marshalled with Go's field names.
    private static Conversation? ReadThread(string json, int print)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            long Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
            bool Flag(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            var participants = root.TryGetProperty("Participants", out var members) && members.ValueKind == JsonValueKind.Object
                ? members.EnumerateObject().Select(member => long.TryParse(member.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                    .Where(id => id != 0).Order().ToArray()
                : [];
            var kind = Number("Kind");
            // table.ThreadType: IsOneToOne, and the two kinds carried over WhatsApp's encryption.
            var oneToOne = kind is 1 or 7 or 10 or 13 or 15 or 201;
            return new(Text("Name"), participants, !oneToOne, kind is 15 or 16, ArchivedFolder(Text("Folder")),
                Flag("Deleted"), Flag("Unresolved"), Number("DeletedAt"), Number("Activity"), print);
        }
        catch (JsonException) { return null; }
    }

    private IReadOnlyList<SearchHit> Query(Snapshot snapshot, SearchRequest request, CancellationToken token)
    {
        var chats = new Dictionary<long, ChatSummary?>();
        IEnumerable<string> candidates = string.IsNullOrWhiteSpace(request.Query) ? snapshot.Rows.Keys : words.Query(request.Query, token);
        long? onlyThread = request.ChatId is { } chatId && long.TryParse(MessengerModels.Native(chatId), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : request.ChatId is null ? null : long.MinValue;
        var hits = new List<SearchHit>();
        foreach (var id in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (!snapshot.Rows.TryGetValue(id, out var row) || snapshot.Deleted.Contains(id)) continue;
            if (onlyThread is { } wanted && row.Thread != wanted) continue;
            // A conversation deleted here, or the part of one from before it was cleared, is gone from its history too.
            var thread = snapshot.Threads.GetValueOrDefault(row.Thread);
            if (thread is not null && (thread.Deleted || row.At <= thread.DeletedAt)) continue;
            if (!chats.TryGetValue(row.Thread, out var chat)) chats[row.Thread] = chat = Summary(snapshot, row.Thread, thread);
            if (chat is null) continue;
            var message = Build(snapshot, id, row);
            if (string.IsNullOrWhiteSpace(message.Text) && message.Attachments.Count == 0) continue;
            if (MessageSearch.Matches(chat, message, request)) hits.Add(new(chat, message));
        }
        return [.. hits.OrderByDescending(hit => hit.Message!.Timestamp).ThenBy(hit => hit.Chat.Id, StringComparer.Ordinal)
            .ThenBy(hit => hit.Message!.Id, StringComparer.Ordinal)];
    }

    // The conversation as the list shows it when the list has shown it this session; otherwise as the helper would, from
    // its kept identity, and not at all where the helper would hide it.
    private ChatSummary? Summary(Snapshot snapshot, long key, Conversation? thread)
    {
        var id = MessengerModels.Qualify(key.ToString(CultureInfo.InvariantCulture));
        if (liveChat(id) is { } live) return live;
        if (thread is null) return null;
        string NameOf(long person) => snapshot.People.GetValueOrDefault(person)?.Name is { Length: > 0 } name ? name : "Participant";
        var self = long.TryParse(snapshot.Self, NumberStyles.Integer, CultureInfo.InvariantCulture, out var me) ? me : 0;
        var others = thread.Participants.Where(person => person != self).ToArray();
        if (thread.Unresolved && thread.Name.Length == 0 && !others.Any(person => snapshot.People.GetValueOrDefault(person)?.Name is { Length: > 0 })) return null;
        var title = thread.Name.Length > 0 ? thread.Name : others.Length > 0 ? string.Join(", ", others.Select(NameOf)) : "Conversation";
        var participants = thread.Participants.Select(person => new ChatParticipant(MessengerModels.Person(person.ToString(CultureInfo.InvariantCulture)),
            NameOf(person), null, person == self)).ToArray();
        return new(id, MessengerModels.Account, title, "Messenger", thread.Group ? "group" : "single",
            thread.Activity > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(thread.Activity) : null, 0, false, false, thread.Archived, false,
            null, null, [], null, null, ChatCapabilities.Unknown, participants, participants.Length, IsEncrypted: thread.Encrypted ? true : null);
    }

    // The same shape MessengerModels.Map gives a message the helper serves, less what a search result does not draw:
    // reactions, receipts and the directions to its pictures, which stay with the helper.
    private static ChatMessage Build(Snapshot snapshot, string id, Row row)
    {
        var sender = row.Sender.ToString(CultureInfo.InvariantCulture);
        var outgoing = sender == snapshot.Self;
        var at = DateTimeOffset.FromUnixTimeMilliseconds(row.At);
        var attachments = (snapshot.ItemsByMessage.GetValueOrDefault(id) ?? []).Select(item => new MessageAttachment(
            // A card of words (link, no media type) is a link card, as MessengerModels.Map draws it.
            Empty(item.Name), item.Video ? "video" : item.Mime.StartsWith("image/", StringComparison.Ordinal) ||
                item.Mime.Length == 0 && !string.IsNullOrEmpty(item.Link) ? "img" : "file",
            Empty(item.Mime), item.Size == 0 ? null : item.Size, false, item.Animated || item.Mime == "image/gif", false,
            Width: item.Width == 0 ? null : item.Width, Height: item.Height == 0 ? null : item.Height,
            Link: Empty(item.Link), Description: string.IsNullOrWhiteSpace(item.Description) ? null : item.Description)).ToArray();
        return new(MessengerModels.Qualify(id), MessengerModels.Qualify(row.Thread.ToString(CultureInfo.InvariantCulture)),
            MessengerModels.Person(sender), Empty(snapshot.People.GetValueOrDefault(row.Sender)?.Name), at, at.UtcTicks.ToString("D19"),
            row.Text, "text", outgoing, false, false, row.EditedAt == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(row.EditedAt), null,
            attachments, [], new(outgoing ? "SUCCESS" : null, null, null, null, new Dictionary<string, bool?>()));
    }

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // One sealed cell, opened: "" for an empty cell, which the helper leaves unsealed; null for anything that does not
    // open here in the helper's format (this Windows user's DPAPI, or the Linux server's helper key).
    internal string? Unseal(byte[] cell)
    {
        if (cell.Length == 0) return "";
        var plain = cells.Open(cell);
        if (plain is null) return null;
        try { return Encoding.UTF8.GetString(plain); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }
}
