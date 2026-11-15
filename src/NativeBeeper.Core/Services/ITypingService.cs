namespace NativeBeeper.Core.Services;

public sealed record TypingParticipant(string UserId, string DisplayName, DateTimeOffset ExpiresAt,
    // What the other end is doing instead of typing, in a few words, where the service says (Hermes: the tool it is using).
    // Null is plain typing. Appended, and written only when set.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Status = null);
public sealed record TypingUpdate(string ChatId, IReadOnlyList<TypingParticipant> Participants);

public interface ITypingService
{
    bool CanReceive { get; }
    bool CanSend { get; }
    IAsyncEnumerable<TypingUpdate> WatchAsync(CancellationToken cancellationToken = default);
    Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken = default);
}

/// <summary>
/// A typing service whose caller says, as an argument, whether the call came through <c>PrivateReadingGate</c>; see
/// <see cref="IAdmittedReadStateWriter"/>. The plain <see cref="ITypingService.SetTypingAsync"/> is not admitted.
/// </summary>
public interface IAdmittedTypingService : ITypingService
{
    Task SetTypingAsync(string chatId, bool isTyping, bool admitted, CancellationToken cancellationToken = default);
}
