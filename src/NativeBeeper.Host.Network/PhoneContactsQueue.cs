using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

/// <summary>
/// Contacts saved through the phone (protocol 29; owner, 2026-11-05): the phones that save them, the requests PCs hand over, and the phones' answers. Requests are
/// kept in <c>phone-contacts.json</c> beside <c>devices.json</c>, sealed the same way, until a phone answers and for <see cref="PhoneContactLimits.KeptDays"/> days
/// after, so a request made while the phone is away is saved once it connects. A search is never kept: it waits a few seconds for the phone and is let go.
/// </summary>
/// <remarks>
/// <para><b>One phone at a time.</b> Requests go to the phone that turned the setting on last and is still paired; another phone with it on waits its turn. A request
/// nobody has taken goes to whichever phone's turn it is when it is taken.</para>
/// <para><b>Never twice.</b> A request is kept by the PC's id: the same id again is the same request. Once a phone takes it, it is that phone's for good: it is offered
/// again only to that phone (which keeps its own record of the ids it wrote and looks the number up first), and never handed to another, which could not tell it had
/// been written. A phone that turns the setting off or is removed with a request taken and unanswered leaves it <c>unknown</c>, for the person to check.
/// A number for an existing contact names that contact by its key on the phone that found it, so it goes to that phone and that account alone.</para>
/// <para><b>Woken by a push.</b> A phone not showing Messages is woken urgently with its encrypted push (kind <c>contacts</c>), for a request as for a search: a
/// normal-priority push waited until Android let it run, often long after the PC's dialog closed, and the owner would rather the phone buzz and save it now
/// (2026-11-13). The phone shows a notice while it works, so these urgent pushes are never ones that show nothing. An open phone hears the <c>phoneContacts</c>
/// signal instead.</para>
/// Logged by kinds, states and counts only: never a name, number, account or photo.
/// </remarks>
internal sealed class PhoneContactsQueue
{
    public const string FileName = "phone-contacts.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Phone(string DeviceId, string Account, DateTimeOffset At);
    private sealed class Kept
    {
        public required PhoneContactRequest Request { get; init; }
        public required DateTimeOffset At { get; init; }
        public string State { get; set; } = PhoneContactStates.Waiting;
        public string? Name { get; set; }
        /// <summary>The phone and account it belongs to: from its take, or from its submit for a number added to a contact one phone found.</summary>
        public string? Owner { get; set; }
        public DateTimeOffset? Taken { get; set; }
        public DateTimeOffset? Done { get; set; }
    }
    private sealed record Stored(List<Phone> Phones, List<StoredRequest> Requests);
    private sealed record StoredRequest(PhoneContactRequest Request, DateTimeOffset At, string State, string? Name, DateTimeOffset? Taken, DateTimeOffset? Done, string? Owner = null);
    private sealed record Search(PhoneContactRequest Request, string Owner, TaskCompletionSource<PhoneContactDoneParams> Answer, DateTimeOffset At);

    private readonly string path;
    private readonly IStateProtector protector;
    private readonly Func<string, bool> paired;
    private readonly Func<string, string?> nameOf;
    private readonly Func<string, bool> connected;
    private readonly Func<string, bool> inFront;
    private readonly Func<string, bool, Task<bool>> wake;
    private readonly Action announce;
    private readonly TimeProvider time;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly Dictionary<string, Phone> phones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Kept> requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Search> searches = new(StringComparer.Ordinal);

    /// <summary>How long a search waits for the phone before the PC is told it did not answer. The harness shortens it.</summary>
    internal TimeSpan SearchWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>The most requests one take hands a phone; it takes again until none is left.</summary>
    internal const int TakeBatch = 20;

    private PhoneContactsQueue(string folder, IStateProtector protector, Func<string, bool> paired, Func<string, string?> nameOf, Func<string, bool> connected,
        Func<string, bool> inFront, Func<string, bool, Task<bool>> wake, Action announce, TimeProvider time, Action<string> log)
    {
        path = Path.Combine(folder, FileName);
        this.protector = protector; this.paired = paired; this.nameOf = nameOf; this.connected = connected; this.inFront = inFront; this.wake = wake;
        this.announce = announce; this.time = time; this.log = log;
    }

