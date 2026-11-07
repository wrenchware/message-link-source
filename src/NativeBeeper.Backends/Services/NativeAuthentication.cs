using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services;

// The argv value is only a file locator. Its single-use credential is sealed to the Windows user, never on argv.
internal sealed record NativeAuthenticationTicket(string Pipe, string Secret, DateTimeOffset Expires)
{
    public override string ToString() => "Native authentication ticket";
}
internal sealed record NativeAuthenticationPacket(string Kind, string? Service = null, string? Slot = null,
    string? Secret = null, Dictionary<string, string>? Cookies = null, string? Profile = null, string? Text = null)
{
    public override string ToString() => "Native authentication packet";
}
internal sealed record NativeAuthenticationTarget(string Service, string? Slot, Func<bool> Valid,
    Func<NativeAuthenticationPacket, IProgress<string>, CancellationToken, Task> Complete, string? Profile = null, Func<Task>? End = null);

internal static class NativeAuthenticationWire
{
    internal const string Failed = "Sign-in did not finish. Return to the server page to try again.";
    // Only these fixed identity refusals are safe to relay. Provider exceptions may carry URLs or credentials.
    internal static string FailureText(Exception failure) => failure is NativeBeeper.Core.Backend.BackendException && failure.Message is
        "The service did not provide a stable account identity." or
        "Sign in with the same account. To use another account, choose Add account." or
        "Reconnect the existing account before adding another account." or
        "The existing Google Messages pairing has no stable account identity. Adding another account is unavailable with this pairing." or
        "This account is already added. Use its Sign in again option." ? failure.Message : Failed;
    internal static string TicketPath(string root, string id) => AccountNamespace.ValidKey(id)
        ? Path.Combine(root, "Server", "Authentication", id + ".protected") : throw new ArgumentException("Invalid authentication request.");
    internal static async Task WriteAsync(Stream stream, NativeAuthenticationPacket packet, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
        try
        {
            if (bytes.Length > 256 * 1024) throw new InvalidDataException();
            await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token);
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static async Task<NativeAuthenticationPacket> ReadAsync(Stream stream, CancellationToken token)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, token);
        var count = BitConverter.ToInt32(length);
        if (count is < 2 or > 256 * 1024) throw new InvalidDataException();
        var bytes = new byte[count];
        try
        {
            await stream.ReadExactlyAsync(bytes, token);
            return JsonSerializer.Deserialize<NativeAuthenticationPacket>(bytes) ?? throw new InvalidDataException();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static async Task<NativeAuthenticationTicket> TakeTicketAsync(string root, string id, IStateProtector protector, CancellationToken token)
    {
        var path = TicketPath(root, id);
        var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(path, token));
        File.Delete(path);
        try
        {
            var ticket = JsonSerializer.Deserialize<NativeAuthenticationTicket>(plain) ?? throw new InvalidDataException();
            if (ticket.Expires <= DateTimeOffset.UtcNow || ticket.Pipe != "messages-auth-" + id) throw new InvalidDataException();
            return ticket;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

// A page authorization owns exactly one authentication attempt. No normal client protocol or inbox is exposed by this pipe.
internal sealed class NativeAuthenticationHandoff(string root, IStateProtector protector, Func<string, bool> launch, Action<string> log, CancellationToken lifetime)
{
    private readonly SemaphoreSlim gate = new(1);
    internal TimeSpan ClaimTimeout { get; init; } = TimeSpan.FromSeconds(45);
    internal TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(20);
    internal Task Finished { get; private set; } = Task.CompletedTask;
    internal async Task<bool> StartAsync(Func<CancellationToken, Task<NativeAuthenticationTarget>> select, Func<bool> authorized)
    {
        if (!authorized() || !await gate.WaitAsync(0, lifetime)) return false;
        NamedPipeServerStream? pipe = null;
        string? path = null;
        NativeAuthenticationTarget? target = null;
        try
        {
            target = await select(lifetime);
            if (!target.Valid() || !authorized()) throw new InvalidOperationException();
            var id = Guid.NewGuid().ToString("N");
            var ticket = new NativeAuthenticationTicket("messages-auth-" + id, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow + Lifetime);
            pipe = new(ticket.Pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            path = NativeAuthenticationWire.TicketPath(root, id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var plain = JsonSerializer.SerializeToUtf8Bytes(ticket);
            try { await File.WriteAllBytesAsync(path, await protector.ProtectAsync(plain), lifetime); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (!launch(id)) throw new InvalidOperationException();
            Finished = RunAsync(pipe, path, ticket, target, authorized);
            return true;
        }
        catch (Exception)
        {
            pipe?.Dispose();
            try { if (target?.End is { } end) await end(); } catch (Exception) { log("server desktop sign_in cleanup=failed"); }
            if (path is not null) try { File.Delete(path); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
            gate.Release();
            log("server desktop sign_in outcome=not_started");
            return false;
        }
    }
    private async Task RunAsync(NamedPipeServerStream pipe, string path, NativeAuthenticationTicket ticket, NativeAuthenticationTarget target, Func<bool> authorized)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        stop.CancelAfter(Lifetime);
        await using var check = new Timer(_ => { try { if (!authorized() || !target.Valid()) stop.Cancel(); } catch (Exception) { stop.Cancel(); } }, null, 250, 250);
        using var close = stop.Token.Register(pipe.Dispose);
        var writes = new SemaphoreSlim(1);
        async Task Send(NativeAuthenticationPacket packet)
        {
            await writes.WaitAsync(stop.Token);
            try { await NativeAuthenticationWire.WriteAsync(pipe, packet, stop.Token); }
            finally { writes.Release(); }
        }
        NativeAuthenticationPacket? supplied = null;
        try
        {
            using var claim = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            claim.CancelAfter(ClaimTimeout);
            await pipe.WaitForConnectionAsync(claim.Token);
            var hello = await NativeAuthenticationWire.ReadAsync(pipe, claim.Token);
            if (hello.Kind != "claim" || hello.Secret is null || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(hello.Secret), System.Text.Encoding.UTF8.GetBytes(ticket.Secret))) throw new InvalidDataException();
            File.Delete(path);
            await Send(new("ready", target.Service, target.Slot, Profile: target.Profile));
            supplied = await NativeAuthenticationWire.ReadAsync(pipe, stop.Token);
            if (supplied.Kind != "complete" || supplied.Service != target.Service || supplied.Slot != target.Slot || supplied.Cookies is null || supplied.Profile != target.Profile
                || !target.Valid() || !authorized()) throw new InvalidDataException();
            // Progress is synchronous with the pairing callback: none may arrive after the final answer.
            using var monitor = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            async Task Monitor()
            {
                try { await pipe.ReadExactlyAsync(new byte[1], monitor.Token); if (!monitor.IsCancellationRequested) stop.Cancel(); }
                catch (Exception) { if (!monitor.IsCancellationRequested) stop.Cancel(); }
            }
            var watching = Monitor();
            try { await target.Complete(supplied, new InlineProgress(text => Send(new("progress", Text: text)).GetAwaiter().GetResult()), stop.Token); }
            finally { monitor.Cancel(); await watching; }
            await Send(new("done"));
            log("server desktop sign_in outcome=complete");
        }
        catch (Exception failure)
        {
            try { await Send(new("failed", Text: NativeAuthenticationWire.FailureText(failure))); } catch (Exception) { }
            log("server desktop sign_in outcome=ended");
        }
        finally
        {
            supplied?.Cookies?.Clear();
            pipe.Dispose();
            try { File.Delete(path); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
            try { if (target.End is { } end) await end(); } catch (Exception) { log("server desktop sign_in cleanup=failed"); } finally { gate.Release(); }
        }
    }
    private sealed class InlineProgress(Action<string> receive) : IProgress<string> { public void Report(string value) => receive(value); }
}
