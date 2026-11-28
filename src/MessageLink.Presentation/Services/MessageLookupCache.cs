using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

// Expendable lookup state, separate from a provider's durable history. A miss is answered by a history read.
public sealed class MessageLookupCache(int capacity = 4000)
{
    private readonly object gate = new();
    private readonly Dictionary<string, (ChatMessage Message, LinkedListNode<string> Node)> entries = [];
    private readonly LinkedList<string> recent = new();
    public int Count { get { lock (gate) return entries.Count; } }
    public void Put(ChatMessage message)
    {
        lock (gate)
        {
            if (entries.Remove(message.Id, out var old)) recent.Remove(old.Node);
            entries[message.Id] = (message, recent.AddLast(message.Id));
            while (entries.Count > Math.Max(1, capacity))
            {
                entries.Remove(recent.First!.Value); recent.RemoveFirst();
            }
        }
    }
    public bool TryGetValue(string id, out ChatMessage message)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(id, out var entry)) { message = null!; return false; }
            recent.Remove(entry.Node); recent.AddLast(entry.Node);
            message = entry.Message; return true;
        }
    }
    public void RemoveWhere(Func<ChatMessage, bool> remove)
    {
        lock (gate)
            foreach (var pair in entries.Where(pair => remove(pair.Value.Message)).ToArray())
            { entries.Remove(pair.Key); recent.Remove(pair.Value.Node); }
    }
}
