using System.Diagnostics;
using System.Globalization;
using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>Where a conversation's Archive is kept: nowhere (not offered), on its service, or in Message Link alone.</summary>
public enum ArchivePlace { None, OnService, KeptHere }

/// <summary>What a conversation's menu and the console offer for it, of the changes <see cref="ConversationActions"/> runs.</summary>
public sealed record ConversationOffers(ArchivePlace Archive, bool Delete, bool Block, bool NotSpam)
{
    public static readonly ConversationOffers None = new(ArchivePlace.None, false, false, false);
}

/// <summary>
/// The side effects a client carries out for <see cref="ConversationActions"/>: its merges store, its list and what it does around a delete.
/// An archive kept in Message Link alone is written by <see cref="ConversationPreferences"/>. Everything else (what is offered, its words, the
/// order of the steps, one change at a time) is the module's.
/// </summary>
public interface IConversationActionSides
{
    /// <summary>Archives or unarchives a merged conversation's row in the merges store, and redraws the list.</summary>
    Task SetMergedArchivedAsync(string mergedId, bool archived);
    /// <summary>Shows a setting in the list as it now stands.</summary>
    void Show(string chatId, ChatSetting setting, bool value);
    /// <summary>Takes a deleted conversation out of the list, with whatever else the client keeps of it.</summary>
    Task DeletedAsync(ChatSummary chat);
    /// <summary>The client has gone (a closed window): what comes back is not shown.</summary>
    bool Closed => false;
    /// <summary>The client has a change of its own to a conversation under way, which this one waits behind.</summary>
    bool Busy => false;
    /// <summary>A diagnostic line: states and counts, never a name or an id.</summary>
    void Log(string line);
}

/// <summary>
/// The conversation actions that change a conversation on its service, or stand in for that change: archive, delete, block and report spam,
/// and not spam. One set of rules for the Windows menu and the console (mlink): what is offered for which conversation, the words each
/// asks and answers with, and how each runs. Archive is shown ahead and put back when the service refuses or the outcome is unknown; delete
/// and spam are shown once the service has answered. One change at a time, each asked once and never retried. Pin, mute, unread and snooze are
/// <see cref="ConversationPreferences"/>; labels are Message Link's own and are not here either.
/// </summary>
public sealed class ConversationActions(IChatSettingsBackend settings, IChatDeletion deletion, IConversationActionSides sides, ConversationPreferences kept)
{
    public const string BusyWords = "Another change to a conversation is still under way. Try again in a moment.";
    public const string ArchiveUnknownWords = "Could not change this conversation. Check your phone before trying again.";
    public const string DeleteUnknownWords = "The delete outcome is unknown. Check your phone before trying again.";
    public const string BlockUnknownWords = "The block is unknown. Refresh chats before trying again.";
    public const string NotSpamUnknownWords = "The Not spam result is unknown. Refresh chats before trying again.";

    public const string DeleteTitle = "Delete this conversation?", DeleteItem = "Delete…", DeleteButton = "Delete";
    public const string BlockTitle = "Block and report spam?", BlockItem = "Block and report spam…", BlockButton = "Block";
    public const string BlockQuestion = "Your phone blocks this number, Google gets a spam report, and the conversation moves to Spam. " +
        "Not spam moves it back and unblocks the number.";
    public const string NotSpamItem = "Not spam";
    public const string InstagramArchiveTip = "Instagram can't archive from Message Link, so this hides it in Message Link only.";

    private int busy;

    /// <summary>A change is under way, here or in the client: the menu's items wait.</summary>
    public bool IsBusy => Volatile.Read(ref busy) != 0 || sides.Busy;

    // ---- What is offered ----

    /// <summary>
    /// Hermes and an id no service owns get none of them. A merged row and Instagram (whose helper changes nothing about a conversation) are
    /// archived in Message Link and offer nothing else. The other services archive and delete on the service; one person's Google Messages
    /// conversation blocks its number; Google Messages and Google Voice take a conversation out of spam.
    /// </summary>
    public static ConversationOffers Offers(ChatSummary chat)
    {
        if (HermesThreads.Is(chat)) return ConversationOffers.None;
        if (MergedConversations.IsMerged(chat.Id)) return new(ArchivePlace.KeptHere, false, false, false);
        var service = ServiceTags.Of(chat.Id);
        if (service is null || service == ServiceTags.Hermes) return ConversationOffers.None;
        if (service == ServiceTags.Instagram) return new(ArchivePlace.KeptHere, false, false, false);
        var google = service == ServiceTags.GoogleMessages;
        return new(ArchivePlace.OnService, Delete: true,
            Block: google && !chat.IsSpam && chat.Type == "single" && !chat.IsCallHistory,
            NotSpam: chat.IsSpam && (google || service == ServiceTags.GoogleVoice));
    }

