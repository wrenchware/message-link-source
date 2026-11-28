using MessageLink.Presentation.ViewModels;

namespace MessageLink.Presentation.Services;

public static class ComposeTransport
{
    // These are expected routes, never a claim about an earlier message's transport.
    public static string Label(string? expected, bool hasAttachment, bool connected) => !connected ? "" : expected switch
    {
        "RCS" => "RCS",
        "SMS" or "SMS/MMS" when hasAttachment => "MMS",
        "SMS" => "SMS",
        "SMS/MMS" => "SMS/MMS",
        _ => ""
    };
    // One shape for the empty box everywhere (owner, 2026-11-16): "Message", then the route after a dot where there is one
    // ("Message · RCS"). With a photo attached the text is its caption: "Add a caption · RCS".
    public static string Placeholder(string label, bool attachment) =>
        (attachment ? "Add a caption" : "Message") + (label.Length == 0 ? "" : " · " + label);

    // A linked conversation's empty box names the route as the service picker does once typing starts
    // ("Google Messages · RCS", "Messenger"), plus what tells it from another route of the same service: the number,
    // else the kind ("Messenger · Encrypted") where no RCS or SMS already says it. A photo puts "Add a caption" in front.
    public static string Placeholder(ConversationMember member, IEnumerable<ConversationMember> routes, string label, bool attachment)
    {
        var apart = member.NumberApart(routes) ?? (label.Length > 0 ? null : member.DistinctDetail(routes));
        var route = string.Join(" · ", new[] { member.DisplayRoute, apart, label.Length > 0 ? label : null }.OfType<string>());
        return attachment ? "Add a caption · " + route : route;
    }
    // Outgoing bubble color family. Unrecognized routes stay null and keep the palette color.
    public static string? BubbleFamily(string? transport) => transport switch
    {
        "RCS" => "RCS",
        "SMS" or "MMS" or "SMS/MMS" => "SMS",
        _ => null
    };
}
