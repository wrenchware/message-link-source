namespace NativeBeeper.Core.Services;

/// <summary>
/// Hermes Agent (Nous Research), reached by the server over Hermes's own chat-completions API server: one thread a Hermes
/// profile, each profile with its own key. There is no sign-in; the key is static.
/// </summary>
/// <remarks>
/// The owner's privacy line (2026-11-14): Hermes only ever gets what the person types or sends in its own thread. Nothing here
/// or anywhere else forwards another conversation's content to it, and it has no way to send through the other services.
/// </remarks>
public static class HermesIds
{
    public const string Network = "Hermes";
    public const string Scope = "hermes:";
    public const string MediaPrefix = "hermes-media:";

    public static bool Owns(string? id) => id?.StartsWith(Scope, StringComparison.Ordinal) == true;
    public static string Chat(string profile) => Scope + profile;

    /// <summary>The profile a chat or message id belongs to: <c>hermes:&lt;profile&gt;</c> or <c>hermes:&lt;profile&gt;:&lt;message&gt;</c>.</summary>
    public static string? ProfileOf(string? id)
    {
        if (!Owns(id)) return null;
        var rest = id![Scope.Length..];
        var colon = rest.IndexOf(':');
        var profile = colon < 0 ? rest : rest[..colon];
        return profile.Length > 0 ? profile : null;
    }
}

/// <summary>How one Hermes profile stands (its name is the profile's; its thread is <see cref="HermesIds.Chat"/> of it). Status is one fixed word: <c>unchecked</c>, <c>connected</c>, <c>unreachable</c>,
/// <c>key_rejected</c> or <c>no_profile</c>.</summary>
public sealed record HermesAgentState(string Name, string? Title, string Status, bool HasKey = true);

/// <summary>
/// What a client may know of Hermes: the address, and each profile's name, title and state. Never a key.
/// <paramref name="CanConfigure"/>: this host takes the address and profiles from this client (an app running its own services).
/// A server takes them on its own page, where every account is set up.
/// </summary>
public sealed record HermesAccountState(string? Address, IReadOnlyList<HermesAgentState> Agents, bool CanConfigure = false)
{
    public static HermesAccountState None { get; } = new(null, []);
}

/// <summary>A profile as entered: a key or <paramref name="Title"/> of null keeps the one already saved for that profile. A blank title names the thread
/// after the profile ("Hermes" for the default, HermesThreads.DefaultTitle); a profile name is lowercase with no spaces, so the owner may give the thread one of their own.</summary>
public sealed record HermesProfileInput(string Name, string? Key, string? Title = null)
{
    public override string ToString() => $"HermesProfileInput {{ Name = {Name}, Key = {(Key is { Length: > 0 } ? "set" : "none")} }}";
}

public interface IHermesAccount
{
    HermesAccountState State { get; }
    event EventHandler? Changed;
    /// <summary>Saves the address and profiles, then checks each. Throws <see cref="ArgumentException"/> with a sentence for a person
    /// for an address or profile that cannot be used.</summary>
    Task ConfigureAsync(string? address, IReadOnlyList<HermesProfileInput> profiles, CancellationToken cancellationToken = default);
    /// <summary>Starts the thread over on a fresh Hermes session. The scrollback stays; a line marks where the new conversation begins.</summary>
    Task NewConversationAsync(string chatId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Answers a request Hermes is holding (the thread's approval message, <see cref="NativeBeeper.Core.Messaging.ChatMessage.Approval"/>): approve runs it once,
    /// anything else denies it. Never "for this session" or "always": those reach past the one command shown. Answers how the request stands afterwards
    /// (<see cref="NativeBeeper.Core.Messaging.AgentApprovalStates"/>); throws <see cref="ArgumentException"/> with a sentence for a person when it can't be sent.
    /// </summary>
    Task<string> AnswerApprovalAsync(string chatId, string messageId, bool approve, CancellationToken cancellationToken = default);
}

/// <summary>The server's repeats (<c>HostRepeats</c>) send their prompt this way, so the thread shows it as a small "Scheduled" label.</summary>
public interface IHermesPrompts
{
    bool Accepts(string chatId);
    Task<SendReceipt> SendScheduledAsync(string chatId, string text, CancellationToken cancellationToken = default);
}
