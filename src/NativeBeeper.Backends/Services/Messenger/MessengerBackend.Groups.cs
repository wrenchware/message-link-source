using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using System.Text.Json;

namespace NativeBeeper.App.Services.Messenger;

internal sealed record MessengerCreatedGroup(MessengerChat? Chat, string ThreadId, bool Named);

/// <summary>
/// Creating a Messenger group conversation.
/// </summary>
/// <remarks>
/// This is the ordinary Lightspeed path, which is the only one Messenger offers for making a group: there is no
/// encrypted create-group task the way there is for a one-to-one, where the helper creates both an ordinary thread
/// and an encrypted one. A group made here is therefore an ordinary Messenger group rather than an end-to-end
/// encrypted one, which is what the account's existing groups already are.
///
/// Messenger has no name field on the create, so naming is a second task against the thread it answered with. A
/// group that is made and then cannot be named is still a group. The create is never retried: retrying would make a
/// second group, which everybody in it can see.
/// </remarks>
internal sealed partial class MessengerBackend : IGroupBackend
{
    // The app's own guardrail rather than a claim about Messenger's ceiling; Messenger refuses what it will not take.
    private const int GroupMaximum = 50;
    private const int GroupNameMaximum = 100;

    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken = default)
        => Task.FromResult(accountId == Models.Account && helper is not null
            ? new GroupOptions(true, "Choose one person to start a chat, or several for a group. A new group is an ordinary Messenger group, not an end-to-end encrypted one.",
                Maximum: GroupMaximum, NameAllowed: true, NameMaximum: GroupNameMaximum)
            : new GroupOptions(false, "Connect Messenger in Settings first."));

    public async Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken = default)
    {
        if (accountId != Models.Account) throw new SendException(false, "Choose the Messenger account.");
        var ids = people.Select(person => person.Id).ToArray();
        if (people.Count is < 2 or > GroupMaximum || people.Any(person => person.AccountId != accountId || person.CannotMessage || person.IsSelf) ||
            ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new SendException(false, $"Choose 2–{GroupMaximum} different Messenger people.");
        var name = title.Trim();
        if (name.Length > GroupNameMaximum) throw new SendException(false, $"A group name can be up to {GroupNameMaximum} characters.");
        if (helper is not { } started) throw new SendException(false, "Connect Messenger in Settings first.");
        JsonElement result;
        try { result = await started.CallAsync("ui.startGroup", new { personIds = ids, title = name }, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure)
        {
            throw new SendException(failure.Message is not ("invalid_input" or "start_rejected" or "login_required" or "not_connected"),
                failure.Message switch
                {
                    "start_rejected" => "Messenger would not create that group.",
                    "start_uncertain" => "Messenger did not say whether the group was created. Check Messenger before creating it again.",
                    "invalid_input" => "Messenger would not accept those members.",
                    "not_connected" or "login_required" => "Connect Messenger in Settings first.",
                    _ => "The result is unknown. Check Messenger before creating the group again.",
                });
        }
        var created = result.Deserialize<MessengerCreatedGroup>(Json);
        if (created is null || string.IsNullOrEmpty(created.ThreadId))
            throw new SendException(true, "Messenger did not confirm the group. Check Messenger before creating it again.");
        // The group exists. A conversation Messenger has not described yet is a drawing problem, not a creation one.
        if (created.Chat is null)
            throw new SendException(false, "The group was created. Messenger has not described it yet — refresh chats and it will appear.") { Done = true };
        chats = [.. chats.Where(chat => chat.Id != created.Chat.Id), created.Chat];
        _ = SaveChatsAsync(chats);
        ChatsChanged?.Invoke(this, EventArgs.Empty);
        return Models.Map(created.Chat);
    }
}

internal sealed record MessengerGroupChange(bool Changed, MessengerChat? Chat);

/// <summary>
/// Changing an existing Messenger group: renaming it, adding people, removing somebody (2026-10-04).
/// </summary>
/// <remarks>
/// The helper sends the same Lightspeed tasks the bridge uses — the rename is the one creating a named group already
/// sends — and only for an ordinary group this account is in; it answers which of the three each conversation takes
/// (MessengerChat.CanRename and the rest), and an encrypted group takes none of them. Everybody in the group sees each
/// change. Each is sent once from a choice the person confirmed: never retried here or in the helper, and an answer that
/// did not arrive is reported as unknown with a pointer to Messenger, never sent again.
/// </remarks>
internal sealed partial class MessengerBackend : IGroupManagement
{
    int IGroupManagement.GroupNameMaximum => GroupNameMaximum;
    int IGroupManagement.AddMaximum => GroupMaximum;
    public bool IsMember(ChatSummary chat, ContactPerson person) => MessengerGroupChanges.IsMember(chat, person);

    public Task<ChatSummary> RenameGroupAsync(string chatId, string title, CancellationToken cancellationToken = default)
    {
        var name = title.Trim();
        if (MessengerGroupChanges.RenameRefusal(name, GroupNameMaximum) is { } refusal) throw new SendException(false, refusal);
        return ChangeGroupAsync("ui.renameGroup", chatId, chat => chat.CanRename, native => new { chatId = native, title = name }, cancellationToken);
    }

    public Task<ChatSummary> AddGroupMembersAsync(string chatId, IReadOnlyList<ContactPerson> people, CancellationToken cancellationToken = default)
    {
        var known = MessengerGroupChanges.Offered(chatId, chats, chat => chat.CanAddMembers);
        if (MessengerGroupChanges.AddRefusal(known, people, GroupMaximum) is { } refusal) throw new SendException(false, refusal);
        var ids = people.Select(person => person.Id).ToArray();
        return ChangeGroupAsync("ui.addGroupMembers", chatId, chat => chat.CanAddMembers, native => new { chatId = native, personIds = ids }, cancellationToken);
    }

    public Task<ChatSummary> RemoveGroupMemberAsync(string chatId, string personId, CancellationToken cancellationToken = default)
    {
        var native = MessengerGroupChanges.NativePerson(personId);
        if (string.IsNullOrWhiteSpace(native)) throw new SendException(false, "Choose somebody in this group.");
        return ChangeGroupAsync("ui.removeGroupMember", chatId, chat => chat.CanRemoveMembers, id => new { chatId = id, personId = native }, cancellationToken);
    }

    private async Task<ChatSummary> ChangeGroupAsync(string method, string chatId, Func<MessengerChat, bool> offered, Func<string, object> parameters,
        CancellationToken cancellationToken)
    {
        // What the helper said this conversation takes (MessengerGroupChanges.Offered); it checks again before sending.
        if (MessengerGroupChanges.Offered(chatId, chats, offered) is not { } known) throw new SendException(false, MessengerGroupChanges.NotHere);
        if (helper is not { } started) throw new SendException(false, "Connect Messenger in Settings first.");
        JsonElement result;
        try { result = await started.CallAsync(method, parameters(known.Id), cancellationToken); }
        // Cancelled after it was asked for: the helper may already have sent it.
        catch (OperationCanceledException) { MessengerLog.Write($"group change method={method} outcome=cancelled"); throw new SendException(true, MessengerGroupChanges.Unknown); }
        catch (Exception failure)
        {
            MessengerLog.Write($"group change method={method} outcome={MessengerFailure.Code(failure)}");
            throw new SendException(MessengerGroupChanges.Uncertain(failure.Message), MessengerGroupChanges.Explain(failure.Message));
        }
        MessengerLog.Write($"group change method={method} outcome=ok");
        var change = result.Deserialize<MessengerGroupChange>(Json);
        if (change?.Chat is not { } updated) return Models.Map(known);
        chats = [.. chats.Select(chat => chat.Id == updated.Id ? updated : chat)];
        _ = SaveChatsAsync(chats);
        ChatsChanged?.Invoke(this, EventArgs.Empty);
        return Models.Map(updated);
    }
}

