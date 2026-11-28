namespace MessageLink.Presentation.Services;

/// <summary>
/// What the dialogs for changing an existing group say (2026-10-04). Every one of these changes is seen by everybody in
/// the group the moment the service takes it, so each dialog says so before its button, and the button names the change.
/// Kept here so the offline checks can hold the wording to that.
/// </summary>
public static class GroupChangeWording
{
    public const string RenameTitle = "Rename group";
    public const string RenameButton = "Rename";
    public const string RenameExplanation = "Everyone in the group will see the new name, and that you changed it.";

    public const string AddTitle = "Add people";
    public const string AddPickButton = "Next";
    public const string AddExplanation = "Search for people to add. Nothing changes until you confirm on the next step.";
    public const string AddButton = "Add to group";

    public const string RemoveButton = "Remove";

    public static string AddConfirmTitle(int count) => count == 1 ? "Add 1 person to the group?" : $"Add {count} people to the group?";

    public static string AddConfirm(IReadOnlyList<string> names, string group) =>
        $"{Names(names)} will be added to “{group}”. Everyone in the group will see that you added them.";

    public static string RemoveConfirmTitle(string name) => $"Remove {name}?";

    public static string RemoveConfirm(string name, string group) =>
        $"{name} will be removed from “{group}”. Everyone in the group will see that you removed them.";

    public static string Renamed(string name) => $"Renamed the group to “{name}”.";
    public static string Added(int count) => count == 1 ? "Added 1 person to the group." : $"Added {count} people to the group.";
    public static string Removed(string name) => $"Removed {name} from the group.";

    public static string AlreadyMember(string name) => $"{name} is already in this group.";

    private static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "Nobody",
        1 => names[0],
        2 => names[0] + " and " + names[1],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };
}
