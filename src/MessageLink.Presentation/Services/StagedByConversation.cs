using System.Diagnostics.CodeAnalysis;

namespace MessageLink.Presentation.Services;

// What is staged in a composer, by conversation. A Messenger conversation's is also by account (DraftOwners): the same thread
// under another account is another entry, so photos staged as one account are not offered to, or sent as, the one signed
// in now, and are there again when the first is back. With the account not known there is nothing to find and nowhere to
// put anything (CanHold), so nothing staged then can reach an account that did not stage it.
public sealed class StagedByConversation<T>(DraftOwners? owners)
{
    private readonly Dictionary<string, T> items = new(StringComparer.Ordinal);

    private string? KeyOf(string chatId)
    {
        if (owners is null || !DraftOwners.Partitioned(chatId)) return chatId;
        return owners.Messenger is { } scope ? chatId + "\nowner:" + scope : null;
    }

    public bool CanHold(string chatId) => KeyOf(chatId) is not null;
    public bool TryGetValue(string chatId, [MaybeNullWhen(false)] out T value)
    {
        value = default;
        return KeyOf(chatId) is { } key && items.TryGetValue(key, out value);
    }
    public bool ContainsKey(string chatId) => KeyOf(chatId) is { } key && items.ContainsKey(key);
    public bool Remove(string chatId) => KeyOf(chatId) is { } key && items.Remove(key);
    public bool Remove(string chatId, [MaybeNullWhen(false)] out T value)
    {
        value = default;
        return KeyOf(chatId) is { } key && items.Remove(key, out value);
    }
    public T this[string chatId]
    {
        set => items[KeyOf(chatId) ?? throw new InvalidOperationException("The account is not known.")] = value;
    }
    // An entry let go by the very list it holds, whichever account is signed in now: what a send took out of the strip is taken
    // out of that list, not out of whatever the lookup finds for the account as it reads at that moment (it may read none, or
    // another, while the upload runs), which left the photos just sent to be staged again and sent twice.
    public bool RemoveInstance(T value)
    {
        var found = items.Where(pair => ReferenceEquals(pair.Value, value)).Select(pair => pair.Key).ToArray();
        foreach (var key in found) items.Remove(key);
        return found.Length > 0;
    }
    public void Clear() => items.Clear();
    // Entries for every account, so the limits on how much may be staged hold across a change of account.
    public int Count => items.Count;
    public IEnumerable<T> Values => items.Values;
}
