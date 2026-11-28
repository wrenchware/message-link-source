package main

import (
	"context"
	"encoding/json"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// A group photo sent on the ordinary connection: Facebook paired the id and sent no row (2026-10-12).
func sentWithoutRow(t *testing.T, kind table.ThreadType) *adapter {
	t.Helper()
	a := recentTestAdapter(t)
	a.client = &messagix.Client{}
	a.chats.setSelf(5)
	a.chats.apply(&table.LSTable{
		LSDeleteThenInsertThread:   []*table.LSDeleteThenInsertThread{{ThreadKey: 4, ThreadType: kind}},
		LSUpsertMessage:            []*table.LSUpsertMessage{{ThreadKey: 4, MessageId: "mid:old", SenderId: 6, Text: "earlier", TimestampMs: 1000}},
		LSReplaceOptimsiticMessage: []*table.LSReplaceOptimsiticMessage{{OfflineThreadingId: "777", MessageId: "mid:photo"}},
	})
	return a
}

func lookupSent(a *adapter, id string) (any, string) {
	raw, _ := json.Marshal(map[string]any{"chatId": "4", "id": id})
	return a.callContext(context.Background(), request{Method: "ui.message", Params: raw})
}

func photoRow(key int64) *table.LSTable {
	return &table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: key, MessageId: "mid:photo", OfflineThreadingId: "777", SenderId: 5, TimestampMs: 2000}},
	}
}

// The app confirms a send by asking for the message; with the pairing but no row it used to get unknown_message for
// ever. The lookup now reads the newest page once, anchored past the present so the newest message is inside it.
func TestLookupOfSentMessageWithoutRowReadsTheNewestPage(t *testing.T) {
	// Either form of the id the app may hold: the offline threading id or Facebook's.
	for _, id := range []string{"777", "mid:photo"} {
		a := sentWithoutRow(t, table.GROUP_THREAD)
		var calls atomic.Int32
		var seen pageAnchor
		a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
			calls.Add(1)
			seen = anchor
			return photoRow(key), nil
		}
		got, code := lookupSent(a, id)
		if code != "" {
			t.Fatalf("looking up %q: %s", id, code)
		}
		if msg, ok := got.(viewMessage); !ok || msg.ID != "mid:photo" || !msg.Outgoing {
			t.Fatalf("looking up %q: unexpected message %+v", id, got)
		}
		if calls.Load() != 1 {
			t.Fatalf("looking up %q: one read expected, got %d", id, calls.Load())
		}
		if seen.id != "mid:photo" || seen.at <= time.Now().UnixMilli() {
			t.Fatalf("the read must be anchored past now at the sent id: %+v", seen)
		}
	}
}

// Nothing may become a storm of reads: one per gap per id, three in all, and none for ids that were never sent.
func TestLookupOfSentMessageWithoutRowIsBounded(t *testing.T) {
	a := sentWithoutRow(t, table.GROUP_THREAD)
	var calls atomic.Int32
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		calls.Add(1)
		return &table.LSTable{}, nil // Facebook still has nothing to give.
	}
	for range 6 {
		if _, code := lookupSent(a, "777"); code != "unknown_message" {
			t.Fatalf("expected unknown_message, got %q", code)
		}
	}
	if calls.Load() != 1 {
		t.Fatalf("lookups inside the gap must not read again, got %d", calls.Load())
	}
	for range 5 {
		a.sentLooks["mid:photo"].last = time.Now().Add(-2 * sentRowGap)
		lookupSent(a, "mid:photo") // the other form of the id shares the same budget
	}
	if calls.Load() != sentRowAsks {
		t.Fatalf("a message gets %d reads in all, got %d", sentRowAsks, calls.Load())
	}
	// An id this account never sent is not looked for.
	before := calls.Load()
	if _, code := lookupSent(a, "mid:stranger"); code != "unknown_message" || calls.Load() != before {
		t.Fatalf("an unpaired id must not read: %q calls=%d", code, calls.Load())
	}
}

// A failed read is just "not yet": nothing is resent, and the app asks again later.
func TestLookupOfSentMessageSurvivesAFailedRead(t *testing.T) {
	a := sentWithoutRow(t, table.GROUP_THREAD)
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		return nil, errors.New("synthetic failure")
	}
	if _, code := lookupSent(a, "777"); code != "unknown_message" {
		t.Fatalf("expected unknown_message, got %q", code)
	}
}

// An encrypted conversation's messages are drawn by the helper from its own connection; the ordinary read has nothing
// to bring for it (pageRefreshable), so it is not asked.
func TestLookupOfSentMessageInEncryptedChatDoesNotRead(t *testing.T) {
	a := sentWithoutRow(t, table.ENCRYPTED_OVER_WA_GROUP)
	var calls atomic.Int32
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		calls.Add(1)
		return photoRow(key), nil
	}
	if _, code := lookupSent(a, "777"); code != "unknown_message" || calls.Load() != 0 {
		t.Fatalf("encrypted chat must not read: %q calls=%d", code, calls.Load())
	}
}
