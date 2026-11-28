namespace MessageLink.Protocol;

/// <summary>
/// One restore per provider for a host that restores its own accounts (SWITCHBOARD.md 1.7d: "one restore only", so Messenger and Instagram never log
/// in twice). A server started with <c>--restore</c> restores every saved account itself, ahead of any client; the window that owns it still asks for
/// the same restore when it launches (<c>accounts.messenger.restore</c>, <c>accounts.instagram.restore</c>, <c>accounts.voice.restore</c>) and
/// connects Google Messages (<c>backend.discover</c>, <c>accounts.list</c>). Those calls must not start a second login beside the host's, and must not
/// start one in the gap before the host's has begun, so the slots exist from the moment the host is composed, not from the moment its restore starts:
/// <list type="bullet">
/// <item><see cref="RunAsync{T}"/> is the host's own restore of a provider. It runs the restore once and keeps the answer.</item>
/// <item><see cref="Joined{T}"/> is what a client's restore call gets instead of running one: the host's answer, waited for if the host's restore is
/// still under way, and <c>skipped</c> if the host's restore ended without reaching that provider (it was stopped, or nothing was saved).</item>
/// <item><see cref="End"/> is the end of the host's restore, however it ended: whatever it did not reach answers <c>skipped</c>, so a client never
/// waits for a restore that will not come.</item>
/// </list>
/// A host with no restore of its own (the app's in-process host, a server started without <c>--restore</c>) has no slots: a client's restore runs as it
/// always did.
/// </summary>
public sealed class RestoreOnce
{
    /// <summary>The provider words (the slots a restoring host creates).</summary>
    public const string Messenger = "messenger", Instagram = "instagram", Voice = "voice", Google = "google";

    private sealed class Slot
    {
        public readonly TaskCompletionSource<(bool Ran, object? Value)> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Taken;
    }

    private readonly Dictionary<string, Slot> slots;

    /// <param name="providers">The providers the host will restore itself; none for a host that restores nothing.</param>
    public RestoreOnce(IEnumerable<string> providers)
    {
        slots = providers.ToDictionary(provider => provider, provider =>
        {
            var slot = new Slot();
            // A failed restore is the client's answer when it asks; nobody asking is not an unobserved task exception.
            _ = slot.Done.Task.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return slot;
        }, StringComparer.Ordinal);
    }

    /// <summary>A host that restores nothing of its own.</summary>
    public static RestoreOnce None { get; } = new([]);

    /// <summary>All four providers: a host started to restore everything saved.</summary>
    public static RestoreOnce ForAll() => new([Messenger, Instagram, Voice, Google]);

    /// <summary>Whether the host restores <paramref name="provider"/> itself.</summary>
    public bool Owns(string provider) => slots.ContainsKey(provider);

    /// <summary>
    /// The host's own restore of <paramref name="provider"/>: runs <paramref name="restore"/> once and keeps its answer (or its failure) for the clients that
    /// ask. A second call, or a provider the host does not restore itself, is answered from the first or simply run.
    /// </summary>
    public async Task<T> RunAsync<T>(string provider, Func<Task<T>> restore)
    {
        if (!slots.TryGetValue(provider, out var slot)) return await restore().ConfigureAwait(false);
        if (Interlocked.Exchange(ref slot.Taken, 1) == 1)
        {
            var (ran, earlier) = await slot.Done.Task.ConfigureAwait(false);
            return ran ? (T)earlier! : throw new OperationCanceledException();
        }
        try
        {
            var value = await restore().ConfigureAwait(false);
            slot.Done.TrySetResult((true, value));
            return value;
        }
        catch (Exception failure)
        {
            // Both ways the restore can end badly are what a client is told, rather than a login of its own.
            if (failure is OperationCanceledException) slot.Done.TrySetResult((false, null));
            else slot.Done.TrySetException(failure);
            throw;
        }
    }

    /// <summary>
    /// What a client's restore of <paramref name="provider"/> answers: the host's own, waited for while it runs. Null when the host does not restore that
    /// provider itself (the client's call then runs). <paramref name="skipped"/> is the answer when the host's restore ended without reaching it.
    /// </summary>
    public Task<T>? Joined<T>(string provider, T skipped) => slots.TryGetValue(provider, out var slot) ? Await(slot, skipped) : null;

    private static async Task<T> Await<T>(Slot slot, T skipped)
    {
        var (ran, value) = await slot.Done.Task.ConfigureAwait(false);
        return ran ? (T)value! : skipped;
    }

    /// <summary>The host's restore is over, however it ended: every provider it did not reach answers skipped.</summary>
    public void End()
    {
        foreach (var slot in slots.Values) slot.Done.TrySetResult((false, null));
    }
}
