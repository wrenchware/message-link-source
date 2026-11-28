using System.Text.Json;

namespace MessageLink.Presentation.Services;

/// <summary>
/// Labels belong to this PC and to the signed-in profile, like pins and merges, and never reach a provider. With no
/// profile there is nowhere to keep them, so they are off: no labels, and nothing offered.
/// </summary>
/// <remarks>
/// Everything is read once per profile and then changed in memory on the interface thread, so the list can rebuild
/// straight away; each change is written behind it, off that thread, in order, so the last change is the one kept.
/// A saved book that cannot be read leaves labels off, and loadFailed hears the error's type name, never its message.
/// <para>Each change is made as a transform over the book (<see cref="LabelBook"/>, which holds the rules a name keeps), so a client of a Messages Server, whose book is one row shared with the
/// server's other clients, can make its changes again over another device's book when its write lost to one
/// (<see cref="CatchUpAsync"/>). The in-process app and the loopback make and write each change exactly as before.</para>
/// </remarks>
public sealed class LabelStore(PrivateState state, Action<string>? loadFailed = null)
{
    // The protected store's category. A backup that carries labels names this one.
    public const string Category = "labels";
    private const string BookId = "book";

    // What a change came to: nothing to do, done, or no longer possible in this book (its label is gone, or its name is taken now).
    private enum Done { Unchanged, Changed, Failed }

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private string? profile;
    private LabelBook book = new();
    // Each conversation's labels as label records, in the order the labels were made. A list rebuild asks for every
    // row's, so they are resolved once per change rather than once per row per rebuild.
    private Dictionary<string, Label[]>? resolved;
    private Task saving = Task.CompletedTask;
    private string? failedLoad;

    /// <summary>Moves on every change and every load, so a reader can tell whether what it last saw is current.</summary>
    public int Version { get; private set; }
    public bool IsAvailable => profile is not null;
    public IReadOnlyList<Label> Labels { get { lock (sync) return [.. book.Labels]; } }
    public int Count { get { lock (sync) return book.Labels.Count; } }
    /// <summary>How many conversation-and-label pairs there are, for the log's counts.</summary>
    public int AssignmentCount { get { lock (sync) return book.AssignmentCount; } }
    /// <summary>The write behind the most recent change. Awaiting it says whether that change reached the store.</summary>
    public Task LastSave { get { lock (sync) return saving; } }

    public Task EnsureLoadedAsync() => LoadAsync(reload: false);

    /// <summary>
    /// Reads the book again for the profile already loaded. A restore adds a saved book beside the one held here, and
    /// the next change would otherwise write this copy over it; the restored labels would then be gone for good.
    /// </summary>
    public Task ReloadAsync() => LoadAsync(reload: true);

    private async Task LoadAsync(bool reload)
    {
        await gate.WaitAsync();
        try
        {
            var next = await state.ProfileAsync();
            // A client of a server whose replica could not bring it to the server's book (AbandonAsync) reads it again.
            if (next == profile && !reload && !stale) return;
            // A client of a server holding changes the server has not taken yet holds more than its replica says while they are on their way; the replica
            // brings the server's book to it as it comes (CatchUpAsync), never by a read here.
            if (next == profile && !stale && Unconfirmed > 0) return;
            // A write still queued lands first, so a profile read again never comes back without its latest change.
            try { await LastSave; } catch (Exception) { /* That change's own caller heard about it. */ }
            LabelBook? saved = null;
            Exception? unreadable = null;
            if (next is not null)
            {
                // A client of a server reads the book and becomes its follower in one step, so a server book the replica takes after this read is handed
                // here (CatchUpAsync) and none taken before it is missed: what this holds is always what the replica's rev says.
                try
                {
                    saved = state.Shared is null ? await state.ReadAsync<LabelBook>(next, Category, BookId)
                        : await state.FollowAsync<LabelBook>(next, Category, BookId, new Follower(this, next));
                }
                catch (Exception error) { unreadable = error; }
            }
            lock (sync)
            {
                // Never keep another profile's labels, whatever the new one holds.
                book = saved ?? new();
                if (next != profile) unconfirmed.Clear();
                // A book that is there but cannot be read leaves labels off rather than empty: the first change to an
                // empty book would be written over it and every label in it lost. The list still loads, and the next
                // load tries the book again.
                profile = unreadable is null ? next : null;
                stale = false;
                resolved = null;
                Version++;
            }
            // Said once per run of failures rather than on every list refresh that retries.
            var failure = unreadable?.GetType().Name;
            if (failure is not null && failure != failedLoad) loadFailed?.Invoke(failure);
            failedLoad = failure;
        }
        finally { gate.Release(); }
    }