    /// <param name="paired">Whether a device is paired now; nothing goes to or is taken by any other.</param>
    /// <param name="nameOf">The name the server lists a device by.</param>
    /// <param name="connected">Whether a device has a connection open now.</param>
    /// <param name="inFront">Whether a device shows Messages now: it hears the signal, and needs no push.</param>
    /// <param name="wake">Sends the device the <c>contacts</c> push, urgently or not; whether one went.</param>
    /// <param name="announce">Appends the <c>phoneContacts</c> signal to the feed.</param>
    public static async Task<PhoneContactsQueue> LoadAsync(string folder, IStateProtector protector, Func<string, bool> paired, Func<string, string?> nameOf,
        Func<string, bool> connected, Func<string, bool> inFront, Func<string, bool, Task<bool>> wake, Action announce, TimeProvider time, Action<string> log)
    {
        var queue = new PhoneContactsQueue(folder, protector, paired, nameOf, connected, inFront, wake, announce, time, log);
        if (File.Exists(queue.path))
        {
            try
            {
                var opened = await protector.UnprotectAsync(await File.ReadAllBytesAsync(queue.path).ConfigureAwait(false)).ConfigureAwait(false);
                var stored = JsonSerializer.Deserialize<Stored>(opened, Json);
                foreach (var phone in stored?.Phones ?? []) queue.phones[phone.DeviceId] = phone;
                foreach (var kept in stored?.Requests ?? [])
                    if (kept.Request is { Id: { Length: > 0 } id })
                        queue.requests[id] = new Kept { Request = kept.Request, At = kept.At, State = kept.State, Name = kept.Name, Taken = kept.Taken, Done = kept.Done, Owner = kept.Owner };
            }
            catch (Exception failure) when (failure is IOException or JsonException or FormatException or CryptographicException or InvalidDataException)
            {
                log($"phone contacts unreadable type={failure.GetType().Name}");
            }
        }
        bool changed;
        lock (queue.gate) changed = queue.Prune();
        log($"phone contacts loaded phones={queue.phones.Count} waiting={queue.requests.Values.Count(r => r.State == PhoneContactStates.Waiting)} answered={queue.requests.Values.Count(r => r.State != PhoneContactStates.Waiting)}");
        if (changed) await queue.SaveAsync().ConfigureAwait(false);
        return queue;
    }

    // A phone and the account it saves into, as one owner: a request taken by a phone for one account is never written into another.
    private static string OwnerOf(Phone phone) => phone.DeviceId + "\n" + phone.Account;

