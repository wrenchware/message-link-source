namespace MessageLink.Core.Services;

// A protector's refusal of a value whose own framing is wrong (a missing prefix, too short to hold the header, not a
// sealed value at all), as opposed to one that failed authentication or decryption. That one may only mean the bytes
// were sealed under a different key (a key file replaced, another Windows account), so a store that sets damaged values
// aside has to leave it where it is. The exception keeps its type for callers that only ask whether it was unreadable.
public static class SealedFraming
{
    private const string Mark = "MessageLink.SealedFraming";

    public static TException Damaged<TException>(TException exception) where TException : Exception
    {
        exception.Data[Mark] = true;
        return exception;
    }

    public static bool IsDamaged(Exception exception) => exception.Data.Contains(Mark);
}
