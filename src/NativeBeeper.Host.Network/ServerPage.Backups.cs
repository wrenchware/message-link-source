using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using NativeBeeper.App.Services.Backup;
using NativeBeeper.Host.Backup;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

/// <summary>
/// What the page needs for the server's backup and restore (SWITCHBOARD.md stage 10, 10.2 and 10.3): the backup itself, how many accounts are signed in on the server
/// (a restore goes only onto a server with none), signing every account out (Reset server), and a start again of the server, which applies a restore or a reset
/// before anything opens (<see cref="ServerRestore.ApplyPendingAsync"/>). <paramref name="Restart"/> answers false when the host cannot start itself again.
/// </summary>
internal sealed record ServerPageBackups(ServerBackupService Service, Func<CancellationToken, Task<int>> SignedIn,
    Func<CancellationToken, Task<(int SignedOut, int Left, bool Failed)>> SignOutAll, Func<string, bool> Restart);

// The Backups card, the restore from a backup with its review of the devices, and Reset server. Every step that changes something asks for the page's password again,
// as a pairing code does; a restore and a reset ask for more (a confirmation that the old server is off, the word RESET typed). The backup's password is never put in
// an address or a page: between the choice of file and the review of the devices it waits in memory, under a random id, for ten minutes.
internal sealed partial class ServerPage
{
    private const int MaxBackupForm = 8 * 1024;
    private static readonly TimeSpan RestoreChoiceLifetime = TimeSpan.FromMinutes(10);
    private readonly object restoreGate = new();
    private (string Id, string File, string Password, RestorePreview Preview, DateTimeOffset At, string PreparedFile)? restoreChoice;

    private static bool IsBackupPath(string? path) => path is "/backups" or "/backups/password" or "/restore" or "/restore/review" or "/reset";

    // What became of a folder typed on the card, said on the card: at the top of the page, above where it jumps to, it went unseen and the box looked as if nothing had happened (2026-11-02).
    private static string? FolderWords(string? done) => done switch
    {
        "backup_folder" => "Backups go to that folder now.",
        "backup_folder_not_full" => "Enter the full folder path as this server sees it.",
        "backup_folder_missing" => "That drive or network share isn't available. Check the path and try again.",
        "backup_folder_denied" => "Message Link can't write to that folder. Choose another one.",
        "backup_folder_internal" => "Choose a folder outside the server's own data and keys folders.",
        "backup_folder_space" => "That backup location is full. Free some space or choose another location.",
        "backup_folder_unsaved" => "The server couldn't save that choice. Try again.",
        "backup_folder_chosen_settings" => "The new location was saved, but the backup schedule couldn't be saved. Check the server's disk space and permissions.",
        "backup_folder_fixed" => "This server's backup folder is set where it was installed, so it can't be changed here.",
        "backup_folder_path" => "Enter a server name, a share name and a folder within that share. Don't use .. or a full path in the subfolder box.",
        "backup_folder_auth" => "The share refused that sign-in. Check the username, password and domain.",
        "backup_folder_share" => "That share wasn't found on the server. Check its name.",
        "backup_folder_unavailable" => "The file server didn't answer. Check its address and connection.",
        "backup_folder_busy" => "A backup or check is running. Wait for it to finish, then try again.",
        "backup_folder_wait" => "Wait 30 seconds before checking another destination.",
        "backup_folder_write" => "The destination didn't keep the test file correctly. The previous choice is still in use.",
        _ => null,
    };

