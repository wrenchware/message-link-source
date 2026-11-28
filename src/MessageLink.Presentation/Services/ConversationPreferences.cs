using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Presentation.Services;

/// <summary>One item of a conversation's menu that <see cref="ConversationPreferences"/> runs: its words, and the value choosing it sets.</summary>
public sealed record PreferenceOffer(string Label, bool TurnsOn);

/// <summary>
/// What the menu and the console offer of Pin, Mute, Mark unread and Remind me later for a conversation. Pin, Mute and Remind me later are
/// always offered, each the way back when it is on; Mark unread is null where it is not offered.
/// </summary>
public sealed record PreferenceOffers(PreferenceOffer Pin, PreferenceOffer Mute, PreferenceOffer? MarkUnread, PreferenceOffer Remind);

/// <summary>What a shortcut's toggle did: how it ended, the value it set and the word that confirms it ("Muted", "Unpinned").</summary>
public sealed record PreferenceToggle(ConsoleActionResult Result, bool On, string Word);

/// <summary>
/// The changes to a conversation that stay in Message Link: pin, mute (until turned off or for a while), marked unread, remind me later and
/// an archive kept here for a service that cannot archive. One set of rules for the Windows app and the console (mlink): what is offered and
/// its words, and how each is written, for one conversation or a merged one. Nothing is sent to the service. A merged conversation's is its
/// merges row; one conversation's is a preference row of the notification profile, given up (nothing shown) when that profile changed while
/// it was written, or the client closed. Reading a conversation clears its snooze and its marked unread elsewhere, not here.
/// </summary>
public sealed class ConversationPreferences(ILocalChatPreferences preferences, IChatSnoozes snoozes, MergedConversations merges, ChatListViewModel chats,
    IAccessTokenStore tokens, Action<string> log, Func<bool>? stillOpen = null)
{
    public const string PreferenceFailedWords = "Could not save this chat preference. Try again.";
    public const string ReminderFailedWords = "Could not save this reminder. Try again.";

    /// <summary>The menu's Mute choices, each with its length (null: until turned off) and the log's word for it.</summary>
    public static readonly IReadOnlyList<(string Text, TimeSpan? Length, string Word)> MuteChoices =
    [
        ("For 1 hour", TimeSpan.FromHours(1), "1h"), ("For 8 hours", TimeSpan.FromHours(8), "8h"),
        ("For 1 week", TimeSpan.FromDays(7), "1w"), ("Until I unmute", null, "always"),
    ];

    private bool Open => stillOpen?.Invoke() ?? true;

    // ---- What is offered ----

    /// <summary>
    /// Pin or Unpin; Mute, or Unmute saying when a timed mute ends by itself (a merged conversation's end is in its merges row); Mark as read
    /// for a conversation marked unread, Mark as unread for one with nothing unread, and neither where the client does not offer them
    /// (<paramref name="markUnreadHere"/>: a conversation shown in its own window is being read there); Remind me later, or Cancel reminder
    /// with its time while one is waiting. <paramref name="now"/> is local time.
    /// </summary>
    public static PreferenceOffers Offers(ChatSummary chat, MergedConversations? merges, bool markUnreadHere, DateTimeOffset now)
    {
        var pin = new PreferenceOffer(chat.IsPinned ? "Unpin" : "Pin", !chat.IsPinned);
        var until = MergedConversations.IsMerged(chat.Id) ? merges?.MutedUntil(chat.Id) : chat.MutedUntil;
        var mute = chat.IsMuted && !(until <= now)
            ? new PreferenceOffer(until is { } end ? $"Unmute (muted until {ChatItemViewModel.MuteMoment(end, now)})" : "Unmute", false)
            : new PreferenceOffer("Mute", true);
        PreferenceOffer? unread = !markUnreadHere ? null
            : chat.IsMarkedUnread ? new("Mark as read", false)
            : chat.UnreadCount == 0 ? new("Mark as unread", true)
            : null;
        var remind = chat.SnoozedUntil is { } snoozed && snoozed > now
            ? new PreferenceOffer($"Cancel reminder ({snoozed.ToLocalTime():t})", false)
            : new PreferenceOffer("Remind me later", true);
        return new(pin, mute, unread, remind);
    }

    /// <summary>The word that confirms a toggled Mute or Pin.</summary>
    public static string ToggleWord(ChatSetting setting, bool on) => setting == ChatSetting.Muted
        ? on ? "Muted" : "Unmuted"
        : on ? "Pinned" : "Unpinned";

    // ---- Writing them ----

    /// <summary>
    /// Pin, mute until turned off, marked unread, or an archive kept here. Unmuting a conversation its service reports muted (set on the phone)
    /// sets that mute aside rather than clearing it there; a merged conversation's mute until turned off replaces a timed one still running.
    /// </summary>
    public async Task<ConsoleActionResult> SetAsync(ChatSummary chat, ChatSetting setting, bool on)
    {
        try
        {
            if (MergedConversations.IsMerged(chat.Id))
            {
                await merges.SetAsync(chat.Id, group => setting switch
                {
                    ChatSetting.Pinned => group with { Pinned = on },
                    ChatSetting.Muted => group with { Muted = on, MutedUntil = null },
                    ChatSetting.MarkedUnread => group with { MarkedUnread = on },
                    _ => group,
                });
                if (Open) chats.RebuildMerged();
                return ConsoleActionResult.Done;
            }
            if (await NotificationIdentity.ProfileAsync(tokens) is not { } profile) return GivenUp;
            if (setting == ChatSetting.Muted && !on && chat.ServiceMute is { } serviceMute)
                await preferences.SetAsideServiceMuteAsync(profile, chat.Id, serviceMute);
            else await preferences.SetAsync(profile, chat.Id, setting, on);
            if (await MovedAsync(profile)) return GivenUp;
            chats.ApplyLocalSetting(chat.Id, setting, on);
            return ConsoleActionResult.Done;
        }
        catch (Exception failure)
        {
            log($"chat preference failed setting={setting} error={failure.GetType().Name}");
            return new(ConsoleActionOutcome.Refused, PreferenceFailedWords);
        }
    }

    /// <summary>
    /// Mute until <paramref name="until"/>, or until turned off when it is null; or unmute. <paramref name="duration"/> is the log's word for a
    /// mute being set (<see cref="MuteChoices"/>), null for none.
    /// </summary>
    public async Task<ConsoleActionResult> MuteAsync(ChatSummary chat, bool muted, DateTimeOffset? until, string? duration = null)
    {
        try
        {
            if (MergedConversations.IsMerged(chat.Id))
            {
                await merges.SetMutedAsync(chat.Id, muted, until);
                if (Open) chats.RebuildMerged();
            }
            else
            {
                if (await NotificationIdentity.ProfileAsync(tokens) is not { } profile) return GivenUp;
                if (!muted && chat.ServiceMute is { } serviceMute) await preferences.SetAsideServiceMuteAsync(profile, chat.Id, serviceMute);
                else await preferences.SetMutedAsync(profile, chat.Id, muted, until);
                if (await MovedAsync(profile)) return GivenUp;
                chats.ApplyMute(chat.Id, muted, until);
            }
            if (duration is not null) log($"chat mute set duration={duration}");
            return ConsoleActionResult.Done;
        }
        catch (Exception failure)
        {
            log($"chat preference failed setting={ChatSetting.Muted} error={failure.GetType().Name}");
            return new(ConsoleActionOutcome.Refused, PreferenceFailedWords);
        }
    }

    /// <summary>Remind me later at <paramref name="until"/>, or call it off when it is null: the snooze row, and a merged conversation's merges row too.</summary>
    public async Task<ConsoleActionResult> SnoozeAsync(ChatSummary chat, DateTimeOffset? until)
    {
        try
        {
            if (await NotificationIdentity.ProfileAsync(tokens) is not { } profile) return GivenUp;
            await snoozes.SetAsync(profile, chat.Id, until);
            if (MergedConversations.IsMerged(chat.Id)) await merges.SetAsync(chat.Id, group => group with { SnoozedUntil = until });
            if (await MovedAsync(profile)) return GivenUp;
            chats.ApplySnooze(chat.Id, until);
            return ConsoleActionResult.Done;
        }
        catch (Exception failure)
        {
            log($"snooze failed error={failure.GetType().Name}");
            return new(ConsoleActionOutcome.Refused, ReminderFailedWords);
        }
    }

    /// <summary>A shortcut's Mute or Pin: the other of what the conversation shows now, until turned off.</summary>
    public async Task<PreferenceToggle> ToggleAsync(ChatSummary chat, ChatSetting setting)
    {
        var on = setting == ChatSetting.Muted ? !chat.IsMuted : !chat.IsPinned;
        log($"shortcut {(setting == ChatSetting.Muted ? "mute" : "pin")} on={on}");
        return new(await SetAsync(chat, setting, on), on, ToggleWord(setting, on));
    }

    // Given up without a word: the profile went or changed while it was written, or the client closed. Nothing is shown, and the row written
    // belongs to the profile it was read under.
    private static readonly ConsoleActionResult GivenUp = new(ConsoleActionOutcome.Refused);

    private async Task<bool> MovedAsync(string profile) => !Open || profile != await NotificationIdentity.ProfileAsync(tokens);
}
