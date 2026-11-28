namespace MessageLink.Server.Network;

/// <summary>
/// What setup progress is worked out from, read from the server's parts once per page (<see cref="ServerPage"/>), so <see cref="SetupProgress"/> can be checked over
/// made-up ones.
/// </summary>
internal sealed record SetupFacts
{
    /// <summary>The door is turned on.</summary>
    public bool TailnetEnabled { get; init; }

    /// <summary>Signed in to Tailscale and running.</summary>
    public bool TailnetSignedIn { get; init; }

    /// <summary>Turned on, running and named: what another device needs to reach a PC's server, which listens on loopback alone.</summary>
    public bool TailnetReachable { get; init; }

    /// <summary>Nothing left on Tailscale's side (key expiry, approval, a rule).</summary>
    public bool TailnetAllDone { get; init; }

    /// <summary>The Message Link phones the door sees on the tailnet.</summary>
    public int TailnetPhones { get; init; }

    /// <summary>The Windows server on its own PC.</summary>
    public bool DesktopTrust { get; init; }

    /// <summary>The accounts the server holds; null when they weren't read (or couldn't be), which counts as none.</summary>
    public int? Accounts { get; init; }

    /// <summary>The server can send notifications (it has a notifications card).</summary>
    public bool PushesOffered { get; init; }

    /// <summary>Notifications are turned on: the owner's Firebase project is in place.</summary>
    public bool PushesOn { get; init; }

    /// <summary>The owner said "I don't use a phone" (<see cref="ServerPage.NoPhoneFile"/>).</summary>
    public bool NoPhoneSaid { get; init; }

    /// <summary>Every paired device, phones and PCs.</summary>
    public int Devices { get; init; }

    /// <summary>Paired devices signed up for notifications, by the push registry.</summary>
    public int PhonesSignedUp { get; init; }

    /// <summary>Paired phones notifications go to now or are failing to (PushHealth's On or Failing).</summary>
    public int PhonesNotified { get; init; }

    /// <summary>The server offers backups (it has a Backups page).</summary>
    public bool BackupsOffered { get; init; }

    /// <summary>A backup folder or share is chosen.</summary>
    public bool BackupsFolder { get; init; }

    /// <summary>A good backup exists in it.</summary>
    public bool BackupGood { get; init; }
}

/// <summary>One line of the front page's checklist: the step it reopens, its title, and what is left when that needs saying.</summary>
internal sealed record SetupItem(string Step, string Title, string? Detail);

/// <summary>
/// How far setting up has come, for the walkthrough, the front page's checklist and its Server card alike: which steps apply, which are done, what is left and where
/// to go on. No page works any of this out itself.
/// </summary>
internal sealed class SetupProgress(SetupFacts facts)
{
    /// <summary>The walkthrough's screens, in order; <see cref="Applies"/> says which of them this server shows now.</summary>
    public static readonly string[] Steps = ["start", "accounts", "tailscale", "notifications", "phone", "pc", "backups"];

    public const string MapBackups = "Choose a folder or network share on the Backups page. A network share needs no Docker folder mapping.";

    /// <summary>A PC's server listens on loopback alone, so another device can join it only through Tailscale, once that is running.</summary>
    public bool DesktopJoinUnavailable => facts.DesktopTrust && !facts.TailnetReachable;

    /// <summary>The owner said there is no phone, and notifications are still off (turning them on brings the phone back).</summary>
    public bool NoPhone => facts.PushesOffered && !facts.PushesOn && facts.NoPhoneSaid;

    /// <summary>
    /// A server that can send notifications but has none set up yet shows no phone's code: the phone would get nothing while its app is closed (owner, 2026-11-02).
    /// </summary>
    public bool PhoneWaitsForNotifications => facts.PushesOffered && !facts.PushesOn;

    /// <summary>Notifications are turned on.</summary>
    public bool NotificationsOn => facts.PushesOn;

    /// <summary>Notifications are on and a phone has signed up for them.</summary>
    public bool NotificationsReachPhone => facts.PushesOn && facts.PhonesSignedUp > 0;

    /// <summary>
    /// How many paired devices are phones: those signed up for notifications, or the Message Link phones on the tailnet, whichever says more. A PC running Message
    /// Link's own Tailscale is listed beside them by the door but is no phone, so it counts toward Add a PC instead.
    /// </summary>
    public int PhonesJoined => Math.Min(facts.Devices, Math.Max(facts.PhonesSignedUp, facts.TailnetPhones));

    /// <summary>The paired devices that aren't phones.</summary>
    public int PcsJoined => facts.Devices - PhonesJoined;

