using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// What a connection scope or a conversation id says about its service. Pure: the prefixes are ServiceTags', the ones
/// every backend qualifies its ids with. A Google Voice scope is its account's own prefix ("gvoice:&lt;account&gt;:").
/// </summary>
public static class ServiceScopes
{
    /// <summary>The service's name for a scope or a conversation id. Anything unrecognised is named Google Messages, as
    /// it always was here, although the server refuses such an id rather than handing it to Google.</summary>
    public static string Name(string scopeOrChatId) => (ServiceTags.Of(scopeOrChatId) ?? ServiceTags.GoogleMessages).Name;

    /// <summary>The connection scope a conversation id (or a scope) belongs to; null when it names no known service.</summary>
    public static string? Scope(string? id)
    {
        if (ServiceTags.Of(id) is not { } tag) return null;
        if (!tag.ScopedByAccount) return tag.Prefix;
        var end = id!.IndexOf(':', tag.Prefix.Length);
        return end >= 0 ? id[..(end + 1)] : null;
    }
}
