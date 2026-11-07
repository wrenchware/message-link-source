using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using System.Text.Json;

namespace NativeBeeper.App.Services.Instagram;

/// <summary>
/// Creating an Instagram group conversation.
/// </summary>
/// <remarks>
/// Unlike a first message to one person, nothing here is addressed by a field whose meaning is unsettled: Instagram's
/// create names its recipients as messaging FBIDs outright, which is the id search already returns, and it answers
/// with the new thread's own id rather than leaving it to be looked up. Naming is a second call, so a group that is
/// made and then cannot be named is still a group and is reported as one.
///
/// The create is never retried. Retrying would make a second group, which is visible to everybody in it.
/// </remarks>
internal sealed partial class InstagramBackend : IGroupBackend
{
    // The app's own guardrail against a runaway caller, not a claim about Instagram's ceiling, which is not
    // documented anywhere this project can check. Instagram refuses what it will not accept, by name.
    private const int GroupMaximum = 32;
    private const int GroupNameMaximum = 100;

    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken = default)
        => Task.FromResult(identity is { } known && accountId == known.Account && IsConnected
            ? new GroupOptions(true, "Choose one person to open the conversation you already share, or several for a new group.",
                Maximum: GroupMaximum, NameAllowed: true, NameMaximum: GroupNameMaximum)
            : new GroupOptions(false, "Sign in to Instagram in Settings first."));

    public async Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken = default)
    {
        if (identity is not { } known || accountId != known.Account)
            throw new SendException(false, "Choose the Instagram account.");
        var ids = people.Select(person => person.Id).ToArray();
        if (people.Count is < 2 or > GroupMaximum || people.Any(person => person.AccountId != accountId || person.CannotMessage || person.IsSelf) ||
            ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new SendException(false, $"Choose 2–{GroupMaximum} different Instagram accounts.");
        var name = title.Trim();
        if (name.Length > GroupNameMaximum) throw new SendException(false, $"A group name can be up to {GroupNameMaximum} characters.");
        if (helper is not { } started || !IsConnected) throw new SendException(false, "Sign in to Instagram in Settings first.");
        JsonElement result;
        try { result = await started.CallAsync("ui.createGroup", new { personIds = ids, title = name }, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        // Creating a group is a mutation everybody in it can see, so an unknown outcome must say so rather than
        // inviting a second attempt that would make a second group.
        catch (Exception failure) { throw InstagramRefusal.Group(failure); }
        InstagramCreatedGroup? created;
        try { created = result.Deserialize<InstagramCreatedGroup>(Json); }
        catch (JsonException) { created = null; }
        if (created is null || string.IsNullOrEmpty(created.ThreadId))
            throw new SendException(true, "Instagram did not confirm the group. Check Instagram before creating it again.");
        // The group exists. A conversation Instagram would not describe afterwards is a drawing problem, not a
        // creation one, and saying otherwise would invite a second group.
        if (created.Chat is null)
            throw new SendException(false, "The group was created. Instagram has not described it yet — refresh Instagram and it will appear.") { Done = true };
        // Instagram names a group in a second call, so it can keep the group and refuse the name. That needs no
        // notice of its own: the conversation opens straight afterwards, titled by its members rather than by the
        // name that was asked for, which says it plainly enough to act on.
        return known.Map(Remember(created.Chat));
    }
}
