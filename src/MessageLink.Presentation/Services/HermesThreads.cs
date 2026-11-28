using System.Globalization;
using System.Text.Json;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// What a client shows of a Hermes Agent thread (protocol 31): one thread a profile, text and pictures in, its reply out. Pure, so every rule
/// the window draws by is checked offline.
/// </summary>
/// <remarks>
/// The owner's privacy line (2026-11-14): Hermes gets only what is typed or sent in its own thread. A Hermes thread is never a place to forward
/// or share to, never merged with another conversation and never in New chat; replying, reacting, editing, removing, archiving and the
/// directory are refused for it by the server, so the window does not offer them.
/// </remarks>
public static class HermesThreads
{
    public const string Network = HermesIds.Network;
    /// <summary>The server keeps up to this many repeats, each prompt up to <see cref="MaxPromptLength"/> characters (ServerRepeats).</summary>
    public const int MaxRepeats = 20, MaxPromptLength = 2000;
    /// <summary>What the file picker offers in a Hermes thread: the pictures Hermes takes, and nothing else.</summary>
    public static IReadOnlyList<string> PictureExtensions { get; } = [".jpg", ".jpeg", ".png", ".gif", ".webp"];
    private static readonly string[] PictureTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"];

    public static bool Is(string? chatId) => HermesIds.Owns(chatId);
    public static bool Is(ChatSummary? chat) => chat is not null && (HermesIds.Owns(chat.Id) || chat.Network == Network);

    /// <summary>Whether a window shows Hermes at all: only once a profile is set up (on the server's page), so someone who never uses Hermes never
    /// sees it. Routing still follows whether the server runs Hermes.</summary>
    public static bool Shown(HermesAccountState? state) => state is { Agents.Count: > 0 };

    public static bool TakesPicture(string? mimeType) => mimeType is not null && PictureTypes.Contains(mimeType.ToLowerInvariant());

    /// <summary>The outgoing prompt one of the server's repeats sent: drawn as a small label, not a bubble of the person's own.</summary>
    public static bool IsScheduledPrompt(ChatMessage message) => message.Type == "scheduled" && HermesIds.Owns(message.ChatId);
    public static string ScheduledLabel(string? text) => "Scheduled: " + OneLine(MessageText.PlainText(MessageText.Literal(text ?? "")));

    /// <summary>A profile's state in the words Settings shows.</summary>
    public static string StatusWord(string? status) => status switch
    {
        "connected" => "Ready",
        "unreachable" => "Can't reach Hermes",
        "key_rejected" => "Wrong API key",
        "no_profile" => "Not found in Hermes",
        _ => "Checking",
    };

    /// <summary>The name a profile goes by: the title its server gives, else <see cref="DefaultTitle"/>.</summary>
    public static string Title(HermesAgentState agent) => agent.Title is { Length: > 0 } title ? title : DefaultTitle(agent.Name);

