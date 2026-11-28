namespace MessageLink.App.Services.GoogleMessages;

// What the window does about the Google sign-in's age (MainWindow.GoogleRecovery), apart from the window so it can be
// tested with a clock of its own. Google refuses a sign-in two hours after it was made (GoogleSignInClock).
// - Early: once per sign-in, between 110 and 117 minutes, the kept profile is loaded and its new cookies handed to the
//   running helper, which renews the sign-in before Google refuses it. Only an answer that would come again every time
//   (the profile hands back the same cookies, wants the password, or Google takes them for another account or none)
//   stops it for the run; a dialog, a slow page, a network blip or a reconnect costs that one sign-in's try, not every
//   later one. A new interactive sign-in (a new profile) starts it again.
// - Checks: 20 s after the two hours, and ten minutes later, one read each, so a refusal is heard at once. A sign-in
//   replaced early keeps its checks, in case Google refuses at its mark anyway; if Google does (heard by a check or by
//   anything else), renewing early does not help, and it stops.
internal sealed class GoogleSignInSchedule
{
    public static readonly TimeSpan EarlyFrom = TimeSpan.FromMinutes(110), EarlyUntil = TimeSpan.FromMinutes(117);
    public static readonly TimeSpan[] Checks = [TimeSpan.FromMinutes(120) + TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(130)];
    // A refusal this long after a replaced sign-in was made is that sign-in's two hours, not the new one's.
    private static readonly TimeSpan OldMarkFrom = TimeSpan.FromMinutes(118), OldMarkUntil = TimeSpan.FromMinutes(126);
    private readonly List<(DateTimeOffset Made, TimeSpan Mark)> due = [];
    private DateTimeOffset? plannedFor, adoptedFrom, earlyTried;

    public bool EarlyOff { get; private set; }
    public int Pending => due.Count;

    // What to do at this tick for a sign-in made at `made`: a check (ticks that passed several marks read once; the
    // sign-in it is for is the newest whose mark has come), an early renewal, or nothing.
    public (bool Check, DateTimeOffset CheckFor, bool Early) Tick(DateTimeOffset now, DateTimeOffset made)
    {
        if (plannedFor != made)
        {
            // A new sign-in. The old one's checks stay only if the early renewal is what replaced it; one renewed after
            // a refusal, or made any other way, has nothing left to check.
            var replaced = plannedFor is { } previous && previous == adoptedFrom ? adoptedFrom : null;
            due.RemoveAll(entry => entry.Made != replaced);
            adoptedFrom = replaced;
            plannedFor = made;
            foreach (var mark in Checks) due.Add((made, mark));
        }
        var fired = due.Where(entry => now - entry.Made >= entry.Mark).ToList();
        if (fired.Count > 0)
        {
            due.RemoveAll(entry => now - entry.Made >= entry.Mark);
            return (true, fired.Max(entry => entry.Made), false);
        }
        var age = now - made;
        if (EarlyOff || age < EarlyFrom || age >= EarlyUntil || earlyTried == made) return (false, default, false);
        earlyTried = made;
        return (false, default, true);
    }

    // The early renewal took new cookies for the sign-in made at `replaced`.
    public void Adopted(DateTimeOffset replaced) => adoptedFrom = replaced;

    // A new interactive sign-in, with a new profile: what the old one kept answering no longer applies.
    public void SignedIn() { EarlyOff = false; adoptedFrom = null; }

    // How an early try ended, as a fixed word. True when this answer is what stopped early renewal for the run.
    public bool EarlyEnded(string outcome) =>
        outcome is "same" or "no_profile" or "sign_in_needed" or "missing_cookies" or "new_window" or "wrong_account" or
            "authentication_required" or "adopted_refused" && Stop();

    // A check's answer for the sign-in made at `checkFor`. Refused at the mark of a sign-in the early renewal replaced:
    // Google counts from the first sign-in regardless, so renewing early only costs a browser load each time. True when
    // this answer is what stopped it.
    public bool CheckAnswered(DateTimeOffset checkFor, string outcome) => outcome == "refused" && checkFor == adoptedFrom && Stop();

    // Google refused the sign-in, heard by whatever heard it first. At the two hours of a sign-in the early renewal
    // replaced, the renewal did not help. True when this refusal is what stopped it.
    public bool Refused(DateTimeOffset now) =>
        adoptedFrom is { } replaced && now - replaced >= OldMarkFrom && now - replaced < OldMarkUntil && Stop();

    private bool Stop()
    {
        if (EarlyOff) return false;
        EarlyOff = true;
        return true;
    }
}