    public Label? Find(string id) { lock (sync) return book.Find(id); }

    /// <summary>Whether another label already has this name (<see cref="LabelBook.Taken"/>).</summary>
    public bool NameTaken(string name, string? exceptId = null) { lock (sync) return book.Taken(name, exceptId); }

    /// <summary>The colour a new label starts with (<see cref="LabelBook.NextColor"/>).</summary>
    public string NextColor() { lock (sync) return book.NextColor(); }

    /// <summary>A conversation's labels, in the order the labels were made. Empty when it has none.</summary>
    public IReadOnlyList<Label> For(string chatId)
    {
        lock (sync)
        {
            resolved ??= book.ChatIds.ToDictionary(id => id, id => book.Labels.Where(label => book.Has(id, label.Id)).ToArray(), StringComparer.Ordinal);
            return resolved.TryGetValue(chatId, out var found) ? found : [];
        }
    }

    public bool Has(string chatId, string labelId) { lock (sync) return book.Has(chatId, labelId); }

    /// <summary>How many conversations wear this label: what deleting it takes the label off.</summary>
    public int CountUsing(string labelId) { lock (sync) return book.CountUsing(labelId); }

    /// <summary>What deleting a label asks first, saying how many conversations would lose it.</summary>
    public static string DeleteQuestion(string name, int conversations) => $"Delete “{name}”? " + conversations switch
    {
        0 => "No conversations have this label.",
        1 => "1 conversation will lose this label.",
        _ => $"{conversations} conversations will lose this label.",
    };

    /// <summary>Makes a label, and puts it on a conversation straight away when it was made from one.</summary>
    public Label Create(string name, string color, string? applyTo = null)
    {
        var clean = LabelBook.CleanName(name) ?? throw new ArgumentException("A label needs a name.", nameof(name));
        if (NameTaken(clean)) throw new ArgumentException("A label with this name already exists.", nameof(name));
        var label = new Label(Guid.NewGuid().ToString("N"), clean, LabelColors.Get(color).Key);
        // Made again over another device's book, a label whose name another device took meanwhile is left out: two labels that read the same cannot be
        // told apart.
        Done Make(LabelBook within)
        {
            if (within.Find(label.Id) is not null) return Done.Unchanged;
            if (within.Taken(clean)) return Done.Failed;
            within.Add(label);
            if (applyTo is not null) within.Assign(applyTo, label.Id);
            return Done.Changed;
        }
        lock (sync)
        {
            RequireProfile();
            if (stale) throw new InvalidOperationException("Labels are being read again. Try again in a moment.");
            if (Apply(Make) == Done.Failed) throw new ArgumentException("A label with this name already exists.", nameof(name));
        }
        return label;
    }

    public bool Rename(string id, string name)
    {
        if (LabelBook.CleanName(name) is not { } clean || NameTaken(clean, id)) return false;
        return Replace(id, label => label with { Name = clean }, clean);
    }

    public bool Recolor(string id, string color) => Replace(id, label => label with { Color = LabelColors.Get(color).Key });

    // A label gone from the book, or (a rename) a name another label has now, leaves the change out.
    private bool Replace(string id, Func<Label, Label> change, string? named = null)
    {
        Done Edit(LabelBook within)
        {
            if (within.Find(id) is null || named is not null && within.Taken(named, id)) return Done.Failed;
            return within.Replace(id, change) == true ? Done.Changed : Done.Unchanged;
        }
        lock (sync) return Apply(Edit) == Done.Changed;
    }

