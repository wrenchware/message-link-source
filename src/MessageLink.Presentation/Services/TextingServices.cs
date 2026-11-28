using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// Google Messages and Google Voice both text phone numbers, so a number either one knows can be written to through the
/// other. New chat uses that two ways: a person found by one is offered on the other (the phone's contact list can be
/// short, Voice's is the account's Google Contacts), and when every service is searched each number is offered once,
/// on the service the user picked for new texts.
/// </summary>
public static class TextingServices
{
    public const string GoogleMessages = "Google Messages";
    public const string GoogleVoice = "Google Voice";

    public static bool IsTexting(string? network) =>
        string.Equals(network, GoogleMessages, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(network, GoogleVoice, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ChatAccount> Accounts(IEnumerable<ChatAccount> accounts) =>
        [.. accounts.Where(account => IsTexting(account.Network))];

    /// <summary>
    /// Where new texts go: the account the user chose when it is connected, otherwise Google Messages (the phone's own
    /// number), otherwise the first Voice number. Null when nothing that texts is connected.
    /// </summary>
    public static string? Default(string? preferred, IEnumerable<ChatAccount> accounts)
    {
        var texting = Accounts(accounts);
        return texting.FirstOrDefault(account => account.Id == preferred)?.Id
            ?? texting.FirstOrDefault(account => string.Equals(account.Network, GoogleMessages, StringComparison.OrdinalIgnoreCase))?.Id
            ?? texting.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// A number complete enough to text from either service: ten digits or more. A short code or a number missing its
    /// area code stays on the service that knows it, since the other might read it differently.
    /// </summary>
    public static string? Number(ChatDestination destination)
    {
        var phone = !string.IsNullOrWhiteSpace(destination.Phone) ? destination.Phone : destination.Contact?.Phone;
        return NameMatch.Digits(phone).Length >= 10 ? phone!.Trim() : null;
    }

    /// <summary>
    /// The same person on another texting account: a new destination writing to their number there, carrying that
    /// account's own conversation with them when one is already here. Anything else is returned as it was.
    /// </summary>
    public static ChatDestination Retarget(ChatDestination destination, string? target, string targetNetwork,
        Func<ContactPerson, ChatSummary?>? existing)
    {
        if (target is null || destination.AccountId == target || !IsTexting(destination.Network) || !IsTexting(targetNetwork) ||
            Number(destination) is not { } phone) return destination;
        var contact = new ContactPerson(target, phone, destination.Name, null, phone, null, false, AvatarUrl: destination.AvatarUrl);
        var chat = existing?.Invoke(contact);
        // Only a conversation that is somewhere to write to in its own right, as for Frequent.
        if (chat is { IsArchived: true } or { IsSpam: true } || chat?.Type == "group") chat = null;
        return new ChatDestination(target, targetNetwork, phone, destination.Name, destination.AvatarUrl ?? chat?.AvatarUrl, phone, chat, contact);
    }
}

/// <summary>
/// Destinations already offered: the same person on the same service, the same conversation reached another way, or the
/// same number on the same service however it was written.
/// </summary>
public sealed class OfferedDestinations
{
    private readonly HashSet<(string, string)> keys = [];
    private readonly HashSet<string> conversations = new(StringComparer.Ordinal);
    private readonly HashSet<(string, string)> numbers = [];

    public OfferedDestinations(IEnumerable<ChatDestination>? already = null)
    {
        foreach (var destination in already ?? []) Add(destination);
    }

    /// <summary>Records the destination, and says whether it was new.</summary>
    public bool Add(ChatDestination destination)
    {
        var number = TextingServices.Number(destination) is { } phone ? (destination.AccountId, NameMatch.PhoneKey(phone)) : default;
        if (keys.Contains(destination.Key) || destination.Conversation?.Id is { } seen && conversations.Contains(seen) ||
            number != default && numbers.Contains(number)) return false;
        keys.Add(destination.Key);
        if (destination.Conversation?.Id is { } id) conversations.Add(id);
        if (number != default) numbers.Add(number);
        return true;
    }
}