    // The short tag a contact's key carries (Existing contact), so the key goes back only to the phone and account that found it. Not a secret, and names nobody.
    private static string Tag(string owner) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)), 0, 6).ToLowerInvariant();

    /// <summary>The phone requests go to: the last that turned the setting on and is still paired. Under the gate.</summary>
    private Phone? Target() => phones.Values.Where(phone => paired(phone.DeviceId)).OrderByDescending(phone => phone.At).FirstOrDefault();

    public async Task OfferAsync(string deviceId, PhoneContactsOfferParams offer)
    {
        if (!paired(deviceId)) throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.Forbidden, "This device is no longer paired."));
        bool changed;
        int stranded;
        lock (gate)
        {
            if (offer.On)
            {
                var account = offer.Account!.Trim();
                var was = phones.GetValueOrDefault(deviceId);
                changed = was is null || was.Account != account;
                // Said again on every connection: the same account keeps its place in the order, so a phone that reconnects never takes the turn of one turned on since.
                if (changed) phones[deviceId] = new Phone(deviceId, account, time.GetUtcNow());
                // Another account: what it took for the old one can no longer be answered there.
                stranded = was is not null && was.Account != account ? Strand(OwnerOf(was)) : 0;
            }
            else
            {
                changed = phones.Remove(deviceId, out var was);
                stranded = was is null ? 0 : Strand(OwnerOf(was));
            }
        }
        if (!changed) return;
        log($"phone contacts phone on={(offer.On ? "yes" : "no")} phones={phones.Count}{(stranded > 0 ? $" stranded={stranded}" : "")}");
        await SaveAsync().ConfigureAwait(false);
        announce();
    }

    /// <summary>Whether <paramref name="deviceId"/> has Save contacts from your PC on, for the server page's device line.</summary>
    public bool SavesContacts(string deviceId)
    {
        lock (gate) return phones.ContainsKey(deviceId);
    }

    public Task<PhoneContactsStatus> StatusAsync()
    {
        List<Phone> on;
        lock (gate) on = [.. phones.Values.Where(phone => paired(phone.DeviceId)).OrderByDescending(phone => phone.At)];
        return Task.FromResult(new PhoneContactsStatus([.. on.Select(phone => new ContactsPhone(nameOf(phone.DeviceId) ?? "Phone", phone.Account, connected(phone.DeviceId)))]));
    }

    // Submits and takes one at a time: a request is seen by a take only once it is on disk, a second submit of an id waits for the first one's save, and an owner
    // is given out only once it is on disk.
    private readonly SemaphoreSlim committing = new(1, 1);

    public async Task<PhoneContactState> SubmitAsync(PhoneContactRequest request)
    {
        PhoneContactState state;
        Phone? woken;
        await committing.WaitAsync().ConfigureAwait(false);
        try { (state, woken) = await SubmitCommittedAsync(request).ConfigureAwait(false); }
        finally { committing.Release(); }
        if (woken is null) return state;
        announce();
        var woke = !inFront(woken.DeviceId) && await wake(woken.DeviceId, true).ConfigureAwait(false);
        log($"phone contacts submitted kind={request.Kind} waiting={Waiting()} woke={(woke ? "yes" : inFront(woken.DeviceId) ? "open" : "no")}");
        return state;
    }

    private async Task<(PhoneContactState, Phone?)> SubmitCommittedAsync(PhoneContactRequest request)
    {
        Phone? target;
        lock (gate)
        {
            // The same id again: where that request stands, never a second one.
            if (requests.TryGetValue(request.Id, out var known)) return (StateOf(known), null);
            Prune();
            target = Target();
            if (target is null) return (new PhoneContactState(request.Id, PhoneContactStates.Refused), null);
            string? owner = null;
            if (request.Kind == PhoneContactKinds.AddNumber)
            {
                // The contact a search found, on the phone and account that found it: on any other its key could be someone else.
                var dot = request.Contact!.IndexOf('.');
                if (dot <= 0 || request.Contact[..dot] != Tag(OwnerOf(target))) return (new PhoneContactState(request.Id, PhoneContactStates.NotFound), null);
                owner = OwnerOf(target);
                request = request with { Contact = request.Contact[(dot + 1)..] };
            }
            if (requests.Values.Count(r => r.State == PhoneContactStates.Waiting) >= PhoneContactLimits.Waiting)
            {
                log("phone contacts submitted outcome=full");
                return (new PhoneContactState(request.Id, PhoneContactStates.Full), null);
            }
            requests[request.Id] = new Kept { Request = request, At = time.GetUtcNow(), Owner = owner };
        }
        // Kept on disk before the PC is told it waits, or not at all: a restart must not lose what was promised.
        if (!await SaveAsync().ConfigureAwait(false))
        {
            lock (gate) requests.Remove(request.Id);
            return (new PhoneContactState(request.Id, PhoneContactStates.Failed), null);
        }
        return (new PhoneContactState(request.Id, PhoneContactStates.Waiting), target);
    }

    public async Task<PhoneContactResults> ResultsAsync(IReadOnlyList<string> ids)
    {
        PhoneContactResults results;
        bool pruned;
        lock (gate)
        {
            pruned = Prune();
            results = new([.. ids.Distinct(StringComparer.Ordinal).Select(id => requests.TryGetValue(id, out var kept) ? StateOf(kept) : new PhoneContactState(id, PhoneContactStates.Unknown))]);
        }
        if (pruned) await SaveAsync().ConfigureAwait(false);
        return results;
    }

    public async Task<PhoneContactSearch> SearchAsync(string query, CancellationToken cancellationToken)
    {
        Phone? target;
        lock (gate) target = Target();
        if (target is null) return new PhoneContactSearch(PhoneContactSearchStates.NoPhone);
        var began = time.GetTimestamp();
        var search = new Search(new PhoneContactRequest(Guid.NewGuid().ToString("N"), PhoneContactKinds.Search, Query: query.Trim()), OwnerOf(target),
            new TaskCompletionSource<PhoneContactDoneParams>(TaskCreationOptions.RunContinuationsAsynchronously), time.GetUtcNow());
        searches[search.Request.Id] = search;
        try
        {
            announce();
            if (!inFront(target.DeviceId)) _ = wake(target.DeviceId, true);
            PhoneContactDoneParams answer;
            try { answer = await search.Answer.Task.WaitAsync(SearchWait, time, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                log($"phone contacts search outcome=no_answer ms={(long)time.GetElapsedTime(began).TotalMilliseconds}");
                return new PhoneContactSearch(PhoneContactSearchStates.NoAnswer);
            }
            var answered = answer.Outcome == PhoneContactStates.Saved;
            log($"phone contacts search outcome={(answered ? "answered" : "refused")} matches={answer.Matches?.Count ?? 0} ms={(long)time.GetElapsedTime(began).TotalMilliseconds}");
            // Each key tagged with the phone and account that found it (SubmitAsync checks it).
            var tag = Tag(search.Owner);
            return answered ? new PhoneContactSearch(PhoneContactSearchStates.Answered, [.. (answer.Matches ?? []).Select(match => match with { Contact = tag + "." + match.Contact })])
                : new PhoneContactSearch(PhoneContactSearchStates.Refused);
        }
        finally { searches.TryRemove(search.Request.Id, out _); }
    }

    /// <summary>
    /// What <paramref name="deviceId"/> has to do: its searches first, then the requests it took and has not answered, then requests nobody has taken, oldest first,
    /// at most <see cref="TakeBatch"/>. Each one given is its own from then on. A phone whose turn it is not gets back only what it took.
    /// </summary>
    public async Task<PhoneContactTake> TakeAsync(string deviceId)
    {
        await committing.WaitAsync().ConfigureAwait(false);
        try { return await TakeCommittedAsync(deviceId).ConfigureAwait(false); }
        finally { committing.Release(); }
    }

    private async Task<PhoneContactTake> TakeCommittedAsync(string deviceId)
    {
        var now = time.GetUtcNow();
        List<PhoneContactRequest> taken;
        List<Kept> claimed = [];
        bool pruned;
        lock (gate)
        {
            pruned = Prune();
            // A phone with the setting on always gets back what it took; what nobody took goes only to the phone whose turn it is.
            if (!phones.TryGetValue(deviceId, out var phone) || !paired(deviceId)) return new PhoneContactTake([]);
            var owner = OwnerOf(phone);
            var turn = Target()?.DeviceId == deviceId;
            taken = [.. searches.Values.Where(search => search.Owner == owner).OrderBy(search => search.At).Select(search => search.Request)];
            foreach (var kept in requests.Values.Where(r => r.State == PhoneContactStates.Waiting && (r.Owner == owner || r.Owner is null && turn))
                .OrderBy(r => r.Owner is null ? 1 : 0).ThenBy(r => r.At).Take(TakeBatch))
            {
                if (kept.Owner is null)
                {
                    claimed.Add(kept);
                    kept.Owner = owner;
                    kept.Taken = now;
                }
                taken.Add(kept.Request);
            }
        }
        // Who took it is kept before the phone has it, so a restart never hands it to another. Not kept: claimed by nobody, and only the searches go.
        if ((claimed.Count > 0 || pruned) && !await SaveAsync().ConfigureAwait(false) && claimed.Count > 0)
        {
            lock (gate) foreach (var kept in claimed) { kept.Owner = null; kept.Taken = null; }
            var unclaimed = claimed.Select(kept => kept.Request.Id).ToHashSet(StringComparer.Ordinal);
            return new PhoneContactTake([.. taken.Where(request => !unclaimed.Contains(request.Id))]);
        }
        if (taken.Count > 0) log($"phone contacts taken count={taken.Count}");
        return new PhoneContactTake(taken);
    }

    public async Task DoneAsync(string deviceId, PhoneContactDoneParams done)
    {
        string kind;
        lock (gate)
        {
            if (!phones.TryGetValue(deviceId, out var phone)) return;
            var owner = OwnerOf(phone);
            if (searches.TryGetValue(done.Id, out var search))
            {
                if (search.Owner == owner) search.Answer.TrySetResult(done);
                return;
            }
            // Only the phone and account that took it answer it.
            if (!requests.TryGetValue(done.Id, out var kept) || kept.State != PhoneContactStates.Waiting || kept.Owner != owner) return;
            kept.State = done.Outcome;
            kept.Name = done.Name is { } name ? name[..Math.Min(name.Length, PhoneContactLimits.Name)] : null;
            kept.Done = time.GetUtcNow();
            kind = kept.Request.Kind;
        }
        log($"phone contacts done kind={kind} outcome={done.Outcome}");
        await SaveAsync().ConfigureAwait(false);
        announce();
    }

    /// <summary>A device removed: it saves contacts no longer, what it took and left unanswered is unknown, and the rest goes to the next phone with the setting on.</summary>
    public async Task ForgetAsync(string deviceId)
    {
        int stranded;
        lock (gate)
        {
            if (!phones.Remove(deviceId, out var was)) return;
            stranded = Strand(OwnerOf(was));
        }
        log($"phone contacts phone on=no why=device_removed{(stranded > 0 ? $" stranded={stranded}" : "")}");
        await SaveAsync().ConfigureAwait(false);
        announce();
    }

    // The requests <paramref name="owner"/> took and never answered: it may have written them, so they become unknown rather than another phone's. Under the gate.
    private int Strand(string owner)
    {
        var now = time.GetUtcNow();
        var count = 0;
        foreach (var kept in requests.Values.Where(r => r.State == PhoneContactStates.Waiting && r.Owner == owner))
        {
            kept.State = PhoneContactStates.Unknown;
            kept.Done = now;
            count++;
        }
        return count;
    }

    private int Waiting()
    {
        lock (gate) return requests.Values.Count(r => r.State == PhoneContactStates.Waiting);
    }

    // A request answered or not, past its days, goes; a waiting one's photo with it. Under the gate.
    private bool Prune()
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(PhoneContactLimits.KeptDays);
        var old = requests.Values.Where(r => (r.Done ?? r.At) < cutoff).Select(r => r.Request.Id).ToArray();
        foreach (var id in old) requests.Remove(id);
        if (old.Length > 0) log($"phone contacts expired count={old.Length}");
        return old.Length > 0;
    }

    private static PhoneContactState StateOf(Kept kept) => new(kept.Request.Id, kept.State, kept.Name);

    /// <summary>Writes the queue; false when it could not be kept on disk.</summary>
    private async Task<bool> SaveAsync()
    {
        await writing.WaitAsync().ConfigureAwait(false);
        try
        {
            Stored snapshot;
            lock (gate) snapshot = new([.. phones.Values], [.. requests.Values.Select(r => new StoredRequest(r.State == PhoneContactStates.Waiting ? r.Request : r.Request with { Photo = null },
                r.At, r.State, r.Name, r.Taken, r.Done, r.Owner))]);
            var sealedBytes = await protector.ProtectAsync(JsonSerializer.SerializeToUtf8Bytes(snapshot, Json)).ConfigureAwait(false);
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
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or CryptographicException)
        {
            log($"phone contacts not saved type={failure.GetType().Name}");
            return false;
        }
        finally { writing.Release(); }
    }
}