    /// <summary>Deletes a label and takes it off every conversation. Returns how many conversations lost it.</summary>
    public int Delete(string id)
    {
        var lost = 0;
        Done Remove(LabelBook within)
        {
            if (within.Delete(id) is not { } count) return Done.Unchanged;
            lost = count;
            return Done.Changed;
        }
        lock (sync) return Apply(Remove) == Done.Changed ? lost : 0;
    }

    /// <summary>Puts a label on a conversation or takes it off. False when nothing changed.</summary>
    public bool Set(string chatId, string labelId, bool on)
    {
        // Putting on a label another device deleted meanwhile is left out; taking it off is then already done.
        Done Put(LabelBook within)
        {
            if (within.Find(labelId) is null) return on ? Done.Failed : Done.Unchanged;
            return (on ? within.Assign(chatId, labelId) : within.Unassign(chatId, labelId)) ? Done.Changed : Done.Unchanged;
        }
        lock (sync) return Apply(Put) == Done.Changed;
    }

    /// <summary>
    /// A merged row wears every label any of its conversations wore. The members' own are taken off, because a member
    /// is not listed while it is merged and its labels would otherwise be kept out of sight.
    /// </summary>
    public bool Merge(string mergedId, IEnumerable<string> memberIds)
    {
        var members = memberIds.Where(id => id != mergedId).Distinct(StringComparer.Ordinal).ToArray();
        Done Move(LabelBook within)
        {
            var changed = false;
            foreach (var member in members)
            {
                if (within.Forget(member) is not { } ids) continue;
                foreach (var id in ids) within.Assign(mergedId, id);
                changed = true;
            }
            return changed ? Done.Changed : Done.Unchanged;
        }
        lock (sync) return Apply(Move) == Done.Changed;
    }

    /// <summary>Unmerging hands the merged row's labels to each conversation that is listed on its own again.</summary>
    public bool Unmerge(string mergedId, IEnumerable<string> memberIds)
    {
        var members = memberIds.Where(id => id != mergedId).Distinct(StringComparer.Ordinal).ToArray();
        Done Hand(LabelBook within)
        {
            if (within.Forget(mergedId) is not { } ids) return Done.Unchanged;
            foreach (var member in members)
                foreach (var id in ids) within.Assign(member, id);
            return Done.Changed;
        }
        lock (sync) return Apply(Hand) == Done.Changed;
    }

    /// <summary>A deleted conversation's labels go with it.</summary>
    public bool Forget(string chatId)
    {
        lock (sync) return Apply(within => within.Forget(chatId) is not null ? Done.Changed : Done.Unchanged) == Done.Changed;
    }

    /// <summary>Every conversation id that wears a label.</summary>
    public IReadOnlyList<string> AssignedChatIds { get { lock (sync) return [.. book.ChatIds]; } }

    /// <summary>
    /// Takes the labels off every conversation the test matches, in one change and one write. The labels themselves stay.
    /// Returns how many conversations lost theirs.
    /// </summary>
    public int ForgetWhere(Func<string, bool> matches)
    {
        var lost = 0;
        Done Clear(LabelBook within)
        {
            var gone = within.ChatIds.Where(matches).ToArray();
            foreach (var chatId in gone) within.Forget(chatId);
            lost = gone.Length;
            return lost > 0 ? Done.Changed : Done.Unchanged;
        }
        lock (sync) return Apply(Clear) == Done.Changed ? lost : 0;
    }

    private void RequireProfile()
    {
        if (profile is null) throw new InvalidOperationException("Sign in to a service before adding labels.");
    }

    // Under the lock: the change made on the book held, and, when it changed it, committed.
    private Done Apply(Func<LabelBook, Done> change)
    {
        // A client of a server whose replica could not bring the book to the server's changes nothing until it has read it again.
        if (stale) return Done.Unchanged;
        var done = change(book);
        if (done == Done.Changed) Commit(change);
        return done;
    }

