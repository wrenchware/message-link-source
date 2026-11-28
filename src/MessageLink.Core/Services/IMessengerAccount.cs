namespace MessageLink.Core.Services;

/// <summary>Messenger's encrypted-conversation connection, in the helper's own states.</summary>
public enum MessengerEncryption { Unknown, Off, Connecting, Ready, Down, DeviceGone, DeviceMissing }

/// <summary>What Settings and the account filters know of the Messenger account, read without a call.</summary>
public sealed record MessengerAccountState(bool Connected, bool Saved, bool EncryptedEnabled, MessengerEncryption Encrypted);

/// <summary>
/// How the launch's reconnect of a saved session went: the conversations it joined, the provider's refusal as a fixed code
/// (an answer only the user can change), Explanation when the provider words its own failure for Settings, or Skipped
/// when nothing saved could be read.
/// </summary>
public sealed record AccountRestore(int? Conversations, string? Refusal = null, bool Skipped = false, string? Explanation = null);

/// <summary>
/// The Messenger account's lifecycle, for any client (SWITCHBOARD.md 2.6). The Facebook sign-in window is the client's;
/// it hands its cookies to <see cref="ConnectAsync"/>, and the host owns the session from then on.
/// </summary>
public interface IMessengerAccount
{
    MessengerAccountState State { get; }

    /// <summary>The account's state or its conversation list changed (a sign-in, a sign-out, a reconnect, new chats).</summary>
    event EventHandler? Changed;

    /// <summary>Whether a saved session can be read here, so Connect can use it rather than open the sign-in window.</summary>
    Task<bool> HasUsableSavedSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>Connects with a new sign-in's cookies, or with the saved session when null. Answers the conversations joined.</summary>
    Task<int> ConnectAsync(Dictionary<string, string>? cookies, CancellationToken cancellationToken = default);

    /// <summary>The launch's reconnect, with its retry policy (MessengerRestore). stopped: the host is closing.</summary>
    Task<AccountRestore> RestoreAsync(Func<bool> stopped);

    /// <summary>What Messenger has sent so far, in a sentence for Settings.</summary>
    Task<string> DescribeAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the helper for the encrypted connection's state again; true when it changed.</summary>
    Task<bool> RefreshEncryptedStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns encrypted conversations on; null when it worked, otherwise a fixed failure code.</summary>
    Task<string?> EnableEncryptedAsync(CancellationToken cancellationToken = default);

    /// <summary>The one automatic turn-on, straight after a new sign-in (MessengerEncryptedAuto).</summary>
    Task<string?> EnableEncryptedAfterSignInAsync();

    Task SignOutAsync();
}
