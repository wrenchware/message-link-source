using NativeBeeper.Core.Services;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Presentation.Services;
using System.Security.Cryptography;

namespace NativeBeeper.App.Services;

public sealed class BeeperAvatarReader(IChatReader chats, IMediaReader media, IAccessTokenStore tokens, NativeBeeper.Storage.MediaDiskCache disk) : IDisposable
{
    private readonly Dictionary<string, (DateTimeOffset Until, string[] Sources)> sources = [];
    private readonly SemaphoreSlim gate = new(3);
    private readonly HashSet<string> refreshing = [];
    private readonly CancellationTokenSource lifetime = new();
    internal TimeSpan RefreshDelay { get; set; } = TimeSpan.FromSeconds(20);
    public event EventHandler<string>? Changed;
    // Which providers keep a picture worth putting on disk, and which need their identifier carried, are both declared
    // once in ProviderMedia rather than listed again here. A provider missing from that table downloads every avatar
    // at every launch, which is what Instagram did until it was added.
    private static bool Cacheable(string source) => ProviderMedia.AvatarIsCacheable(source);
    private static string CacheKey(string source) => "provider-avatar:" + source;
    // The last picture a conversation showed, under the conversation's own id, which is the same in every session.
    // Google Messages names an avatar differently in every helper session, so its pictures were never found on disk
    // again, and a row whose saved summary carries no source had to wait for its service to connect before asking
    // which picture to show — nine seconds for Messenger at one launch. Both drew initials first and then popped a
    // photo in. The kept picture is shown at once and checked against the provider a little later.
    private static string ChatKey(string chatId) => "chat-avatar:" + chatId;
    private static MessageAttachment AvatarSource(string source) => new(null, "img", null, null, false, false, false,
        ProviderMedia.AvatarCarriesItsId(source) ? source : null, source);
    // The picture already on disk, without asking any service. Separate so a row whose picture is kept is never queued
    // behind rows waiting on a provider that has not connected yet (ChatAvatar keeps the two apart).
    // keepAs: the stable identity a picture is kept under when it is not a conversation's own — a group member's face,
    // under the member's id, because Google renames the face itself every session. Defaults to the conversation.
    // check: whether a kept picture is compared with the provider's a little later, as a row's is. Off for a notification's
    // photo, which must never cause a service to be asked anything.
    public async Task<byte[]?> ReadKeptAsync(string? chatId, string? supplied, CancellationToken token, string? keepAs = null, bool check = true)
    {
        var profile = await NotificationIdentity.ProfileAsync(tokens, token);
        return profile is null ? null : await ReadKeptAsync(profile, chatId, supplied, keepAs ?? chatId, token, check);
    }

    public async Task<byte[]?> ReadAsync(string? chatId, string? supplied, CancellationToken token, string? keepAs = null) =>
        (await ReadOrWaitAsync(chatId, supplied, token, keepAs)).Bytes;

    // What a read found. WaitingFor names the service (AvatarConnectRetry.Service) a source could not be asked of because
    // it had not connected yet, when nothing else answered: that is no answer about the picture, and the avatar asks again
    // once that service connects rather than taking it as "no picture" (ChatAvatar).
    public readonly record struct AvatarRead(byte[]? Bytes, string? WaitingFor);

    public async Task<AvatarRead> ReadOrWaitAsync(string? chatId, string? supplied, CancellationToken token, string? keepAs = null)
    {
        var profile = await NotificationIdentity.ProfileAsync(tokens, token);
        if (profile is null) return default;
        var keep = keepAs ?? chatId;
        if (await ReadKeptAsync(profile, chatId, supplied, keep, token) is { } kept) return new(kept, null);
        var (fetched, _, waiting) = await FetchAsync(profile, chatId, supplied, token);
        if (fetched is not null && keep is not null) await disk.WriteAsync(profile, ChatKey(keep), fetched, KeptFor, token);
        return new(fetched, fetched is null ? waiting : null);
    }

    private async Task<byte[]?> ReadKeptAsync(string profile, string? chatId, string? supplied, string? keep, CancellationToken token, bool check = true)
    {
        // The list's saved ChatSummary contains the stable helper media id. Read that encrypted cache entry before
        // asking either the conversation service or helper, so startup does not show an initials-only pass first.
        if (supplied is { Length: > 0 } savedSource && Cacheable(savedSource) &&
            await disk.ReadAsync(profile, CacheKey(savedSource), token) is { Length: > 0 and <= 4 * 1024 * 1024 } saved)
        {
            if (check) QueueRefresh(profile, savedSource, saved);
            return saved;
        }
        // Only where the source cannot find the picture itself. A stable source that misses means the picture has
        // changed (those providers name a new picture with a new id), and the kept one would be the old face.
        if (keep is not null && (string.IsNullOrWhiteSpace(supplied) || !Cacheable(supplied)) &&
            await disk.ReadAsync(profile, ChatKey(keep), token) is { Length: > 0 and <= 4 * 1024 * 1024 } kept)
        {
            if (check) QueueChatRefresh(profile, keep, chatId, supplied, kept);
            return kept;
        }
        return null;
    }

