package main

import (
	"context"
	"sync"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// A Lightspeed task reply can be an empty acknowledgement: its history arrives on the database stream later.
// After a socket reconnect, do not interpret that acknowledgement as either a page or a working subscription.
// Recovery needs two different ordinary conversations, known older neighbours, and one further read after a quiet
// grace period. This detector never starts a connection, sends a message, or marks anything read.
const historyRecoveryGrace = 10 * time.Second
const historyRecoveryAnswerWait = 8 * time.Second

type historyRecoveryTicket struct {
	generation uint64
	answers    uint64
	active     bool
}

type historyRecovery struct {
	mu          sync.Mutex
	generation  uint64
	answers     uint64
	interrupted bool
	armed       bool
	attempted   bool
	signalled   bool
	suspects    map[int64]pageAnchor
	cancel      context.CancelFunc
	// Offline checks control the two waits without shortening the production grace period.
	wait func(context.Context, time.Duration) bool
}

func (a *adapter) historyConnectionInterrupted() {
	h := &a.historyRecovery
	h.mu.Lock()
	defer h.mu.Unlock()
	h.generation++
	h.interrupted, h.armed, h.attempted, h.signalled = true, false, false, false
	h.suspects = nil
	if h.cancel != nil {
		h.cancel()
		h.cancel = nil
	}
}

func (a *adapter) historyConnectionResumed() {
	h := &a.historyRecovery
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.interrupted {
		h.interrupted, h.armed = false, true
	}
}

// Take this before issuing the original anchored request. A result from an older connection, or before another
// history answer landed, cannot become evidence against the current connection.
func (a *adapter) historyRecoveryAttempt() historyRecoveryTicket {
	h := &a.historyRecovery
	h.mu.Lock()
	defer h.mu.Unlock()
	return historyRecoveryTicket{h.generation, h.answers, h.armed}
}

func (h *historyRecovery) matches(t historyRecoveryTicket) bool {
	return t.active && h.armed && t.generation == h.generation && t.answers == h.answers
}

// Receipt/typing/thread-only traffic is not an answer to a history read. Actual messages or attachments conservatively
// clear suspicion even when they could be live arrivals. Range completion counts for a legitimately empty page. An
// explicit server failure prevents this heuristic from diagnosing it as an unresponsive subscription.
func (a *adapter) observeHistoryTable(tbl *table.LSTable) {
	if tbl == nil {
		return
	}
	failure := len(tbl.LSHandleSyncFailure) > 0 || len(tbl.LSHandleFailedTask) > 0 || len(tbl.LSIssueNewError) > 0
	for _, row := range tbl.LSMailboxTaskCompletionApiOnTaskCompletion {
		failure = failure || !row.Success
	}
	attachments := len(tbl.LSInsertBlobAttachment) + len(tbl.LSInsertXmaAttachment) + len(tbl.LSInsertAttachment) +
		len(tbl.LSInsertStickerAttachment) + len(tbl.LSInsertAttachmentItem) + len(tbl.LSInsertAttachmentCta)
	messages := len(tbl.LSUpsertMessage) + len(tbl.LSInsertMessage) + len(tbl.LSDeleteThenInsertMessage)
	if !failure && attachments == 0 && messages == 0 && len(tbl.LSInsertNewMessageRange) == 0 && len(tbl.LSUpdateExistingMessageRange) == 0 {
		return
	}
	h := &a.historyRecovery
	h.mu.Lock()
	defer h.mu.Unlock()
	h.answers++
	h.suspects = nil
	if failure {
		h.armed = false
	}
	if h.cancel != nil {
		h.cancel()
		h.cancel = nil
	}
}

// A successful anchored refresh can also have returned attachment rows alone. Its revision wait excludes receipt-
// only changes. Count it only against the generation in which that request started.
func (a *adapter) historyRefreshAnswered(ticket historyRecoveryTicket) {
	h := &a.historyRecovery
	h.mu.Lock()
	defer h.mu.Unlock()
	if !h.matches(ticket) {
		return
	}
	h.answers++
	h.suspects = nil
	if h.cancel != nil {
		h.cancel()
		h.cancel = nil
	}
}

// Only completely empty acknowledged replies qualify. A range, explicit task failure, or any other content is
// positive protocol information, not the silent history failure this recovery handles.
func historyEmptyAcknowledgement(tbl *table.LSTable) bool {
	return tbl != nil && len(tbl.NonNilFields()) == 0
}

func (s *store) historyRecoveryAnchor(key int64, anchor pageAnchor) bool {
	if anchor.id == "" || anchor.at <= 0 {
		return false
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	thread := s.threads[key]
	if thread == nil || thread.deleted || thread.unresolved {
		return false
	}
	// Restrict automatic recovery to ordinary chat kinds whose history this app already understands.
	switch thread.kind {
	case table.ONE_TO_ONE, table.GROUP_THREAD, table.MARKETPLACE:
	default:
		return false
	}
	known := s.messages[key]
	current := known[anchor.id]
	if current == nil || current.encrypted || current.unsent || current.timestamp != anchor.at {
		return false
	}
	for _, older := range known {
		if !older.encrypted && !older.unsent && older.timestamp < anchor.at {
			return true
		}
	}
	return false
}

// Called only after the regular refresh's wait ran out with a successful (nil-error) fetch. Errors and cancellation
// are never counted. Repeated opens of one unavailable conversation cannot trigger account-wide recovery.
func (a *adapter) historyUnanswered(ctx context.Context, key int64, anchor pageAnchor, ticket historyRecoveryTicket, tbl *table.LSTable) {
	if ctx.Err() != nil || a.lifetime.Err() != nil || !ticket.active || !historyEmptyAcknowledgement(tbl) || !a.chats.historyRecoveryAnchor(key, anchor) {
		return
	}
	h := &a.historyRecovery
	h.mu.Lock()
	defer h.mu.Unlock()
	if !h.matches(ticket) || h.attempted {
		return
	}
	if h.suspects == nil {
		h.suspects = make(map[int64]pageAnchor, 2)
	}
	h.suspects[key] = anchor
	if len(h.suspects) < 2 {
		return
	}
	h.attempted = true // At most one extra read, even if that confirmation itself errors or the answer arrives late.
	confirmationCtx, cancel := context.WithCancel(a.lifetime)
	h.cancel = cancel
	go func() {
		defer cancel()
		a.confirmHistoryStall(confirmationCtx, ticket, key, anchor)
	}()
}

func historyRecoveryDelay(ctx context.Context, delay time.Duration) bool {
	timer := time.NewTimer(delay)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-timer.C:
		return ctx.Err() == nil
	}
}

func (a *adapter) historyRecoveryCandidatesValid(ticket historyRecoveryTicket) bool {
	h := &a.historyRecovery
	h.mu.Lock()
	if !h.matches(ticket) || len(h.suspects) != 2 {
		h.mu.Unlock()
		return false
	}
	candidates := make(map[int64]pageAnchor, 2)
	for key, anchor := range h.suspects {
		candidates[key] = anchor
	}
	h.mu.Unlock()
	for key, anchor := range candidates {
		if !a.chats.historyRecoveryAnchor(key, anchor) {
			return false
		}
	}
	return true
}

func (a *adapter) confirmHistoryStall(ctx context.Context, ticket historyRecoveryTicket, key int64, anchor pageAnchor) {
	h := &a.historyRecovery
	wait := h.wait
	if wait == nil {
		wait = historyRecoveryDelay
	}
	if !wait(ctx, historyRecoveryGrace) || !a.historyRecoveryCandidatesValid(ticket) {
		return
	}
	h.mu.Lock()
	valid := h.matches(ticket) && !h.signalled
	h.mu.Unlock()
	if !valid || ctx.Err() != nil {
		return
	}
	// This is the same existing read-only FetchMessagesTask, with an explicit deadline and no recursive retry.
	readCtx, cancel := context.WithTimeout(ctx, 20*time.Second)
	tbl, err := a.fetchFrom(readCtx, key, anchor)
	cancel()
	if err != nil || a.lifetime.Err() != nil {
		return
	}
	// A late socket answer can cancel the watchdog while a valid direct response is already returning. That cancels
	// further diagnostic waiting, not persistence of received data. Only helper shutdown cancels this apply.
	touched := a.applyTable(a.lifetime, tbl)
	a.observeHistoryTable(tbl)
	if !touched.empty() {
		a.out.write(response{Event: "changed", Result: touched})
	}
	if !historyEmptyAcknowledgement(tbl) || !wait(ctx, historyRecoveryAnswerWait) || !a.historyRecoveryCandidatesValid(ticket) {
		return
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	if ctx.Err() != nil || !h.matches(ticket) || h.signalled {
		return
	}
	h.signalled = true
	logf("history recovery outcome=stalled chats=2 confirmation=unanswered")
	// Keep the generation check and emission together. The consumer defers restarting until mutations are idle.
	a.out.write(response{Event: "history_stalled", Result: struct{}{}})
}
