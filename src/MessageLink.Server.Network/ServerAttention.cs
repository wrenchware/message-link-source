using System.Globalization;
using MessageLink.App.Services.Backup;
using MessageLink.Protocol;

namespace MessageLink.Server.Network;

/// <summary>
/// What the front page's Needs attention and its Server lines are worked out from, read from the server's parts once per page (<see cref="ServerPage"/>), so
/// <see cref="ServerAttention"/> can be checked over made-up ones.
/// </summary>
internal sealed record AttentionFacts
{
    /// <summary>Devices waiting for an answer to join.</summary>
    public IReadOnlyList<JoinRequest> Joins { get; init; } = [];

    /// <summary>Accounts the server holds but isn't connected to, in the page's service order.</summary>
    public IReadOnlyList<UnconnectedAccount> Unconnected { get; init; } = [];

    /// <summary>The door is turned on.</summary>
    public bool TailnetEnabled { get; init; }

    /// <summary>The door's last report; null before its first, or while it is off.</summary>
    public TailnetView? Tailnet { get; init; }

    /// <summary>The server can send notifications (it has a notifications card).</summary>
    public bool PushesOffered { get; init; }

    /// <summary>Notifications are turned on.</summary>
    public bool PushesOn { get; init; }

    /// <summary>Paired phones notifications go to now or are failing to (<see cref="SetupProgress.PhonesNotified"/>).</summary>
    public int PhonesNotified { get; init; }

    /// <summary>Each paired phone as the server's pushes see it.</summary>
    public IReadOnlyList<PushPhone> Phones { get; init; } = [];

    /// <summary>How the backup stands; null on a server that has no backups.</summary>
    public ServerBackupStatus? Backup { get; init; }

    /// <summary>A reset chosen while the server couldn't start again by itself waits for the next start.</summary>
    public bool ResetPlanned { get; init; }

    /// <summary>A restore chosen the same way waits for the next start (and isn't one left unfinished).</summary>
    public bool RestorePlanned { get; init; }

    /// <summary>The front page's setup list is showing and still has its backup line.</summary>
    public bool ChecklistHasBackups { get; init; }

    /// <summary>Google Chrome's sandbox isn't working.</summary>
    public bool ChromeSandboxInactive { get; init; }

    /// <summary>Hermes refused an assistant's API key.</summary>
    public bool HermesKeyRejected { get; init; }

    /// <summary>An assistant's profile isn't in Hermes.</summary>
    public bool HermesNoProfile { get; init; }
}

/// <summary>An account saved but not connected: its service (<see cref="ServerAccountProviders"/>), that service's name, who it is, and where Sign in goes (null when this server can't sign in to it here).</summary>
internal sealed record UnconnectedAccount(string Provider, string Name, string? Who, string? SignInHref);

/// <summary>What an <see cref="AttentionItem"/> is about, and so which Server line it belongs to.</summary>
internal static class AttentionKinds
{
    public const string Join = "join", Account = "account", Tailscale = "tailscale", Phone = "phone", Backup = "backup", Chrome = "chrome", Hermes = "hermes";
}

/// <summary>
/// One line of Needs attention: what needs the owner, the sentence under it, the value beside it, and where its button goes (none when <paramref name="Href"/> is
/// null). <paramref name="Provider"/> names the service whose mark it carries.
/// </summary>
internal sealed record AttentionItem(string Kind, string Title, string? Detail = null, string? Value = null, string? Href = null, string Button = "Review", string? Provider = null);

/// <summary>A Server line's state word, and whether it is said in the warning color.</summary>
internal sealed record ServerLine(string Value, bool Warn);

/// <summary>
/// What needs the owner now, and how each Server line stands, from one snapshot of facts. A Server line warns exactly when Needs attention has an item for it
/// (owner, 2026-11-26), so the two never disagree; no page works any of this out itself.
/// </summary>
internal sealed class ServerAttention(AttentionFacts facts, DateTimeOffset now)
{
    public const string HermesProvider = "hermes";

