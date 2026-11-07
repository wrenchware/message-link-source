using System.Text.Json;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

// Group chats are created by the phone (helper ui.startGroup, as upstream CreateGroup): all member numbers and an
// optional name, which applies only when the phone creates an RCS group. SMS/MMS groups have no name.
internal sealed partial class GoogleMessagesBackend : IGroupBackend
{
    private const int GroupMaximum = 20, GroupNameMaximum = 100;

    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken = default)
        => Task.FromResult(accountId == Models.Account
            ? new GroupOptions(true, "Choose one person to start a chat, or several for a group.", Maximum: GroupMaximum, NameAllowed: true, NameMaximum: GroupNameMaximum)
            : new GroupOptions(false, "Choose the Google Messages account."));

    public async Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken = default)
    {
        var phones = people.Select(p => GoogleDirectory.Phone(p.Phone)).ToArray();
        if (accountId != Models.Account || people.Count is < 2 or > GroupMaximum || people.Any(p => p.AccountId != accountId || p.CannotMessage) ||
            phones.Any(p => p is null) || phones.Distinct(StringComparer.Ordinal).Count() != phones.Length || title.Trim().Length > GroupNameMaximum)
            throw new SendException(false, $"Choose 2–{GroupMaximum} different people with valid phone numbers.");
        await ConnectAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        GoogleChat chat;
        try
        {
            var result = await MediaCallAsync("ui.startGroup", new { phones, name = title.Trim() }, cancellationToken);
            if (result.GetProperty("outcome").GetString() != "accepted" ||
                result.GetProperty("chat").Deserialize<GoogleChat>(Json) is not { Id.Length: > 0 } created)
                throw new SendException(true, "The group may have been created. Check your conversations before trying again.");
            chat = created;
        }
        catch (SendException) { throw; }
        catch { throw new SendException(true, "The group may have been created. Check your conversations before trying again."); }
        SetChat(chat);
        ArchiveChats([chat]);
        Publish(new(RealtimeKind.ChatChanged, Models.Qualify(chat.Id)));
        return Models.Map(chat);
    }
}
