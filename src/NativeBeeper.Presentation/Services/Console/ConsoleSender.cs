using System.Globalization;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// Where a console text goes: one provider conversation, fixed when the target was chosen and never moved afterwards.
/// </summary>
/// <param name="SourceChatId">The provider conversation the text is submitted to; never a merged id.</param>
/// <param name="DisplayChatId">Where the conversation is shown (its merged conversation when it has one).</param>
/// <param name="Title">The name the prompt shows: the conversation's (or merged person's) title.</param>
/// <param name="Service">The source's route label: "Messenger", or "Google Voice · Work" where accounts need telling apart.</param>
/// <param name="Target">The named conversation the recipient was resolved from, when there is one.</param>
public sealed record ConsoleRecipient(string SourceChatId, string DisplayChatId, string Title, string Service, string Network,
    string AccountId, ChatTarget? Target);

public abstract record ConsoleSendResult
{
    /// <summary>In the outbox under this local id; follow it through <see cref="ConsoleSender.Updated"/>.</summary>
    public sealed record Submitted(string LocalId) : ConsoleSendResult;
    /// <summary>Nothing was submitted, for the reason given in the console's words.</summary>
    public sealed record Refused(string Message) : ConsoleSendResult;
}

/// <summary>A console send's state moved. Notice is the line to print with it, when there is one.</summary>
public sealed record ConsoleSendUpdate(string LocalId, ConsoleSendState State, string? Notice, ConsoleLineKind NoticeKind = ConsoleLineKind.Error);

public abstract record ConsoleRoute
{
    public sealed record To(ConsoleRecipient Recipient) : ConsoleRoute;
    public sealed record Refused(string Message, bool NeedsService) : ConsoleRoute;
}

/// <summary>
/// Sends a console text through the same outbox the composer uses: one submission per Enter, never a retry, confirmed by
/// the outbox's lookups only. A refused send is taken back out of the outbox once the console has said so: its text is
/// in the console's own Up history, so neither Quit nor the conversation keeps offering it. Sends no typing and marks
/// nothing read. Runs on the interface thread, as the outbox does.
/// </summary>
public sealed class ConsoleSender : IDisposable
{
    private readonly IMessageSender sender;
    private readonly Outbox outbox;
    private readonly ConversationTargets targets;
    private readonly MergedConversations merges;
    private readonly TimeProvider clock;
    private readonly Action<string> log;
    private readonly IMessageActions? replies;
    private readonly object gate = new();
    // Every console send still worth following, by local id: its chat, its state so far and every provider id it has had.
    private readonly Dictionary<string, Tracked> tracked = new(StringComparer.Ordinal);
    // What the outbox said about sends whose local id Submit had not yet returned: a provider that answers at once moves
    // the send on before Submit gives its id back.
    private readonly Dictionary<string, OutgoingMessage?> early = new(StringComparer.Ordinal);
    private int submitting;
    private const int MaxTracked = 200;

    private sealed class Tracked(string localId, string chatId, string provider, long started)
    {
        public string LocalId { get; } = localId;
        public string ChatId { get; } = chatId;
        public string Provider { get; } = provider;
        public long Started { get; } = started;
        public ConsoleSendState State { get; set; } = ConsoleSendState.Sending;
        public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
        public bool Confirmed { get; set; }
        public bool InOutbox { get; set; } = true;
    }

    /// <summary>A console send's state moved. Raised on whatever thread the outbox raised its own change.</summary>
    public event EventHandler<ConsoleSendUpdate>? Updated;

    /// <param name="replies">The provider's reply operation, as the composer's quoted reply uses it; without it the console
    /// cannot send a quoted reply.</param>
    public ConsoleSender(IMessageSender sender, Outbox outbox, ConversationTargets targets, MergedConversations merges,
        TimeProvider? clock = null, Action<string>? log = null, IMessageActions? replies = null)
    {
        this.replies = replies;
        this.sender = sender;
        this.outbox = outbox;
        this.targets = targets;
        this.merges = merges;
        this.clock = clock ?? TimeProvider.System;
        this.log = log ?? (_ => { });
        outbox.ItemChanged += Outbox_ItemChanged;
        outbox.Resolved += Outbox_Resolved;
        outbox.BatchResolved += Outbox_BatchResolved;
    }

