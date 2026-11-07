using System.Net;
using System.Security.Cryptography;
using SMBLibrary;
using SMBLibrary.Client;
using NativeBeeper.App.Services.Backup;
using FileAttributes = SMBLibrary.FileAttributes;

namespace NativeBeeper.Host.Backup;

// Kept only inside destination.protected. Never serialize this record into a response or a log.
internal sealed record BackupDestination(string Kind, string? Local = null, string? Server = null, string? Share = null,
    string? Subfolder = null, string? User = null, string? Domain = null, string? Password = null, string? Id = null)
{
    public override string ToString() => "Backup destination (credentials omitted)";
    public string Display => Kind == "smb" ? $@"\\{Server}\{Share}" + (string.IsNullOrEmpty(Subfolder) ? "" : "\\" + Subfolder) : Local!;

    public static bool ValidSmb(BackupDestination value) => value.Kind == "smb" &&
        value.Server is { Length: > 0 and <= 253 } host && (IPAddress.TryParse(host, out _) ||
            Uri.CheckHostName(host) == UriHostNameType.Dns && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) &&
        Segment(value.Share) && value.Subfolder is { Length: <= 768 } path &&
        (path.Length == 0 || path.Split('\\').All(Segment)) &&
        value.User is { Length: > 0 and <= 256 } && value.Domain is { Length: <= 256 } && value.Password is { Length: > 0 and <= 1024 };

    internal static bool Segment(string? text) => text is { Length: > 0 and <= 255 } && text is not ("." or "..") &&
        text[^1] is not ('.' or ' ') && !text.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c));
}

internal sealed class BackupDestinationException(string code) : IOException("The backup destination operation failed.")
{
    public string Code { get; } = code;
}

// One connection per operation, no reconnect or mutation retry. The scheduler owns retry pacing. Only encrypted backup
// files cross this connection; read-back before rename proves the complete ciphertext arrived, including its final tag.
internal sealed class SmbBackupDestination : IDisposable
{
    private sealed class Client : SMB2Client
    {
        public Client() : base(5000, true, false) { }
        public bool Open(IPAddress address, int port) => Connect(address, SMBTransportType.DirectTCPTransport, port);
    }

    private readonly Client client = new();
    private readonly ISMBFileStore store;
    private readonly string prefix;
    private readonly CancellationToken token;
    private readonly CancellationTokenRegistration cancellation;

