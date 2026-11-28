using System.Text.Json;
using MessageLink.Storage;

namespace MessageLink.App.Services.Instagram;

// The two limits that protect the real account and used to live in memory only: the hourly budget of reads nobody asked for, and the stop after Instagram
// asked for an account check (or ended the session). A server that restarts, or an app that is relaunched, started both again from nothing, so a restart
// was a way round them. They are kept in a small sealed file beside the session (counts, times and one fixed code word, never anything of the account),
// loaded when the backend is made. A file that is there and cannot be read starts in the strictest state: the budget spent and the check held, until the
// person signs in again (which is what releases a hold).
internal sealed partial class InstagramBackend
{
    // The code Instagram refused the session with, as kept across runs; null when nothing is held. Changed under guardGate, written by PersistGuardAsync.
    private string? heldCode;
    private readonly object guardGate = new();
    private readonly SemaphoreSlim guardWrites = new(1, 1);
    private int guardUnsavedLogged;

    // What the held state is for the strictest case.
    internal const string StrictHold = "checkpoint_required";

    private void LoadGuard()
    {
        var now = DateTimeOffset.UtcNow;
        GuardRead read;
        try { read = Task.Run(() => store.ReadGuardAsync()).GetAwaiter().GetResult(); }
        catch (Exception) { read = new(null, true); }
        var state = InstagramGuardFile.Read(read, now);
        if (state.Strict)
        {
            ownReads.Exhaust(now);
            InstagramLog.Write("instagram guard state=unreadable held=checkpoint budget=spent");
        }
        else ownReads.Restore(state.Reads, now);
        heldCode = state.Hold;
        // The same word the poll's own stop uses, so everything that reads "the session was refused" sees it.
        if (state.Hold is not null)
        {
            refused = state.Hold;
            startRefused = state.Hold;
            InstagramLog.Write($"instagram guard state=held code={state.Hold}");
        }
        else if (read.Present) InstagramLog.Write($"instagram guard state=loaded reads={state.Reads.Length}");
        ownReads.Taken = counting => { _ = PersistGuardAsync(); };
    }

    // Instagram refused the session, or asked for an account check: nothing reconnects it until the person signs in again.
    internal void Hold(string code)
    {
        if (!InstagramGuardFile.Persistable(code)) return;
        lock (guardGate)
        {
            if (heldCode == code) return;
            heldCode = code;
        }
        _ = PersistGuardAsync();
    }

    // A connection that succeeded (a sign-in, since a held session is not resumed) or a sign-out: the hold is over.
    private void ReleaseHold()
    {
        lock (guardGate)
        {
            if (heldCode is null) return;
            heldCode = null;
        }
        _ = PersistGuardAsync();
    }

    /// <summary>The budget of reads nobody asked for, for the checks.</summary>
    internal InstagramReadBudget OwnReadBudget => ownReads;

    /// <summary>The code the account is held with, for the checks.</summary>
    internal string? HeldCode { get { lock (guardGate) return heldCode; } }

    /// <summary>What a resume is refused with now, for the checks.</summary>
    internal string? HeldRefusal => RefusedWith();

    // Every guard write asked for so far, the ones Hold, ReleaseHold and the budget start and nobody awaits included.
    private Task guardWritten = Task.CompletedTask;

    /// <summary>
    /// Writes the limits' state as it is now; the latest state wins whatever order the writes are asked in. When the file cannot be written the budget stops
    /// handing out reads. Ends once every write asked for before it has ended too: awaited, nothing of this backend's is still to land on the file (an earlier
    /// write of a hold could otherwise land after it, over a release another backend wrote since).
    /// </summary>
    internal Task PersistGuardAsync()
    {
        var write = WriteGuardAsync();
        lock (guardGate) return guardWritten = guardWritten.IsCompleted ? write : Task.WhenAll(guardWritten, write);
    }

    /// <summary>Ends once every guard write asked for so far has ended, starting none: before the file leaves with a moved account, and before a sign-out
    /// says it is done (review, 2026-11-18).</summary>
    internal Task GuardWrittenAsync() { lock (guardGate) return guardWritten; }

    private Task WriteGuardAsync() => Task.Run(async () =>
    {
        await guardWrites.WaitAsync().ConfigureAwait(false);
        try
        {
            string? hold;
            lock (guardGate) hold = heldCode;
            await store.SaveGuardAsync(InstagramGuardFile.Write(hold, ownReads.Counting(DateTimeOffset.UtcNow))).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A limit that cannot be kept is not relied on: no more reads nobody asked for, and a line saying so (once).
            ownReads.Stop();
            if (Interlocked.Exchange(ref guardUnsavedLogged, 1) == 0) InstagramLog.Write("instagram guard state=not_saved budget=stopped");
        }
        finally { guardWrites.Release(); }
    });
}

/// <summary>The limits' file: <c>{"v":1,"hold":"checkpoint_required","reads":[1760000000000, ...]}</c>. Fixed words and times, nothing else.</summary>
internal static class InstagramGuardFile
{
    internal sealed record State(string? Hold, DateTimeOffset[] Reads, bool Strict);

    /// <summary>The file's format (<c>v</c>): the one this build writes and reads. A newer one reads as the strictest state.</summary>
    public const int Version = 1;

    /// <summary>
    /// The refusals that say something about the account itself, and so outlive a restart: an account check, a consent, a suspension, a session Instagram
    /// ended. The others a start can fail with (cookies that were pasted wrong, a session of another account) are about what was just tried, not about the
    /// account, and a restart is the right time to try the saved session again.
    /// </summary>
    public static bool Persistable(string? code) =>
        code is "checkpoint_required" or "challenge_required" or "consent_required" or "account_suspended" or "session_expired";

    public static byte[] Write(string? hold, IReadOnlyList<DateTimeOffset> reads) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["v"] = Version,
            ["hold"] = hold,
            ["reads"] = reads.Select(time => time.ToUnixTimeMilliseconds()).ToArray(),
        });

    /// <summary>Nothing written yet is a clean start; anything written that cannot be understood is the strictest state.</summary>
    public static State Read(GuardRead read, DateTimeOffset now)
    {
        var strict = new State(InstagramBackend.StrictHold, [], true);
        if (!read.Present) return new(null, [], false);
        if (read.Plain is not { } plain) return strict;
        try
        {
            using var document = JsonDocument.Parse(plain);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != Version) return strict;
            string? hold = null;
            if (root.TryGetProperty("hold", out var held) && held.ValueKind != JsonValueKind.Null)
            {
                // Only a code the helper can answer with and only the user can change; anything else is not a hold this build wrote.
                if (held.ValueKind != JsonValueKind.String || held.GetString() is not { } code || !Persistable(code)) return strict;
                hold = code;
            }
            if (!root.TryGetProperty("reads", out var reads) || reads.ValueKind != JsonValueKind.Array || reads.GetArrayLength() > 1000) return strict;
            var times = new List<DateTimeOffset>();
            foreach (var item in reads.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var millis) || millis < 0 || millis > 32503680000000) return strict;
                times.Add(DateTimeOffset.FromUnixTimeMilliseconds(millis));
            }
            return new(hold, [.. times], false);
        }
        catch (Exception) { return strict; }
    }
}