    public void Dispose()
    {
        outbox.ItemChanged -= Outbox_ItemChanged;
        outbox.Resolved -= Outbox_Resolved;
        outbox.BatchResolved -= Outbox_BatchResolved;
    }

    /// <summary>
    /// The source conversation a text to this target goes through (ConversationTargets.Route), as a recipient; or why there
    /// is none. Never a merged id, and never changes a merged person's saved sending choice.
    /// </summary>
    public ConsoleRoute Route(ChatTarget target, string? service, Func<string, ChatMessage?> newestIncoming)
    {
        switch (targets.Route(target, service, newestIncoming))
        {
            case RouteResult.To { Source: var source } when !MergedConversations.IsMerged(source.Id):
                return new ConsoleRoute.To(new ConsoleRecipient(source.Id, target.DisplayId, target.Title, merges.ServiceRouteLabel(source),
                    source.Network, source.AccountId, target));
            case RouteResult.NeedsService { Services: var services } when services.Count > 0:
                return new ConsoleRoute.Refused(NeedsServiceText(target.Title, services), true);
            default:
                return new ConsoleRoute.Refused($"{target.Title} can't be sent a text from here.", false);
        }
    }

    // "Alex is linked on Messenger and Instagram, and nothing here says which Alex last wrote on. Add @messenger or @instagram."
    private static string NeedsServiceText(string title, IReadOnlyList<string> services)
    {
        var words = services.Select(ServiceWord).ToArray();
        if (services.Count == 1) return $"{title} can only be reached on {services[0]} from here. Add @{words[0]}.";
        return $"{title} is linked on {Join(services, "and")}, and nothing here says which {title} last wrote on. Add {Join([.. words.Select(w => "@" + w)], "or")}.";
    }

    private static string Join(IReadOnlyList<string> items, string last) =>
        items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " " + last + " " + items[^1];

    /// <summary>The word that names a route label after @: google, messenger, instagram, voice or voice:label.</summary>
    public static string ServiceWord(string routeLabel)
    {
        foreach (var (network, word) in new[] { ("Google Messages", "google"), ("Messenger", "messenger"), ("Instagram", "instagram"), ("Google Voice", "voice") })
        {
            if (routeLabel == network) return word;
            if (routeLabel.StartsWith(network + " · ", StringComparison.Ordinal))
                return word == "voice" ? "voice:" + routeLabel[(network.Length + 3)..] : word;
        }
        return routeLabel.ToLowerInvariant();
    }

    /// <summary>
    /// Checks the source may be sent to now, then submits the text once. A pending permission, a refusal, a text too long
    /// and a full outbox each refuse with nothing queued; nothing is ever retried.
    /// </summary>
    public Task<ConsoleSendResult> SendAsync(ConsoleRecipient to, string text, CancellationToken cancellationToken = default) =>
        SubmitAsync(to, text, null, cancellationToken);

    /// <summary>Whether a quoted reply can be submitted here at all (the reply operation was given).</summary>
    public bool CanReply => replies is not null;

    /// <summary>
    /// <see cref="SendAsync"/> as a quoted reply to <paramref name="replyToId"/>, through the provider's reply operation:
    /// the same checks, one submission, never retried. The caller has already asked the message menu whether the message
    /// can be replied to.
    /// </summary>
    public Task<ConsoleSendResult> SendReplyAsync(ConsoleRecipient to, string text, string replyToId, CancellationToken cancellationToken = default) =>
        replies is null ? Task.FromResult<ConsoleSendResult>(new ConsoleSendResult.Refused("Not sent: replies aren't available here."))
            : SubmitAsync(to, text, replyToId, cancellationToken);