    private async Task BackupsCardAsync(StringBuilder body, string token, HttpRequest request, CancellationToken cancellationToken)
    {
        var done = request.Query["done"].ToString();
        var tried = done is "backup_folder" ? null : request.Query["tried"].ToString() is { Length: > 0 } typed ? typed : null;
        var fromSteps = request.Path == "/welcome";
        var service = backups!.Service;
        // A restore is offered only while no account is signed in; a server that cannot say counts as signed in.
        var accounts = 1;
        try { accounts = await backups.SignedIn(cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OperationCanceledException) { }
        var status = service.Status();
        body.Append("<section id=\"backups\"><h2>Backups</h2>");
        if (FolderWords(done) is { } folderSaid) body.Append(Notice(folderSaid));
        if (ServerRestore.LastOutcome(service.Paths) is { } outcome && time.GetUtcNow() - outcome.At < TimeSpan.FromDays(2))
            body.Append(Notice(OutcomeWords(outcome)));
        // A reset or a restore chosen while the server could not start again by itself waits for the next start; it can be called off until then.
        if (ServerRestore.ResetPlanned(service.Paths) || ServerRestore.Planned(service.Paths) && !ServerRestore.IsUnfinished(service.Paths))
            body.Append(Notice(ServerRestore.ResetPlanned(service.Paths) ? "A reset waits for the server to start again." : "A restore waits for the server to start again."))
                .Append(PendingCancel(token));
        if (!status.HasFolder)
        {
            body.Append(Para(service.DestinationUnreadable ? "The saved backup location couldn't be read. Choose it again." :
                "Choose a folder on this server or a network share for your backups."))
                .Append(DestinationForms(token, tried, fromSteps));
            if (accounts == 0) body.Append("<p><a href=\"/restore\">Restore from a backup</a></p>");
            body.Append("</section>");
            return;
        }
        var said = status.State switch
        {
            ServerBackupStates.Running => "Backing up now.",
            ServerBackupStates.NoPassword => "Set a password to start. Backups are sealed with it, so a lost password means the backups are lost.",
            ServerBackupStates.Failing when status.Failure == "messenger" => "The last backup is incomplete. " +
                (service.Settings.MessengerLeftOut == "not_connected" ? "A Messenger account wasn't connected." : "Messenger history couldn't be read.") + " Daily backups continue.",
            ServerBackupStates.Failing => "The last backup failed. " + ServerFailure(status.Failure) + (status.Failure == "auth" ? " Backups wait until you update the share sign-in." : " See the next backup time below."),
            _ when status.Stale => "No backup has worked for two days.",
            _ => null,
        };
        if (said is not null) body.Append(Notice(said));
        body.Append("<p><a href=\"/manage/backups\">Refresh backup status</a></p>");
        var idle = status.State != ServerBackupStates.Running;
        // Each button sits in the line it acts on, as one small form.
        string Do(string what, string words) =>
            "<form method=\"post\" action=\"/backups\"><input type=\"hidden\" name=\"form\" value=\"" + Html(token) + "\"><button type=\"submit\" name=\"do\" value=\"" + what + "\" class=\"quiet\">" + Html(words) + "</button></form>";
        body.Append("<div class=\"sh\"><h2>Daily backup</h2></div><div class=\"box\">")
            .Append(Row("Daily backups", "Conversations, settings and devices.", status.Enabled ? null : "Off", Switch("/backups", token, status.Enabled, "do", status.Enabled ? "off" : "on", "Daily backups")))
            .Append(Row("Last backup", string.Join(" · ", new[] { status.Next is { } next && status.Enabled ? "Next " + (next <= time.GetUtcNow() ? "shortly" : When(next)) : null, status.Kept > 0 ? $"{status.Kept} of {BackupFolder.Keep} kept" : null }.OfType<string>()) is { Length: > 0 } under ? under : null,
                status.LastGood is { } good ? Ago(good) + " · " + BackupSettings.Size(status.Bytes) : "None yet", status.HasPassword && idle ? Do("now", "Back up now") : null));
        if (status.LastGood is not null)
            body.Append(Row("Backup integrity", status.Check is { } check && check != "ok" ? CheckWords(check) : "Checked by itself a day after each backup, then weekly.",
                status.Check is null || status.CheckedAt is null ? "Not checked" : (status.Check == "ok" ? "Good · " : "Problem · ") + Ago(status.CheckedAt.Value), idle ? Do("check", "Check now") : null, warn: status.Check is not (null or "ok")));
        body.Append("</div>");
        body.Append("<div class=\"sh\"><h2>Where and how</h2></div><div class=\"box\">");
        body.Append("<div class=\"line\">").Append(CopyRow("Location", service.Folder!)).Append("<div class=\"more flat\"><details><summary>Change backup location</summary>")
            .Append(DestinationForms(token, tried ?? (service.UsesSmb ? null : service.Folder), fromSteps)).Append("</details></div></div>");
        body.Append(Row("Backup password", "Needed to restore a backup, so be sure to remember it.", status.HasPassword ? null : "Not set",
            "<a class=\"button quiet\" href=\"/backups/password\" aria-label=\"" + (status.HasPassword ? "Change the password" : "Set a password") + "\">" + (status.HasPassword ? "Change…" : "Set…") + "</a>", warn: !status.HasPassword));
        // A restore goes only onto a server with no service signed in; one in use is reset first.
        if (accounts == 0) body.Append(Row("Restore", "Restore your conversations, settings and devices.", null, "<a class=\"button quiet\" href=\"/restore\" aria-label=\"Restore from a backup\">Restore…</a>"));
        else body.Append(Row("Restore", "To restore a backup, first reset this server to sign out its accounts.", null, "<a class=\"button quiet\" href=\"/reset\">Reset server…</a>"));
        body.Append("</div><p class=\"hint\">Each backup holds your conversations, the server's settings and its devices, never a sign-in: after a restore you sign in to each account again.</p></section>");
    }

    private static string CheckWords(string check) => check switch
    {
        "ok" => "The latest backup reads back correctly.",
        "unreadable" => "The latest backup couldn't be read back. A new backup runs shortly.",
        "unavailable" => "The backup location couldn't be reached or opened. The backup itself hasn't been checked; check the connection and sign-in.",
        "missing" => "The latest backup isn't in the folder.",
        "old_password" => "The latest backup uses the previous password.",
        "unsupported" => "The latest backup was made by a newer version.",
        "unsaved" => "The check finished, but its result couldn't be saved. Check the server's disk space and permissions.",
        _ => "Not checked.",
    };

    private static string ServerFailure(string? code) => code switch
    {
        ServerBackupFailures.Folder => "The backup folder isn't available.",
        ServerBackupFailures.Space => "The backup folder is full.",
        "local_space" => "The server's own disk is full. Free space there before trying again.",
        ServerBackupFailures.Password => "The saved password can't be read. Set it again.",
        ServerBackupFailures.Messenger => "A partial backup was kept without Messenger history. It doesn't count as a complete backup.",
        "auth" => "The network share refused the saved sign-in. Enter its credentials again.",
        "denied" => "The destination refused access. Check its permissions.",
        "share" or "missing" => "The share or folder wasn't found.",
        "unavailable" => "The file server didn't answer.",
        "settings" => "The server couldn't save the backup result. Check its own disk space and permissions.",
        ServerBackupFailures.Local => "The server's own data couldn't be read.",
        _ => "The backup couldn't be written.",
    };

    private static string OutcomeWords(RestoreOutcome outcome) => outcome.Outcome switch
    {
        RestoreOutcomes.Restored when outcome.Kind == "pc" =>
            $"Restored from a PC backup. Its Messenger history{(outcome.MessengerPages > 0 ? "" : " (none)")} and choices are added once you sign in to those accounts here.",
        RestoreOutcomes.Restored =>
            $"Restored: {outcome.Messages.ToString("N0", CultureInfo.CurrentCulture)} messages, {outcome.Devices} device{(outcome.Devices == 1 ? "" : "s")} kept"
            + (outcome.Forgotten > 0 ? $", {outcome.Forgotten} forgotten" : "")
            + (outcome.Held > 0 ? $". {outcome.Held} scheduled message{(outcome.Held == 1 ? " is" : "s are")} held and won't go by {(outcome.Held == 1 ? "itself" : "themselves")}: send or drop each in Message Link" : "")
            + ". Sign in to each account again; the server then fills in what arrived meanwhile.",
        RestoreOutcomes.Reset => "The server was reset. It holds nothing now: restore a backup, or sign in to your accounts.",
        RestoreOutcomes.ResetInUse => "The reset didn't run: an account was signed in on this server again before it started. Nothing was changed; reset again to sign it out.",
        RestoreOutcomes.InUse => "The restore didn't run: an account is signed in on this server. Reset the server first.",
        RestoreOutcomes.Missing => "The restore didn't run: the backup file is no longer in the folder.",
        RestoreOutcomes.Unsupported => "The restore didn't run: the backup was made by a newer version of Message Link Server.",
        RestoreOutcomes.Unreadable => "The restore didn't run: the backup couldn't be read with that password, or it's damaged. Nothing was changed.",
        _ => "The restore didn't finish, and nothing was changed. Try again.",
    };

    // POST /backups: Back up now, Check latest, on and off. The sign-in is enough: none of them gives anything to anyone.
    private async Task BackupsAsync(HttpContext context, IFormCollection form)
    {
        var service = backups!.Service;
        switch (form["do"].ToString())
        {
            case "now":
                var started = await service.RunAsync(context.RequestAborted).ConfigureAwait(false);
                Redirect(context.Response, started.Started ? "/manage/backups?done=backup_started" : "/manage/backups?done=backup_busy");
                return;
            case "check":
                _ = Task.Run(service.CheckNowAsync);
                Redirect(context.Response, "/manage/backups?done=backup_checking");
                return;
            case "cancel":
                var called = ServerRestore.CancelPending(service.Paths);
                log($"server page pending cancelled={(called ? "yes" : "no")}");
                Redirect(context.Response, "/manage/backups");
                return;
            case "on":
            case "off":
                try { service.SetEnabled(form["do"] == "on"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    await PlainAsync(context, 500, "The server couldn't save the backup schedule. Try again.").ConfigureAwait(false);
                    return;
                }
                Redirect(context.Response, "/manage/backups");
                return;
            case "folder":
                var chosen = service.ChooseFolder(form["folder"].ToString());
                log($"server page backup folder outcome={chosen}");
                // Back where the form was, with what was typed when it needs typing again.
                var from = form["from"].ToString() == "steps" ? "/welcome?step=backups&done=" : "/manage/backups?done=";
                Redirect(context.Response, chosen == "chosen" ? from + "backup_folder#backups" : from + "backup_folder_" + chosen + "&tried=" + Uri.EscapeDataString(form["folder"].ToString()) + "#backups");
                return;
            case "smb":
                var result = await service.ChooseSmbAsync(form["server"].ToString(), form["share"].ToString(), form["subfolder"].ToString(),
                    form["user"].ToString(), form["domain"].ToString(), form["share_password"].ToString(), context.RequestAborted).ConfigureAwait(false);
                log($"server page backup destination outcome={result}");
                var back = form["from"].ToString() == "steps" ? "/welcome?step=backups&done=" : "/manage/backups?done=";
                Redirect(context.Response, back + (result == "chosen" ? "backup_folder" : "backup_folder_" + result) + "#backups");
                return;
            default:
                Redirect(context.Response, "/");
                return;
        }
    }

    private static string DestinationForms(string token, string? current, bool fromSteps) =>
        "<h3>Folder on this server</h3><p>A local folder or mapped drive outside the server's data folder. With Docker, use an existing mapped folder: a folder inside the container is lost when it is replaced.</p>" + FolderForm(token, current, fromSteps) +
        "<h3>Network share (SMB)</h3><p>Save directly to a file server or NAS. No Docker folder mapping is needed. Create the subfolder on the share first.</p>" +
        "<form method=\"post\" action=\"/backups\"><input type=\"hidden\" name=\"form\" value=\"" + Html(token) + "\">" +
        (fromSteps ? "<input type=\"hidden\" name=\"from\" value=\"steps\">" : "") +
        "<label for=\"smb-server\">File server</label><input id=\"smb-server\" name=\"server\" required maxlength=\"253\" placeholder=\"nas\" spellcheck=\"false\">" +
        "<label for=\"smb-share\">Share</label><input id=\"smb-share\" name=\"share\" required maxlength=\"255\" placeholder=\"backups\">" +
        "<label for=\"smb-folder\">Subfolder (optional)</label><input id=\"smb-folder\" name=\"subfolder\" maxlength=\"768\" placeholder=\"Message Link\">" +
        "<label for=\"smb-user\">Username</label><input id=\"smb-user\" name=\"user\" required maxlength=\"256\" autocomplete=\"off\">" +
        "<label for=\"smb-domain\">Domain (optional)</label><input id=\"smb-domain\" name=\"domain\" maxlength=\"256\" autocomplete=\"off\">" +
        "<label for=\"smb-password\">Share password</label><input id=\"smb-password\" name=\"share_password\" type=\"password\" required maxlength=\"1024\" autocomplete=\"off\">" +
        "<p class=\"hint\">The server keeps this sign-in protected. Your backup password is separate.</p><button type=\"submit\" name=\"do\" value=\"smb\">Check and use share</button></form>";

    // The Windows server's folder, typed as a path: the server runs as the person, so whatever they can open in File Explorer it can write to.
    private static string FolderForm(string token, string? current, bool fromSteps) =>
        new StringBuilder("<form method=\"post\" action=\"/backups\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
            .Append(fromSteps ? "<input type=\"hidden\" name=\"from\" value=\"steps\">" : "")
            .Append("<label for=\"folder\">Folder</label><input id=\"folder\" name=\"folder\" type=\"text\" required maxlength=\"1024\" spellcheck=\"false\" placeholder=\"D:\\Backups\\Message Link\" value=\"")
            .Append(Html(current ?? "")).Append("\">")
            .Append("<button type=\"submit\" name=\"do\" value=\"folder\">Use this folder</button></form>").ToString();

    // The backup's password, with the page's own: whoever sets it decides who can read every backup from now on.
    private async Task BackupPasswordAsync(HttpContext context, IFormCollection? form, string token)
    {
        const string title = "Backup password";
        string Ask(string? problem)
        {
            var body = new StringBuilder("<section>");
            if (problem is not null) body.Append(Notice(problem));
            body.Append(Para("Backups are sealed with this password. Without it nobody can read them, the server included: if it's lost, the backups are lost. Keep it somewhere safe, away from the server."))
                .Append(Para("A new password is used from the next backup on; the backups made before stay sealed with the old one."))
                .Append("<form method=\"post\" action=\"/backups/password\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
                .Append("<label for=\"backup\">Backup password (at least 8 characters)</label><input id=\"backup\" name=\"backup\" type=\"password\" autocomplete=\"new-password\" required minlength=\"8\">")
                .Append("<label for=\"again\">The backup password again</label><input id=\"again\" name=\"again\" type=\"password\" autocomplete=\"new-password\" required minlength=\"8\">")
                .Append("<label for=\"password\">Server password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required>")
                .Append("<button type=\"submit\">Set the password</button><a class=\"cancel\" href=\"/\">Cancel</a></form>");
            return body.Append("</section>").ToString();
        }
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, title, Ask(null)).ConfigureAwait(false);
            return;
        }
        var chosen = form["backup"].ToString();
        if (chosen.Length is < ServerBackupLimits.MinPassword or > ServerBackupLimits.MaxPassword)
        {
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask("Choose a password of 8 to 256 characters.")).ConfigureAwait(false);
            return;
        }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(chosen), Encoding.UTF8.GetBytes(form["again"].ToString())))
        {
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask("The two backup passwords aren't the same.")).ConfigureAwait(false);
            return;
        }
        if (!await PasswordAgainAsync(context, form, "backup_password", title, Ask).ConfigureAwait(false)) return;
        try { await backups!.Service.SetPasswordAsync(chosen, context.RequestAborted).ConfigureAwait(false); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            await PageAsync(context, StatusCodes.Status500InternalServerError, title, Ask("The server couldn't keep the password. Try again.")).ConfigureAwait(false);
            return;
        }
        Redirect(context.Response, "/manage/backups?done=backup_password");
    }

    // Restore, step one: which backup, its password, and that the old server is off. The file is read whole before anything else is asked.
    private async Task RestoreAsync(HttpContext context, IFormCollection? form, string token)
    {
        const string title = "Restore from a backup";
        var service = backups!.Service;
        if (await backups.SignedIn(context.RequestAborted).ConfigureAwait(false) > 0)
        {
            await PlainAsync(context, StatusCodes.Status409Conflict, "A backup is restored only onto a server with no account signed in. To restore onto this one, reset it first (at the bottom of the server's page).").ConfigureAwait(false);
            return;
        }
        IReadOnlyList<BackupListing> files = [];
        var unavailable = false;
        try { files = await service.ListRestoreAsync(context.RequestAborted).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unavailable = true; }
        string Ask(string? problem)
        {
            var body = new StringBuilder("<section>");
            if (problem is not null) body.Append(Notice(problem));
            if (files.Count == 0)
            {
                body.Append(Para(unavailable ? "The backup location couldn't be read. Check its connection and sign-in."
                    : service.Folder is null ? "Choose the folder or network share that holds your backup first."
                    : "No backup was found there. Copy the .msgbackup file into that location, or choose another location."));
                return body.Append("<p><a href=\"/manage/backups\">Choose backup location</a></p></section>").ToString();
            }
            body.Append(Para("Choose a backup from the selected location, or copy a PC backup there first. A server's backup brings back its conversations, settings and devices; a PC's brings its Messenger history and choices, which are added once you sign in to those accounts here."))
                .Append("<form method=\"post\" action=\"/restore\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
                .Append("<label for=\"file\">Backup</label><select id=\"file\" name=\"file\" required>");
            foreach (var file in files.Take(50))
                body.Append("<option value=\"").Append(Html(file.Name)).Append("\">").Append(Html(file.Name)).Append(" · ").Append(Html(BackupSettings.Size(file.Bytes))).Append("</option>");
            body.Append("</select>")
                .Append("<label for=\"backup\">The backup's password</label><input id=\"backup\" name=\"backup\" type=\"password\" autocomplete=\"off\" required>")
                .Append("<p><label><input type=\"checkbox\" name=\"off\" value=\"yes\" required> The server this backup came from is turned off.</label></p>")
                .Append("<p class=\"quiet\">Two servers with one identity would split your devices between them. To move a server on purpose: back it up, stop it, then restore here.</p>")
                .Append("<button type=\"submit\">Read the backup</button><a class=\"cancel\" href=\"/\">Cancel</a></form>");
            return body.Append("</section>").ToString();
        }
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, title, Ask(null)).ConfigureAwait(false);
            return;
        }
        if (form["off"] != "yes")
        {
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask("Confirm the old server is turned off first.")).ConfigureAwait(false);
            return;
        }
        var name = form["file"].ToString();
        var password = form["backup"].ToString();
        if (!files.Any(f => f.Name == name) || password.Length == 0)
        {
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask("That backup isn't in the folder any more.")).ConfigureAwait(false);
            return;
        }
        RestorePreview preview;
        string? preparedPath = null;
        var keepPrepared = false;
        try
        {
            preparedPath = await service.PrepareRestoreAsync(name, context.RequestAborted).ConfigureAwait(false);
            preview = await Task.Run(() => ServerRestore.Preview(preparedPath, password, context.RequestAborted), context.RequestAborted).ConfigureAwait(false);
            keepPrepared = true;
        }
        catch (BackupFileException failure)
        {
            log($"server page restore read outcome={failure.Failure.ToString().ToLowerInvariant()}");
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask(failure.Failure switch
            {
                BackupFileFailure.NotABackup => "That file isn't a Message Link backup.",
                BackupFileFailure.Unsupported => "That backup was made by a newer version of Message Link. Update the server first.",
                _ => "The backup couldn't be opened: the password is wrong, or the file is damaged. Nothing was changed.",
            })).ConfigureAwait(false);
            return;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            await PageAsync(context, StatusCodes.Status500InternalServerError, title, Ask("The server couldn't read that file. Try again.")).ConfigureAwait(false);
            return;
        }
        finally
        {
            if (!keepPrepared && preparedPath is not null) try { File.Delete(preparedPath); } catch (IOException) { }
        }
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (restoreGate)
        {
            if (restoreChoice is { } previous) service.DiscardPrepared(previous.PreparedFile);
            restoreChoice = (id, name, password, preview, time.GetUtcNow(), Path.GetFileName(preparedPath!));
        }
        log($"server page restore read outcome=ok kind={preview.Kind} devices={preview.Devices.Count}");
        await PageAsync(context, StatusCodes.Status200OK, title, ReviewBody(token, id, preview, null)).ConfigureAwait(false);
    }

    // Step two: every device the backup would bring back, with when it was last seen, each kept unless unticked; or all forgotten. Then the page's password.
    private string ReviewBody(string token, string id, RestorePreview preview, string? problem)
    {
        var body = new StringBuilder("<section>");
        if (problem is not null) body.Append(Notice(problem));
        if (preview.MessengerLeftOut is "not_connected" or "failed")
            body.Append(Notice("This is a partial backup: Messenger history was left out. Choose a complete backup if you need that history."));
        body.Append(Para(preview.Kind == "pc"
            ? $"A PC backup from {When(preview.Created)}: Messenger history ({preview.MessengerPages} pages) and the PC's choices. This server's devices and settings stay as they are."
            : $"A server backup from {When(preview.Created)}: {preview.Conversations.ToString("N0", CultureInfo.CurrentCulture)} conversations, {preview.Messages.ToString("N0", CultureInfo.CurrentCulture)} messages, "
              + $"{preview.Photos} saved photos{(preview.Scheduled > 0 ? $" and {preview.Scheduled} scheduled messages (held for you to send or drop)" : "")}."));
        body.Append("<form method=\"post\" action=\"/restore/review\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
            .Append("<input type=\"hidden\" name=\"choice\" value=\"").Append(Html(id)).Append("\">");
        if (preview.Kind == "server")
        {
            if (preview.Devices.Count == 0) body.Append(Para("The backup holds no devices. New devices join as usual afterwards."));
            else
            {
                body.Append("<h2>Devices in the backup</h2><p class=\"quiet\">Each one ticked comes back and reconnects by itself. Untick a device you removed or no longer have; it would otherwise be let in again.</p><ul class=\"rows\">");
                foreach (var device in preview.Devices)
                    body.Append("<li><label class=\"grow\"><span><input type=\"checkbox\" name=\"keep\" value=\"").Append(Html(device.Id)).Append("\" checked> <strong>").Append(Html(device.Name)).Append("</strong></span>")
                        .Append("<span class=\"quiet\">").Append(device.LastSeen is { } seen ? "last connected " + Html(When(seen)) : "never connected").Append("</span></label></li>");
                body.Append("</ul><p><label><input type=\"checkbox\" name=\"forget_all\" value=\"yes\"> Don't bring back any devices (each one asks to join again)</label></p>");
            }
            body.Append(Para("A device that joined after this backup was made is refused and told to ask to join again. Every device starts its copy of your conversations over and keeps its drafts. The server's certificate comes back too, so your browser may warn about this page once."));
            body.Append(Para("The phone finds the server by its Tailscale name: if this is new hardware, remove the old machine in Tailscale first, or this one becomes messages-server-1."));
            body.Append(Para("The server page's saved password returns to the one in this backup. Use that password after the restart. A password set in the server's installation settings stays as it is."));
        }
        body.Append("<label for=\"password\">Server password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required>")
            .Append("<button type=\"submit\" class=\"danger\">Restore and start again</button><a class=\"cancel\" href=\"/\">Cancel</a></form>");
        return body.Append("</section>").ToString();
    }

    // The choice applied: kept as a plan sealed with the server's key, and the server started again, which restores before it opens anything.
    private async Task RestoreReviewAsync(HttpContext context, IFormCollection form, string token)
    {
        const string title = "Restore from a backup";
        (string Id, string File, string Password, RestorePreview Preview, DateTimeOffset At, string PreparedFile) choice;
        lock (restoreGate)
        {
            if (restoreChoice is not { } held || held.Id != form["choice"].ToString() || time.GetUtcNow() - held.At > RestoreChoiceLifetime)
            {
                if (restoreChoice is { } expired) backups!.Service.DiscardPrepared(expired.PreparedFile);
                restoreChoice = null;
                Redirect(context.Response, "/restore");
                return;
            }
            choice = held;
        }
        if (!await PasswordAgainAsync(context, form, "restore", title, problem => ReviewBody(token, choice.Id, choice.Preview, problem)).ConfigureAwait(false)) return;
        if (await backups!.SignedIn(context.RequestAborted).ConfigureAwait(false) > 0)
        {
            await PlainAsync(context, StatusCodes.Status409Conflict, "An account was signed in on this server meanwhile, so nothing was restored.").ConfigureAwait(false);
            return;
        }
        var keep = form["keep"].Select(value => value ?? "").ToHashSet(StringComparer.Ordinal);
        var forget = form["forget_all"] == "yes" ? ["*"] : choice.Preview.Devices.Where(device => !keep.Contains(device.Id)).Select(device => device.Id).ToList();
        var service = backups.Service;
        try { await service.StageRestoreAsync(new RestorePlan(1, choice.File, choice.Password, forget, time.GetUtcNow(), choice.PreparedFile)).ConfigureAwait(false); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            await PlainAsync(context, StatusCodes.Status500InternalServerError, "The server couldn't keep the restore. Nothing was changed; try again.").ConfigureAwait(false);
            return;
        }
        lock (restoreGate) restoreChoice = null;
        log($"server page restore chosen kind={choice.Preview.Kind} forget={(forget is ["*"] ? "all" : forget.Count.ToString(CultureInfo.InvariantCulture))}");
        var restarting = backups.Restart("restore");
        await PageAsync(context, StatusCodes.Status200OK, title, "<section>" + Para(restarting
            ? "Restoring. The server starts again on its own; reload this page in a minute. If your browser warns about the page, it's the server's own certificate coming back."
            : "The restore is ready. Start the server again to apply it: it restores before it opens anything.") + (restarting ? "" : PendingCancel(token)) + "</section>").ConfigureAwait(false);
    }

    // Reset server: every account signed out, everything the server holds gone at its next start. The word RESET and the page's password.
    private async Task ResetAsync(HttpContext context, IFormCollection? form, string token)
    {
        const string title = "Reset server";
        string Ask(string? problem)
        {
            var body = new StringBuilder("<section>");
            if (problem is not null) body.Append(Notice(problem));
            body.Append(Para("This signs every account out of the server and deletes everything it holds: conversations, settings and paired devices. Backups in the backups folder are kept. The server starts again empty, ready to restore a backup or to be set up as new."))
                .Append(Para("Google Messages is unpaired here only; remove the pairing on your phone too. The server's password and its name on your tailnet stay, so you can reach this page again."))
                .Append("<form method=\"post\" action=\"/reset\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
                .Append("<label for=\"word\">Type RESET to confirm</label><input id=\"word\" name=\"word\" autocomplete=\"off\" required>")
                .Append("<label for=\"password\">Server password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required>")
                .Append("<button type=\"submit\" class=\"danger\">Reset the server</button><a class=\"cancel\" href=\"/\">Cancel</a></form>");
            return body.Append("</section>").ToString();
        }
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, title, Ask(null)).ConfigureAwait(false);
            return;
        }
        if (form["word"].ToString().Trim() != "RESET")
        {
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask("Type RESET, in capitals, to confirm.")).ConfigureAwait(false);
            return;
        }
        if (!await PasswordAgainAsync(context, form, "reset", title, Ask).ConfigureAwait(false)) return;
        var (signedOut, left, failed) = await backups!.SignOutAll(context.RequestAborted).ConfigureAwait(false);
        log($"server page reset signed_out={signedOut} left={left} failed={failed}");
        if (failed)
        {
            await PageAsync(context, StatusCodes.Status500InternalServerError, title, Ask("An account couldn't be signed out, so nothing was reset. Try again, or remove it on the Accounts card of this page.")).ConfigureAwait(false);
            return;
        }
        try { await ServerRestore.StageResetAsync(backups.Service.Paths).ConfigureAwait(false); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            await PlainAsync(context, StatusCodes.Status500InternalServerError, "The accounts were signed out, but the server couldn't keep the reset. Try again.").ConfigureAwait(false);
            return;
        }
        var restarting = backups.Restart("reset");
        await PageAsync(context, StatusCodes.Status200OK, title, "<section>" + Para(restarting
            ? "Resetting. The server starts again on its own, empty; reload this page in a minute. Your browser may warn about the page once: the server makes a new certificate."
            : "The reset is ready. Start the server again to finish it.") + (restarting ? "" : PendingCancel(token)) + "</section>").ConfigureAwait(false);
    }

    // Calls off a reset or a restore that waits for the next start (Restart answered that the server cannot start itself again).
    private static string PendingCancel(string token) =>
        "<form method=\"post\" action=\"/backups\"><input type=\"hidden\" name=\"form\" value=\"" + Html(token) + "\"><button type=\"submit\" name=\"do\" value=\"cancel\" class=\"quiet\">Cancel it</button></form>";

    private void ResetCard(StringBuilder body) =>
        body.Append("<section><h2>Reset server</h2><p class=\"quiet\">Signs every account out and deletes everything the server holds, to start again or restore a backup.</p>")
            .Append("<form method=\"get\" action=\"/reset\"><button type=\"submit\" class=\"quiet\">Reset server…</button></form></section>");
}
