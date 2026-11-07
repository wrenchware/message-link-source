namespace NativeBeeper.Core.Messaging;

/// <summary>A stable account slot. The original slot keeps its existing identifiers.</summary>
public sealed class AccountNamespace
{
    public string? Key { get; }
    public AccountNamespace(string? key = null)
    {
        if (key is not null && !ValidKey(key)) throw new ArgumentException("Invalid account key.", nameof(key));
        Key = key;
    }

    public static bool ValidKey(string? key) => key is { Length: 32 } && key.All(c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c));
    // The marker cannot be confused with legacy numeric conversation ids or the helper's media namespaces.
    private string Marker => "account:" + Key + ":";
    public string Qualify(string id)
    {
        if (Key is null || id.Length == 0) return id;
        var colon = id.IndexOf(':');
        if (colon < 0) throw new ArgumentException("An identity must name its service.", nameof(id));
        return id[..(colon + 1)] + Marker + id[(colon + 1)..];
    }
    public string Native(string id)
    {
        var colon = id.IndexOf(':');
        if (colon < 0) throw new ArgumentException("An identity must name its service.", nameof(id));
        var rest = id[(colon + 1)..];
        if (Key is null)
        {
            if (rest.StartsWith("account:", StringComparison.Ordinal)) throw new ArgumentException("Another account owns this identity.");
            return id;
        }
        if (!rest.StartsWith(Marker, StringComparison.Ordinal)) throw new ArgumentException("Another account owns this identity.");
        return id[..(colon + 1)] + rest[Marker.Length..];
    }
    public static string? KeyOf(string id)
    {
        var colon = id.IndexOf(':');
        if (colon < 0 || !id[(colon + 1)..].StartsWith("account:", StringComparison.Ordinal)) return null;
        var rest = id[(colon + 9)..];
        if (rest.Length < 33 || rest[32] != ':' || !ValidKey(rest[..32])) throw new ArgumentException("Invalid account identity.");
        return rest[..32];
    }

    public string? NativeMediaValue(string? value) => value is not null && (value.StartsWith("google-", StringComparison.Ordinal) || value.StartsWith("messenger-media:", StringComparison.Ordinal) || value.StartsWith("instagram-media:", StringComparison.Ordinal)) ? Native(value) : value;
    public NativeBeeper.Core.Services.RealtimeEvent Map(NativeBeeper.Core.Services.RealtimeEvent value) => Key is null ? value : value with
    {
        ChatId = value.ChatId is null ? null : Qualify(value.ChatId),
        Ids = value.Ids?.Select(Qualify).ToArray(), Scope = value.Scope is null ? null : Qualify(value.Scope)
    };
    public MessageAttachment Map(MessageAttachment value) => Key is null ? value : value with
    { Id = Media(value.Id), SourceUrl = Media(value.SourceUrl) };
    public string? Media(string? value) => value is not null && (value.StartsWith("google-media:", StringComparison.Ordinal)
        || value.StartsWith("google-avatar:", StringComparison.Ordinal) || value.StartsWith("google-preview:", StringComparison.Ordinal)
        || value.StartsWith("messenger-media:", StringComparison.Ordinal) || value.StartsWith("instagram-media:", StringComparison.Ordinal))
        ? Qualify(value) : value;
    public ChatSummary Map(ChatSummary value) => Key is null ? value : value with
    {
        Id = Qualify(value.Id), AccountId = Qualify(value.AccountId), AvatarUrl = Media(value.AvatarUrl),
        Preview = value.Preview is null ? null : Map(value.Preview),
        Participants = value.Participants.Select(p => p with { AvatarUrl = Media(p.AvatarUrl) }).ToArray()
    };
    public ChatMessage Map(ChatMessage value) => Key is null ? value : value with
    {
        Id = Qualify(value.Id), ChatId = Qualify(value.ChatId), ReplyToId = value.ReplyToId is null ? null : Qualify(value.ReplyToId),
        PendingId = value.PendingId is null ? null : Qualify(value.PendingId), Attachments = value.Attachments.Select(Map).ToArray()
    };
}