    /// <summary>Needs attention's lines, in the page's order; none when nothing needs the owner.</summary>
    public IReadOnlyList<AttentionItem> Items
    {
        get
        {
            var items = new List<AttentionItem>();
            // Deny is on the page Review opens, with the rest of the answer.
            foreach (var asking in facts.Joins)
                items.Add(new(AttentionKinds.Join, $"{asking.DeviceName} wants to join", Value: "Code " + asking.Code, Href: "/joins/approve?id=" + Uri.EscapeDataString(asking.Id)));
            foreach (var account in facts.Unconnected)
                items.Add(new(AttentionKinds.Account, $"{account.Name} isn't connected", account.Who, Href: account.SignInHref, Button: "Sign in", Provider: account.Provider));
            if (TailscaleItem() is { } tailscale) items.Add(tailscale);
            items.AddRange(PhoneItems());
            if (BackupItem(listed: true) is { } backup) items.Add(backup);
            // Only said when something is wrong: a running sandbox, or one turned off by whoever installed the server, is nothing a person needs to read.
            if (facts.ChromeSandboxInactive)
                items.Add(new(AttentionKinds.Chrome, "Google Chrome", "Its sandbox isn't working, so the pages it opens aren't kept apart from the rest of the server."));
            // A key Hermes refused or a profile it doesn't have stays so until it is changed. Hermes not answering is left to its own line.
            if (facts.HermesKeyRejected || facts.HermesNoProfile)
                items.Add(new(AttentionKinds.Hermes, facts.HermesKeyRejected ? "Hermes rejected an API key" : "An assistant isn't found in Hermes", Href: "/manage/hermes", Provider: HermesProvider));
            return items;
        }
    }

    /// <summary>The Notifications line: off, or on with the phones they reach.</summary>
    public ServerLine Notifications => new(!facts.PushesOn ? "Off" : facts.PhonesNotified switch { 0 => "On · no phone yet", 1 => "On · 1 phone", var count => $"On · {count} phones" },
        PhoneItems().Any());

    /// <summary>The Remote access line.</summary>
    public ServerLine RemoteAccess => new(!facts.TailnetEnabled ? "Off" : facts.Tailnet switch { null => "Starting", { Running: true } => "Connected", { State: "NeedsLogin" } => "Signed out", _ => "Not connected" },
        facts.TailnetEnabled && TailscaleItem() is not null);

    /// <summary>
    /// The Daily backup line (null on a server without backups). It warns whenever the backup has an item, even while the setup list says it for Needs attention.
    /// </summary>
    public ServerLine? Backup(Func<DateTimeOffset, string> ago) => facts.Backup is not { } status ? null : new(status switch
    {
        { HasFolder: false } => "No folder",
        { Enabled: false } => "Off",
        { State: ServerBackupStates.Running } => "Backing up now",
        { HasPassword: false } => "Needs a password",
        { State: ServerBackupStates.Failing } => "Failed",
        { LastGood: { } good } => ago(good) + " · " + BackupSettings.Size(status.Bytes),
        _ => "None yet",
    }, BackupItem(listed: false) is not null);

    private AttentionItem? TailscaleItem()
    {
        if (facts.Tailnet is not { NeedsAttention: true } view) return null;
        // Key expiry left on goes silent until the phone can't reach the server, so its last month names the day (owner, 2026-11-10).
        var soon = view is { Running: true, ExpiryOff: false } && DateTimeOffset.TryParse(view.Expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            && at - now < TimeSpan.FromDays(30) ? at : (DateTimeOffset?)null;
        var title = view.State == "NeedsLogin" ? "Tailscale needs signing in" : view.NeedsApproval ? "Tailscale needs the server approved"
            : soon is { } day ? $"Tailscale signs the server out on {day.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}" : "Tailscale has a step left";
        return new(AttentionKinds.Tailscale, title, soon is null ? null : "Disable key expiry in Tailscale to keep it signed in.", Href: "/manage/tailscale");
    }

    // A phone signed up that pushes fail to, one whose app Firebase lacks, or one that asked and never signed up. Only while notifications are on.
    private IEnumerable<AttentionItem> PhoneItems() => !facts.PushesOn ? []
        : facts.Phones.Where(phone => phone.State is PushPhoneStates.Failing or PushPhoneStates.MissingApp or PushPhoneStates.NotSignedUp)
            .Select(phone => new AttentionItem(AttentionKinds.Phone, $"{phone.Name} isn't getting notifications", Href: "/manage/notifications"));

    // `listed`: for Needs attention, which leaves a missing folder to the setup list while that says it.
    private AttentionItem? BackupItem(bool listed)
    {
        if (facts.Backup is not { } status) return null;
        // A reset outranks a restore, and either outranks how the backup stands: it's what happens next.
        if (facts.ResetPlanned) return new(AttentionKinds.Backup, "A reset waits for the server to start again", Href: "/manage/backups");
        if (facts.RestorePlanned) return new(AttentionKinds.Backup, "A restore waits for the server to start again", Href: "/manage/backups");
        if (!ServerBackupStanding.WantsALook(status)) return null;
        var title = status switch
        {
            { State: ServerBackupStates.Failing } => "The last backup failed",
            { State: ServerBackupStates.NoPassword } => "Backups need a password",
            { State: ServerBackupStates.NoFolder } => listed && facts.ChecklistHasBackups ? null : "Backups need a folder",
            _ => "No backup has worked for two days",
        };
        return title is null ? null
            : new(AttentionKinds.Backup, title, status.State == ServerBackupStates.Failing ? ServerBackupStanding.Failure(status.Failure) : null, Href: "/manage/backups");
    }
}
