using System.Globalization;
using MessageLink.App.Services.Messenger;
using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Terminal.Session;

// The conversation and message actions: the window's conversation menu (MainWindow.ChatMenu, MainWindow.Labels) and message menu (MessageActionsViewModel,
// Saved Messages), against the server. Pin, mute, marked unread, an archive kept for Instagram, snoozes, labels and saved messages are the server's shared
// rows, written through the replica, which sends each change on for every device; archive, delete and spam are ConversationActions' over the protocol. Each is asked once.
internal sealed partial class TerminalHost
{
    // Pin, mute, marked unread, snooze and an archive kept here: ConversationPreferences' rules over this terminal's list.
    private ConversationPreferences? preferences;
    private ConversationPreferences Preferences => preferences ??=
        new(parts.ChatPreferences, parts.Snoozes, parts.Merges, parts.Chats, parts.Tokens, line => parts.Context.Log("mlink " + line));

    // The console says why whenever a change is not saved, also when the account changed meanwhile (which the window passes over in silence).
    private static ConsoleActionResult Said(ConsoleActionResult result, string words) =>
        result is { Outcome: ConsoleActionOutcome.Refused, Message: null } ? result with { Message = words } : result;

    // Mute and unmute, as the conversation menu sets them: on every device that uses this server.
    public async Task SetMutedAsync(ChatSummary chat, bool muted)
    {
        if ((await Preferences.SetAsync(chat, ChatSetting.Muted, muted)).Outcome != ConsoleActionOutcome.Done)
            throw new InvalidOperationException("The mute preference was not saved.");
    }

    public async Task<ConsoleActionResult> SetPinnedAsync(ChatSummary chat, bool pinned) =>
        Said(await Preferences.SetAsync(chat, ChatSetting.Pinned, pinned), ConversationPreferences.PreferenceFailedWords);

    public async Task<ConsoleActionResult> MarkUnreadAsync(ChatSummary chat) =>
        Said(await Preferences.SetAsync(chat, ChatSetting.MarkedUnread, true), ConversationPreferences.PreferenceFailedWords);

    // Archive, delete, block and not spam: ConversationActions' rules and running, over the server, with this terminal's side of each below.
    private ConversationActions? conversationActions;
    private ConversationActions ConversationActionRunner => conversationActions ??= new(parts.Messaging, parts.Messaging, new TerminalActionSides(this), Preferences);

    private sealed class TerminalActionSides(TerminalHost host) : IConversationActionSides
    {
        private TerminalHostParts Parts => host.parts;
        public void Log(string line) => Parts.Context.Log("mlink " + line);
        public void Show(string chatId, ChatSetting setting, bool value) => Parts.Chats.ApplyLocalSetting(chatId, setting, value);

        public async Task SetMergedArchivedAsync(string mergedId, bool archived)
        {
            await Parts.Merges.SetAsync(mergedId, group => group with { Archived = archived });
            Parts.Chats.RebuildMerged();
        }

        public Task DeletedAsync(ChatSummary chat)
        {
            Parts.Chats.RemoveChat(chat.Id);
            try { if (Parts.Labels.Forget(chat.Id)) Parts.Chats.ApplyLabels(); }
            catch (Exception failure) { Parts.Context.Log($"mlink labels forget failed error={failure.GetType().Name}"); }
            return Task.CompletedTask;
        }
    }

    public Task<ConsoleActionResult> SetArchivedAsync(ChatSummary chat, bool archived) => ConversationActionRunner.ArchiveAsync(chat, archived);

    public Task<ConsoleActionResult> SetSpamAsync(ChatSummary chat, bool spam) => ConversationActionRunner.SetSpamAsync(chat, spam);

    public Task<ConsoleActionResult> DeleteChatAsync(ChatSummary chat) => ConversationActionRunner.DeleteAsync(chat);

    // Remind me later: the snooze row, and a linked conversation's merge row too. A null moment calls it off.
    public async Task<ConsoleActionResult> SnoozeAsync(ChatSummary chat, DateTimeOffset? until) =>
        Said(await Preferences.SnoozeAsync(chat, until), ConversationPreferences.ReminderFailedWords);

    public IReadOnlyList<string> LabelNames => parts.Labels.IsAvailable ? [.. parts.Labels.Labels.Select(label => label.Name)] : [];

