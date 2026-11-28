package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

type historyRecoveryBuffer struct {
	mu sync.Mutex
	bytes.Buffer
}

func (b *historyRecoveryBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.Buffer.Write(p)
}

func (b *historyRecoveryBuffer) stalled() int {
	b.mu.Lock()
	defer b.mu.Unlock()
	return strings.Count(b.Buffer.String(), `"event":"history_stalled"`)
}

type historyRecoveryWait struct {
	delay   time.Duration
	release chan struct{}
	left    chan struct{}
}

type historyRecoveryFixture struct {
	a       *adapter
	out     *historyRecoveryBuffer
	waits   chan historyRecoveryWait
	fetches atomic.Int32
}

func newHistoryRecoveryFixture(t *testing.T) *historyRecoveryFixture {
	t.Helper()
	t.Setenv("MESSAGELINK_DATA_ROOT", t.TempDir())
	f := &historyRecoveryFixture{out: &historyRecoveryBuffer{}, waits: make(chan historyRecoveryWait, 4)}
	f.a = newAdapter(&output{encoder: json.NewEncoder(f.out)})
	t.Cleanup(f.a.stop)
	for _, key := range []int64{7, 8, 9} {
		f.a.chats.get(key).kind = table.GROUP_THREAD
		f.a.chats.messages[key] = map[string]*message{
			fmt.Sprintf("new-%d", key): {id: fmt.Sprintf("new-%d", key), timestamp: 2000},
			fmt.Sprintf("old-%d", key): {id: fmt.Sprintf("old-%d", key), timestamp: 1000},
		}
	}
	f.a.fetch = func(context.Context, int64, pageAnchor) (*table.LSTable, error) {
		f.fetches.Add(1)
		return &table.LSTable{}, nil
	}
	f.a.historyRecovery.wait = func(ctx context.Context, delay time.Duration) bool {
		step := historyRecoveryWait{delay: delay, release: make(chan struct{}), left: make(chan struct{})}
		defer close(step.left)
		f.waits <- step
		select {
		case <-ctx.Done():
			return false
		case <-step.release:
			return ctx.Err() == nil
		}
	}
	return f
}

func (f *historyRecoveryFixture) reconnect() {
	f.a.historyConnectionInterrupted()
	f.a.historyConnectionResumed()
}

func (f *historyRecoveryFixture) unanswered(key int64) {
	f.a.historyUnanswered(context.Background(), key, pageAnchor{id: fmt.Sprintf("new-%d", key), at: 2000},
		f.a.historyRecoveryAttempt(), &table.LSTable{})
}

func (f *historyRecoveryFixture) nextWait(t *testing.T, delay time.Duration) historyRecoveryWait {
	t.Helper()
	select {
	case wait := <-f.waits:
		if wait.delay != delay {
			t.Fatalf("wait=%v, want %v", wait.delay, delay)
		}
		return wait
	case <-time.After(3 * time.Second):
		t.Fatal("confirmation did not reach its controlled wait")
		return historyRecoveryWait{}
	}
}

func waitHistoryRecoveryExit(t *testing.T, step historyRecoveryWait) {
	t.Helper()
	select {
	case <-step.left:
	case <-time.After(3 * time.Second):
		t.Fatal("cancelled recovery wait remained alive")
	}
}

func TestHistoryRecoveryRequiresReconnectTwoChatsAndPacedConfirmation(t *testing.T) {
	f := newHistoryRecoveryFixture(t)
	f.unanswered(7)
	f.unanswered(8)
	if f.a.historyRecovery.attempted {
		t.Fatal("ordinary startup triggered reconnect recovery")
	}
	f.reconnect()
	f.unanswered(7)
	f.unanswered(7)
	if f.a.historyRecovery.attempted {
		t.Fatal("repeated visits to one conversation triggered account recovery")
	}
	f.unanswered(8)
	grace := f.nextWait(t, historyRecoveryGrace)
	if f.fetches.Load() != 0 || f.out.stalled() != 0 {
		t.Fatal("recovery acted before late answers had a grace period")
	}
	close(grace.release)
	answerWait := f.nextWait(t, historyRecoveryAnswerWait)
	if f.fetches.Load() != 1 || f.out.stalled() != 0 {
		t.Fatal("confirmation must be exactly one read and wait for its asynchronous answer")
	}
	// Receipt-only activity does not demonstrate that the history subscription answered.
	f.a.observeHistoryTable(&table.LSTable{LSUpdateReadReceipt: []*table.LSUpdateReadReceipt{{ThreadKey: 7}}})
	close(answerWait.release)
	if !settle(t, func() bool { return f.out.stalled() == 1 }) {
		t.Fatal("confirmed history stall was not reported")
	}
	f.unanswered(9)
	f.a.historyConnectionResumed() // Duplicate status cannot authorize another read in this generation.
	f.unanswered(7)
	if f.fetches.Load() != 1 || f.out.stalled() != 1 {
		t.Fatal("one reconnect produced repeated confirmation reads or events")
	}
}