    public SmbBackupDestination(BackupDestination destination, CancellationToken token, int port = 445)
    {
        if (!BackupDestination.ValidSmb(destination)) throw new BackupDestinationException("path");
        this.token = token;
        prefix = destination.Subfolder!;
        // Disconnect bounds in-flight requests. A bounded connect runs on its own task because this library's socket
        // connect is synchronous; a late connection is disposed by that task, never left alive after a timeout.
        var connect = Task.Run(async () =>
        {
            var addresses = await Dns.GetHostAddressesAsync(destination.Server!, token).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.First();
            return client.Open(address, port);
        });
        try
        {
            if (!connect.WaitAsync(TimeSpan.FromSeconds(8), token).GetAwaiter().GetResult()) throw new BackupDestinationException("unavailable");
            cancellation = token.Register(() => { try { client.Disconnect(); } catch { } });
            Check(client.Login(destination.Domain!, destination.User!, destination.Password!, AuthenticationMethod.NTLMv2));
            store = client.TreeConnect(destination.Share!, out var status);
            Check(status);
            // The user creates the subfolder on the share: never silently create a misspelled destination.
            var handle = Open("", AccessMask.GENERIC_READ, directory: true);
            Close(handle);
        }
        catch (Exception error)
        {
            cancellation.Dispose();
            _ = connect.ContinueWith(_ => { try { client.Disconnect(); } catch { } }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (error is OperationCanceledException or BackupDestinationException) throw;
            throw new BackupDestinationException("unavailable");
        }
    }

    private string PathOf(string name)
    {
        if (name.Length > 0 && !BackupDestination.Segment(name)) throw new BackupDestinationException("path");
        return prefix.Length == 0 ? name : name.Length == 0 ? prefix : prefix + "\\" + name;
    }

    private void Check(NTStatus status)
    {
        token.ThrowIfCancellationRequested();
        if (status == NTStatus.STATUS_SUCCESS) return;
        throw new BackupDestinationException(status switch
        {
            NTStatus.STATUS_LOGON_FAILURE or NTStatus.STATUS_WRONG_PASSWORD or NTStatus.STATUS_ACCOUNT_DISABLED => "auth",
            NTStatus.STATUS_ACCESS_DENIED => "denied",
            NTStatus.STATUS_BAD_NETWORK_NAME => "share",
            NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND => "missing",
            NTStatus.STATUS_DISK_FULL => "space",
            _ => "unavailable",
        });
    }

    private object Open(string name, AccessMask access, bool create = false, bool directory = false)
    {
        token.ThrowIfCancellationRequested();
        Check(store.CreateFile(out var handle, out _, PathOf(name), access | AccessMask.SYNCHRONIZE,
            directory ? FileAttributes.Directory : FileAttributes.Normal, ShareAccess.Read,
            create ? CreateDisposition.FILE_CREATE : CreateDisposition.FILE_OPEN,
            (directory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE) | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null));
        return handle;
    }

    private void Close(object handle) => Check(store.CloseFile(handle));

    public IReadOnlyList<BackupListing> List()
    {
        var handle = Open("", AccessMask.GENERIC_READ, directory: true);
        try
        {
            var status = store.QueryDirectory(out var entries, handle, "*", FileInformationClass.FileDirectoryInformation);
            if (status != NTStatus.STATUS_NO_MORE_FILES) Check(status);
            return entries.Cast<FileDirectoryInformation>().Where(e => (e.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                BackupDestination.Segment(e.FileName)).Select(e => new BackupListing(e.FileName, e.EndOfFile, new DateTimeOffset(e.LastWriteTime.ToUniversalTime()))).ToArray();
        }
        finally { Close(handle); }
    }

    public void Download(string name, Stream output)
    {
        var handle = Open(name, AccessMask.GENERIC_READ);
        try
        {
            long offset = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var status = store.ReadFile(out var bytes, handle, offset, (int)Math.Min(client.MaxReadSize, 65536));
                if (status == NTStatus.STATUS_END_OF_FILE) break;
                Check(status);
                if (bytes.Length == 0) break;
                output.Write(bytes);
                offset += bytes.Length;
            }
        }
        finally { Close(handle); }
    }

    private void Upload(string name, Stream input)
    {
        var handle = Open(name, AccessMask.GENERIC_WRITE, create: true);
        try
        {
            var buffer = new byte[Math.Min(client.MaxWriteSize, 65536)];
            long offset = 0;
            int length;
            while ((length = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                var chunk = length == buffer.Length ? buffer : buffer[..length];
                Check(store.WriteFile(out var written, handle, offset, chunk));
                if (written != length) throw new BackupDestinationException("write");
                offset += written;
            }
            Check(store.FlushFileBuffers(handle));
        }
        finally { Close(handle); }
    }

    public void Delete(string name)
    {
        var handle = Open(name, AccessMask.DELETE);
        try { Check(store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true })); }
        finally { Close(handle); }
    }

    private void Rename(string from, string to)
    {
        var handle = Open(from, AccessMask.DELETE);
        try { Check(store.SetFileInformation(handle, new FileRenameInformationType2 { FileName = PathOf(to), ReplaceIfExists = false })); }
        finally { Close(handle); }
    }

    public void Publish(string local, string name)
    {
        var temporary = BackupFolder.TemporaryPath(name);
        try
        {
            using var input = File.OpenRead(local);
            var expected = SHA256.HashData(input);
            input.Position = 0;
            Upload(temporary, input);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sink = new HashSink(hash);
            Download(temporary, sink);
            if (!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset()) || sink.Bytes != input.Length)
                throw new BackupDestinationException("write");
            token.ThrowIfCancellationRequested();
            Rename(temporary, name);
        }
        catch
        {
            try { Delete(temporary); } catch { /* A lost connection leaves only our temporary name; a later run sweeps it. */ }
            throw;
        }
    }

    public void Probe()
    {
        var name = ".message-link-check-" + Guid.NewGuid().ToString("N");
        var renamed = name + "-done";
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            using var input = new MemoryStream(bytes);
            Upload(name, input);
            using var output = new MemoryStream();
            Download(name, output);
            if (!bytes.AsSpan().SequenceEqual(output.ToArray())) throw new BackupDestinationException("write");
            Rename(name, renamed);
            Delete(renamed);
            _ = List();
        }
        catch { try { Delete(name); } catch { } try { Delete(renamed); } catch { } throw; }
    }

    public void Dispose()
    {
        cancellation.Dispose();
        // Do not wait for a network logoff during shutdown; closing the socket ends the session.
        try { client.Disconnect(); } catch { }
    }

    private sealed class HashSink(IncrementalHash hash) : Stream
    {
        public long Bytes { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) { hash.AppendData(buffer, offset, count); Bytes += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { hash.AppendData(buffer); Bytes += buffer.Length; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
