package main

import (
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func TestReceiptProbeRequiresExplicitOptIn(t *testing.T) {
	for _, observer := range []string{"", "1"} {
		t.Setenv("MESSAGELINK_MESSENGER_OBSERVE_RECEIPTS", observer)
		t.Setenv("MESSAGELINK_MESSENGER_TEST_RECEIPT", "")
		a := newAdapter(nil)
		defer a.stop()
		if _, code := a.call(request{Method: "probe.receipt"}); code != "observation_only" {
			t.Fatal("test receipt enabled in app or passive observer")
		}
	}
}

func TestReceiptProbeOnlySelectsFreshIncomingInExactUniqueDM(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	now := time.Now()
	s.get(7).name = "Synthetic test"
	s.get(7).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	s.addEncrypted(7, "existing", 2, "old", "", now.UnixMilli())
	if _, code := s.newReceiptProbe("Synthetic", "single", now); code == "" {
		t.Fatal("partial name allowed")
	}
	if _, code := s.newReceiptProbe("Synthetic test", "unsupported", now); code == "" {
		t.Fatal("unknown mode allowed")
	}
	p, code := s.newReceiptProbe("Synthetic test", "single", now)
	if code != "" {
		t.Fatal(code)
	}
	s.addEncrypted(7, "late-history", 2, "old", "", now.Add(-time.Hour).UnixMilli())
	s.addEncrypted(7, "own", 1, "own", "", now.Add(time.Second).UnixMilli())
	s.addEncrypted(8, "other-chat", 3, "other", "", now.Add(time.Second).UnixMilli())
	if s.freshReceiptMessage(p) != nil {
		t.Fatal("probe selected history, own or other chat")
	}
	s.addEncrypted(7, "fresh", 2, "new", "", now.Add(time.Second).UnixMilli())
	if got := s.freshReceiptMessage(p); got == nil || got.id != "fresh" {
		t.Fatal("fresh test message not selected")
	}
	s.get(7).deletedAt = now.Add(2 * time.Second).UnixMilli()
	if s.freshReceiptMessage(p) != nil {
		t.Fatal("deleted message selected")
	}
	s.get(8).name = "Synthetic test"
	if _, code := s.newReceiptProbe("Synthetic test", "single", now); code == "" {
		t.Fatal("ambiguous name allowed")
	}
	s.get(8).name = "Other"
	s.get(7).kind = table.ENCRYPTED_OVER_WA_GROUP
	if _, code := s.newReceiptProbe("Synthetic test", "single", now); code == "" {
		t.Fatal("group allowed")
	}
}
