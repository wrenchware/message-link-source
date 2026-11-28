using MessageLink.Core.Messaging;

namespace MessageLink.Core.Services;

/// <summary>
/// One service as its ids and its name say it: the prefix every conversation id, account id and connection scope of
/// that service begins with (an added account's ids keep it: <c>messenger:account:&lt;key&gt;:…</c>), and the name a
/// person reads. The server's lookup pairs each with the service that answers for it (ConversationOwners).
/// </summary>
/// <param name="ScopedByAccount">Whether each account is its own connection scope (<c>gvoice:&lt;key&gt;:</c>) rather than the
/// prefix alone being one.</param>
/// <param name="HasHelper">Whether a helper process connects it, so Connection health has a card for it.</param>
/// <param name="AddedAccounts">Whether an added account (<see cref="AccountNamespace"/>) can belong to it, so the account's slot ending takes its conversations there too.</param>
public sealed record ServiceTag(string Prefix, string Name, bool ScopedByAccount = false, bool HasHelper = true, bool AddedAccounts = false)
{
    public bool Owns(string? id) => id?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}

/// <summary>Every service there is, in the order the guide and Settings name them.</summary>
public static class ServiceTags
{
    public static readonly ServiceTag GoogleMessages = new("google-direct:", "Google Messages", AddedAccounts: true);
    public static readonly ServiceTag Messenger = new("messenger:", "Messenger", AddedAccounts: true);
    // Voice's accounts are each their own scope by key, and end by it, never by a slot.
    public static readonly ServiceTag GoogleVoice = new(GoogleVoiceIds.Prefix, GoogleVoiceIds.Network, ScopedByAccount: true);
    public static readonly ServiceTag Instagram = new("instagram:", "Instagram", AddedAccounts: true);
    public static readonly ServiceTag Hermes = new(HermesIds.Scope, HermesIds.Network, HasHelper: false);

    public static IReadOnlyList<ServiceTag> All { get; } = [GoogleMessages, Messenger, GoogleVoice, Instagram, Hermes];

    /// <summary>The service whose prefix the id carries; null when none does.</summary>
    public static ServiceTag? Of(string? id)
    {
        foreach (var tag in All) if (tag.Owns(id)) return tag;
        return null;
    }

    /// <summary>
    /// The conversation id prefixes an ended account owned (<c>ProtocolServices.AccountEnded</c>): an added account's slot (<c>account-slot</c> with its
    /// key) in every service it can belong to, <c>messenger</c> or <c>instagram</c> signed out, a Voice number by its key, Google Messages' local data reset
    /// (<c>google-reset</c>), or an account id that carries its own key. None for anything else.
    /// </summary>
    public static IReadOnlyList<string> EndedPrefixes(string account, string? key)
    {
        if (account == "account-slot")
            return AccountNamespace.ValidKey(key) ? [.. All.Where(tag => tag.AddedAccounts).Select(tag => new AccountNamespace(key).Qualify(tag.Prefix))] : [];
        string? prefix = account switch
        {
            "messenger" => Messenger.Prefix,
            "instagram" => Instagram.Prefix,
            "voice" when !string.IsNullOrEmpty(key) => GoogleVoiceIds.Account(key) + ":",
            "google-reset" => GoogleMessages.Prefix,
            _ => AccountNamespace.KeyOf(account) is not null ? account : null,
        };
        return prefix is null ? [] : [prefix];
    }

    /// <summary>
    /// Whether a conversation belongs to an ended prefix from <see cref="EndedPrefixes"/>: a service's own prefix is its first account's, which an added
    /// account's conversations share but aren't part of.
    /// </summary>
    public static bool EndedOwns(string prefix, string id) =>
        id.StartsWith(prefix, StringComparison.Ordinal) && (AccountNamespace.KeyOf(prefix) is not null || AccountNamespace.KeyOf(id) is null);

    /// <summary>
    /// What a log line may say about an id no service owns: its leading word and colon when that is a short word of
    /// lowercase letters, digits and dashes (<c>merged:</c>, <c>synthetic:</c>), otherwise <c>none</c>. Never the id.
    /// </summary>
    public static string LogPrefix(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "empty";
        var colon = id.IndexOf(':');
        if (colon is < 1 or > 24) return "none";
        for (var index = 0; index < colon; index++)
            if (id[index] is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) return "none";
        return id[..(colon + 1)];
    }
}
