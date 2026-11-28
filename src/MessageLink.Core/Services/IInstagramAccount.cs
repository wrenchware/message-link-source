namespace MessageLink.Core.Services;

/// <summary>What Settings and the account filters know of the Instagram account, read without a call.</summary>
/// <param name="Connected">The helper is running and signed in.</param>
/// <param name="Saved">A saved session exists, whether or not it has reconnected yet.</param>
/// <param name="AccountId">The account's own id, or null until a session has been read.</param>
/// <param name="AccountName">The account's name, or null until a session starts (or where Instagram named none).</param>
public sealed record InstagramAccountState(bool Connected, bool Saved, string? AccountId, string? AccountName);

/// <summary>
/// The Instagram account's lifecycle, for any client (SWITCHBOARD.md 2.6). The Instagram sign-in window is the client's;
/// it hands its cookies to <see cref="ConnectAsync"/>, and the host owns the session from then on. Saved cookies never
/// leave the host.
/// </summary>
public interface IInstagramAccount
{
    InstagramAccountState State { get; }

    /// <summary>The account's state or its conversation list changed (a sign-in, a sign-out, a reconnect, new chats).</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Raised when nothing will reconnect Instagram any more without the user, so what an open conversation says about
    /// sending can change from "reconnecting" to "not connected". May be raised on any thread.
    /// </summary>
    event EventHandler? ReconnectAbandoned;

    /// <summary>
    /// Connects with the saved session, which the host reads itself. Answers the conversations joined, or -1 when there is
    /// none this Windows user can read (a fresh sign-in is what that needs; the host has logged why).
    /// </summary>
    Task<int> ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>Connects with a new sign-in's cookies and remembers the session. Answers the conversations joined.</summary>
    Task<int> ConnectAsync(Dictionary<string, string> cookies, CancellationToken cancellationToken = default);

    /// <summary>
    /// The launch's reconnect, with its retry policy (InstagramRestore). stopped: the host is closing. A failure comes back
    /// as <see cref="AccountRestore.Refusal"/>, the sentence to show; none means there is nothing safe to say.
    /// </summary>
    Task<AccountRestore> RestoreAsync(Func<bool> stopped);

    Task SignOutAsync();

    /// <summary>Whether the options lookup for a message answers from memory, asking Instagram nothing.</summary>
    bool AnswersFromMemory(string chatId, string messageId);
}
