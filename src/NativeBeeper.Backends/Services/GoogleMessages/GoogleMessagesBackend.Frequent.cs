using System.Text.Json;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

// Frequent contacts for New chat, from the phone's own ranking (the helper's ui.topContacts, libgm ListTopContacts). A
// read that creates, sends and marks nothing. It is asked for at most once per connection: the answer is kept until the
// helper is replaced, New chat asks at most once per opening, and nothing polls it.
internal sealed partial class GoogleMessagesBackend : IFrequentContacts
{
    private readonly object frequentLock = new();
    private Task<IReadOnlyList<ContactPerson>>? frequentLoad;
    private object? frequentConnection;

    public async Task<IReadOnlyList<ContactPerson>?> GetFrequentContactsAsync(string accountId, CancellationToken cancellationToken = default)
    {
        // Never connects just for this: a New chat opened while Google Messages is down simply has no Frequent section.
        if (accountId != Models.Account || !connected || helper is not { } owner) return null;
        Task<IReadOnlyList<ContactPerson>> load;
        lock (frequentLock)
        {
            // A failed read is asked again next time New chat opens, never in a loop.
            if (!ReferenceEquals(frequentConnection, owner) || frequentLoad is { IsFaulted: true } or { IsCanceled: true })
            {
                frequentConnection = owner;
                frequentLoad = LoadFrequentAsync();
            }
            load = frequentLoad!;
        }
        try { return await load.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private async Task<IReadOnlyList<ContactPerson>> LoadFrequentAsync()
    {
        // Not tied to whoever asked first: the answer is kept for the connection, so a New chat closed while it was on
        // its way still leaves it for the next one.
        var result = await ReadAsync("ui.topContacts", null, CancellationToken.None);
        var people = GoogleDirectory.Frequent(result.Deserialize<GoogleContact[]>(Json) ?? []);
        GoogleMessagesLog.Write($"top contacts returned={people.Count}");
        return people.Select(person => person with { AccountId = Models.Account, AvatarUrl = Slot.Ids.Media(person.AvatarUrl) }).ToArray();
    }
}
