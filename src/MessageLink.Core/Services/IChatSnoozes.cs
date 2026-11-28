namespace MessageLink.Core.Services;
// One conversation held quiet until Until, when a single reminder is raised for whatever is still unread.
public sealed record ChatSnooze(string Profile, string ChatId, DateTimeOffset Until);
public interface IChatSnoozes
{
    Task<DateTimeOffset?> GetAsync(string profile, string chatId, CancellationToken cancellationToken = default);
    // A null moment ends the snooze. Setting one that has already passed is the same as ending it.
    Task SetAsync(string profile, string chatId, DateTimeOffset? until, CancellationToken cancellationToken = default);
    // Every snooze this PC holds, including lapsed ones: a reminder whose moment passed while the app was
    // closed is still owed, so nothing is dropped for being late.
    Task<IReadOnlyList<ChatSnooze>> ListAsync(CancellationToken cancellationToken = default);
}
// What a snooze store kept as files holds, for taking it in elsewhere once (a server's import, SWITCHBOARD.md stage 6 slice 3):
// every snooze that could be read, and how many files could not be (locked, unreadable to this user, or damaged). An import
// with any unread is not finished, so it is tried again rather than recorded done without them. Reading never deletes a file.
public sealed record ChatSnoozeFiles(IReadOnlyList<ChatSnooze> Snoozes, int Unreadable);
public interface IChatSnoozeFiles
{
    Task<ChatSnoozeFiles> ReadAllAsync(CancellationToken cancellationToken = default);
}
