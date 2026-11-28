namespace MessageLink.Core.Services;

/// <summary>
/// One Google Voice account as Settings and the account filters read it: a snapshot at the moment it was asked for.
/// Key is the hash that scopes the account's conversations (gvoice:&lt;key&gt;:&lt;id&gt;); Number is null until login names it.
/// </summary>
public sealed record GoogleVoiceAccountState(string Key, string? Number, string State, int? ConversationCount, bool Connected);

/// <summary>
/// The Google Voice accounts signed in on this PC, for any client (SWITCHBOARD.md 2.6). Each account has its own helper;
/// renewals, restarts and recovery stay inside the host. The Google sign-in window is the client's: it hands the cookies
/// and the browser profile it captured to <see cref="AddAsync"/>, and the host owns the session from then on.
/// </summary>
public interface IGoogleVoiceAccounts
{
    /// <summary>The accounts now listed, including those still connecting, reconnecting or needing a new sign-in.</summary>
    IReadOnlyList<GoogleVoiceAccountState> Accounts { get; }

    /// <summary>Listed accounts and saved ones that have not restored yet; both still own cached rows and retry cursors.</summary>
    IReadOnlyList<string> KnownAccountKeys { get; }

    /// <summary>An account was added, removed, or changed state or conversation count.</summary>
    event EventHandler? Changed;

    /// <summary>Reconnects every saved account. Answers why each one that could not reconnect failed, as fixed codes.</summary>
    Task<IReadOnlyList<string>> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Signs a new account in from a sign-in's cookies and browser profile, or refreshes one already here.</summary>
    Task<GoogleVoiceAccountState> AddAsync(Dictionary<string, string> cookies, string profileId, CancellationToken cancellationToken = default);

    Task SignOutAsync(string key);
}
