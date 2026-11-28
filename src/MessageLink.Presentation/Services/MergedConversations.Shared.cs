using System.Text.Json;

namespace MessageLink.Presentation.Services;

/// <summary>
/// What another device's merges left out of this one's (a client of a Messages Server only): how many of this device's changes no longer held over them,
/// and the merges among those that were never made, by the id they would have had, each with the conversations it would have taken.
/// </summary>
public sealed record MergesDropped(int Changes, IReadOnlyDictionary<string, string[]> Unmade);

// The merges as one row of a Messages Server's shared state (SWITCHBOARD.md stage 6, slice 5), in a client of one only: PrivateState.Shared is null for
// the in-process app and the loopback, and nothing here runs for them. While the host keeps the row (PrivateState.Shares), each change is kept, as the
// transform it was, until the host has taken a write holding it (Confirm). When the host's row comes (another device's merge, or this device's write lost to
// one), the replica hands it here (CatchUpAsync) and the changes still kept are made again over it, as they were made here: one that no longer holds (the
// conversation is in another merge now) throws as it would have, is left out, and is said to be (ChangedElsewhere).
public sealed partial class MergedConversations
{
    // After: the merges as this change left them, which the host holds once it confirms the change's tag.
    private sealed record Change(Func<MergedConversation[], MergedConversation[]> Apply, long Tag, string? Makes, string[] Members, MergedConversation[] After);
    private readonly object changes = new();
    private List<Change> unconfirmed = [];
    // The merges as the host last held them, as far as this device knows (loaded, taken from it, or confirmed): what a server's host judges the incoming
    // unarchive by (HostMayHold).
    private volatile MergedConversation[] hostGroups = [];
    // The replica could not bring this to the host's row (Abandon): read again before the next change, and no change until then.
    private volatile bool stale;
    // Under the gate, while CatchUpAsync makes the kept changes again.
    private bool remaking;

    /// <summary>
    /// Changes of this device's were left out because another device's merges no longer allow them, or because the replica could not bring these merges to
    /// the host's (a client of a server only). Raised off the interface thread.
    /// </summary>
    public event EventHandler<MergesDropped>? ChangedElsewhere;

    private int Unconfirmed { get { lock (changes) return unconfirmed.Count; } }

    /// <summary>
    /// Whether the merges the host last held hold <paramref name="chatId"/> while this device's own changes to them are still on their way (a client of a
    /// server only). Such a conversation this device no longer merges is the client's to return to the inbox (IncomingUnarchiveSplit): its host, which
    /// reads the merges it holds, still leaves it to the merge. False whenever nothing is on its way, and always in process.
    /// </summary>
    public bool HostMayHold(string chatId)
    {
        if (Unconfirmed == 0) return false;
        foreach (var group in hostGroups)
            if (Members(group).Any(member => member.Id == chatId)) return true;
        return false;
    }

    // Under the gate, so a catch-up never runs between a change and its write. Kept before the write: the host can confirm it before the write returns. Kept
    // only while the host keeps the row: before that (a host below protocol 5) nothing is sent, nothing is confirmed, and nothing needs making again.
    private async Task WriteSharedAsync(string owner, MergedConversation[] next, Change change)
    {
        var kept = state.Shares(owner, Category, Row);
        if (kept) lock (changes) unconfirmed.Add(change);
        try { await state.WriteAsync(owner, Category, Row, next, change.Tag); }
        catch
        {
            if (kept) lock (changes) unconfirmed.Remove(change);
            throw;
        }
    }

    // Tags grow across profiles and runs, so a late confirmation of an older write never takes a newer change for done. Called by the replica's worker, as
    // CatchUpAsync is, so the two never overlap.
    private void Confirm(long tag)
    {
        lock (changes)
        {
            if (unconfirmed.LastOrDefault(change => change.Tag == tag) is { } confirmed) hostGroups = confirmed.After;
            unconfirmed.RemoveAll(change => change.Tag <= tag);
        }
    }

    // Under the gate, so no change is half made: the kept changes go (said to be), and the merges are read again before the next one.
    private async Task AbandonAsync(string owner)
    {
        List<Change> dropped;
        await gate.WaitAsync();
        try
        {
            if (owner != profile) return;
            stale = true;
            lock (changes)
            {
                dropped = unconfirmed;
                unconfirmed = [];
            }
        }
        finally { gate.Release(); }
        if (dropped.Count > 0) ChangedElsewhere?.Invoke(this, Dropped(dropped));
    }

    private async Task<SharedRowCaughtUp> CatchUpAsync(string owner, byte[]? current, CancellationToken cancellationToken)
    {
        var dropped = new List<Change>();
        SharedRowCaughtUp outcome;
        await gate.WaitAsync(cancellationToken);
        try
        {
            // A profile no longer loaded: nothing here holds its row.
            if (owner != profile) return new(false, 0);
            var host = current is null ? [] : Usable(JsonSerializer.Deserialize<MergedConversation[]>(current) ?? []);
            List<Change> pending;
            lock (changes) pending = [.. unconfirmed];
            var next = host;
            var kept = new List<Change>();
            remaking = true;
            try
            {
                foreach (var change in pending)
                {
                    // Made again as it was made here: a change the host's merges no longer allow throws as it would have, and is left out.
                    try
                    {
                        next = change.Apply(next);
                        kept.Add(change);
                    }
                    catch (InvalidOperationException) { dropped.Add(change); }
                }
            }
            finally { remaking = false; }
            var through = pending.Count > 0 ? pending[^1].Tag : 0;
            hostGroups = host;
            if (kept.Count > 0 && !Same(next, host))
            {
                // The write below holds every kept change; its tag is the newest's, which the host confirms with what it leaves.
                kept[^1] = kept[^1] with { After = next };
                lock (changes) unconfirmed = kept;
                await state.WriteAsync(owner, Category, Row, next, kept[^1].Tag);
                outcome = new(true, through);
            }
            else
            {
                // Nothing of this device's is left to send (left out, or already what the host holds): the host's row as it is.
                lock (changes) unconfirmed = [];
                next = host;
                outcome = new(false, through);
            }
            groups = next;
            stale = false;
        }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        if (dropped.Count > 0) ChangedElsewhere?.Invoke(this, Dropped(dropped));
        return outcome;
    }

    private static MergesDropped Dropped(List<Change> dropped) =>
        new(dropped.Count, dropped.Where(change => change.Makes is not null).ToDictionary(change => change.Makes!, change => change.Members, StringComparer.Ordinal));

    private static bool Same(MergedConversation[] a, MergedConversation[] b) =>
        JsonSerializer.SerializeToUtf8Bytes(a).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(b));

    private sealed class Follower(MergedConversations owner, string profile) : ISharedRowFollower
    {
        public Task<SharedRowCaughtUp> CatchUpAsync(byte[]? current, CancellationToken cancellationToken) => owner.CatchUpAsync(profile, current, cancellationToken);
        public void Confirmed(long tag) => owner.Confirm(tag);
        public Task AbandonAsync() => owner.AbandonAsync(profile);
    }
}