    /// <summary>
    /// The phones notifications go to, or fail to, now: the Server card's count. Not <see cref="PhonesJoined"/>: a phone on the tailnet that never signed up for
    /// notifications has joined but gets none, and one whose token Google dropped still counts here until it signs up again.
    /// </summary>
    public int PhonesNotified => facts.PhonesNotified;

    // Whether a screen is part of the walkthrough now. Next, Back, "Step N of M" and the front page's checklist all go by this, so a step that was skipped or
    // can't be done isn't shown as one (owner, 2026-11-22): a PC's server joins other devices through Tailscale alone, so until it is signed in a phone, its
    // notifications and another PC have nothing to do; and once the owner said there is no phone, the phone and its notifications are passed, until
    // notifications are turned on from Manage, which brings the phone back.
    public bool Applies(string step) => step switch
    {
        "notifications" => facts.PushesOffered && !DesktopJoinUnavailable && !NoPhone,
        "phone" => !DesktopJoinUnavailable && !NoPhone,
        "pc" => !DesktopJoinUnavailable,
        _ => true,
    };

    /// <summary>The walkthrough's screens shown now, in order.</summary>
    public string[] Shown => [.. Steps.Where(Applies)];

    /// <summary>The screen's number among those shown ("Step N of M"), or 0 for one that isn't.</summary>
    public int Number(string step) => Array.IndexOf(Shown, step) + 1;

    /// <summary>The walkthrough's screen before this one, among those it shows now.</summary>
    public string? Before(string step) => Steps.Take(Array.IndexOf(Steps, step)).LastOrDefault(Applies);

    /// <summary>The walkthrough's screen after this one, among those it shows now; null after the last.</summary>
    public string? After(string step) => Steps.Skip(Array.IndexOf(Steps, step) + 1).FirstOrDefault(Applies);

    /// <summary>Whether the walkthrough's screen is done, so it offers Next rather than Skip for now.</summary>
    public bool DoneInWalkthrough(string step) => step switch
    {
        "start" => true,
        // Signed in is enough to go on: what is left (key expiry, a rule) stays on the front page's list until it's done (DoneInChecklist).
        "tailscale" => facts.TailnetSignedIn,
        "accounts" => facts.Accounts > 0,
        "notifications" => facts.PushesOn,
        "phone" => PhonesJoined > 0,
        "pc" => !DesktopJoinUnavailable && PcsJoined > 0,
        _ => facts.BackupGood,
    };

    /// <summary>Whether the checklist's line is done. Only Tailscale differs from the walkthrough: its line stays until nothing is left there.</summary>
    public bool DoneInChecklist(string step) => step == "tailscale" ? facts.TailnetAllDone : DoneInWalkthrough(step);

    /// <summary>What is left on a step when it needs saying: backups without a folder.</summary>
    public string? Left(string step) => step == "backups" && !facts.BackupsFolder ? "Backups need a folder. " + MapBackups : null;

    // The checklist's lines, in its own order (Tailscale before accounts, where the walkthrough asks for accounts first): every step that applies but the first,
    // except backups on a server that has none, which could never be done there and would keep the list from ever clearing.
    private static readonly string[] ChecklistOrder = ["tailscale", "accounts", "notifications", "phone", "pc", "backups"];

    private bool Listed(string step) => Applies(step) && (step != "backups" || facts.BackupsOffered);

    private string Title(string step) => step switch
    {
        "tailscale" => facts.TailnetEnabled ? "Reach it away from home with Tailscale" : "Reach it away from home",
        "accounts" => "Connect your accounts",
        "notifications" => "Turn on notifications",
        "phone" => "Add a phone",
        "pc" => "Add a PC",
        _ => "Back up the server",
    };

    /// <summary>The checklist's lines not done yet; none once setting up is finished.</summary>
    public IReadOnlyList<SetupItem> ChecklistLeft =>
        [.. ChecklistOrder.Where(step => Listed(step) && !DoneInChecklist(step)).Select(step => new SetupItem(step, Title(step), Left(step)))];

    /// <summary>
    /// Where the checklist's Continue setup goes back into the walkthrough: the screen last opened (<paramref name="lastStep"/>, null after a restart), or the next
    /// one left after it when that was finished since, or the first left; step 1 is always where it was left (owner, 2026-11-10). Null when nothing is left.
    /// </summary>
    public string? Resume(string? lastStep)
    {
        var left = ChecklistLeft.Select(item => item.Step).OrderBy(step => Array.IndexOf(Steps, step)).ToArray();
        if (left.Length == 0) return null;
        if (lastStep is "start") return "start";
        var from = lastStep is null ? -1 : Array.IndexOf(Steps, lastStep);
        return left.FirstOrDefault(step => Array.IndexOf(Steps, step) >= from) ?? left[0];
    }
}
