package main

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
)

// The ledger reaches the app only in the answer to "snapshot", and survives a close: the app subtracts one answer from
// the next, so a count that went back to zero at every reconnect would read as a burst of negative requests.
func TestRequestLedgerIsAnsweredBySnapshotAndOutlivesAClose(t *testing.T) {
	a, _ := chatFixture()
	chatsOf(t, a, `{}`)
	before := a.requests.counts()
	if before.Mailbox != 1 || before.MailboxPages != 1 || before.Failures != 0 {
		t.Fatalf("a list read was not counted as one mailbox and the spam folder's page: %+v", before)
	}
	a.close()
	result, code := a.call(context.Background(), request{Method: "snapshot"})
	if code != "" {
		t.Fatalf("snapshot: %s", code)
	}
	reply, ok := result.(snapshotReply)
	if !ok || reply.Requests != before || reply.State != "disconnected" {
		t.Fatalf("snapshot did not carry the ledger beside the state: %+v", result)
	}
	raw, _ := json.Marshal(reply)
	if !strings.Contains(string(raw), `"requests":{"index":0,"mailbox":1,"mailboxPages":1,`) || !strings.Contains(string(raw), `"state":"disconnected"`) {
		t.Fatalf("snapshot's shape changed: %s", raw)
	}
}

// Whatever the helper pushes on every upstream event is the connection's state and nothing more.
func TestCountsFrameCarriesNoLedger(t *testing.T) {
	a, _, buffer := testReadAdapter()
	a.requests.mailbox.Add(7)
	if err := a.handle(context.Background(), a.generation, &slidetypes.Connected{}); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(buffer.String(), `"event":"counts"`) {
		t.Fatal("no counts frame was written")
	}
	if strings.Contains(buffer.String(), "requests") || strings.Contains(buffer.String(), "mailbox") {
		t.Fatal("the ledger rode on the counts frame")
	}
}

// A read the dispatcher aborted is counted as made but not as refused; one that ran out of time is both.
func TestACancelledRequestIsNotAFailure(t *testing.T) {
	var ledger requestLedger
	aborted, abort := context.WithCancel(context.Background())
	abort()
	late, stop := context.WithTimeout(context.Background(), 0)
	defer stop()
	<-late.Done()
	ledger.note(aborted, &ledger.threads, true)
	ledger.note(late, &ledger.threads, true)
	ledger.note(context.Background(), &ledger.threads, true)
	ledger.note(context.Background(), &ledger.threads, false)
	if got := ledger.counts(); got.Threads != 4 || got.Failures != 2 {
		t.Fatalf("expected four thread reads and two failures, got %+v", got)
	}
}

// Only a failed avatar sends the list to be read again. A message's pictures are named only by reading its
// conversation, so re-paging the inbox for one could never give it a new address.
func TestOnlyAFailedAvatarMakesTheListStale(t *testing.T) {
	for _, test := range []struct {
		id    string
		err   error
		stale bool
	}{
		{"avatar:123", nil, true},
		{"avatar:thread:abc", errors.New("status"), true},
		{"fbid-1", nil, false},
		{"preview:m1", nil, false},
		{"0123456789abcdef", nil, false},
		{"avatar:123", context.Canceled, false},
		{"avatar:123", context.DeadlineExceeded, false},
	} {
		if got := listRefreshes(test.id, test.err); got != test.stale {
			t.Fatalf("%q with %v: stale=%v, expected %v", test.id, test.err, got, test.stale)
		}
	}
}