/// <summary>A connection's view of the queue: the device it belongs to is the phone that offers, takes and answers.</summary>
internal sealed class DevicePhoneContacts(PhoneContactsQueue queue, string deviceId) : IPhoneContacts
{
    public Task OfferAsync(PhoneContactsOfferParams offer, CancellationToken cancellationToken = default) => queue.OfferAsync(deviceId, offer);
    public Task<PhoneContactsStatus> StatusAsync(CancellationToken cancellationToken = default) => queue.StatusAsync();
    public Task<PhoneContactState> SubmitAsync(PhoneContactRequest request, CancellationToken cancellationToken = default) => queue.SubmitAsync(request);
    public Task<PhoneContactResults> ResultsAsync(PhoneContactResultsParams ids, CancellationToken cancellationToken = default) => queue.ResultsAsync(ids.Ids);
    public Task<PhoneContactSearch> SearchAsync(PhoneContactSearchParams search, CancellationToken cancellationToken = default) => queue.SearchAsync(search.Query, cancellationToken);
    public Task<PhoneContactTake> TakeAsync(CancellationToken cancellationToken = default) => queue.TakeAsync(deviceId);
    public Task DoneAsync(PhoneContactDoneParams done, CancellationToken cancellationToken = default) => queue.DoneAsync(deviceId, done);
}
