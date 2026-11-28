using System.Text;
using System.Text.Json;
using MessageLink.Core.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>
/// A server's <c>snoozes.*</c>: the <c>snooze:</c> rows of its shared state, which every client of version 3 reads from its
/// replica instead of asking, so a client of version 2 (which still asks) and one of version 3 see the same snoozes. Only a server composes this; the app's
/// in-process host and the loopback keep the snooze files as before.
/// </summary>
/// <remarks>
/// The server's own <c>ChatSnoozes-v1</c> files, which served <c>snoozes.*</c> until now, are taken into rows once (<see cref="SharedStateStore.ImportOnce"/>),
/// verbatim: each file's digest is its row's key and its entry the row's value. The files are left as they are and never written again by a server.
/// </remarks>
internal sealed class SharedChatSnoozes : IChatSnoozes
{
    public const string ImportName = "snoozes";
    private readonly SharedSpaceView<ChatSnooze> rows;

    // Each row by its conversation, in the snooze files' own spelling (SharedRows), so a row reads back as its file did.
    private SharedChatSnoozes(SharedStateStore store) =>
        rows = store.View<ChatSnooze>(SharedCategories.Snooze, snooze => snooze.ChatId, SharedRows.SnoozeValue, value => SharedRows.ReadSnooze(value));

    /// <summary>The snoozes over <paramref name="store"/>, taking in once what <paramref name="files"/> (the server's own snooze store) held before.</summary>
    public static SharedChatSnoozes Over(SharedStateStore store, IChatSnoozeFiles files)
    {
        // Every file that could be read, and how many could not: a file passed over (locked, or unreadable for now) leaves the import unfinished, to be tried
        // again at the next start, rather than recorded done without it.
        store.ImportOnce(ImportName, async cancellationToken =>
        {
            var read = await files.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            return new SharedStateStore.ImportRows([.. read.Snoozes.Select(snooze => (SharedRows.SnoozeKey(snooze.Profile, snooze.ChatId), SharedRows.SnoozeValue(snooze)))],
                read.Unreadable);
        });
        return new SharedChatSnoozes(store);
    }

    // Read and written by the server's own calls, not through state.*: a snooze is kept under the profile its client gave, as its file was, so it is never judged
    // under the space key, never pins it and never waits for it. snoozes.* then answers whatever the server's sign-ins and space are, as it did from the files.
    public async Task<DateTimeOffset?> GetAsync(string profile, string chatId, CancellationToken cancellationToken = default) =>
        (await rows.Under(profile).ReadAsync(chatId, cancellationToken).ConfigureAwait(false))?.Value.Until;

    // The dispatcher's journal already makes one snoozes.set one write. Ending a snooze that was never set commits nothing, as the files did nothing.
    public Task SetAsync(string profile, string chatId, DateTimeOffset? until, CancellationToken cancellationToken = default) =>
        rows.Under(profile).WriteAsync([(chatId, until is { } moment ? new ChatSnooze(profile, chatId, moment) : null)], cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<ChatSnooze>> ListAsync(CancellationToken cancellationToken = default) =>
        // A row that cannot be read is passed over, as an unreadable file is.
        await rows.EveryProfileAsync(cancellationToken).ConfigureAwait(false);
}
