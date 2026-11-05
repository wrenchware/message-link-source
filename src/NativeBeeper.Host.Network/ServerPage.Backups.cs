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
    private (string Id, string File, string Password, RestorePreview Preview, DateTimeOffset At)? restoreChoice;

    private static bool IsBackupPath(string? path) => path is "/backups" or "/backups/password" or "/restore" or "/restore/review" or "/reset";

    // What became of a folder typed on the card, said on the card: at the top of the page, above where it jumps to, it went unseen and the box looked as if nothing had happened (2026-11-02).
    private static string? FolderWords(string? done) => done switch
    {
        "backup_folder" => "Backups go to that folder now.",
        "backup_folder_not_full" => "Type the whole path, starting with a drive letter, or with \\\\ for a network share.",
        "backup_folder_missing" => "That drive or network share isn't available. Check the path and try again.",
        "backup_folder_denied" => "Messages can't write to that folder. Choose another one.",
        "backup_folder_unsaved" => "The server couldn't save that choice. Try again.",
        "backup_folder_fixed" => "This server's backup folder is set where it was installed, so it can't be changed here.",
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
            // A Windows server's folder is the person's choice (SWITCHBOARD.md 10.7, D7); a Docker server's is its mapping.
            if (service.FolderChoiceFile is not null)
            {
                body.Append(Para("Backups are off until you choose a folder for them: any folder on this PC, a mapped drive, a network share such as \\\\nas\\backups, or a OneDrive folder."))
                    .Append(FolderForm(token, tried, fromSteps));
                body.Append("</section>");
                return;
            }
            body.Append(Para("Backups are off until the server has a folder for them. " + MapBackups));
            body.Append("</section>");
            return;
        }
        var said = status.State switch
        {
            ServerBackupStates.Running => "Backing up now.",
            ServerBackupStates.NoPassword => "Set a password to start. Backups are sealed with it, so a lost password means the backups are lost.",
            ServerBackupStates.Failing => "The last backup failed. " + ServerFailure(status.Failure) + " It's tried again within the hour.",
            _ when status.Stale => "No backup has worked for two days.",
            _ => null,
        };
        if (said is not null) body.Append(Notice(said));
        var idle = status.State != ServerBackupStates.Running;
        string Do(string what, string words) => "<button type=\"submit\" name=\"do\" value=\"" + what + "\" class=\"quiet\">" + Html(words) + "</button>";
        body.Append("<div class=\"sh\"><h2>Daily backup</h2></div><div class=\"box\">")
            .Append(Row("Daily backups", null, status.Enabled ? "On" : "Off", null))
            .Append(Row("Last backup", null, status.LastGood is { } good ? Ago(good) + " · " + BackupSettings.Size(status.Bytes) : "None yet", null));
        if (status.Next is { } next && status.Enabled) body.Append(Row("Next", null, next <= time.GetUtcNow() ? "shortly" : When(next), null));
        if (status.Kept > 0) body.Append(Row("Kept", null, status.Kept == 1 ? "1 backup" : $"{status.Kept} backups (the newest 5)", null));
        if (status.LastGood is not null)
            body.Append(Row("Read back", status.Check is { } check && check != "ok" ? CheckWords(check) : null, status.Check is null || status.CheckedAt is null ? "Not checked" : (status.Check == "ok" ? "Good · " : "Problem · ") + Ago(status.CheckedAt.Value), null, warn: status.Check is not (null or "ok")));
        body.Append("</div><form class=\"btns\" method=\"post\" action=\"/backups\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
            .Append(status.HasPassword && idle ? Do("now", "Back up now") : "")
            .Append(status.LastGood is not null && idle ? Do("check", "Check latest") : "")
            .Append(Do(status.Enabled ? "off" : "on", status.Enabled ? "Turn off daily backups" : "Turn on daily backups")).Append("</form>");
        body.Append("<div class=\"sh\"><h2>Where and how</h2></div><div class=\"box\">");
        if (service.FolderChoiceFile is not null)
            body.Append("<div class=\"line\">").Append(CopyRow("Folder", service.Folder!)).Append("<div class=\"more flat\">").Append(tried is null ? "<details><summary>" : "<details open><summary>").Append("Use another folder</summary>").Append(FolderForm(token, tried ?? service.Folder, fromSteps)).Append("</details></div></div>");
        else body.Append(CopyRow("Folder", service.Folder!));
        body.Append(Row("Password", null, status.HasPassword ? "Set" : "Not set", null, warn: !status.HasPassword));
        body.Append("</div><div class=\"btns\"><a class=\"button quiet\" href=\"/backups/password\">").Append(status.HasPassword ? "Change the password…" : "Set a password…").Append("</a>");
        // A restore goes only onto a server with no service signed in; one in use is reset first.
        if (accounts == 0) body.Append("<a class=\"button quiet\" href=\"/restore\">Restore from a backup…</a>");
        body.Append("</div><p class=\"hint\">Each backup holds your conversations, the server's settings and its devices, never a sign-in: after a restore you sign in to each account again.</p></section>");
    }

    private static string CheckWords(string check) => check switch
    {
        "ok" => "The latest backup reads back correctly.",
        "unreadable" => "The latest backup couldn't be read back. A new backup runs shortly.",
        "missing" => "The latest backup isn't in the folder.",
        "old_password" => "The latest backup uses the previous password.",
        "unsupported" => "The latest backup was made by a newer version.",
        _ => "Not checked.",
    };

    private static string ServerFailure(string? code) => code switch
    {
        ServerBackupFailures.Folder => "The backup folder isn't available.",
        ServerBackupFailures.Space => "The backup folder is full.",
        ServerBackupFailures.Password => "The saved password can't be read. Set it again.",
        ServerBackupFailures.Messenger => "Messenger history couldn't be read.",
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
            + (outcome.Held > 0 ? $". {outcome.Held} scheduled message{(outcome.Held == 1 ? " is" : "s are")} held and won't go by {(outcome.Held == 1 ? "itself" : "themselves")}: send or drop each in Messages" : "")
            + ". Sign in to each account again; the server then fills in what arrived meanwhile.",
        RestoreOutcomes.Reset => "The server was reset. It holds nothing now: restore a backup, or sign in to your accounts.",
        RestoreOutcomes.ResetInUse => "The reset didn't run: an account was signed in on this server again before it started. Nothing was changed; reset again to sign it out.",
        RestoreOutcomes.InUse => "The restore didn't run: an account is signed in on this server. Reset the server first.",
        RestoreOutcomes.Missing => "The restore didn't run: the backup file is no longer in the folder.",
        RestoreOutcomes.Unsupported => "The restore didn't run: the backup was made by a newer version of Messages Server.",
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
                service.SetEnabled(form["do"] == "on");
                Redirect(context.Response, "/manage/backups");
                return;
            case "folder":
                var chosen = service.ChooseFolder(form["folder"].ToString());
                log($"server page backup folder outcome={chosen}");
                // Back where the form was, with what was typed when it needs typing again.
                var from = form["from"].ToString() == "steps" ? "/welcome?step=backups&done=" : "/manage/backups?done=";
                Redirect(context.Response, chosen == "chosen" ? from + "backup_folder#backups" : from + "backup_folder_" + chosen + "&tried=" + Uri.EscapeDataString(form["folder"].ToString()) + "#backups");
                return;
            default:
                Redirect(context.Response, "/");
                return;
        }
    }

    // The Windows server's folder, typed as a path: the server runs as the person, so whatever they can open in File Explorer it can write to.
    private static string FolderForm(string token, string? current, bool fromSteps) =>
        new StringBuilder("<form method=\"post\" action=\"/backups\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
            .Append(fromSteps ? "<input type=\"hidden\" name=\"from\" value=\"steps\">" : "")
            .Append("<label for=\"folder\">Folder</label><input id=\"folder\" name=\"folder\" type=\"text\" required maxlength=\"1024\" spellcheck=\"false\" placeholder=\"D:\\Backups\\Messages\" value=\"")
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
        var files = ServerRestore.List(service.Folder);
        string Ask(string? problem)
        {
            var body = new StringBuilder("<section>");
            if (problem is not null) body.Append(Notice(problem));
            if (files.Count == 0)
            {
                body.Append(Para(service.Folder is null ? "The server has no backups folder. Map one to /backups, start the server again, then come back here."
                    : "There's no backup in the backups folder. Copy the backup file (it ends in .msgbackup) into that folder, then reload this page."));
                return body.Append("<p><a href=\"/\">Back</a></p></section>").ToString();
            }
            body.Append(Para("Choose a backup from this server's folder, or one a PC made (copy it into the folder first). A server's backup brings back its conversations, settings and devices; a PC's brings its Messenger history and choices, which are added once you sign in to those accounts here."))
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
        if (ServerRestore.PathOf(service.Folder, name) is not { } path || password.Length == 0)
        {
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask("That backup isn't in the folder any more.")).ConfigureAwait(false);
            return;
        }
        RestorePreview preview;
        try { preview = await Task.Run(() => ServerRestore.Preview(path, password, context.RequestAborted), context.RequestAborted).ConfigureAwait(false); }
        catch (BackupFileException failure)
        {
            log($"server page restore read outcome={failure.Failure.ToString().ToLowerInvariant()}");
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask(failure.Failure switch
            {
                BackupFileFailure.NotABackup => "That file isn't a Messages backup.",
                BackupFileFailure.Unsupported => "That backup was made by a newer version of Messages. Update the server first.",
                _ => "The backup couldn't be opened: the password is wrong, or the file is damaged. Nothing was changed.",
            })).ConfigureAwait(false);
            return;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            await PageAsync(context, StatusCodes.Status500InternalServerError, title, Ask("The server couldn't read that file. Try again.")).ConfigureAwait(false);
            return;
        }
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (restoreGate) restoreChoice = (id, name, password, preview, time.GetUtcNow());
        log($"server page restore read outcome=ok kind={preview.Kind} devices={preview.Devices.Count}");
        await PageAsync(context, StatusCodes.Status200OK, title, ReviewBody(token, id, preview, null)).ConfigureAwait(false);
    }

    // Step two: every device the backup would bring back, with when it was last seen, each kept unless unticked; or all forgotten. Then the page's password.
    private string ReviewBody(string token, string id, RestorePreview preview, string? problem)
    {
        var body = new StringBuilder("<section>");
        if (problem is not null) body.Append(Notice(problem));
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
        }
        body.Append("<label for=\"password\">Server password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required>")
            .Append("<button type=\"submit\" class=\"danger\">Restore and start again</button><a class=\"cancel\" href=\"/\">Cancel</a></form>");
        return body.Append("</section>").ToString();
    }

    // The choice applied: kept as a plan sealed with the server's key, and the server started again, which restores before it opens anything.
    private async Task RestoreReviewAsync(HttpContext context, IFormCollection form, string token)
    {
        const string title = "Restore from a backup";
        (string Id, string File, string Password, RestorePreview Preview, DateTimeOffset At) choice;
        lock (restoreGate)
        {
            if (restoreChoice is not { } held || held.Id != form["choice"].ToString() || time.GetUtcNow() - held.At > RestoreChoiceLifetime)
            {
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
        try { await service.StageRestoreAsync(new RestorePlan(1, choice.File, choice.Password, forget, time.GetUtcNow())).ConfigureAwait(false); }
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
