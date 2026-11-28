namespace MessageLink.Core.Services;
public interface IPrivateStateStore
{
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken = default);
    // A write that leaves the row where it stands among the expendable copies the store trims oldest first: a closed
    // conversation kept up to date by the messages it receives has not been used, and moving it up pushed out the quiet
    // ones that had been (2026-10-04). A store with no such order just writes.
    Task WriteInPlaceAsync(string key, byte[] value, CancellationToken cancellationToken = default) => WriteAsync(key, value, cancellationToken);
    // Removing a value is not the same as storing an empty one: an empty draft should leave nothing behind.
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    // Every key stored under a category, so a sweep can find rows whose conversation the app can no longer name.
    Task<IReadOnlyList<string>> KeysAsync(string prefix, CancellationToken cancellationToken = default);
    // Stores a value only where the key holds none, and says whether it did. A backup restore adds what this PC lacks
    // and never replaces what it has. A store that can do this in one step should; this fallback checks, then writes.
    async Task<bool> AddAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        if ((await KeysAsync(key, cancellationToken)).Contains(key, StringComparer.Ordinal)) return false;
        await WriteAsync(key, value, cancellationToken);
        return true;
    }
}
public interface IStateProtector
{
    Task<byte[]> ProtectAsync(byte[] value);
    Task<byte[]> UnprotectAsync(byte[] value);
}
