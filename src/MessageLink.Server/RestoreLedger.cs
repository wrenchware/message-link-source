using System.Text.Json;
using MessageLink.Core.Services;

namespace MessageLink.Server;

/// <summary>
/// How often a Linux host may restore its saved accounts (each restore logs Messenger and Instagram in again), kept across starts in
/// <c>&lt;data&gt;/Server/restores.protected</c>, sealed with the host's key. A container that keeps crashing is restarted by Docker within seconds, and
/// without this every start would log in again: that is the loop that gets an account checkpointed.
/// <list type="bullet">
/// <item>At most <see cref="MaxPerWindow"/> restores in any <see cref="Window"/>. A few deliberate restarts an hour fit; a loop does not. (The Windows app
/// restarts a server that crashed at most twice in 10 minutes, but it also stops trying; Docker never stops, so the bound here is per hour.)</item>
/// <item>After a start that did not end cleanly (the run marker was left behind), the restore waits <see cref="FirstBackoff"/>, doubling with each unclean
/// start in a row up to <see cref="LongestBackoff"/>: the 1 to 30 minutes of the resume pacing the server foundation never shortens. A clean stop resets it.</item>
/// </list>
/// The host starts and serves meanwhile; only the restore is held (<c>server restore held reason=paced wait_s=</c>). A restore is counted when it begins,
/// so one that crashes the host still counts. A ledger that cannot be read is treated as the longest back-off, never as permission.
/// </summary>
internal sealed class RestoreLedger(string path, IStateProtector protector, TimeProvider time, Action<string> log)
{
    public const string FileName = "restores.protected";
    public const int MaxPerWindow = 4;
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan LongestBackoff = TimeSpan.FromMinutes(30);
    // Past this many unclean starts in a row the back-off is at its longest anyway; the count stops there.
    private const int MostUnclean = 16;

    private sealed record Stored(int Unclean, List<DateTimeOffset> Restores);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What the start decided: how long the restore waits, and why.</summary>
    public sealed record Decision(TimeSpan Wait, int Unclean, int Recent, bool Saved = true);

    private Stored state = new(0, []);

    /// <summary>The ledger for <paramref name="root"/>.</summary>
    public static RestoreLedger For(string root, IStateProtector protector, TimeProvider time, Action<string> log) =>
        new(Path.Combine(root, "Server", FileName), protector, time, log);

    /// <summary>The back-off after <paramref name="unclean"/> unclean starts in a row.</summary>
    public static TimeSpan Backoff(int unclean) =>
        unclean <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(LongestBackoff.Ticks, FirstBackoff.Ticks << Math.Min(unclean - 1, 20)));

    /// <summary>
    /// Reads the ledger, counts this start (<paramref name="previousExit"/> is the run marker's word: anything but <c>clean</c> counts as unclean), keeps that,
    /// and says how long the restore must wait from now. A restore that logs no Messenger or Instagram account in (<paramref name="counted"/> false) waits only
    /// for the unclean back-off: the hourly cap is there for those logins, and the caller then records nothing. Moving day (2026-10-12): six restarts that
    /// logged nothing of them in had spent the hour, and the moved accounts' first logins waited ten minutes.
    /// </summary>
    public async Task<Decision> StartAsync(string previousExit, bool counted = true)
    {
        var now = time.GetUtcNow();
        var read = await ReadAsync().ConfigureAwait(false);
        var unclean = read is null ? MostUnclean : previousExit == "clean" ? 0 : Math.Min(read.Unclean + 1, MostUnclean);
        state = new Stored(unclean, [.. (read?.Restores ?? []).Where(at => now - at < Window && at <= now + Window)]);
        var saved = await SaveAsync().ConfigureAwait(false);
        var recent = state.Restores.Count;
        var capWait = counted && recent >= MaxPerWindow ? state.Restores.Order().ElementAt(recent - MaxPerWindow) + Window - now : TimeSpan.Zero;
        var wait = Backoff(unclean) > capWait ? Backoff(unclean) : capWait;
        return new Decision(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, unclean, recent, saved);
    }

    /// <summary>
    /// A restore begins now: counted before it runs, so a crash it causes is counted too. False when the count could not be written, in which case the
    /// restore must not run: a count that is not on disk is not a limit, and the next start would find the cap and the back-off gone.
    /// </summary>
    public async Task<bool> RecordAsync()
    {
        var now = time.GetUtcNow();
        state = state with { Restores = [.. state.Restores.Where(at => now - at < Window), now] };
        return await SaveAsync().ConfigureAwait(false);
    }

    private bool loaded;
    private DateTimeOffset? granted;
    private readonly SemaphoreSlim permitting = new(1, 1);

    /// <summary>How long a permit also covers the next login asked for: a move logs Messenger and Instagram in one after the other, and that is one restore, as a start's is.</summary>
    public static readonly TimeSpan ShareWithin = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A restore the owner asked for (an account that has just moved here) wants to begin now: zero, and it is counted, when the hour holds fewer than <see cref="MaxPerWindow"/>
    /// restores; otherwise how long until the oldest leaves the window, and nothing is counted. The unclean-start back-off does not apply to it: that is for a crash loop at start,
    /// and this is one deliberate login. A ledger that cannot be read counts as full. A login asked for within <see cref="ShareWithin"/> of a permit is that permit's (counted once:
    /// on moving day the second of the two waited another eight minutes); the window runs from the permit, not from each login it covers.
    /// </summary>
    public async Task<TimeSpan> PermitAsync()
    {
        // One at a time (2026-10-12, code review): a move's two logins can ask together, and the second, arriving while the first awaited its save, found
        // that restore counted but its permit not yet granted, and was held for the hour.
        await permitting.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            if (granted is { } last && now - last >= TimeSpan.Zero && now - last < ShareWithin) return TimeSpan.Zero;
            if (!loaded && state is { Restores.Count: 0 })
            {
                var read = await ReadAsync().ConfigureAwait(false);
                if (read is null) return Window;
                state = read;
            }
            loaded = true;
            var recent = state.Restores.Where(at => now - at < Window && at <= now + Window).Order().ToList();
            if (recent.Count >= MaxPerWindow) return recent[recent.Count - MaxPerWindow] + Window - now;
            // A deliberate move still needs a durable count: granting the shared permit after a failed save would bypass the cap on the next container start.
            if (!await RecordAsync().ConfigureAwait(false)) return Window;
            granted = now;
            return TimeSpan.Zero;
        }
        finally { permitting.Release(); }
    }

    // Null when the ledger is there and cannot be read back; an empty ledger when there is none yet.
    private async Task<Stored?> ReadAsync()
    {
        if (!File.Exists(path)) return new Stored(0, []);
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(await protector.UnprotectAsync(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false), Json);
            if (stored?.Restores is not null && stored.Unclean >= 0) return stored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        log("server restore ledger unreadable action=paced");
        return null;
    }

    // Written whole and moved over the old one. A ledger that cannot be written is said and reported as not saved; the host serves but restores nothing on its own that run.
    private async Task<bool> SaveAsync()
    {
        try
        {
            var sealedBytes = await protector.ProtectAsync(JsonSerializer.SerializeToUtf8Bytes(state, Json)).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(sealedBytes).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log($"server restore ledger save failed type={ex.GetType().Name}"); return false; }
    }
}
