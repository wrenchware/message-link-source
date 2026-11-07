using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services;

/// <summary>
/// The three providers' catch-ups as one feed (<see cref="ICaughtUpFeed"/>): what came while the app was closed, named as each connection
/// starts. Messenger delivers its messages as it connects and announces them, so its notice says so (<c>Announced</c>); Instagram's and
/// Google Messages' are page reads that no event follows. A host serves this as the <c>caughtUp</c> kind of its event feed, and an
/// application in the host's process hears it directly.
/// </summary>
internal sealed class BackendCaughtUp : ICaughtUpFeed, IDisposable
{
    private readonly InstagramBackend instagram;
    private readonly MessengerBackend messenger;
    private readonly GoogleMessagesBackend google;
    private readonly Action<string, IReadOnlyList<string>> fromInstagram, fromMessenger, fromGoogle;

    private readonly ProviderAccounts? accounts;
    private readonly List<BackendCaughtUp> added = [];
    private readonly HashSet<string> subscribed = [];
    private void Refresh()
    {
        lock (added) foreach (var account in accounts?.Added ?? [])
        {
            if (!subscribed.Add(account.Registration.Key)) continue;
            if (account.Google is { } g) g.CaughtUp += fromGoogle;
            if (account.Messenger is { } m) m.CaughtUp += fromMessenger;
            if (account.Instagram is { } i) i.CaughtUp += fromInstagram;
        }
    }
    public BackendCaughtUp(GoogleMessagesBackend google, MessengerBackend messenger, InstagramBackend instagram, ProviderAccounts? accounts = null)
    {
        this.accounts = accounts;
        this.google = google; this.messenger = messenger; this.instagram = instagram;
        fromInstagram = (chat, ids) => Raise(chat, ids, announced: false);
        fromMessenger = (chat, ids) => Raise(chat, ids, announced: true);
        fromGoogle = (chat, ids) => Raise(chat, ids, announced: false);
        instagram.CaughtUp += fromInstagram;
        messenger.CaughtUp += fromMessenger;
        google.CaughtUp += fromGoogle;
        if (accounts is not null) { accounts.Changed += Refresh; Refresh(); }
    }

    public event Action<CaughtUpNotice>? CaughtUp;

    private void Raise(string chat, IReadOnlyList<string> ids, bool announced)
    {
        if (ids.Count == 0) return;
        CaughtUp?.Invoke(new CaughtUpNotice(chat, ids, announced));
    }

    public void Dispose()
    {
        if (accounts is not null)
        {
            accounts.Changed -= Refresh;
            foreach (var account in accounts.Added)
            {
                if (account.Google is { } g) g.CaughtUp -= fromGoogle;
                if (account.Messenger is { } m) m.CaughtUp -= fromMessenger;
                if (account.Instagram is { } i) i.CaughtUp -= fromInstagram;
            }
        }
        instagram.CaughtUp -= fromInstagram;
        messenger.CaughtUp -= fromMessenger;
        google.CaughtUp -= fromGoogle;
    }
}
