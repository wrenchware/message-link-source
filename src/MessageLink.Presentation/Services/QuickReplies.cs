using System.Text.Json.Serialization;

namespace MessageLink.Presentation.Services;

/// <summary>A named snippet the user keeps for putting into the composer. Never sent by itself.</summary>
public sealed record QuickReply(string Id, string Name, string Text)
{
    /// <summary>The text on one line, for the secondary line under the name.</summary>
    [JsonIgnore]
    public string Preview => string.Join(" ", (Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

/// <summary>A read of the quick replies, and whether the row could be read at all.</summary>
public sealed record QuickReplyRead(IReadOnlyList<QuickReply> Replies, bool Readable);

/// <summary>
/// Quick replies belong to this PC and the signed-in profile, like emoji recents: one protected row in the preferences
/// category. They are read when they are about to be shown, not kept in memory, so a backup restore that adds the row is
/// seen at the next opening. A row that cannot be read shows as an empty book rather than an error, but the editor is
/// told it could not be read and does not save over it. Names and text are
/// never logged.
/// </summary>
public sealed class QuickReplyBook(PrivateState state)
{
    // The protected store's category and row. A backup that carries quick replies names this row.
    public const string Category = "preferences";
    public const string RowId = "quick-replies";
    public const int MaxEntries = 50, MaxNameLength = 40, MaxTextLength = 2000;

    /// <summary>Raised after a save reached the store.</summary>
    public event EventHandler? Changed;

    public Task<string?> ProfileAsync() => state.ProfileAsync();

    /// <summary>For showing: a row that cannot be read is an empty book.</summary>
    public async Task<IReadOnlyList<QuickReply>> LoadAsync(string profile) => (await ReadAsync(profile)).Replies;

    /// <summary>
    /// For editing: the book and whether it could be read at all. No row is a readable empty book; a store that failed or
    /// a row that does not parse is not, and an editor must not save over what it could not read.
    /// </summary>
    public async Task<QuickReplyRead> ReadAsync(string profile)
    {
        try { return new(Clean(await state.ReadAsync<QuickReply?[]>(profile, Category, RowId) ?? []), true); }
        catch (Exception) { return new([], false); }
    }

    /// <summary>
    /// Saves only over the book the editor started from. Null, and nothing written, when the saved book can no longer be
    /// read or no longer holds what <paramref name="startedFrom"/> held — another window saved meanwhile, or a backup
    /// was restored — so one editor's Save never silently drops another's replies.
    /// </summary>
    public async Task<IReadOnlyList<QuickReply>?> SaveOverAsync(string profile, IReadOnlyList<QuickReply> startedFrom, IReadOnlyList<QuickReply> replies)
    {
        var now = await ReadAsync(profile);
        if (!now.Readable || !Same(now.Replies, startedFrom)) return null;
        return await SaveAsync(profile, replies);
    }

    // By what the user wrote rather than by id: a row whose ids had to be made up again reads with new ones each time.
    private static bool Same(IReadOnlyList<QuickReply> a, IReadOnlyList<QuickReply> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First.Name == pair.Second.Name && pair.First.Text == pair.Second.Text);

    /// <summary>Writes the book as it will be read back, and returns that. An empty book leaves no row behind.</summary>
    public async Task<IReadOnlyList<QuickReply>> SaveAsync(string profile, IReadOnlyList<QuickReply> replies)
    {
        var clean = Clean(replies);
        if (clean.Count == 0) await state.RemoveAsync(profile, Category, RowId);
        else await state.WriteAsync(profile, Category, RowId, clean);
        Changed?.Invoke(this, EventArgs.Empty);
        return clean;
    }

    /// <summary>
    /// In order: names and text trimmed, a name cut to 40 characters and text to 2,000 without splitting a character, an
    /// entry with no text dropped, one with no name named after its first line, ids made unique, and at most 50 kept.
    /// </summary>
    public static IReadOnlyList<QuickReply> Clean(IEnumerable<QuickReply?> replies)
    {
        var clean = new List<QuickReply>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reply in replies)
        {
            if (clean.Count == MaxEntries) break;
            if (reply is null) continue;
            var text = Cut((reply.Text ?? "").Trim(), MaxTextLength).Trim();
            if (text.Length == 0) continue;
            var name = CleanName(reply.Name) ?? CleanName(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0]) ?? "Quick reply";
            var id = string.IsNullOrWhiteSpace(reply.Id) || ids.Contains(reply.Id) ? Guid.NewGuid().ToString("N") : reply.Id;
            ids.Add(id);
            clean.Add(new(id, name, text));
        }
        return clean;
    }

    /// <summary>One line, trimmed and cut to the limit; null when nothing is left.</summary>
    public static string? CleanName(string? name)
    {
        if (name is null) return null;
        var text = Cut(new string(name.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim(), MaxNameLength).TrimEnd();
        return text.Length == 0 ? null : text;
    }

    private static string Cut(string text, int limit) =>
        text.Length <= limit ? text : text[..(char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit)];
}

/// <summary>How text chosen elsewhere lands in the composer. Pure, so the rule is checked without a window.</summary>
public static class ComposerInsertion
{
    public const string TooLongToAdd = "That's too long to add here.";

    public enum Staged { Inserted, TooLong, NotReady }

    /// <summary>How long a forward waits for its composer whatever the window shows meanwhile.</summary>
    public static readonly TimeSpan ForwardFirstWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether forwarded text still waits for the chosen conversation's composer: for the first few seconds regardless,
    /// then for as long as that conversation is still the one shown. A combined conversation can be without a send route
    /// for longer, until its members load or a service is picked in Send using, and the text lands once it has one rather
    /// than being dropped.
    /// </summary>
    public static bool KeepWaiting(TimeSpan waited, bool stillShown) => stillShown || waited <= ForwardFirstWait;

    /// <summary>
    /// Forwarded text into a composer: appended by <see cref="Append"/>, or, when it would not fit, nothing inserted and
    /// the composer saying so. Only the composer's text changes; this never sends, and a send queued before the composer
    /// was ready is for the text it was queued with, which this replaces.
    /// </summary>
    public static Staged Stage(MessageLink.Presentation.ViewModels.ComposerViewModel composer, string text, int maxLength)
    {
        if (!composer.IsReady || string.IsNullOrEmpty(text) || composer.IsSavingEdit) return Staged.NotReady;
        // Forwarded words are for a new message: an edit in the box ends, and they join the draft it had set aside.
        composer.CancelEdit();
        if (Append(composer.Text ?? "", text, maxLength) is not { } joined) { composer.Status = TooLongToAdd; return Staged.TooLong; }
        composer.Text = joined;
        return Staged.Inserted;
    }

    /// <summary>
    /// A forwarded message goes after whatever is already being written, on a line of its own. Null when the result would
    /// be longer than the composer takes, in which case nothing is inserted at all.
    /// </summary>
    public static string? Append(string current, string text, int maxLength, string newLine = "\r")
    {
        current ??= "";
        var joined = current.Length == 0 ? text
            : current.EndsWith('\r') || current.EndsWith('\n') ? current + text
            : current + newLine + text;
        return joined.Length > maxLength ? null : joined;
    }

    /// <summary>Text at the caret, over any selection. Null when the result would be longer than the composer takes.</summary>
    public static (string Text, int Caret)? Insert(string current, int selectionStart, int selectionLength, string text, int maxLength)
    {
        current ??= "";
        var start = Math.Clamp(selectionStart, 0, current.Length);
        var length = Math.Clamp(selectionLength, 0, current.Length - start);
        if (current.Length - length + text.Length > maxLength) return null;
        return (string.Concat(current.AsSpan(0, start), text, current.AsSpan(start + length)), start + text.Length);
    }
}