func TestHistoryRecoveryLateRowsAndExplicitFailuresCancel(t *testing.T) {
	cases := map[string]*table.LSTable{
		"history":         {LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7}}},
		"insert":          {LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 7}}},
		"replacement":     {LSDeleteThenInsertMessage: []*table.LSDeleteThenInsertMessage{{ThreadKey: 7}}},
		"empty end":       {LSUpdateExistingMessageRange: []*table.LSUpdateExistingMessageRange{{ThreadKey: 7}}},
		"message range":   {LSInsertNewMessageRange: []*table.LSInsertNewMessageRange{{ThreadKey: 7}}},
		"late blob":       {LSInsertBlobAttachment: []*table.LSInsertBlobAttachment{{}}},
		"late xma":        {LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{{}}},
		"late attachment": {LSInsertAttachment: []*table.LSInsertAttachment{{}}},
		"failed task":     {LSHandleFailedTask: []*table.LSHandleFailedTask{{}}},
		"explicit error":  {LSIssueNewError: []*table.LSIssueNewError{{}}},
		"sync failure":    {LSHandleSyncFailure: []*table.LSHandleSyncFailure{{}}},
		"task completion": {LSMailboxTaskCompletionApiOnTaskCompletion: []*table.LSMailboxTaskCompletionApiOnTaskCompletion{{Success: false}}},
	}
	for name, tbl := range cases {
		t.Run(name, func(t *testing.T) {
			for _, afterConfirmation := range []bool{false, true} {
				f := newHistoryRecoveryFixture(t)
				f.reconnect()
				f.unanswered(7)
				f.unanswered(8)
				step := f.nextWait(t, historyRecoveryGrace)
				if afterConfirmation {
					close(step.release)
					step = f.nextWait(t, historyRecoveryAnswerWait)
				}
				f.a.observeHistoryTable(tbl)
				waitHistoryRecoveryExit(t, step)
				if f.out.stalled() != 0 {
					t.Fatal("a late answer or explicit failure was diagnosed as an unanswered subscription")
				}
				wantFetches := int32(0)
				if afterConfirmation {
					wantFetches = 1
				}
				if f.fetches.Load() != wantFetches {
					t.Fatal("late answer did not cancel the confirmation read")
				}
			}
		})
	}
}

func TestHistoryRecoveryRejectsUnusableAnchorsAndNonemptyReplies(t *testing.T) {
	cases := []struct {
		name string
		edit func(*historyRecoveryFixture, *pageAnchor, **table.LSTable)
	}{
		{"encrypted", func(f *historyRecoveryFixture, _ *pageAnchor, _ **table.LSTable) {
			f.a.chats.get(8).kind = table.ENCRYPTED_OVER_WA_GROUP
		}},
		{"unknown kind", func(f *historyRecoveryFixture, _ *pageAnchor, _ **table.LSTable) {
			f.a.chats.get(8).kind = table.UNKNOWN_THREAD_TYPE
		}},
		{"deleted", func(f *historyRecoveryFixture, _ *pageAnchor, _ **table.LSTable) { f.a.chats.get(8).deleted = true }},
		{"no older neighbour", func(f *historyRecoveryFixture, _ *pageAnchor, _ **table.LSTable) {
			delete(f.a.chats.messages[8], "old-8")
		}},
		{"encrypted older neighbour", func(f *historyRecoveryFixture, _ *pageAnchor, _ **table.LSTable) {
			f.a.chats.messages[8]["old-8"].encrypted = true
		}},
		{"missing anchor", func(_ *historyRecoveryFixture, anchor *pageAnchor, _ **table.LSTable) { anchor.id = "" }},
		{"unknown anchor", func(_ *historyRecoveryFixture, anchor *pageAnchor, _ **table.LSTable) { anchor.id = "not-held" }},
		{"different timestamp", func(_ *historyRecoveryFixture, anchor *pageAnchor, _ **table.LSTable) { anchor.at++ }},
		{"nil reply", func(_ *historyRecoveryFixture, _ *pageAnchor, tbl **table.LSTable) { *tbl = nil }},
		{"explicit end", func(_ *historyRecoveryFixture, _ *pageAnchor, tbl **table.LSTable) {
			*tbl = &table.LSTable{LSUpdateExistingMessageRange: []*table.LSUpdateExistingMessageRange{{ThreadKey: 8}}}
		}},
		{"task rejection", func(_ *historyRecoveryFixture, _ *pageAnchor, tbl **table.LSTable) {
			*tbl = &table.LSTable{LSHandleFailedTask: []*table.LSHandleFailedTask{{}}}
		}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newHistoryRecoveryFixture(t)
			f.reconnect()
			f.unanswered(7)
			anchor := pageAnchor{id: "new-8", at: 2000}
			tbl := &table.LSTable{}
			tc.edit(f, &anchor, &tbl)
			f.a.historyUnanswered(context.Background(), 8, anchor, f.a.historyRecoveryAttempt(), tbl)
			if f.a.historyRecovery.attempted || f.fetches.Load() != 0 || f.out.stalled() != 0 {
				t.Fatal("excluded history case scheduled recovery")
			}
		})
	}
}