    // The change is visible at once; the write follows on the thread pool, after any write still under way, so writes
    // land in the order the changes were made. Under the lock.
    private void Commit(Func<LabelBook, Done> change)
    {
        resolved = null;
        Version++;
        if (profile is not { } owner) return;
        if (state.Shared is null)
        {
            saving = WriteAfterAsync(saving, owner, book.Copy());
            return;
        }
        // A client of a server: the change is kept, to be made again if the write loses to another device's (CatchUpAsync), until the server has taken it;
        // only while the server keeps the book (protocol 5 and later). Before that it stays here, nothing is confirmed, and nothing needs making again.
        var kept = new LabelChange(change, SharedRowTags.Next());
        if (state.Shares(owner, Category, BookId)) unconfirmed.Add(kept);
        saving = WriteAfterAsync(saving, owner, book.Copy(), kept.Tag);
    }

    private async Task WriteAfterAsync(Task previous, string owner, LabelBook snapshot, long? tag = null)
    {
        try { await previous.ConfigureAwait(false); } catch (Exception) { /* That change's own caller heard about it. */ }
        if (tag is { } shared) await Task.Run(() => state.WriteAsync(owner, Category, BookId, snapshot, shared)).ConfigureAwait(false);
        else await Task.Run(() => state.WriteAsync(owner, Category, BookId, snapshot)).ConfigureAwait(false);
    }

    // ---- A client of a Messages Server ----

    private sealed record LabelChange(Func<LabelBook, Done> Apply, long Tag);
    // Under the lock: the changes the server has not taken yet, in the order they were made.
    private readonly List<LabelChange> unconfirmed = [];
    // Under the lock: the replica could not bring the book to the server's (AbandonAsync); read again before the next change, and no change until then.
    private bool stale;

    private int Unconfirmed { get { lock (sync) return unconfirmed.Count; } }

    /// <summary>
    /// Changes of this device's were left out because another device's book no longer allows them (its label was deleted, or its name taken). Raised off the
    /// interface thread with how many; a client of a server only.
    /// </summary>
    public event EventHandler<int>? ChangedElsewhere;

    // The server's book, handed over by the replica: each change still kept is made again over it, as it was made here. A change that no longer holds there is
    // left out and said to be. The writes of changes made before this are awaited first (they were made over the old book, and land ahead of this one), and
    // so is this one's, so the replica sends nothing in between.
    // Under the load's gate, as MergedConversations' catch-up is under its own: a server book the replica takes while a load is still on its way (registered,
    // read, not yet put in memory) is handed here only once that load has put its older book in place, so the load never puts it back over this one.
    // Before, a first load dropped the newer book (no profile yet) and a reload wrote the older one over it, while the replica held the newer rev: the next
    // change was then made from the older book and written over another device's.
    private async Task<SharedRowCaughtUp> CatchUpAsync(string owner, byte[]? current)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await CatchUpLoadedAsync(owner, current).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<SharedRowCaughtUp> CatchUpLoadedAsync(string owner, byte[]? current)
    {
        try { await LastSave.ConfigureAwait(false); } catch (Exception) { /* That change's own caller heard about it. */ }
        var host = current is null ? new LabelBook()
            : JsonSerializer.Deserialize<LabelBook>(current) ?? throw new InvalidDataException("The labels could not be read.");
        Task? write = null;
        SharedRowCaughtUp outcome;
        var dropped = 0;
        lock (sync)
        {
            // A profile no longer loaded, or a book that could not be read: nothing here holds this row.
            if (owner != profile) return new(false, 0);
            var next = host.Copy();
            var kept = new List<LabelChange>();
            foreach (var change in unconfirmed)
            {
                var done = change.Apply(next);
                if (done == Done.Failed) dropped++;
                else if (done == Done.Changed) kept.Add(change);
            }
            var through = unconfirmed.Count > 0 ? unconfirmed[^1].Tag : 0;
            unconfirmed.Clear();
            if (kept.Count > 0 && !next.SameAs(host))
            {
                unconfirmed.AddRange(kept);
                book = next;
                saving = write = WriteAfterAsync(saving, owner, next.Copy(), kept[^1].Tag);
                outcome = new(true, through);
            }
            else
            {
                // Nothing of this device's is left to send (left out, or already in the server's book): the server's book as it is.
                book = host;
                outcome = new(false, through);
            }
            resolved = null;
            stale = false;
            Version++;
        }
        if (write is not null) await write.ConfigureAwait(false);
        // The writes of changes made before, which the replica drops now, have all landed.
        else try { await LastSave.ConfigureAwait(false); } catch (Exception) { /* That change's own caller heard about it. */ }
        if (dropped > 0) ChangedElsewhere?.Invoke(this, dropped);
        return outcome;
    }