    // Absent: every source asked answered, and answered with no picture. A failure anywhere is not proof of that.
    // keptHash: a later check of a kept picture. A source that answers with those same bytes renews its entry rather
    // than rewriting it (Renew). WaitingFor: the service that could not be asked because it had not connected yet.
    private async Task<(byte[]? Bytes, bool Absent, string? WaitingFor)> FetchAsync(string profile, string? chatId, string? supplied, CancellationToken token,
        byte[]? keptHash = null)
    {
        var candidates = new List<string>();
        var uncertain = false;
        string? waiting = null;
        if (!string.IsNullOrWhiteSpace(supplied)) candidates.Add(supplied);
        if (chatId is not null)
        {
            await gate.WaitAsync(token);
            try
            {
                var key = profile + "\n" + chatId;
                (DateTimeOffset Until, string[] Sources) cached;
                // The gate lets three readers in at once, so the map has its own lock.
                bool fresh;
                lock (sources) fresh = sources.TryGetValue(key, out cached) && cached.Until >= DateTimeOffset.UtcNow;
                if (!fresh)
                {
                    // A conversation that cannot be read right now must not cost the picture that came with it: the
                    // supplied source is already in the candidates and is usually the one that works.
                    var chat = await chats.GetChatAsync(chatId, token);
                    var urls = new List<string>();
                    if (!string.IsNullOrWhiteSpace(chat.AvatarUrl)) urls.Add(chat.AvatarUrl);
                    if (chat.Type != "group" && chat.MemberChatIds.Count == 0)
                    {
                        var others = chat.Participants.Where(p => p.IsSelf == false).ToArray();
                        if (others.Length == 1 && others[0].AvatarUrl is { Length: > 0 } avatar) urls.Add(avatar);
                    }
                    cached = (DateTimeOffset.UtcNow.AddMinutes(2), urls.Distinct().ToArray());
                    lock (sources)
                    {
                        if (sources.Count >= 256) sources.Clear();
                        sources[key] = cached;
                    }
                }
                candidates.AddRange(cached.Sources);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (NativeBeeper.Core.Backend.BackendException ex) when (ex.Waits) { uncertain = true; waiting ??= AvatarConnectRetry.Service(chatId, null); }
            catch (Exception) { uncertain = true; /* Fall through to whatever source was supplied with the conversation. */ }
            finally { gate.Release(); }
        }
        var attempted = 0;
        foreach (var source in candidates.Distinct())
        {
            token.ThrowIfCancellationRequested();
            attempted++;
            try
            {
                var bytes = await media.ReadImageAsync(AvatarSource(source), token);
                if (profile != await NotificationIdentity.ProfileAsync(tokens, token)) return (null, false, null);
                if (bytes is { Length: 0 }) continue;
                if (bytes is not { Length: > 0 and <= 4 * 1024 * 1024 }) { uncertain = true; continue; }
                if (Cacheable(source)) await KeepAsync(profile, CacheKey(source), bytes, keptHash, token);
                Messenger.MessengerLog.Avatar(chatId, supplied, attempted, bytes.Length);
                return (bytes, false, null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            // Still connecting, or putting itself right: asked again when it connects, never taken as "no picture".
            catch (NativeBeeper.Core.Backend.BackendException ex) when (ex.Waits) { uncertain = true; waiting ??= AvatarConnectRetry.Service(chatId, source); }
            catch { uncertain = true; /* An inaccessible avatar is not a reason to bypass the asset API. */ }
        }
        Messenger.MessengerLog.Avatar(chatId, supplied, attempted, -1);
        return (null, attempted > 0 && !uncertain, waiting);
    }

    // The kept picture is checked once the providers have had time to connect. A different picture replaces it and is
    // announced under the conversation id; the same one, or no answer at all, leaves what is on screen alone. A service
    // that answers that there is no picture any more takes the kept one away: kept forever, it was the face of whoever
    // the conversation used to be.
    private void QueueChatRefresh(string profile, string keep, string? chatId, string? supplied, byte[] kept)
    {
        var key = profile + "\nchat\n" + keep;
        lock (refreshing) if (!refreshing.Add(key)) return;
        StartSummary();
        var keptHash = SHA256.HashData(kept);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RefreshDelay, lifetime.Token);
                var (bytes, absent, _) = await FetchAsync(profile, chatId, supplied, lifetime.Token, keptHash);
                if (profile != await NotificationIdentity.ProfileAsync(tokens, lifetime.Token)) return;
                if (absent)
                {
                    await disk.DeleteAsync(profile, [ChatKey(keep)], lifetime.Token);
                    Interlocked.Increment(ref dropped);
                    AppLog.Write($"avatar kept dropped provider={Provider(supplied)}");
                    Changed?.Invoke(this, keep);
                    return;
                }
                if (bytes is null) return;
                if (await KeepAsync(profile, ChatKey(keep), bytes, keptHash, lifetime.Token)) Interlocked.Increment(ref same);
                else
                {
                    Interlocked.Increment(ref changed);
                    AppLog.Write($"avatar kept replaced provider={Provider(supplied)}");
                    Changed?.Invoke(this, keep);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the kept face; the next session checks again. Named, because this catch once hid every failure
                // to redraw a changed picture (ChatAvatar.ProviderAvatarChanged).
                AppLog.Write($"avatar kept check failed error={ex.GetType().Name}");
            }
            catch { /* Closing. */ }
            finally { lock (refreshing) refreshing.Remove(key); }
        });
    }

