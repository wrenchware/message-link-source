using System.Security.Cryptography;
using System.Text;

namespace MessageLink.Protocol;

/// <summary>
/// <c>host.logs</c> over files: a fixed map from <see cref="HostLogNames"/> to the host's own log files, given by the host when it is composed. A name not
/// in the map, or a file that is not there or cannot be read, is <see cref="HostLogChunk.Missing"/>: the answer never says where a file is or why.
/// </summary>
public sealed class HostLogFiles : IHostLogs
{
    private readonly Dictionary<string, string> paths;

    public HostLogFiles(IReadOnlyDictionary<string, string> paths)
    {
        // Only the names the wire knows: a host cannot widen the list by mistake.
        this.paths = paths.Where(pair => HostLogNames.IsKnown(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Names => paths.Keys;

    public async Task<HostLogChunk> ReadAsync(string name, long offset, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || !paths.TryGetValue(name, out var path)) return HostLogChunk.Missing;
        try
        {
            // Shared read: the log's writer keeps writing, and may start a new run (a rename) while this is open.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
            var length = stream.Length;
            var head = new byte[(int)Math.Min(length, HostLogChunk.HeadBytes)];
            await stream.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
            var digest = Convert.ToHexStringLower(SHA256.HashData(head))[..16];
            if (offset >= length) return new(true, length, offset, "", digest);
            var bytes = new byte[(int)Math.Min(length - offset, HostLogChunk.MaxChunk)];
            stream.Seek(offset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            // Whole lines only, so a chunk never ends inside a character or a line still being written. A line longer than a whole chunk goes as it is.
            var cut = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            if (cut == 0 && bytes.Length == HostLogChunk.MaxChunk) cut = bytes.Length;
            return new(true, length, offset + cut, Encoding.UTF8.GetString(bytes, 0, cut), digest);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return HostLogChunk.Missing; }
    }
}
