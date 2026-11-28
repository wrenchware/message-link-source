package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"reflect"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func recentTestAdapter(t *testing.T) *adapter {
	t.Helper()
	a := newAdapter(&output{encoder: json.NewEncoder(io.Discard)})
	t.Cleanup(a.stop)
	return a
}

func seedRecentChat(a *adapter, key int64, count int) {
	tbl := &table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: key, ThreadType: table.GROUP_THREAD}},
	}
	for i := 0; i < count; i++ {
		tbl.LSUpsertMessage = append(tbl.LSUpsertMessage, &table.LSUpsertMessage{
			ThreadKey: key, MessageId: fmt.Sprintf("synthetic-%d-%d", key, i), SenderId: 1,
			Text: "synthetic", TimestampMs: int64(1000 + i),
		})
	}
	a.chats.apply(tbl)
}

func advanceRecentChat(a *adapter, key, at int64) {
	a.chats.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: key, MessageId: fmt.Sprintf("synthetic-latest-%d", key),
			SenderId: 1, Text: "synthetic latest", TimestampMs: at}},
	})
}

func noRecentPause(ctx context.Context, _ time.Duration) bool { return ctx.Err() == nil }

// Reproduces an inbox preview delivering only the following text while a photo is absent altogether. Existing old
// messages exceed the archive's limit, and the newest page is already full; neither may suppress reconciliation.
func TestRecentCatchUpRecoversMissingPhotoAboveArchiveDepth(t *testing.T) {
	a := recentTestAdapter(t)
	var events bytes.Buffer
	a.out.encoder = json.NewEncoder(&events)
	seedRecentChat(a, 7, 600)
	a.prepareRecentCatchUp()
	advanceRecentChat(a, 7, 3000)
	var calls atomic.Int32
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		calls.Add(1)
		return &table.LSTable{
			LSUpsertMessage: []*table.LSUpsertMessage{
				{ThreadKey: key, MessageId: "synthetic-latest-7", SenderId: 1, Text: "synthetic latest", TimestampMs: 3000},
				{ThreadKey: key, MessageId: "synthetic-missing-photo", SenderId: 1, TimestampMs: 2999},
			},
			LSInsertBlobAttachment: []*table.LSInsertBlobAttachment{{ThreadKey: key, MessageId: "synthetic-missing-photo",
				AttachmentFbid: "synthetic-photo", AttachmentType: table.AttachmentTypeImage,
				PreviewUrl: "https://example.test/photo.jpg", PreviewWidth: 4, PreviewHeight: 3}},
		}, nil
	}
	a.recentCatchUp.run(a.lifetime, a, noRecentPause)
	if got := calls.Load(); got == 0 || got > refreshAsks {
		t.Fatalf("newest page was not fetched within the existing bound: calls=%d", got)
	}
	page := a.chats.historyBefore(7, 0, recentCatchUpMessages)
	if len(page) != recentCatchUpMessages || page[len(page)-2].ID != "synthetic-missing-photo" || len(page[len(page)-2].Media) != 1 {
		t.Fatal("the missing photo did not join the newest page immediately before the following text")
	}
	if !a.chats.pageRefreshed(7, 0) || !bytes.Contains(events.Bytes(), []byte(`"event":"changed"`)) {
		t.Fatal("catch-up did not complete and announce the refreshed conversation")
	}
	// A page already repaired through this same guard is not fetched again.
	before := calls.Load()
	a.recentCatchUp.run(a.lifetime, a, noRecentPause)
	if calls.Load() != before {
		t.Fatal("an already refreshed newest page was fetched again")
	}
}

func TestRecentCatchUpSelectionExcludesUnchangedEncryptedAndUnknown(t *testing.T) {
	a := recentTestAdapter(t)
	for key := int64(1); key <= 5; key++ {
		seedRecentChat(a, key, 1)
	}
	// The normal slight skew of activity beyond the last message must not look like new history at each login.
	a.chats.get(1).lastActivity = 1005
	a.chats.get(2).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	a.chats.get(3).kind = table.ENCRYPTED_OVER_WA_GROUP
	a.chats.get(4).kind = table.UNKNOWN_THREAD_TYPE
	a.chats.get(5).deleted = true
	a.prepareRecentCatchUp()
	for key := int64(2); key <= 4; key++ {
		advanceRecentChat(a, key, 2000)
	}
	if got := a.recentCatchUp.candidates(a.chats); len(got) != 0 {
		t.Fatalf("unexpected catch-up candidates: %v", got)
	}
	seedRecentChat(a, 6, 1)
	if got := a.recentCatchUp.candidates(a.chats); !reflect.DeepEqual(got, []int64{6}) {
		t.Fatalf("a newly discovered ordinary conversation was excluded: %v", got)
	}
}