    private async Task<ConsoleSendResult> SubmitAsync(ConsoleRecipient to, string text, string? replyToId, CancellationToken cancellationToken)
    {
        var service = to.Network.Length > 0 ? to.Network : ServiceScopes.Name(to.SourceChatId);
        if (MergedConversations.IsMerged(to.SourceChatId)) return new ConsoleSendResult.Refused($"Not sent: {to.Title} has no conversation to send through.");
        SendPermission permission;
        try { permission = await sender.GetSendPermissionAsync(to.SourceChatId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new ConsoleSendResult.Refused($"Not sent: {service} couldn't say whether it can send right now. Nothing was queued."); }
        if (!permission.Allowed && permission.Pending) return new ConsoleSendResult.Refused($"Not sent: {service} is still connecting. Nothing was queued.");
        if (!permission.Allowed)
            return new ConsoleSendResult.Refused("Not sent: " + (string.IsNullOrWhiteSpace(permission.Reason) ? $"{service} can't send to this conversation right now." : permission.Reason.Trim()));
        var max = Math.Min(permission.MaxTextLength ?? 65536, 65536);
        if (text.Length > max) return new ConsoleSendResult.Refused($"Not sent: that's longer than {service} allows ({max.ToString("N0", CultureInfo.CurrentCulture)} characters).");

        string localId;
        var started = clock.GetTimestamp();
        Interlocked.Increment(ref submitting);
        try
        {
            localId = replyToId is null ? outbox.Submit(to.SourceChatId, text)
                : outbox.SubmitReply(to.SourceChatId, text, replyToId, ct => replies!.ReplyAsync(to.SourceChatId, replyToId, text, ct));
        }
        catch (InvalidOperationException) { return new ConsoleSendResult.Refused("Not sent: review pending sends in their conversations first."); }
        finally { Interlocked.Decrement(ref submitting); }

        var item = new Tracked(localId, to.SourceChatId, ConsoleLineFormatter.Provider(service), started);
        OutgoingMessage? seen;
        bool hadEarly;
        lock (gate)
        {
            hadEarly = early.Remove(localId, out seen);
            tracked[localId] = item;
            early.Clear();
        }
        log($"console send provider={item.Provider} outcome=sending ms=0" + (replyToId is null ? "" : " kind=reply"));
        // Whatever the outbox already did with it before Submit returned: a provider that answers at once has moved it on.
        if (hadEarly && seen is not null) Note(item, seen);
        Evaluate(localId);
        return new ConsoleSendResult.Submitted(localId);
    }

    /// <summary>The console send a provider message is the echo of: the id the send was accepted under, confirmed as, or
    /// resolved to. Null when it is none of them.</summary>
    public string? ClaimEcho(ChatMessage message)
    {
        lock (gate)
            foreach (var item in tracked.Values)
                if (item.ChatId == message.ChatId && item.Ids.Contains(message.Id)) return item.LocalId;
        return null;
    }

    /// <summary>Whether a console send in this conversation may still learn the id its echo will carry: an echo there
    /// is worth holding a moment rather than printing twice.</summary>
    public bool MayClaimLater(string chatId)
    {
        lock (gate)
            return tracked.Values.Any(item => item.ChatId == chatId && item.InOutbox && !item.Confirmed &&
                item.State is ConsoleSendState.Sending or ConsoleSendState.Sent or ConsoleSendState.Unconfirmed);
    }

    /// <summary>Forgets every send (the console was cleared or the account changed). The outbox keeps them.</summary>
    public void Forget()
    {
        lock (gate) { tracked.Clear(); early.Clear(); }
    }

    private void Outbox_ItemChanged(object? source, string localId)
    {
        var item = outbox.Find(localId);
        lock (gate)
        {
            if (!tracked.ContainsKey(localId))
            {
                if (Volatile.Read(ref submitting) > 0) early[localId] = item ?? (early.TryGetValue(localId, out var last) ? last : null);
                return;
            }
        }
        Evaluate(localId);
    }

    private void Outbox_Resolved(object? source, ChatMessage message)
    {
        Tracked[] items;
        lock (gate) items = [.. tracked.Values];
        foreach (var item in items)
            if (outbox.Find(item.LocalId) is { ConfirmedId: { } confirmed } found && confirmed == message.Id)
            {
                lock (gate) { item.Ids.Add(message.Id); item.Confirmed = true; }
                Note(item, found);
            }
        // Resolved while Submit was still under way: remember the id against the early record.
        lock (gate)
            foreach (var (localId, _) in early.ToArray())
                if (outbox.Find(localId) is { ConfirmedId: { } id } found && id == message.Id) early[localId] = found;
    }

    private void Outbox_BatchResolved(object? source, ResolvedOutgoingBatch batch)
    {
        lock (gate)
            if (tracked.TryGetValue(batch.Outgoing.LocalId, out var item))
            {
                foreach (var message in batch.Messages) item.Ids.Add(message.Id);
                item.Confirmed = true;
            }
    }

    // Takes every provider id the outbox has for a send.
    private void Note(Tracked item, OutgoingMessage found)
    {
        lock (gate)
        {
            if (found.PendingId is { } pending) item.Ids.Add(pending);
            if (found.ConfirmedId is { } confirmed) { item.Ids.Add(confirmed); item.Confirmed = true; }
            foreach (var id in found.PendingMessageIds ?? []) item.Ids.Add(id);
        }
    }

    private void Evaluate(string localId)
    {
        Tracked? item;
        lock (gate) tracked.TryGetValue(localId, out item);
        if (item is null) return;
        var found = outbox.Find(localId);
        ConsoleSendState next;
        string? notice = null;
        var noticeKind = ConsoleLineKind.Error;
        if (found is not null)
        {
            Note(item, found);
            next = found.State switch
            {
                OutgoingState.Pending => found.Unconfirmed ? ConsoleSendState.Unconfirmed : ConsoleSendState.Sent,
                OutgoingState.Rejected => ConsoleSendState.NotSent,
                OutgoingState.Unknown => ConsoleSendState.Unknown,
                _ => ConsoleSendState.Sending
            };
            if (next == ConsoleSendState.NotSent)
            {
                var why = string.IsNullOrWhiteSpace(found.Explanation) ? "" : found.Explanation.Trim();
                if (why.Length > 0 && !why.EndsWith('.')) why += ".";
                notice = "Not sent: " + (why.Length > 0 ? why + " " : "") + "Press Up to get the text back.";
            }
            else if (next == ConsoleSendState.Unknown) notice = "Outcome unknown. Check the conversation (/msgs) before sending again.";
            else if (next == ConsoleSendState.Unconfirmed) { notice = "Not confirmed yet. It may still arrive."; noticeKind = ConsoleLineKind.Info; }
        }
        else
        {
            lock (gate) item.InOutbox = false;
            bool accepted;
            lock (gate) accepted = item.Ids.Count > 0;
            // Removed after the provider accepted it: confirmed. A refusal taken back by the composer stays refused; a send
            // that vanished before any answer (the account changed under it) has no known outcome.
            next = accepted ? ConsoleSendState.Sent : item.State == ConsoleSendState.NotSent ? ConsoleSendState.NotSent : ConsoleSendState.Unknown;
            if (next == ConsoleSendState.Unknown && item.State != ConsoleSendState.Unknown)
                notice = "Outcome unknown. Check the conversation (/msgs) before sending again.";
        }
        bool changed;
        lock (gate)
        {
            changed = item.State != next;
            item.State = next;
            // Out of the outbox with no provider id, nothing can be its echo. One with an id is kept a while, because the
            // stream's copy of a confirmation can arrive after the outbox has let it go.
            if (!item.InOutbox && item.Ids.Count == 0) tracked.Remove(localId);
            if (tracked.Count > MaxTracked)
                foreach (var old in tracked.Values.Where(t => !t.InOutbox).OrderBy(t => t.Started).Take(tracked.Count - MaxTracked).ToArray())
                    tracked.Remove(old.LocalId);
        }
        if (!changed) return;
        var outcome = next switch
        {
            ConsoleSendState.Sent => "accepted",
            ConsoleSendState.NotSent => "rejected",
            ConsoleSendState.Unknown => "unknown",
            ConsoleSendState.Unconfirmed => "unconfirmed",
            _ => "sending"
        };
        var ms = (long)clock.GetElapsedTime(item.Started).TotalMilliseconds;
        log($"console send provider={item.Provider} outcome={outcome} ms={ms.ToString(CultureInfo.InvariantCulture)}");
        Updated?.Invoke(this, new ConsoleSendUpdate(localId, next, notice, noticeKind));
        // Said and in the Up history: the refused record leaves the outbox. After the state is kept, so the outbox's
        // removal reads back as refused rather than vanished.
        if (next == ConsoleSendState.NotSent && found is not null) outbox.DiscardRejected(localId);
    }
}
