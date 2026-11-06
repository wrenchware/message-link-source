namespace NativeBeeper.App.Services.Backup;

/// <summary>
/// What a restore did. For Messenger, in messages: added, already here, kept out because they were deleted here since
/// the backup, and removed here because the backup knew they had been deleted; Pages says how many pages went in.
/// Local is the drafts, pins, labels, names and preferences; NoProfile says its refused rows had nowhere to go because
/// neither Google Messages nor Google Voice is signed in here. Messenger says what became of its history when it was
/// not all restored: not_connected, not_signed_in, other_account or failed, with Partial when some pages went in first.
/// </summary>
internal sealed record BackupRestore(int Pages, int MessengerAdded, int MessengerSkipped, int MessengerKeptOut, int MessengerRemoved,
    LocalRestoreResult Local, bool NoProfile, string? Messenger, bool Partial)
{
    /// <summary>The result in plain words, a line each, in the order the dialog shows them.</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>();
        if (Pages > 0)
        {
            lines.Add($"Messenger history: {Count(MessengerAdded, "message")} added, {MessengerSkipped:N0} already here.");
            if (MessengerKeptOut > 0)
                lines.Add($"{Count(MessengerKeptOut, "Messenger message")} deleted here since the backup {Was(MessengerKeptOut)} left out.");
            if (MessengerRemoved > 0)
                lines.Add($"Removed {Count(MessengerRemoved, "Messenger message")} the backup knew {Was(MessengerRemoved)} deleted.");
        }
        if (MessengerNote() is { } note) lines.Add(note);
        lines.Add($"Drafts, pins, labels, names and preferences: {Local.Added:N0} added, {Local.Skipped:N0} already here.");
        if (Local.Held > 0) lines.Add("Some drafts, labels, names or saved messages weren't restored, because this PC already keeps its own.");
        if (Local.Refused > 0)
            lines.Add(NoProfile
                ? $"{Local.Refused:N0} couldn't be placed, because no account is signed in here. Sign in, then restore again."
                : $"{Local.Refused:N0} couldn't be placed on this PC.");
        // Messenger's history, labels, merged conversations and the pinned order show at once (MainWindow.ReloadRestoredAsync);
        // names, photos, pins and mutes can be held in memory once loaded.
        if (Local.Added > 0) lines.Add("Some changes show after Messages restarts.");
        return lines;
    }

    private string? MessengerNote() => Messenger switch
    {
        "not_connected" when Partial => "Messenger history stopped partway because Messenger disconnected. Connect it and restore again; anything already added is skipped.",
        "not_connected" => "Messenger history was skipped because Messenger isn't connected. Connect it and restore again.",
        "not_signed_in" => "Messenger history was skipped because Messenger isn't signed in.",
        "other_account" => "Messenger history was skipped because it belongs to a different Facebook account.",
        "failed" when Partial => "Messenger history stopped partway. Restoring again picks up where it left off.",
        "failed" => "Messenger history couldn't be restored.",
        _ => null,
    };

    private static string Count(int count, string noun) => $"{count:N0} {noun}{(count == 1 ? "" : "s")}";
    private static string Was(int count) => count == 1 ? "was" : "were";
}