func TestRecentCatchUpSelectsOnlyNewestFiveAndPacesReads(t *testing.T) {
	a := recentTestAdapter(t)
	for key := int64(1); key <= 8; key++ {
		seedRecentChat(a, key, 1)
	}
	a.prepareRecentCatchUp()
	for key := int64(1); key <= 8; key++ {
		advanceRecentChat(a, key, 2000+key)
	}
	var reads []int64
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		reads = append(reads, key)
		return &table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: key,
			MessageId: fmt.Sprintf("synthetic-latest-%d", key), SenderId: 1, Text: "synthetic latest", TimestampMs: 2000 + key}}}, nil
	}
	pauses := 0
	a.recentCatchUp.run(a.lifetime, a, func(ctx context.Context, duration time.Duration) bool {
		if duration != recentCatchUpPace {
			t.Errorf("unexpected pacing duration %v", duration)
		}
		pauses++
		return ctx.Err() == nil
	})
	if !reflect.DeepEqual(reads, []int64{8, 7, 6, 5, 4}) || pauses != recentCatchUpChats-1 {
		t.Fatalf("bounded paced reads=%v pauses=%d", reads, pauses)
	}
}

func TestRecentCatchUpCancellationStopsFurtherReads(t *testing.T) {
	a := recentTestAdapter(t)
	a.prepareRecentCatchUp()
	seedRecentChat(a, 7, 1)
	seedRecentChat(a, 8, 1)
	entered, exited := make(chan struct{}), make(chan struct{})
	var calls atomic.Int32
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		if calls.Add(1) == 1 {
			close(entered)
		}
		<-ctx.Done()
		close(exited)
		return nil, ctx.Err()
	}
	done := make(chan struct{})
	go func() { defer close(done); a.recentCatchUp.run(a.lifetime, a, noRecentPause) }()
	select {
	case <-entered:
	case <-time.After(3 * time.Second):
		t.Fatal("catch-up never began")
	}
	a.stop()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("catch-up did not stop on cancellation")
	}
	<-exited
	if calls.Load() != 1 || a.chats.pageRefreshed(7, 0) {
		t.Fatal("cancellation either continued into another chat or marked an incomplete page covered")
	}
	// Starting a cancelled pass must not request even its first page.
	a.recentCatchUp.run(a.lifetime, a, noRecentPause)
	if calls.Load() != 1 {
		t.Fatal("a cancelled catch-up fetched another page")
	}
}

func TestRecentCatchUpRechecksQueuedConversation(t *testing.T) {
	for _, change := range []string{"deleted", "unknown", "encrypted", "removed"} {
		t.Run(change, func(t *testing.T) {
			a := recentTestAdapter(t)
			a.prepareRecentCatchUp()
			seedRecentChat(a, 7, 1)
			seedRecentChat(a, 8, 2)
			var reads []int64
			a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
				reads = append(reads, key)
				return answerFor(key, anchor), nil
			}
			a.recentCatchUp.run(a.lifetime, a, func(ctx context.Context, _ time.Duration) bool {
				// Change the next conversation after the first read and before its turn.
				a.chats.mu.Lock()
				switch change {
				case "deleted":
					a.chats.threads[7].deleted = true
				case "unknown":
					a.chats.threads[7].kind = table.UNKNOWN_THREAD_TYPE
				case "encrypted":
					a.chats.threads[7].kind = table.ENCRYPTED_OVER_WA_GROUP
				case "removed":
					delete(a.chats.threads, 7)
				}
				a.chats.mu.Unlock()
				return ctx.Err() == nil
			})
			if !reflect.DeepEqual(reads, []int64{8}) || a.chats.restoredFrom(7) {
				t.Fatalf("ineligible queued conversation was fetched or marked for retry: reads=%v", reads)
			}
		})
	}
}

func TestRecentCatchUpSharesForegroundRefreshAndStartsOnlyOnce(t *testing.T) {
	a := recentTestAdapter(t)
	a.prepareRecentCatchUp()
	seedRecentChat(a, 7, 1)
	entered, release := make(chan struct{}), make(chan struct{})
	var calls atomic.Int32
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		if calls.Add(1) == 1 {
			close(entered)
		}
		select {
		case <-release:
		case <-ctx.Done():
			return nil, ctx.Err()
		}
		return answerFor(key, anchor), nil
	}
	if !a.refreshBehind(7, 0, recentCatchUpMessages) {
		t.Fatal("foreground refresh was not started")
	}
	<-entered
	a.recentCatchUp.run(a.lifetime, a, noRecentPause)
	if calls.Load() != 1 {
		t.Fatal("catch-up duplicated a foreground page refresh already in flight")
	}
	close(release)
	if !settle(t, func() bool { return !a.refreshRunning(7, 0) }) {
		t.Fatal("foreground page did not finish")
	}
	// Several Connected notifications may race; the per-login once guard admits only one worker.
	var launches sync.WaitGroup
	for range 10 {
		launches.Go(a.startRecentCatchUp)
	}
	launches.Wait()
	if !settle(t, func() bool { return a.chats.pageRefreshed(7, 0) }) || calls.Load() != 1 {
		t.Fatal("a successful foreground refresh was repeated at connect")
	}
}