    // ---- Their words ----

    private static bool InstagramAlone(ChatSummary chat) => !MergedConversations.IsMerged(chat.Id) && ServiceTags.Instagram.Owns(chat.Id);

    /// <summary>The menu's Archive item: Instagram's says it stays in Message Link.</summary>
    public static string ArchiveItem(ChatSummary chat) => chat.IsArchived ? "Unarchive" : InstagramAlone(chat) ? "Archive in Message Link" : "Archive";

    /// <summary>The Archive item's tooltip; null where it needs none.</summary>
    public static string? ArchiveTip(ChatSummary chat) => InstagramAlone(chat) ? InstagramArchiveTip : null;

    /// <summary>What Delete… asks, under <see cref="DeleteTitle"/>; null where Delete is not offered.</summary>
    public static string? DeleteQuestion(ChatSummary chat)
    {
        if (!Offers(chat).Delete) return null;
        var service = ServiceTags.Of(chat.Id);
        if (service == ServiceTags.Messenger) return $"“{chat.Title}” will be deleted from Messenger, along with the history Message Link keeps of it. This can't be undone.";
        // Google Voice deletes from this account only (delete.go, 2026-10-04).
        if (service == ServiceTags.GoogleVoice) return $"“{chat.Title}” will be deleted from your Google Voice account. The people in it keep their copy. This can't be undone.";
        return $"“{chat.Title}” will be deleted from your phone. This can't be undone.";
    }

    /// <summary>The console's line once an archive or unarchive is done, saying where it was kept.</summary>
    public static string ArchiveDone(string title, ChatSummary chat, bool archived)
    {
        var kept = Offers(chat).Archive == ArchivePlace.KeptHere;
        var where = kept ? "in Message Link" : "on " + ServiceName(chat);
        var why = InstagramAlone(chat) ? " (Instagram can't archive from Message Link)" : "";
        return (archived ? "Archived " : "Unarchived ") + title + " " + where + why + ".";
    }

    public static string DeleteDone(string title) => $"Deleted {title}.";
    public static string BlockDone(string title) => $"Blocked {title} and reported it as spam. It's in Spam now.";
    public static string NotSpamDone(string title) => $"Moved {title} out of Spam.";

    /// <summary>Why the console offers no such change for this conversation.</summary>
    public static string NotOffered(string action, string title, ChatSummary chat)
    {
        if (HermesThreads.Is(chat)) return action switch
        {
            "delete" => "Hermes conversations can't be deleted.",
            "archive" or "unarchive" => "Hermes conversations can't be archived.",
            _ => "Hermes conversations have no spam.",
        };
        if (MergedConversations.IsMerged(chat.Id) && action == "delete") return $"{title} links several conversations; delete each one from its own conversation.";
        if (MergedConversations.IsMerged(chat.Id)) return $"{title} links several conversations; block or move each one from its own conversation.";
        if (ServiceTags.Of(chat.Id) is null) return $"Message Link can't tell which service {title} is on, so it can't change it.";
        return action switch
        {
            "delete" => $"{ServiceName(chat)} conversations can't be deleted from Message Link.",
            "archive" or "unarchive" => $"{ServiceName(chat)} conversations can't be archived from Message Link.",
            "notspam" when !chat.IsSpam => $"{title} isn't in Spam.",
            "notspam" => $"{ServiceName(chat)} conversations can't be moved out of Spam from Message Link.",
            _ when chat.IsSpam => $"{title} is already in Spam.",
            _ when ServiceTags.Of(chat.Id) != ServiceTags.GoogleMessages => $"{ServiceName(chat)} conversations can't be blocked from Message Link.",
            _ => "Only one person's conversation can be blocked, not a group or a call.",
        };
    }

    private static string ServiceName(ChatSummary chat) => chat.Network.Length > 0 ? chat.Network : ServiceTags.Of(chat.Id)?.Name ?? "This service";

    // ---- Running them ----

    private static ConsoleActionResult Busy() => new(ConsoleActionOutcome.Refused, BusyWords);

    private bool TryBegin() => !sides.Busy && Interlocked.CompareExchange(ref busy, 1, 0) == 0;
    private void End() => Volatile.Write(ref busy, 0);