    /// <summary>
    /// A profile's name made a conversation's (owner, 2026-11-16: an assistant called "Oracle", not "Hermes Agent - oracle"): "Hermes" for the default
    /// profile, else the profile's name with its dashes and underscores as spaces and a capital first letter, so research-bot is "Research bot". The
    /// server's backend names threads by this too, so every client says the same.
    /// </summary>
    public static string DefaultTitle(string profile)
    {
        if (profile == "default") return "Hermes";
        var words = profile.Replace('-', ' ').Replace('_', ' ').Trim();
        return words.Length == 0 ? profile : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>
    /// What the conversation's dots say to a screen reader and on hover while someone is typing. Hermes is working on its reply rather than
    /// typing; the tool it is using (its typing status) is not said in the apps, only in mlink (owner, 2026-11-18: a chat app shows dots).
    /// </summary>
    public static string TypingText(IReadOnlyList<TypingParticipant> people, bool hermes)
    {
        if (people.Count == 0) return "";
        var names = people.Select(p => p.DisplayName).ToArray();
        if (hermes) return $"{names[0]} is working…";
        return names.Length switch
        {
            1 => $"{names[0]} is typing…",
            2 => $"{names[0]} and {names[1]} are typing…",
            _ => $"{names[0]} and {names.Length - 1} others are typing…",
        };
    }

    // ---- Repeats ----

    /// <summary>"Weekdays at 7:00 AM · Morning brief": the days, the server's wall-clock time and the start of the prompt.</summary>
    public static string Describe(RepeatingSend repeat, CultureInfo? culture = null) =>
        $"{repeat.Rule.DaysText()} at {TimeText(repeat.Minute, culture)} · {Short(OneLine(repeat.Text), 60)}";

    public static string TimeText(int minute, CultureInfo? culture = null) =>
        DateTime.MinValue.Date.AddMinutes(Math.Clamp(minute, 0, 24 * 60 - 1)).ToString("t", culture ?? CultureInfo.CurrentCulture);

    /// <summary>What the last time did, when it did not send: a line under the repeat, or null.</summary>
    public static string? LastText(RepeatingSend repeat) => repeat.LastOutcome switch
    {
        "skipped" => "Skipped last time",
        "failed" => "Didn't send last time",
        _ => null,
    };

    /// <summary>What stops a new repeat, as a sentence, or null when it can be asked for.</summary>
    public static string? Problem(string? text, int days, int existing)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Write the prompt to send.";
        if (text.Trim().Length > MaxPromptLength) return $"A prompt can be up to {MaxPromptLength:N0} characters.";
        if (days is <= 0 or > RepeatRule.Everyday) return "Choose at least one day.";
        if (existing >= MaxRepeats) return $"You can have up to {MaxRepeats} repeats. Cancel one first.";
        return null;
    }

    /// <summary>The day mask for the days chosen (bit 0 Sunday).</summary>
    public static int Days(IEnumerable<DayOfWeek> days) => days.Aggregate(0, (mask, day) => mask | 1 << (int)day);

    /// <summary>
    /// The server's repeats among the client's replica rows: each a <see cref="RepeatingSend"/> in the default JSON shape, counted only under the
    /// key its own id gives it (<paramref name="keyOf"/>, <c>SharedCategories.KeyOf(profile, "repeat", id)</c>), in a Hermes thread, with days and a
    /// time that make sense. Anything else is not this profile's repeat and is left out. In time order.
    /// </summary>
    public static IReadOnlyList<RepeatingSend> ReadRows(IEnumerable<(string Key, byte[] Value)> rows, Func<string, string> keyOf)
    {
        var found = new List<RepeatingSend>();
        foreach (var (key, value) in rows)
        {
            RepeatingSend? repeat;
            try { repeat = JsonSerializer.Deserialize<RepeatingSend>(value); }
            catch (JsonException) { continue; }
            if (repeat is { Id.Length: > 0, ChatId.Length: > 0, Text: not null } && HermesIds.Owns(repeat.ChatId) && repeat.Rule.Valid &&
                string.Equals(key, keyOf(repeat.Id), StringComparison.Ordinal))
                found.Add(repeat);
        }
        return [.. found.OrderBy(r => r.Minute).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal)];
    }

    private static string OneLine(string text) => string.Join(' ', text.Split(new[] { '\r', '\n', '\t' },StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    private static string Short(string text, int length) => text.Length <= length ? text : text[..(length - 1)].TrimEnd() + "…";
}

/// <summary>
/// The server's repeats as a client reads them (<c>repeat:</c> rows of its replica, protocol 31). Registered only for a client of a server, the one
/// kind of server that keeps them; elsewhere there is none and no repeat is offered.
/// </summary>
public interface IRepeatRows
{
    event EventHandler? RowsChanged;
    Task<IReadOnlyList<RepeatingSend>> ReadAsync(CancellationToken cancellationToken = default);
}
