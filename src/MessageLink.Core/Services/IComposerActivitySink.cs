namespace MessageLink.Core.Services;

/// <summary>
/// The composer was edited. What the backends do with it is theirs: Messenger and Instagram put off the typing stop for a conversation
/// typing was sent to, Google yields its background history indexing, and the shared composer clock lets a synchronization apply stand out of
/// the way. It never starts typing and sends nothing to a service, so it needs no private-reading admission.
/// </summary>
public interface IComposerActivitySink
{
    void NoteComposerActivity(string? chatId);
}