    // Kept against the row the conversation is listed as, as the menu's Labels does. The log has counts only, never a name.
    public Task<ConsoleActionResult> SetLabelAsync(ChatSummary chat, string label, bool on)
    {
        if (!parts.Labels.IsAvailable || parts.Labels.Labels.FirstOrDefault(l => l.Name == label) is not { } found) return ConsoleActionResult.NotHere;
        try
        {
            if (parts.Labels.Set(parts.Merges.DisplayId(chat.Id), found.Id, on))
            {
                parts.Chats.ApplyLabels();
                parts.Context.Log($"mlink labels change={(on ? "add" : "remove")} definitions={parts.Labels.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"assignments={parts.Labels.AssignmentCount.ToString(CultureInfo.InvariantCulture)}");
            }
            return Task.FromResult(ConsoleActionResult.Done);
        }
        catch (Exception)
        {
            return Task.FromResult(new ConsoleActionResult(ConsoleActionOutcome.Refused, "Could not change this conversation's labels. Try again."));
        }
    }

    // ---- The message menu ----

    // Its options and the account its actions are checked against; null when the service isn't connected or the account can't be told.
    private async Task<(MessageActionOptions Options, string Profile)?> MenuAsync(ChatMessage message)
    {
        var (options, profile) = await parts.Actions.GetMenuAsync(message);
        return profile is null ? null : (options, profile);
    }

    public async Task<MessageActionOptions?> MessageOptionsAsync(ChatMessage message) => await MenuAsync(message) is { } menu ? menu.Options : null;

    private ConsoleActionResult Mutation(MessageMutationOutcome outcome) => outcome switch
    {
        MessageMutationOutcome.Accepted => ConsoleActionResult.Done,
        MessageMutationOutcome.Unknown => new(ConsoleActionOutcome.Unknown, NonEmpty(parts.Actions.Status)),
        _ => new(ConsoleActionOutcome.Refused, NonEmpty(parts.Actions.Status)),
    };

    private static string? NonEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    public async Task<ConsoleActionResult> ReactAsync(ChatMessage message, string reaction, bool remove)
    {
        if (await MenuAsync(message) is not { } menu) return ConsoleActionResult.Unavailable;
        return Mutation(await parts.Actions.ReactAsync(message, reaction, remove, menu.Profile));
    }

    public async Task<ConsoleActionResult> EditAsync(ChatMessage message, string text)
    {
        if (await MenuAsync(message) is not { Options.CanEdit: true } menu) return ConsoleActionResult.Unavailable;
        return Mutation(await parts.Actions.EditAsync(message, text, menu.Profile));
    }

    public async Task<ConsoleActionResult> DeleteMessageAsync(ChatMessage message, bool everyone)
    {
        if (await MenuAsync(message) is not { } menu || !(everyone ? menu.Options.CanDeleteForEveryone : menu.Options.CanDeleteForMe))
            return ConsoleActionResult.Unavailable;
        return Mutation(await parts.Actions.DeleteAsync(message, everyone, menu.Profile));
    }

    // The account a saved message belongs to, as the server names it (App.SavedMessageScopeAsync): none for a row that outlived an account switch.
    private async Task<string?> SavedScopeAsync(ChatSummary source)
    {
        var scopes = (await parts.Identity.ReadAsync()).SavedScopes;
        if (scopes.FirstOrDefault(scope => scope.AccountId == source.AccountId) is not { } found) return null;
        if (source.AccountId == MessengerModels.Account && source.Participants.FirstOrDefault(p => p.IsSelf == true) is { } self &&
            found.Scope != "messenger:" + NotificationIdentity.Hash(self.Id)) return null;
        return found.Scope;
    }

    public async Task<ConsoleActionResult> SaveMessageAsync(ChatSummary chat, ChatMessage message)
    {
        try
        {
            if (await SavedScopeAsync(chat) is not { } scope)
                return new(ConsoleActionOutcome.Unavailable, "Saved Messages needs this conversation's account to be signed in.");
            if (await parts.Saved.GetAsync(scope, message.ChatId, message.Id) is null) await parts.Saved.SaveAsync(scope, chat, message);
            return ConsoleActionResult.Done;
        }
        catch (Exception) { return new(ConsoleActionOutcome.Refused, "Could not update Saved Messages. Check that the original account is still signed in and try again."); }
    }

    // A reminder is a saved message with a time; the devices that show notifications raise it.
    public async Task<ConsoleActionResult> RemindAsync(ChatSummary chat, ChatMessage message, DateTimeOffset at)
    {
        if (message.IsDeleted || message.IsHidden || message.Id.StartsWith("local:", StringComparison.Ordinal)) return ConsoleActionResult.Unavailable;
        try
        {
            if (await SavedScopeAsync(chat) is not { } scope)
                return new(ConsoleActionOutcome.Unavailable, "Reminders need this conversation's account to be signed in.");
            await parts.Saved.SetReminderAsync(scope, chat, message, at);
            parts.Context.Log($"mlink reminder set in_min={((long)Math.Ceiling((at - DateTimeOffset.UtcNow).TotalMinutes)).ToString(CultureInfo.InvariantCulture)}");
            return ConsoleActionResult.Done;
        }
        catch (Exception) { return new(ConsoleActionOutcome.Refused, "Could not set the reminder. Check that the original account is still signed in and try again."); }
    }
}