func TestHistoryRecoveryCancellationAndGenerationChanges(t *testing.T) {
	f := newHistoryRecoveryFixture(t)
	f.reconnect()
	oldTicket := f.a.historyRecoveryAttempt()
	f.a.historyConnectionInterrupted()
	f.a.historyConnectionResumed()
	f.unanswered(7)
	f.a.historyUnanswered(context.Background(), 8, pageAnchor{id: "new-8", at: 2000}, oldTicket, &table.LSTable{})
	if f.a.historyRecovery.attempted {
		t.Fatal("a reply from the previous connection implicated the new connection")
	}
	cancelled, cancel := context.WithCancel(context.Background())
	cancel()
	f.a.historyUnanswered(cancelled, 8, pageAnchor{id: "new-8", at: 2000}, f.a.historyRecoveryAttempt(), &table.LSTable{})
	if f.a.historyRecovery.attempted {
		t.Fatal("a cancelled refresh became evidence")
	}
	f.unanswered(8)
	grace := f.nextWait(t, historyRecoveryGrace)
	f.a.historyConnectionInterrupted()
	waitHistoryRecoveryExit(t, grace)
	if f.fetches.Load() != 0 || f.out.stalled() != 0 {
		t.Fatal("a stale confirmation survived another disconnect")
	}
	f.a.historyConnectionResumed()
	f.unanswered(7)
	f.unanswered(8)
	grace = f.nextWait(t, historyRecoveryGrace)
	f.a.stop()
	waitHistoryRecoveryExit(t, grace)
	if f.fetches.Load() != 0 || f.out.stalled() != 0 {
		t.Fatal("helper shutdown left a confirmation running")
	}
}

func TestHistoryRecoveryConfirmationErrorDoesNotRetryOrSignal(t *testing.T) {
	f := newHistoryRecoveryFixture(t)
	f.a.fetch = func(context.Context, int64, pageAnchor) (*table.LSTable, error) {
		f.fetches.Add(1)
		return nil, errors.New("synthetic read error")
	}
	f.reconnect()
	f.unanswered(7)
	f.unanswered(8)
	grace := f.nextWait(t, historyRecoveryGrace)
	close(grace.release)
	if !settle(t, func() bool { return f.fetches.Load() == 1 }) {
		t.Fatal("confirmation read was not made")
	}
	f.unanswered(9)
	if f.fetches.Load() != 1 || f.out.stalled() != 0 {
		t.Fatal("failed confirmation was retried or diagnosed as an acknowledged stall")
	}
}

func TestHistoryRecoverySuccessInvalidatesEarlierUnansweredRequests(t *testing.T) {
	f := newHistoryRecoveryFixture(t)
	f.reconnect()
	ticket := f.a.historyRecoveryAttempt()
	f.unanswered(7)
	f.a.historyRefreshAnswered(ticket)
	f.a.historyUnanswered(context.Background(), 8, pageAnchor{id: "new-8", at: 2000}, ticket, &table.LSTable{})
	if f.a.historyRecovery.attempted || len(f.a.historyRecovery.suspects) != 0 {
		t.Fatal("an answer did not invalidate earlier unanswered requests")
	}
}

