package main

import (
	"context"
	"errors"
	"sync/atomic"

	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
)

// How many requests this process has made of Instagram, by kind. Being throttled on Instagram risks a checkpoint on
// the real account, so every change to how often the app asks has to be judged against a measured rate rather than an
// estimate, and nothing else here could say what that rate is: the client logs nothing and the app wrote nothing.
//
// Counts only — no ids, no names, no text — and kept apart from readSnapshot on purpose. That snapshot is rebuilt at
// every login and close, which would reset these mid-session and make the app's deltas lie, and it is copied into the
// counts frame written for every upstream event, which these have no business riding on. They reach the app only in
// the answer to "snapshot", which is answered from memory and asks Instagram nothing.
type requestLedger struct {
	index, mailbox, mailboxPages, threads, messagePages, mutations, directory, media, failures atomic.Uint64
	// Requests answered request_cancelled: a read the app stopped waiting for, aborted or never started. Not a request
	// of Instagram, but the measure of how much reading ahead of the screen the dispatcher now spares it.
	cancelled atomic.Uint64
	typing    typingLedger
	deltas    deltaLedger
}

// Typing, in both directions. The typing stream and the socket typing is sent on are websockets rather than GraphQL,
// so none of it is in the request counts above; it is counted apart so that it can be seen to add none of those.
type typingLedger struct {
	in, out, unplaced, failed, skipped, reopened atomic.Uint64
	// How often upstream reopened the typing stream by itself (flap.go), which the flap guard is judged by.
	reconnects atomic.Uint64
	// The last time-to-live Instagram gave a typing report, as a bare number: its unit is not documented, and live
	// numbers are what will say whether it could replace the fixed six seconds upstream uses.
	ttl atomic.Int64
}

type requestCounts struct {
	Index        uint64 `json:"index"`
	Mailbox      uint64 `json:"mailbox"`
	MailboxPages uint64 `json:"mailboxPages"`
	Threads      uint64 `json:"threads"`
	MessagePages uint64 `json:"messagePages"`
	Mutations    uint64 `json:"mutations"`
	Directory    uint64 `json:"directory"`
	Media        uint64 `json:"media"`
	// Requests Instagram refused or did not answer in time; not one the app cancelled. The list read swallows a failed
	// refresh whenever it has a list to fall back on, so without this a session Instagram had stopped answering looked
	// exactly like a quiet one.
	Failures  uint64 `json:"failures"`
	Cancelled uint64 `json:"cancelled"`
}

type typingCounts struct {
	In         uint64 `json:"in"`
	Out        uint64 `json:"out"`
	Unplaced   uint64 `json:"unplaced"`
	Failed     uint64 `json:"failed"`
	Skipped    uint64 `json:"skipped"`
	Reopened   uint64 `json:"reopened"`
	Reconnects uint64 `json:"reconnects"`
	TTL        int64  `json:"ttl"`
}

// One request of a kind, and whether it failed. Called once per request actually made, never for one refused locally
// before reaching Instagram, so the counts are what Instagram saw.
//
// A request that ended because its call was cancelled is not a failure. The dispatcher aborts a read the app stopped
// waiting for, and every call at end of input, so clicking through conversations would otherwise raise failures on a
// healthy session, and failures is the one place a refusing session shows. Those reads are in cancelled instead. A
// call that ran out of its budget still counts: Instagram not answering in time is what failures is for.
func (l *requestLedger) note(ctx context.Context, kind *atomic.Uint64, failed bool) {
	kind.Add(1)
	if failed && !errors.Is(ctx.Err(), context.Canceled) {
		l.failures.Add(1)
	}
}

func (l *requestLedger) counts() requestCounts {
	return requestCounts{
		Index: l.index.Load(), Mailbox: l.mailbox.Load(), MailboxPages: l.mailboxPages.Load(), Threads: l.threads.Load(),
		MessagePages: l.messagePages.Load(), Mutations: l.mutations.Load(), Directory: l.directory.Load(),
		Media: l.media.Load(), Failures: l.failures.Load(), Cancelled: l.cancelled.Load(),
	}
}

func (l *typingLedger) counts() typingCounts {
	return typingCounts{In: l.in.Load(), Out: l.out.Load(), Unplaced: l.unplaced.Load(), Failed: l.failed.Load(),
		Skipped: l.skipped.Load(), Reopened: l.reopened.Load(), Reconnects: l.reconnects.Load(), TTL: l.ttl.Load()}
}

// The answer to "snapshot": the connection's state as before, and the ledger beside it rather than inside it.
type snapshotReply struct {
	readSnapshot
	Requests requestCounts `json:"requests"`
	Typing   typingCounts  `json:"typing"`
	Realtime deltaCounts   `json:"realtime"`
}

// What arrived on the realtime connection, by kind, counted as it arrives and never acted on. The list is still kept
// fresh by the app's poll, because no delta shape has been verified against the real account; these counts, beside the
// arrivals the poll announced over the same quarter of an hour, are what will say whether the deltas are a faithful
// record of what happened and could one day stand in for polling. Counts only: no ids, no senders, no text.
type deltaLedger struct {
	total, messages, incoming, reads, reactions, edits, other atomic.Uint64
}

type deltaCounts struct {
	Total uint64 `json:"total"`
	// New messages, and of those the ones someone else sent: what the poll's arrivals should match.
	Messages uint64 `json:"messages"`
	Incoming uint64 `json:"incoming"`
	// Read markers, this account's and other people's receipts alike, and marking unread.
	Reads     uint64 `json:"reads"`
	Reactions uint64 `json:"reactions"`
	// Corrections and messages taken back.
	Edits uint64 `json:"edits"`
	Other uint64 `json:"other"`
}

// One delta, sorted by upstream's own decoding of its type. self is this account's messaging id, zero when not known,
// in which case no new message is counted as someone else's.
func (l *deltaLedger) note(delta *slidetypes.Delta, self int64) {
	if delta == nil {
		return
	}
	l.total.Add(1)
	switch data := delta.Data.(type) {
	case *slidetypes.NewMessageEvent:
		l.messages.Add(1)
		if self > 0 && data.Message != nil && data.Message.SenderFBID != 0 && data.Message.SenderFBID != self {
			l.incoming.Add(1)
		}
	case *slidetypes.MarkReadEvent, *slidetypes.ReadReceiptEvent, *slidetypes.MarkUnreadEvent:
		l.reads.Add(1)
	case *slidetypes.CreateReactionEvent, *slidetypes.DeleteReactionEvent:
		l.reactions.Add(1)
	case *slidetypes.EditMessageEvent, *slidetypes.DeleteMessageEvent:
		l.edits.Add(1)
	default:
		l.other.Add(1)
	}
}

func (l *deltaLedger) counts() deltaCounts {
	return deltaCounts{Total: l.total.Load(), Messages: l.messages.Load(), Incoming: l.incoming.Load(), Reads: l.reads.Load(),
		Reactions: l.reactions.Load(), Edits: l.edits.Load(), Other: l.other.Load()}
}