    private void Confirm(long tag)
    {
        lock (sync) unconfirmed.RemoveAll(change => change.Tag <= tag);
    }

    // The replica could not bring the book to the server's: the kept changes go (said to be), nothing changes until the book is read again, and every write of
    // a change made before has landed when this returns (the replica then drops them).
    private async Task AbandonAsync(string owner)
    {
        int dropped;
        // Under the load's gate too, so a load on its way does not clear `stale` behind it.
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                if (owner != profile) return;
                stale = true;
                dropped = unconfirmed.Count;
                unconfirmed.Clear();
            }
            try { await LastSave.ConfigureAwait(false); } catch (Exception) { /* That change's own caller heard about it. */ }
        }
        finally { gate.Release(); }
        if (dropped > 0) ChangedElsewhere?.Invoke(this, dropped);
    }

    private sealed class Follower(LabelStore owner, string profile) : ISharedRowFollower
    {
        public Task<SharedRowCaughtUp> CatchUpAsync(byte[]? current, CancellationToken cancellationToken) => owner.CatchUpAsync(profile, current);
        public void Confirmed(long tag) => owner.Confirm(tag);
        public Task AbandonAsync() => owner.AbandonAsync(profile);
    }
}

/// <summary>One of the fixed label colours, as a fill and the ink on it for each theme.</summary>
public sealed record LabelColor(string Key, string Name, Rgba LightFill, Rgba LightInk, Rgba DarkFill, Rgba DarkInk);