func TestHistoryRecoveryHooksObserveActualUnansweredRefreshes(t *testing.T) {
	f := newHistoryRecoveryFixture(t)
	f.a.handle(context.Background(), &messagix.TransientDisconnectEvent{})
	f.a.handle(context.Background(), &messagix.ReconnectedEvent{})
	var refreshes sync.WaitGroup
	for _, key := range []int64{7, 8} {
		refreshes.Add(1)
		go func(key int64) {
			defer refreshes.Done()
			if f.a.refreshPage(context.Background(), key, 0, 30) {
				t.Error("an empty acknowledgement falsely covered a page")
			}
		}(key)
	}
	refreshes.Wait()
	grace := f.nextWait(t, historyRecoveryGrace)
	close(grace.release)
	answer := f.nextWait(t, historyRecoveryAnswerWait)
	close(answer.release)
	if !settle(t, func() bool { return f.out.stalled() == 1 }) || f.fetches.Load() != 3 {
		t.Fatal("two real failed refreshes did not produce one bounded confirmation and signal")
	}
}

func TestHistoryRecoveryDirectConfirmationAnswerIsApplied(t *testing.T) {
	f := newHistoryRecoveryFixture(t)
	f.a.fetch = func(_ context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		f.fetches.Add(1)
		return answerFor(key, anchor), nil
	}
	f.reconnect()
	f.unanswered(7)
	f.unanswered(8)
	grace := f.nextWait(t, historyRecoveryGrace)
	// Successful evidence cancels the watchdog context. Its response must have been applied before that cancellation,
	// otherwise a durable store would receive a cancelled context while writing the successful response.
	appliedBeforeCancel := make(chan bool, 1)
	f.a.historyRecovery.mu.Lock()
	cancelWatchdog := f.a.historyRecovery.cancel
	f.a.historyRecovery.cancel = func() {
		appliedBeforeCancel <- f.a.chats.revision(8) > 0
		cancelWatchdog()
	}
	f.a.historyRecovery.mu.Unlock()
	close(grace.release)
	select {
	case applied := <-appliedBeforeCancel:
		if !applied {
			t.Fatal("successful evidence cancelled its own context before the returned history was applied")
		}
	case <-time.After(3 * time.Second):
		t.Fatal("a successful confirmation did not cancel its watchdog")
	}
	if f.out.stalled() != 0 || f.fetches.Load() != 1 {
		t.Fatal("a successful confirmation was diagnosed as stalled or retried")
	}
}

func TestHistoryRecoveryRemovedOrEncryptedCandidateCancelsConfirmation(t *testing.T) {
	for _, beforeConfirmation := range []bool{true, false} {
		f := newHistoryRecoveryFixture(t)
		f.reconnect()
		f.unanswered(7)
		f.unanswered(8)
		step := f.nextWait(t, historyRecoveryGrace)
		if !beforeConfirmation {
			close(step.release)
			step = f.nextWait(t, historyRecoveryAnswerWait)
		}
		f.a.chats.mu.Lock()
		f.a.chats.threads[7].kind = table.ENCRYPTED_OVER_WA_GROUP
		f.a.chats.mu.Unlock()
		close(step.release)
		waitHistoryRecoveryExit(t, step)
		if f.out.stalled() != 0 {
			t.Fatal("a conversation no longer on ordinary history still counted toward recovery")
		}
	}
}

func TestHistoryRecoveryLateSocketAnswerDoesNotDiscardSuccessfulDirectResponse(t *testing.T) {
	f := newHistoryRecoveryFixture(t)
	f.a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		f.fetches.Add(1)
		// Another actual answer arrived while this read completed. It cancels the watchdog/read context, but the
		// successful response still belongs to this live helper and must reach memory and its durable store.
		f.a.observeHistoryTable(&table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7}}})
		if ctx.Err() == nil || f.a.lifetime.Err() != nil {
			t.Error("synthetic interleaving did not cancel only the watchdog")
		}
		return answerFor(key, anchor), nil
	}
	f.reconnect()
	f.unanswered(7)
	f.unanswered(8)
	grace := f.nextWait(t, historyRecoveryGrace)
	close(grace.release)
	if !settle(t, func() bool { return f.a.chats.revision(8) > 0 }) {
		t.Fatal("watchdog cancellation discarded the successful direct history response")
	}
	if f.out.stalled() != 0 || f.fetches.Load() != 1 {
		t.Fatal("concurrent successful evidence restarted or retried recovery")
	}
}
