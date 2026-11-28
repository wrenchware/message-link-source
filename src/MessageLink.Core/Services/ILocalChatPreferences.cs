using MessageLink.Core.Messaging;
namespace MessageLink.Core.Services;
// MutedUntil: when a timed mute ends, as saved; null for no mute or a mute until turned off. Whether the mute still holds
// is decided when it is read (LocalPreferenceChatReader), not here. ArchivedAt: when the conversation was archived on
// this PC only (Instagram, which cannot archive from here); null when it is not. Both appended.
// ServiceMuteSetAside: the service's mute (ServiceMute.Key) that was unmuted on this PC; it shows unmuted here while the
// service still reports that same mute, and a different one from the service shows again. Appended (2026-10-04).
public sealed record LocalChatPreferences(bool Pinned = false, bool Muted = false, bool MarkedUnread = false,
    DateTimeOffset? MutedUntil = null, DateTimeOffset? ArchivedAt = null, long? ServiceMuteSetAside = null);
public interface ILocalChatPreferences
{
    Task<LocalChatPreferences> GetAsync(string profile, string chatId, CancellationToken cancellationToken = default);
    Task SetAsync(string profile, string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken = default);
    /// <summary>
    /// Mutes until a moment, or until turned off when <paramref name="until"/> is null. Unmuting ignores the moment.
    /// A store that keeps no moment can still mute until turned off; asked for a timed mute it refuses rather than
    /// saving a mute that would never end.
    /// </summary>
    Task SetMutedAsync(string profile, string chatId, bool muted, DateTimeOffset? until, CancellationToken cancellationToken = default)
        => muted && until is not null
            ? throw new NotSupportedException("This store keeps no timed mute.")
            : SetAsync(profile, chatId, ChatSetting.Muted, muted, cancellationToken);
    /// <summary>
    /// Unmutes on this PC a conversation the service itself reports muted (set on the phone), without telling the
    /// service: this PC notifies again while the phone stays muted. Held against that one mute, so a newer mute from the
    /// service shows again. A store that keeps no such record only clears its own mute.
    /// </summary>
    Task SetAsideServiceMuteAsync(string profile, string chatId, ServiceMute serviceMute, CancellationToken cancellationToken = default)
        => SetAsync(profile, chatId, ChatSetting.Muted, false, cancellationToken);
}