    // For a diagnostic line: which service, never the source itself. Google avatars are named apart from its media.
    private static string Provider(string? source) =>
        source?.StartsWith("google-avatar:", StringComparison.Ordinal) == true ? "google" : ProviderMedia.DiagnosticFor(source);

    // Puts a fetched picture on disk and says whether it was the one already kept. The same bytes only renew the entry
    // they came from: a rewrite would protect, purge, insert and commit on its own under the media cache's one gate, and
    // after every launch nearly every kept avatar comes back unchanged, all of them in one burst that the open
    // conversation's photos wait behind. Renewals are batched with the cache's read marks. A different picture, or a
    // fetch with nothing kept to compare against, is written as before.
    private async Task<bool> KeepAsync(string profile, string id, byte[] bytes, byte[]? keptHash, CancellationToken token)
    {
        if (keptHash is not null && SHA256.HashData(bytes).AsSpan().SequenceEqual(keptHash))
        {
            disk.Renew(profile, id, KeptFor);
            Interlocked.Increment(ref renewed);
            return true;
        }
        await disk.WriteAsync(profile, id, bytes, KeptFor, token);
        return false;
    }
    private static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);

    // One line a minute after the first check is queued, when a launch's burst has long finished: how the kept pictures
    // compared, and how many entries were renewed rather than rewritten. Counts only.
    private int same, changed, dropped, renewed, summarized;
    internal TimeSpan SummaryDelay { get; set; } = TimeSpan.FromMinutes(1);
    internal (int Same, int Changed, int Dropped, int Renewed) RefreshCounts =>
        (Volatile.Read(ref same), Volatile.Read(ref changed), Volatile.Read(ref dropped), Volatile.Read(ref renewed));
    private void StartSummary()
    {
        if (Interlocked.Exchange(ref summarized, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(SummaryDelay, lifetime.Token); }
            catch (OperationCanceledException) { return; }
            var (kept, replaced, gone, renewals) = RefreshCounts;
            AppLog.Write($"avatar refresh same={kept} changed={replaced} dropped={gone} renewed={renewals}");
        });
    }

    private void QueueRefresh(string profile, string source, byte[] saved)
    {
        var key = profile + "\n" + source;
        lock (refreshing) if (!refreshing.Add(key)) return;
        StartSummary();
        var savedHash = SHA256.HashData(saved);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RefreshDelay, lifetime.Token);
                var bytes = await media.ReadImageAsync(AvatarSource(source), lifetime.Token);
                if (bytes is not { Length: > 0 and <= 4 * 1024 * 1024 } || profile != await NotificationIdentity.ProfileAsync(tokens, lifetime.Token)) return;
                if (await KeepAsync(profile, CacheKey(source), bytes, savedHash, lifetime.Token)) Interlocked.Increment(ref same);
                else { Interlocked.Increment(ref changed); Changed?.Invoke(this, source); }
            }
            catch { /* Keep the cached face; the next app session checks again. */ }
            finally { lock (refreshing) refreshing.Remove(key); }
        });
    }

    // After their kept pictures were deleted: each id is announced, so a picture already decoded or drawn for it is dropped
    // and asked for again. The sources remembered per conversation need nothing: they are read from the conversation's
    // current summary and expire within minutes.
    public void Forget(IEnumerable<string> ids)
    {
        foreach (var id in ids.Distinct(StringComparer.Ordinal)) Changed?.Invoke(this, id);
    }

    public void Dispose() => lifetime.Cancel();
}
