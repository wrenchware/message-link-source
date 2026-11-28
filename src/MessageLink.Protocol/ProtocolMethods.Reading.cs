using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// What was still direct after the account actions: mark read and typing out with their private-reading admission,
// link previews, and a history window with its later refresh. Typing in is not a method: it is the `typing` kind of the event feed.
//
//   Admission. PrivateReadingGate admits a read or a typing start with an AsyncLocal that cannot cross the dispatcher, so the request
//   carries `admitted` and the server applies it before its router decides (IAdmittedReadStateWriter, PrivateReadingServerReadState). The
//   client sets it only from the gate. A request that leaves it out is not admitted. `admitted` alone does not make the server tell a service about
//   a conversation it reads privately: the client's view of the choice can be behind another device's. The server admits only a read that also
//   says `explicit` (the person asked for it by name; the gate's explicit writer sets it), and decides every other read, and every typing start,
//   by its own view (ServerClientState). A request from before `explicit` is therefore automatic. The server still trusts its authenticated client
//   that an explicit read is one.
//
//   Not journaled. A read marker is a watermark: sending it again leaves the conversation as read as it was, and the caller already sends
//   one for each time the conversation is seen. Typing is a state that expires on its own. An interrupted call is therefore an ordinary
//   failure (unavailable), and the next view of the conversation marks it read again.
//
//   Restarting a helper (host.restartHelper). Not journaled either: it is not a send, and a repeat only restarts again, which the server
//   paces (one at a time per helper, Instagram's recovery budget). Every refusal and failure is the result's own outcome and fixed code word,
//   never an error, so the window words them as it always has. Sends still in flight after the server's wait end the first call as `asking`
//   with nothing stopped; the person's yes is a second call with `force`, which does not wait again. The sign-in windows are the client's,
//   so `signingIn` is the client's say-so, and it must be stated.
//
//   A window's refresh. HistoryWindow.Refreshed is a Task inside the result. The window comes back with a `refresh` token when the server
//   has fresher copies on their way, and `history.windowRefresh` is the call that waits for them: it answers once, with the rows, and the
//   token is spent. A call, not a pushed event, so it has an owner (it fails with the connection and can be cancelled), is not stored
//   in the resumable log, and goes to nobody but the caller.
public static partial class ProtocolMethods
{
    public const string ChatsMarkRead = "chats.markRead";
    public const string TypingSet = "typing.set";
    /// <summary>The composer was edited. Fire and forget, throttled on the client to one call per second per conversation; not journaled.</summary>
    public const string TypingNoteActivity = "typing.noteActivity";
    public const string MessagesLoadLinkPreviews = "messages.loadLinkPreviews";
    public const string HistoryWindow = "history.window";
    public const string HistoryWindowRefresh = "history.windowRefresh";
    /// <summary>Connection health's Restart for one helper. Not journaled: it is not a send, and a repeat only restarts again, which the server paces and refuses (see <see cref="HelperRestartParams"/>).</summary>
    public const string ServerRestartHelper = "host.restartHelper";

    private static Entry[] ReadingEntries() =>
    [
        new(ChatsMarkRead, "IReadStateWriter.MarkReadAsync", typeof(MarkReadParams), typeof(Done), ProtocolVersion.Original),
        new(TypingSet, "ITypingService.SetTypingAsync", typeof(TypingSetParams), typeof(Done), ProtocolVersion.Original),
        new(TypingNoteActivity, "IComposerActivitySink.NoteComposerActivity", typeof(ChatIdParams), typeof(Done), ProtocolVersion.Original),
        new(MessagesLoadLinkPreviews, "ILinkPreviewLoader.LoadLinkPreviewsAsync", typeof(LinkPreviewsParams), typeof(Done), ProtocolVersion.Original),
        new(HistoryWindow, "IHistoryWindowReader.ReadWindowAsync", typeof(HistoryWindowParams), typeof(HistoryWindowReply), ProtocolVersion.Original),
        new(HistoryWindowRefresh, "HistoryWindow.Refreshed", typeof(WindowRefreshParams), typeof(WindowRefreshed), ProtocolVersion.Original),
        new(ServerRestartHelper, "IHelperRestarter.RestartAsync", typeof(HelperRestartParams), typeof(HelperRestartResult), ProtocolVersion.Original),
    ];
}

/// <summary>
/// <paramref name="Scope"/> names the helper (<c>google-direct:</c>, <c>messenger:</c>, <c>instagram:</c>, <c>gvoice:&lt;key&gt;:</c>).
/// <paramref name="SigningIn"/> is the caller's say-so that it is signing in to that service itself, which refuses the restart; it must be
/// stated, because a flag left out would read as "not signing in", and stated afresh in every call. <paramref name="Settle"/> asks only for
/// the wait for sends and stops nothing; without it the call stops the helper when nothing is in flight. <paramref name="Force"/> is the
/// person's yes to restarting under sends still in flight, after an earlier call answered <c>asking</c>; it counts only with that answer's
/// <paramref name="Token"/>.
/// </summary>
public sealed record HelperRestartParams(string Scope, bool SigningIn, bool Force = false, string? Token = null, bool Settle = false);

/// <summary>
/// <paramref name="Admitted"/>: the client's gate let this read through. Absent means no. <paramref name="Explicit"/>: the person asked for this
/// read by name (the gate's explicit writer). Absent means no, which is how an older client's read arrives: the server treats it as automatic and
/// admits only an explicit one, so what an older client loses is at worst a receipt asked for by name for a conversation the server reads privately.
/// </summary>
public sealed record MarkReadParams(string ChatId, string MessageId, bool Admitted = false, bool Explicit = false);

/// <summary><paramref name="Admitted"/>: the client's gate let this call through. Absent means no.</summary>
public sealed record TypingSetParams(string ChatId, bool IsTyping, bool Admitted = false);

public sealed record LinkPreviewsParams(string ChatId, IReadOnlyList<string> MessageIds);

public sealed record HistoryWindowParams(string ChatId, DateTimeOffset At, int Older, int Newer);

/// <summary>A history window. <paramref name="Refresh"/> is a token for <see cref="ProtocolMethods.HistoryWindowRefresh"/> when fresher copies of some rows are on their way.</summary>
public sealed record HistoryWindowReply(IReadOnlyList<ChatMessage> Messages, bool MoreOlder, bool MoreNewer, string? Refresh = null);

public sealed record WindowRefreshParams(string Refresh);

/// <summary>The fresher rows, or none: a token that is spent, unknown or too old has nothing to say.</summary>
public sealed record WindowRefreshed(IReadOnlyList<ChatMessage> Messages);
