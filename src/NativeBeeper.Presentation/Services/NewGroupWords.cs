namespace NativeBeeper.Presentation.Services;

/// <summary>The words asking before a group is made, which every member sees at once and which can't be undone from here (the phone's are the same).</summary>
public static class NewGroupWords
{
    public static string Confirm(IEnumerable<string> names)
    {
        var people = names.ToArray();
        var who = people.Length switch
        {
            0 => "Everyone you added",
            1 => people[0],
            _ => string.Join(", ", people[..^1]) + " and " + people[^1],
        };
        return $"{who} will see it in their conversations right away. It can't be undone from here.";
    }
}
