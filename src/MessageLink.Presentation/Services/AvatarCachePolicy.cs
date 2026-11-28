namespace MessageLink.Presentation.Services;

public enum AvatarCacheAction { UsePhoto, UseNoPhoto, Fetch, WaitAfterFailure }

// Saved avatars show immediately, including after a restart. A background check runs at most once per
// CheckInterval per chat, and a failed attempt is not repeated for RetryAfterFailure, even across restarts.
public static class AvatarCachePolicy
{
    public static readonly TimeSpan PhotoLifetime = TimeSpan.FromDays(30), NoPhotoLifetime = TimeSpan.FromDays(3),
        CheckInterval = TimeSpan.FromDays(1), RetryAfterFailure = TimeSpan.FromMinutes(30);

    public static (AvatarCacheAction Action, bool RefreshInBackground) Decide(bool photo, bool noPhoto, bool recentlyChecked, bool recentlyFailed)
    {
        var refresh = !recentlyChecked && !recentlyFailed;
        if (photo) return (AvatarCacheAction.UsePhoto, refresh);
        if (noPhoto) return (AvatarCacheAction.UseNoPhoto, refresh);
        return recentlyFailed ? (AvatarCacheAction.WaitAfterFailure, false) : (AvatarCacheAction.Fetch, false);
    }

    // Whether Decide's answer can depend on recentlyFailed, so a caller can skip reading the failure marker when it cannot.
    public static bool FailureMatters(bool photo, bool noPhoto, bool recentlyChecked) => !recentlyChecked || !photo && !noPhoto;
}
