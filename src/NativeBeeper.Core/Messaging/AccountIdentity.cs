namespace NativeBeeper.Core.Messaging;

/// <summary>
/// Names the account a service is signed in as, from its own entries in the conversations already loaded: nothing is asked of the service, and nothing is invented. Google Messages is paired
/// with a phone rather than signed in to a named account, so it prefers that phone's number; the others carry the name the service itself shows. Null before a conversation has loaded,
/// or when its own entry carries neither.
/// </summary>
public static class AccountIdentity
{
    public static string? Of(IEnumerable<(string? Id, string? Name)> selves, bool preferNumber = false)
    {
        var known = selves.ToArray();
        var number = known.Select(self => self.Id).FirstOrDefault(IsNumber);
        // "Participant" is the Google helper's last-resort label for somebody it could not name, not an identity.
        var name = known.Select(self => self.Name).FirstOrDefault(candidate => candidate is { Length: > 0 } and not "Participant");
        return preferNumber ? number ?? name : name ?? number;
    }

    private static bool IsNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var digits = value.StartsWith('+') ? value[1..] : value;
        return digits.Length >= 7 && digits.All(char.IsAsciiDigit);
    }
}
