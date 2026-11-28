using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// The three states every connected service can be in. Connecting is not one of them: that is an action, and it is reported on the card's own status line
/// rather than replacing the steady state.
/// </summary>
public enum ServiceState { Connected, SignedIn, NotConnected }

/// <summary>
/// One row of a Settings → Connections card: who the account is, and how it stands at the row's right, in plain words when all is well and amber with a dot
/// when it needs a look, as Windows' own Settings shows a Bluetooth device (owner, 2026-11-04). <paramref name="Key"/> tells two accounts with the same name
/// apart. A service with no account has the one <see cref="ConnectionRows.NoAccount"/> row, which is not an account.
/// </summary>
public sealed record ConnectionRow(string Identity, ServiceState State, string Word, bool IsAccount, string? Key = null)
{
    /// <summary>A service with no account has nothing wrong with it, so only an account that isn't connected asks for attention.</summary>
    public bool NeedsAttention => IsAccount && State != ServiceState.Connected;
}

/// <summary>
/// Messenger's encrypted conversations as its card shows them: the pill (shown while they are turned on, its word and colour), the line under it, whether the
/// section is there at all, and whether to ask the server again for the encrypted connection's state.
/// </summary>
public sealed record MessengerEncryptedCard(bool PillShown, string Word, bool? Healthy, string Description, bool SectionShown, bool AskAgain);

/// <summary>
/// Settings → Connections: each service's account rows from the service's own state and the accounts the window has loaded, in one vocabulary, so five cards
/// cannot drift into five. The window draws what this returns: a brush for the state, the caution brush for a row that needs attention, and the words.
/// </summary>
/// <remarks>
/// A service's original account (its unslotted one, which Google Messages, Messenger and Instagram each have) comes from the service's state; accounts added
/// beside it (<see cref="AccountNamespace"/> keys) come from the loaded accounts and follow it, in the order they were loaded. With an added account, the
/// original row stays only while that account stands (paired, signed in or connected), so a card never says No account above an account it lists.
/// </remarks>
public static class ConnectionRows
{
    /// <summary>The generic noun for an account whose name is not known yet, and the row for a service with no account at all.</summary>
    public const string NoAccountWord = "No account";

    /// <summary>The Settings line while encrypted conversations are off: a saved session from before a new sign-in turned them on by itself, or a sign-in where
    /// that did not work. A new sign-in on the server's page is what turns them on.</summary>
    public const string MessengerEncryptedOff = "Encrypted conversations aren't on. Sign in to Messenger again on the server's page.";

    public static ConnectionRow NoAccount { get; } = new(NoAccountWord, ServiceState.NotConnected, Word(ServiceState.NotConnected), IsAccount: false);

    /// <summary>A state's usual word. A service may say more than one word for its state (Google Messages is Paired, Hermes names what is wrong), but its dot
    /// still follows the state.</summary>
    public static string Word(ServiceState state) => state switch
    {
        ServiceState.Connected => "Connected",
        ServiceState.SignedIn => "Signed in",
        _ => "Not connected",
    };

    /// <summary>
    /// Google Messages is paired with a phone, not signed in to an account, so its amber state says Paired. The row's label is also what a screen reader
    /// announces, so the two stay in step. <paramref name="identity"/> (here and for Messenger) reads the loaded conversations, so it is asked only for a row
    /// that names an account.
    /// </summary>
    public static IReadOnlyList<ConnectionRow> GoogleMessages(bool connected, bool paired, Func<string?> identity, IEnumerable<ChatAccount> loaded)
    {
        var state = connected ? ServiceState.Connected : paired ? ServiceState.SignedIn : ServiceState.NotConnected;
        var row = state == ServiceState.NotConnected ? NoAccount : Account(identity() ?? "Paired phone", state, state == ServiceState.SignedIn ? "Paired" : null);
        return WithAdded(row, paired, "Google Messages", loaded);
    }

    /// <summary>The line at the foot of Google's card (the last connect's word): a connected card says nothing beyond its row, and one never paired has no
    /// connection to report on.</summary>
    public static bool GoogleStatusLineShown(bool connected, bool paired) => paired && !connected;

