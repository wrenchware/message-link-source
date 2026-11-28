using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>What a client states with <c>client.state</c>, put together from the four things it holds. One place, so every client words them the same way.</summary>
public static class ClientStateReport
{
    /// <param name="present">Some window is shown and not minimized, and the session is not locked (<c>AppPresence</c>).</param>
    /// <param name="busy">A dialog is open (<c>AppBusy</c>).</param>
    /// <param name="reading">The choice private reading is read from; its services go as their words.</param>
    /// <param name="linkPreviews">The link-previews setting.</param>
    /// <param name="active">Someone is at this PC and Messages would show them a banner (<c>UserPresence.BannersHere</c>); null from a client that cannot tell.</param>
    public static ClientStateParams Of(bool present, bool busy, PrivateReading reading, bool linkPreviews, bool? active = null) =>
        new(present, busy, [.. PrivateReading.All.Where(reading.IsOn).Select(PrivateReading.Word)], linkPreviews, active);
}