/// <summary>
/// Eight label colours. Each theme's ink passes 4.5:1 on its fill, and none is a neutral grey: grey is the Pinned
/// label's, and a label that looked like it would read as a state rather than something the user chose.
/// </summary>
public static class LabelColors
{
    private static Rgba Hex(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    public static readonly IReadOnlyList<LabelColor> Colors =
    [
        new("red", "Red", Hex(0xF9DADD), Hex(0xA4262C), Hex(0x4A2426), Hex(0xF4B9BD)),
        new("orange", "Orange", Hex(0xFBDFCB), Hex(0x8A3A0A), Hex(0x4A2D1A), Hex(0xF6C8A4)),
        new("yellow", "Yellow", Hex(0xF8EBB5), Hex(0x6B5300), Hex(0x423814), Hex(0xEEDC98)),
        new("green", "Green", Hex(0xD3EDD1), Hex(0x1E6B22), Hex(0x1E3A20), Hex(0xA6DDA8)),
        new("teal", "Teal", Hex(0xCCEBE9), Hex(0x0B6464), Hex(0x173938), Hex(0x9CDCD8)),
        new("blue", "Blue", Hex(0xD3E4F6), Hex(0x184F87), Hex(0x1C3250), Hex(0xAACDF2)),
        new("purple", "Purple", Hex(0xE3D8F3), Hex(0x5B3494), Hex(0x33284A), Hex(0xD2C0F0)),
        new("pink", "Pink", Hex(0xF6D7E7), Hex(0x98285E), Hex(0x45223A), Hex(0xF1B7D5)),
    ];

    /// <summary>The colour with this key, or the first one for a key this build does not know.</summary>
    public static LabelColor Get(string? key) => Colors.FirstOrDefault(c => c.Key == key) ?? Colors[0];

    // WCAG 2 contrast ratio between two opaque colours.
    public static double Contrast(Rgba a, Rgba b)
    {
        static double Channel(byte value) { var c = value / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double Luminance(Rgba c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>How far a colour is from grey: the spread between its strongest and weakest channel.</summary>
    public static int Chroma(Rgba c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
}

/// <summary>What one pill on a conversation row says. No colour is the neutral grey that Pinned and "+N" use.</summary>
public sealed record LabelChip(string Text, string? Color = null, string? ToolTip = null);

public static class LabelPills
{
    // Two fit after the preview on a narrow list without the preview giving up more than it already does for Pinned.
    public const int MaxPills = 2;
    public static readonly LabelChip Pinned = new("Pinned");

    /// <summary>
    /// Pinned first, then labels in the order they were made; at most two pills. With more than that, the second pill
    /// becomes "+N" and its tooltip names the rest.
    /// </summary>
    public static LabelChip[] Layout(bool pinned, IReadOnlyList<Label> labels)
    {
        var all = new List<LabelChip>(labels.Count + 1);
        if (pinned) all.Add(Pinned);
        foreach (var label in labels) all.Add(new(label.Name, label.Color));
        if (all.Count <= MaxPills) return [.. all];
        var rest = all.Skip(MaxPills - 1).ToArray();
        return [.. all.Take(MaxPills - 1), new("+" + rest.Length, null, string.Join(", ", rest.Select(chip => chip.Text)))];
    }

    /// <summary>
    /// Whether a pill keeps its whole width however narrow the row. Pinned does, as it did before there were labels:
    /// for it the preview gives way as far as its floor. A label or "+N" can be shortened, or left out.
    /// </summary>
    public static bool KeepsWidth(LabelChip? chip) => chip == Pinned;

    // About six characters of text: the default floor for what the pills share a line with (ChatPreviewRow sets its own).
    public const double NameFloor = 48;
    // Narrower than this, with its gap, a label would show little more than its ellipsis.
    public const double MinPill = 36;

    /// <summary>
    /// Shares a row's line between its text (the "name" here; the preview on the row) and its pills, once anything that
    /// always keeps its width has taken it. The first <paramref name="kept"/> pills keep theirs too (Pinned; see
    /// <see cref="KeepsWidth"/>), and for them the name gives way as far as it must. For the rest, the name gives way
    /// first, down to <see cref="NameFloor"/>; only then do they get narrower — the narrow ones keep their width and
    /// the wide ones share what is left. One that would be narrower than <see cref="MinPill"/> gets no width at all,
    /// the last first, and the others share again. The name takes whatever is left.
    /// </summary>
    public static (double Name, double[] Pills) Fit(double room, double name, IReadOnlyList<double> pills, int kept = 0, double nameFloor = NameFloor)
    {
        room = Math.Max(0, room);
        var widths = pills.ToArray();
        if (name + widths.Sum() <= room) return (name, widths);
        kept = Math.Clamp(kept, 0, widths.Length);
        var pillRoom = Math.Max(0, room - Math.Min(name, nameFloor) - widths.Take(kept).Sum());
        var shared = Enumerable.Range(kept, widths.Length - kept).Where(i => pills[i] > 0).ToList();
        while (shared.Count > 0)
        {
            var order = shared.OrderBy(i => pills[i]).ToArray();
            var remaining = pillRoom;
            for (var k = 0; k < order.Length; k++)
            {
                var share = remaining / (order.Length - k);
                if (pills[order[k]] <= share) { widths[order[k]] = pills[order[k]]; remaining -= pills[order[k]]; continue; }
                for (var j = k; j < order.Length; j++) widths[order[j]] = share;
                break;
            }
            if (shared.All(i => widths[i] >= Math.Min(pills[i], MinPill))) break;
            widths[shared[^1]] = 0;
            shared.RemoveAt(shared.Count - 1);
        }
        return (Math.Max(0, room - widths.Sum()), widths);
    }
}
