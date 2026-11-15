using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

// Hermes Agent on the wire (protocol 31). Its state travels in HostState.Hermes (the address, each profile's name, title and state; never a key).
//
//   accounts.hermes.configure       the address and profiles with their keys. Page-only on a server, where every account is set up (the server's page has
//                                   the form); an app that runs its own services takes it from its own Settings. The keys cross only as this mutation's
//                                   parameters: the journal keeps a hash, never the parameters.
//   accounts.hermes.newConversation a Hermes thread starts over on a fresh Hermes session; the scrollback stays.
//   repeats.create / repeats.cancel a repeating scheduled send, Hermes threads only (HostRepeats). The rows are the host's own (repeat:).
//
// The owner's privacy line (2026-11-14): nothing here carries another conversation's content to Hermes, and nothing lets Hermes send anywhere.
public static partial class ProtocolMethods
{
    public const string AccountsHermesConfigure = "accounts.hermes.configure";
    public const string AccountsHermesNewConversation = "accounts.hermes.newConversation";
    public const string RepeatsCreate = "repeats.create";
    public const string RepeatsCancel = "repeats.cancel";

    private static Entry[] HermesEntries() =>
    [
        new(AccountsHermesConfigure, "IHermesAccount.ConfigureAsync", typeof(HermesConfigureParams), typeof(Done), true),
        new(AccountsHermesNewConversation, "IHermesAccount.NewConversationAsync", typeof(HermesChatParams), typeof(Done), true),
        new(RepeatsCreate, "IRepeatCommands.CreateAsync", typeof(RepeatCreateParams), typeof(RepeatAnswer), true),
        new(RepeatsCancel, "IRepeatCommands.CancelAsync", typeof(RepeatCancelParams), typeof(RepeatAnswer), true),
    ];
}

public sealed record HermesConfigureParams(string ClientOpId, string? Address, IReadOnlyList<HermesProfileInput> Profiles) : MutationParams(ClientOpId);
public sealed record HermesChatParams(string ClientOpId, string ChatId) : MutationParams(ClientOpId);

/// <summary>A new repeat: the prompt, the weekdays (bit 0 Sunday) and the minute after local midnight, in the server's own time zone.</summary>
public sealed record RepeatCreateParams(string ClientOpId, string ChatId, string Text, int Days, int Minute) : MutationParams(ClientOpId);
public sealed record RepeatCancelParams(string ClientOpId, string Id) : MutationParams(ClientOpId);

/// <summary>Status: <c>done</c>, or <c>refused</c> with a sentence for a person (not a Hermes thread, too many, an empty prompt).</summary>
public sealed record RepeatAnswer(string Status, string? Id = null, string? Message = null, DateTimeOffset? NextDue = null);

public static class RepeatStatuses
{
    public const string Done = "done";
    public const string Refused = "refused";
}

/// <summary>The server's repeats. A host that keeps none has no port and answers <c>not_supported</c>.</summary>
public interface IRepeatCommands
{
    Task<RepeatAnswer> CreateAsync(RepeatCreateParams request, CancellationToken cancellationToken = default);
    Task<RepeatAnswer> CancelAsync(RepeatCancelParams request, CancellationToken cancellationToken = default);
}

/// <summary>Repeats over the wire: journaled mutations, never retried.</summary>
public sealed class RepeatsProxy(ProtocolClient client) : IRepeatCommands
{
    public Task<RepeatAnswer> CreateAsync(RepeatCreateParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<RepeatCreateParams, RepeatAnswer>(ProtocolMethods.RepeatsCreate, request, cancellationToken);
    public Task<RepeatAnswer> CancelAsync(RepeatCancelParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<RepeatCancelParams, RepeatAnswer>(ProtocolMethods.RepeatsCancel, request, cancellationToken);
}

/// <summary>Hermes's account over the wire: the state from the host's pushed state, New conversation and (on a host that takes it) the settings.</summary>
public sealed class HermesAccountProxy(ProtocolClient client, HostStateCache cache) : IHermesAccount
{
    public HermesAccountState State => cache.Hermes;
    public event EventHandler? Changed
    {
        add => cache.HermesChanged += value;
        remove => cache.HermesChanged -= value;
    }
    public Task ConfigureAsync(string? address, IReadOnlyList<HermesProfileInput> profiles, CancellationToken cancellationToken = default) =>
        AccountWire.MutateAsync<HermesConfigureParams, Done>(client, ProtocolMethods.AccountsHermesConfigure, new(AccountWire.OperationId(), address, profiles), cancellationToken);
    public Task NewConversationAsync(string chatId, CancellationToken cancellationToken = default) =>
        AccountWire.MutateAsync<HermesChatParams, Done>(client, ProtocolMethods.AccountsHermesNewConversation, new(AccountWire.OperationId(), chatId), cancellationToken);
}
