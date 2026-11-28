using MessageLink.Core.Services;

namespace MessageLink.App.Services.Instagram;

// The Instagram account behind IInstagramAccount, so a window (or a remote client, later) manages it without holding
// InstagramBackend. Every call is the backend's own; nothing here decides anything. Not named
// InstagramAccount, which is the class that names the signed-in account (InstagramModels.cs).
internal sealed class InstagramAccountLifecycle(InstagramBackend instagram) : IInstagramAccount
{
    public InstagramAccountState State =>
        new(instagram.IsConnected, InstagramBackend.AnySavedSession, instagram.AccountId, instagram.AccountName);

    public event EventHandler? Changed
    {
        add => instagram.ChatsChanged += value;
        remove => instagram.ChatsChanged -= value;
    }

    public event EventHandler? ReconnectAbandoned
    {
        add => instagram.ReconnectAbandoned += value;
        remove => instagram.ReconnectAbandoned -= value;
    }

    public async Task<int> ResumeAsync(CancellationToken cancellationToken = default)
    {
        var conversations = await instagram.ResumeAsync(cancellationToken: cancellationToken);
        if (conversations < 0)
            InstagramLog.Write(InstagramLog.Connection("manual", "saved_session_" + (instagram.SavedSessionProblem ?? "unreadable")));
        return conversations;
    }

    public Task<int> ConnectAsync(Dictionary<string, string> cookies, CancellationToken cancellationToken = default) =>
        instagram.ConnectAsync(cookies, cancellationToken);

    // A failure is said as the sentence Settings shows (never exception text); an unreadable session, or a restore that
    // stopped because the server is closing, says nothing, and the window uses its generic line.
    public async Task<AccountRestore> RestoreAsync(Func<bool> stopped)
    {
        var outcome = await InstagramRestore.RunAsync(instagram, stopped);
        var failure = outcome.LastFailure is { } last && !outcome.Unreadable ? last : null;
        return new(outcome.Conversations, failure is null ? null : InstagramConnectionFailure.Code(failure),
            Explanation: failure is null ? null : InstagramConnectionFailure.SentenceFor(failure));
    }

    public Task SignOutAsync() => instagram.SignOutAsync();
    public bool AnswersFromMemory(string chatId, string messageId) => instagram.AnswersFromMemory(chatId, messageId);
}
