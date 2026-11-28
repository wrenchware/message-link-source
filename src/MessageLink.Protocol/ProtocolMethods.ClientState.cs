namespace MessageLink.Protocol;

// What only a client knows and a server's own code acts on. Today the server's code reads these from in-process objects of the
// window; a server in another process cannot, so the client states them.
//
//   presence       Some Messages window is shown and not minimized and the session is not locked (AppPresence). Instagram's poll slows while false.
//   busy           A dialog is open in the main window (MainWindow.dialogOpen). The Google sign-in keeper puts a renewal off while true.
//   privateReading The services (the words PrivateReading.Word gives) whose conversations are read here without telling the service. The router's
//                  safety net refuses an unadmitted read or typing start for these.
//   linkPreviews   The "load link previews" setting: false means no page is fetched for a link the host would otherwise draw a card for.
//   active         Optional, a PC's: someone is at it and Messages there would show a new message as a banner (notifications on, not paused,
//                  input in the last 3 minutes, not locked). A phone's notification then arrives without a sound (PushNotifier). Left out by a
//                  client that cannot tell, and by every client before it, which reads as no.
//
// Idempotent and latest-wins: the client sends it once after connecting, again after every reconnect, and whenever any of these changes; the
// server keeps the newest per connection and nothing is journaled. The first four must be stated: a field left out would otherwise read as its default
// and could loosen what the person chose, so a request that omits one is refused and the connection's previous state stands.
//
// The host combines its connections (ServerClientState): present and busy if any is, a service private if any connection says so, link previews
// only if every connection allows them. A connection that has said nothing yet adds nothing; with none the server keeps the last it knew.
public static partial class ProtocolMethods
{
    public const string ClientStateSet = "client.state";

    private static Entry[] ClientStateEntries() =>
    [
        new(ClientStateSet, "IClientStateSink.Apply", typeof(ClientStateParams), typeof(Done), ProtocolVersion.Original),
    ];
}

/// <summary>What a client says about itself (see <see cref="ProtocolMethods.ClientStateSet"/>). <paramref name="PrivateReading"/> holds service words; one the server does not know is ignored.</summary>
public sealed record ClientStateParams(bool Presence, bool Busy, IReadOnlyList<string> PrivateReading, bool LinkPreviews, bool? Active = null);

/// <summary>Where a dispatcher puts what a connection states. <paramref name="connection"/> names the connection and is only ever compared by reference.</summary>
public interface IClientStateSink
{
    void Apply(object connection, ClientStateParams state);

    /// <summary>The connection ended: what it said no longer counts.</summary>
    void Disconnected(object connection);
}
