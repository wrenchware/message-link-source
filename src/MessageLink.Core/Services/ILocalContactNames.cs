namespace MessageLink.Core.Services;

/// <summary>
/// Names this PC keeps for people the provider has no name for. A conversation with an unnamed counterpart shows a
/// bare phone number or handle, and this is the way to call it something.
/// </summary>
/// <remarks>
/// Local only, and deliberately so: writing a name back to Google Contacts is a different piece of work needing
/// OAuth with a write scope this app holds nowhere else, and it would mutate the real account. A name kept here
/// changes nothing outside this computer.
///
/// The identity is the one local contact photos already use, so a name follows a person across their one-to-one
/// conversations and a group keeps a name of its own.
/// </remarks>
public interface ILocalContactNames
{
    /// <summary>The name kept for this identity, or null when there is none.</summary>
    Task<string?> GetAsync(string profile, string identity, CancellationToken cancellationToken = default);

    /// <summary>Keeps a name, or forgets it when the name is empty.</summary>
    Task SetAsync(string profile, string identity, string? name, CancellationToken cancellationToken = default);
}
