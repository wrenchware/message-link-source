namespace MessageLink.Presentation.Services;

/// <summary>
/// A protected store some of whose rows other devices write as well: a client's replica of a Messages Server's shared state.
/// The merges and the labels are each one row there, written by compare-and-set: each write goes with the rev its value was made from, and one
/// that lost to another device's is made again by what holds the row in memory (its <see cref="ISharedRowFollower"/>), from the server's row and what this
/// device changed since. The in-process app's and the loopback's <c>state.db</c> are not one, so nothing there changes.
/// </summary>
public interface ISharedRows
{
    /// <summary>
    /// Reads the row <paramref name="key"/> (as <see cref="PrivateState.KeyOf"/> files it) for what will hold it in memory, and makes that its follower in the
    /// same step: every value of the server's the store takes for the row after this read is handed to it, so what it loaded and what the store holds never
    /// part (a write is conditional on the rev the store holds). One a key; a later one replaces it.
    /// </summary>
    Task<byte[]?> FollowAsync(string key, ISharedRowFollower follower, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a write of the row goes to the server now: the server keeps its category (its protocol is that recent). While it does not, the row stays on this
    /// device and nothing it does needs making again, so a follower keeps no changes for that.
    /// </summary>
    bool Shares(string key, string profile);

    /// <summary>
    /// As <see cref="Shares(string, string)"/> for a key of a category that shares every key under its prefix (the drafts), which needs no profile to
    /// be told from the rows beside it; false for any one-row category, which only its key under the profile identifies.
    /// </summary>
    bool Shares(string key) => Shares(key, "");

    /// <summary>
    /// The largest value, in bytes, the server keeps for a shared row; a larger one stays on this device only, and is dropped by the next snapshot of the server's rows.
    /// What writes a row that may be large (the avatars) checks it first. No limit for a store that shares nothing.
    /// </summary>
    int SharedValueLimit => int.MaxValue;

    /// <summary>
    /// A change to a followed row, as <see cref="Core.Services.IPrivateStateStore.WriteAsync"/>, tagged with the newest change of the follower's it holds
    /// (<see cref="SharedRowTags.Next"/>): what the store says back about it names that tag.
    /// </summary>
    Task WriteAsync(string key, byte[] value, long tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keys whose value changed because of the server (another device's write, or a snapshot), raised once the store holds them, off the caller's thread. For what
    /// keeps rows in memory that no follower holds (the composers' drafts): they read the new value from the store. Absent from the in-process and
    /// loopback <c>state.db</c>, so nothing subscribes there.
    /// </summary>
    event EventHandler<IReadOnlyList<string>>? ServerChanged;
}

/// <summary>
/// A replica that can also write a last-writer-wins row on a condition: the saved messages are saved by a create-only write, and a reminder is
/// marked raised only while the row is as it was read. A separate interface so the stores that only follow rows need not take it.
/// </summary>
public interface IConditionalSharedRows : ISharedRows
{
    /// <summary>
    /// The value and the rev the replica holds for a key (0 for no rev from the server), with the epoch of the space that rev was read in (null: none known yet), for a write to
    /// be made against: the rev and its epoch are one condition, and go to <see cref="WriteIfAsync"/> together.
    /// </summary>
    Task<(byte[]? Value, long Rev, string? Epoch)> ReadWithRevAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="value"/> to the replica at once and to the server only while its row is still at <paramref name="ifRev"/> there (0: while there is
    /// none, create only). If the server's row has moved on, the server's is taken instead and this is dropped, and <see cref="ISharedRows.ServerChanged"/> says so.
    /// <paramref name="epoch"/> is the space <paramref name="ifRev"/> was read in (<see cref="ReadWithRevAsync"/>): if the replica has moved to another space since, the write is
    /// kept with the old epoch and the server answers it as lost, never matched against a different row that happens to have the same number. Null (a create-only write, or no
    /// read) is the replica's epoch now.
    /// </summary>
    Task WriteIfAsync(string key, byte[] value, long ifRev, string? epoch, CancellationToken cancellationToken = default);

    /// <summary>
    /// A write made from a row this device wrote and the server has not taken yet (<see cref="ReadWithRevAsync"/> gave rev 0): it goes against the rev that first
    /// write gets, once the server answers it, and never unconditionally. If nothing of the row is on its way it is create only. One that finds the server's row moved on
    /// is dropped and the server's row taken, as for <see cref="WriteIfAsync"/>.
    /// </summary>
    Task WriteAfterPendingAsync(string key, byte[] value, CancellationToken cancellationToken = default);
}

/// <summary>What holds a shared row in memory (<see cref="ISharedRows.FollowAsync"/>): <c>MergedConversations</c>, <c>LabelStore</c>.</summary>
public interface ISharedRowFollower
{
    /// <summary>
    /// The store holds the server's row now, <paramref name="current"/> (null: none): another device's write arrived while nothing of this device's was on its
    /// way, or this device's write lost to one. What the follower holds is brought to it: every change of this device's the server has not taken is made again
    /// over it, and a change that no longer holds there (the conversation is in another merge now) is left out, and said to be. When anything of them is
    /// left, the follower writes the result (<see cref="ISharedRows.WriteAsync"/>) before this returns.
    /// </summary>
    /// <remarks>
    /// Called by the store alone, one at a time, and while it sends nothing: a write of this follower's that was on its way when the server's row came,
    /// made from what it held before, then never reaches the server ahead of the one made here.
    /// </remarks>
    Task<SharedRowCaughtUp> CatchUpAsync(byte[]? current, CancellationToken cancellationToken);

    /// <summary>The server took this follower's write tagged <paramref name="tag"/>, and so every change up to that tag.</summary>
    void Confirmed(long tag);

    /// <summary>
    /// <see cref="CatchUpAsync"/> failed (a row a newer build wrote, a change that threw). What the follower holds is no longer the row's: it lets its kept
    /// changes go (said to be when there were any), refuses changes from now on, and reads the row again before it takes one; every write it made has landed
    /// in the store when this returns. The store then drops this device's waiting writes of the row and holds the server's. Called by the store's worker; must
    /// not throw.
    /// </summary>
    Task AbandonAsync();
}

/// <summary>
/// What <see cref="ISharedRowFollower.CatchUpAsync"/> did. <paramref name="Wrote"/>: it wrote what it holds now. Otherwise it holds the server's row as it is,
/// and every write of its tagged up to <paramref name="Through"/> that has not reached the server is one the store drops.
/// </summary>
public readonly record struct SharedRowCaughtUp(bool Wrote, long Through);

/// <summary>
/// The tags of the changes to followed rows: increasing within a run and, being from the clock, past what a run before it left waiting in the store, so
/// a confirmation that comes for a write of another run never takes a change of this one for done.
/// </summary>
public static class SharedRowTags
{
    private static long last;

    public static long Next()
    {
        while (true)
        {
            var seen = Interlocked.Read(ref last);
            var next = Math.Max(seen + 1, DateTime.UtcNow.Ticks);
            if (Interlocked.CompareExchange(ref last, next, seen) == seen) return next;
        }
    }
}
