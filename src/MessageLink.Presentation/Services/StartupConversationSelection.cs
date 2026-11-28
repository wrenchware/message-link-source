namespace MessageLink.Presentation.Services;

// A normal launch may select the first settled inbox row once. Explicit navigation wins,
// and a background notification activation never arms this policy.
public sealed class StartupConversationSelection
{
    private bool enabled, ready, finished;
    public bool Pending => enabled && !finished;
    public void Enable() { if (!finished) enabled = true; }
    public void Cancel() { finished = true; }
    public void InitialLoadCompleted() { ready = true; }

    public string? TakeFirst(IEnumerable<string> orderedChatIds, bool visibleAndActive, bool busy, bool blocked)
    {
        if (!Pending || !ready || !visibleAndActive || busy || blocked) return null;
        // An empty first inbox keeps the empty state. A later sign-in is user-driven work,
        // not another chance to silently open a conversation.
        finished = true;
        return orderedChatIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
    }
}