    public static IReadOnlyList<ConnectionRow> Messenger(MessengerAccountState state, Func<string?> identity, IEnumerable<ChatAccount> loaded)
    {
        var row = !state.Connected && !state.Saved ? NoAccount : Account(identity() ?? "Facebook account", state.Connected ? ServiceState.Connected : ServiceState.SignedIn);
        return WithAdded(row, state.Saved || state.Connected, "Messenger", loaded);
    }

    /// <summary>
    /// On is not the same as working: the pill and the line under it follow what the helper last said about the encrypted connection, so a dropped one is not
    /// shown as On (2026-10-04); a copy not connected reads it as unknown, which keeps the plain On. Encryption comes on by itself at sign-in, so a card that says
    /// On says nothing (owner, 2026-11-04): it is still a connection of its own that can drop, so the section is there only when something is wrong. Nothing on
    /// the card turns it on; a new sign-in on the server's page does. <paramref name="card"/> is the encrypted connection's words for a state
    /// (MessengerEncryptedStatus.Card), given whether Messenger is signed in.
    /// </summary>
    public static MessengerEncryptedCard MessengerEncrypted(MessengerAccountState state,
        Func<MessengerEncryption, bool, (string Label, bool? Healthy, string Description)> card)
    {
        var signedIn = state.Connected || state.Saved;
        var (word, healthy, line) = card(state.Connected ? state.Encrypted : MessengerEncryption.Unknown, signedIn);
        var enabled = state.EncryptedEnabled;
        return new(enabled, word, healthy, enabled ? line : MessengerEncryptedOff, SectionShown: signedIn && (!enabled || healthy == false),
            AskAgain: enabled && state.Connected);
    }

    /// <summary>Each number is a row of its own; the card says nothing of its own about them.</summary>
    public static IReadOnlyList<ConnectionRow> GoogleVoice(IReadOnlyList<GoogleVoiceAccountState> accounts) => accounts.Count == 0 ? [NoAccount]
        : [.. accounts.Select(account => Account(account.Number ?? "Google Voice account", account.Connected ? ServiceState.Connected : ServiceState.SignedIn, key: account.Key))];

    /// <summary>
    /// Instagram is the one service that cannot be read off a conversation: it does not list the viewer among a thread's participants, so the helper reports
    /// the account from its own viewer record instead.
    /// </summary>
    public static IReadOnlyList<ConnectionRow> Instagram(InstagramAccountState state, IEnumerable<ChatAccount> loaded)
    {
        var row = !state.Connected && !state.Saved ? NoAccount
            : Account(string.IsNullOrWhiteSpace(state.AccountName) ? "Instagram account" : state.AccountName, state.Connected ? ServiceState.Connected : ServiceState.SignedIn);
        return WithAdded(row, state.Saved || state.Connected, "Instagram", loaded);
    }

    /// <summary>One row a profile, in the words <see cref="HermesThreads.StatusWord"/> gives its state.</summary>
    public static IReadOnlyList<ConnectionRow> Hermes(HermesAccountState state) => state.Agents.Count == 0 ? [NoAccount]
        : [.. state.Agents.Select(agent => Account(HermesThreads.Title(agent), agent.Status == "connected" ? ServiceState.Connected : ServiceState.SignedIn,
            HermesThreads.StatusWord(agent.Status), agent.Name))];

    private static ConnectionRow Account(string identity, ServiceState state, string? word = null, string? key = null) =>
        new(identity, state, word ?? Word(state), IsAccount: true, key);

    private static IReadOnlyList<ConnectionRow> WithAdded(ConnectionRow original, bool originalStands, string network, IEnumerable<ChatAccount> loaded)
    {
        var added = loaded.Where(account => account.Network == network && AccountNamespace.KeyOf(account.Id) is not null).ToArray();
        if (added.Length == 0) return [original];
        return [.. (originalStands && original.IsAccount ? [original] : Array.Empty<ConnectionRow>()),
            .. added.Select(account => Account(account.DisplayName, account.Status == "Connected" ? ServiceState.Connected : ServiceState.SignedIn, key: account.Id))];
    }
}