    private static ConsoleActionResult Failed(Exception failure, string unknown) => failure switch
    {
        SendException send => new(send.OutcomeUnknown ? ConsoleActionOutcome.Unknown : ConsoleActionOutcome.Refused, send.Message),
        BackendException backend => new(ConsoleActionOutcome.Refused, backend.Message),
        _ => new(ConsoleActionOutcome.Unknown, unknown),
    };

    /// <summary>
    /// Archive or unarchive. A merged row's is its merges row, Instagram's a preference row; anything else is asked of the service once. That
    /// row moves at once and goes back when the service refuses or the outcome is unknown: the service's next read of the list says what really
    /// happened.
    /// </summary>
    public async Task<ConsoleActionResult> ArchiveAsync(ChatSummary chat, bool archived)
    {
        var place = Offers(chat).Archive;
        if (place == ArchivePlace.None) return ConsoleActionResult.Unavailable;
        if (!TryBegin()) return Busy();
        var shownAhead = false;
        try
        {
            if (MergedConversations.IsMerged(chat.Id))
            {
                await sides.SetMergedArchivedAsync(chat.Id, archived);
                return ConsoleActionResult.Done;
            }
            // Nothing is sent. The next message to arrive lifts it (LocalPreferenceChatReader). Only Instagram is kept here alone, so its line
            // says so (the window sends it to Instagram's log).
            if (place == ArchivePlace.KeptHere)
            {
                var result = await kept.SetAsync(chat, ChatSetting.Archived, archived);
                if (result.Outcome == ConsoleActionOutcome.Done) sides.Log($"instagram local archive set={Word(archived)}");
                return result;
            }
            if (!sides.Closed) { sides.Show(chat.Id, ChatSetting.Archived, archived); shownAhead = true; }
            var asked = Stopwatch.GetTimestamp();
            var updated = await settings.SetAsync(chat.Id, ChatSetting.Archived, archived);
            if (!sides.Closed && updated.IsArchived != archived) sides.Show(chat.Id, ChatSetting.Archived, updated.IsArchived);
            sides.Log($"archive shown ahead set={Word(archived)} result={(updated.IsArchived == archived ? "kept" : "service differs")} " +
                $"service ms={((long)Stopwatch.GetElapsedTime(asked).TotalMilliseconds).ToString(CultureInfo.InvariantCulture)}");
            return ConsoleActionResult.Done;
        }
        catch (Exception failure)
        {
            if (shownAhead && !sides.Closed)
            {
                sides.Show(chat.Id, ChatSetting.Archived, chat.IsArchived);
                sides.Log($"archive shown ahead set={Word(archived)} result=put back");
            }
            return Failed(failure, ArchiveUnknownWords);
        }
        finally { End(); }
    }

    /// <summary>Delete, once the person has said yes to <see cref="DeleteQuestion"/>. The list changes once the service has said it is done.</summary>
    public async Task<ConsoleActionResult> DeleteAsync(ChatSummary chat)
    {
        if (!Offers(chat).Delete) return ConsoleActionResult.Unavailable;
        if (!TryBegin()) return Busy();
        try
        {
            try { await deletion.DeleteChatAsync(chat.Id); }
            catch (Exception failure) { return Failed(failure, DeleteUnknownWords); }
            if (sides.Closed) return ConsoleActionResult.Done;
            // Deleted on the service: a failure in what the client tidies after it is the client's, not the delete's.
            try { await sides.DeletedAsync(chat); }
            catch (Exception failure) { sides.Log($"conversation delete tidy failed error={failure.GetType().Name}"); }
            return ConsoleActionResult.Done;
        }
        finally { End(); }
    }

    /// <summary>
    /// Block and report spam (<paramref name="spam"/> true), once the person has said yes to <see cref="BlockQuestion"/>, or Not spam. The list
    /// shows what the service answered.
    /// </summary>
    public async Task<ConsoleActionResult> SetSpamAsync(ChatSummary chat, bool spam)
    {
        var offers = Offers(chat);
        if (spam ? !offers.Block : !offers.NotSpam) return ConsoleActionResult.Unavailable;
        if (!TryBegin()) return Busy();
        try
        {
            var updated = await settings.SetAsync(chat.Id, ChatSetting.Spam, spam);
            if (!sides.Closed) sides.Show(chat.Id, ChatSetting.Spam, updated.IsSpam);
            sides.Log($"spam set={Word(spam)} result={(updated.IsSpam == spam ? "kept" : "service differs")}");
            return ConsoleActionResult.Done;
        }
        catch (Exception failure) { return Failed(failure, spam ? BlockUnknownWords : NotSpamUnknownWords); }
        finally { End(); }
    }

    private static string Word(bool value) => value ? "True" : "False";
}
