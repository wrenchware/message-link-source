package main

import (
	"context"
	"path/filepath"
	"slices"
	"strconv"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// Moves the waiting receipts' clock for one test.
func atTime(t *testing.T, now *int64) {
	previous := receiptClock
	receiptClock = func() int64 { return *now }
	t.Cleanup(func() { receiptClock = previous })
}

// A receipt for a message that never arrives must not hold its place for good. With every place taken the one that
// has waited longest makes way, and once a week has passed they all go, so newer receipts are never refused.
func TestWaitingReceiptsMakeRoomRatherThanRefuse(t *testing.T) {
	now := int64(1_000_000)
	atTime(t, &now)
	s := directStore(table.ENCRYPTED_OVER_WA_ONE_TO_ONE)
	s.mu.Lock()
	defer s.mu.Unlock()
	for index := range maxWaitingReceipts {
		now++
		s.applyEncryptedReceipt(7, 2, receiptRead, []string{"w" + strconv.Itoa(index)}, 1)
	}
	now++
	_, _, waiting, dropped := s.applyEncryptedReceipt(7, 2, receiptRead, []string{"newest"}, 1)
	if waiting != 1 || dropped != 1 || len(s.receipts.waiting) != maxWaitingReceipts || s.receipts.waiting["newest"] == nil {
		t.Fatalf("a full list kept waiting=%d dropped=%d size=%d", waiting, dropped, len(s.receipts.waiting))
	}
	// The one that had waited longest made way, and leaves the kept store at the next keep.
	if s.receipts.waiting["w0"] != nil || !s.receipts.cleared["w0"] || s.receipts.waiting["w1"] == nil {
		t.Fatal("the receipt that made way was not the one that had waited longest")
	}
	// A second receipt for a message already waiting takes no new place.
	if _, _, _, dropped := s.applyEncryptedReceipt(7, 2, receiptDelivered, []string{"newest"}, 1); dropped != 0 {
		t.Fatalf("a receipt joining one already waiting dropped %d", dropped)
	}
	// A week on, everything that was waiting has waited too long.
	now += waitingReceiptAge + 1
	if _, _, _, dropped := s.applyEncryptedReceipt(7, 2, receiptRead, []string{"later"}, 1); dropped != maxWaitingReceipts ||
		len(s.receipts.waiting) != 1 {
		t.Fatalf("expired receipts dropped=%d left=%d", dropped, len(s.receipts.waiting))
	}
}

// A restart brings back the newest waiting receipts that fit and none past their age, and removes the rest from the
// kept store so memory and store agree.
func TestRestoreKeepsTheNewestWaitingReceipts(t *testing.T) {
	ctx := context.Background()
	now := int64(10_000_000_000)
	atTime(t, &now)
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	rows := []keptWaiting{{message: "stale", thread: 7, contact: 2, kind: 1, at: 1, parked: now - waitingReceiptAge - 1}}
	for index := range maxWaitingReceipts + 1 {
		rows = append(rows, keptWaiting{message: "m" + strconv.Itoa(index), thread: 7, contact: 2, kind: 1, at: 1, parked: now - int64(index)})
	}
	if err := kept.saveReceipts(ctx, nil, rows, nil); err != nil {
		t.Fatal(err)
	}
	restored := directStore(table.ENCRYPTED_OVER_WA_ONE_TO_ONE)
	if _, waiting, _ := kept.restoreReceipts(ctx, restored); waiting != maxWaitingReceipts {
		t.Fatalf("restored %d waiting receipts", waiting)
	}
	last := "m" + strconv.Itoa(maxWaitingReceipts)
	if restored.receipts.waiting["stale"] != nil || restored.receipts.waiting[last] != nil || restored.receipts.waiting["m0"] == nil {
		t.Fatal("the restart did not keep exactly the newest waiting receipts")
	}
	var left int
	if err := kept.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM receipt_waiting`).Scan(&left); err != nil || left != maxWaitingReceipts {
		t.Fatalf("kept store holds %d waiting receipts: %v", left, err)
	}
}

// Deleting a conversation removes its receipts along with everything else kept for it, and leaves another
// conversation's alone.
func TestDeletingAConversationForgetsItsReceipts(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	s := directStore(table.ENCRYPTED_OVER_WA_ONE_TO_ONE)
	s.messages[9] = map[string]*message{"o": {id: "o", sender: 1, timestamp: 100}}
	s.get(9).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	s.mu.Lock()
	s.applyEncryptedReceipt(7, 2, receiptRead, []string{"c", "not-here"}, 8000)
	s.applyEncryptedReceipt(9, 2, receiptRead, []string{"o", "not-there"}, 8000)
	// One message named from both conversations: only the deleted one's receipt goes.
	s.applyEncryptedReceipt(7, 3, receiptDelivered, []string{"shared"}, 8000)
	s.applyEncryptedReceipt(9, 3, receiptDelivered, []string{"shared"}, 8000)
	s.mu.Unlock()
	watermarks, waiting, cleared := s.receiptsToKeep()
	if err := kept.saveReceipts(ctx, watermarks, waiting, cleared); err != nil {
		t.Fatal(err)
	}

	s.forgetThread(7)
	kept.forgetThread(ctx, 7)
	if s.receipts.marks[7] != nil || s.receipts.waiting["not-here"] != nil || s.receipts.waiting["not-there"] == nil ||
		len(s.receipts.waiting["shared"]) != 1 || s.receipts.waiting["shared"][0].thread != 9 {
		t.Fatalf("memory still holds the deleted conversation's receipts: %+v", s.receipts.waiting)
	}
	watermarks, waiting, cleared = s.receiptsToKeep()
	if err := kept.saveReceipts(ctx, watermarks, waiting, cleared); err != nil {
		t.Fatal(err)
	}
	count := func(query string, args ...any) int {
		var n int
		if err := kept.db.QueryRowContext(ctx, query, args...).Scan(&n); err != nil {
			t.Fatal(err)
		}
		return n
	}
	if count(`SELECT COUNT(*) FROM receipt_watermark WHERE thread = 7`) != 0 || count(`SELECT COUNT(*) FROM receipt_waiting WHERE thread = 7`) != 0 {
		t.Fatal("the deleted conversation's receipts are still kept")
	}
	if count(`SELECT COUNT(*) FROM receipt_watermark WHERE thread = 9`) != 1 || count(`SELECT COUNT(*) FROM receipt_waiting WHERE thread = 9`) != 2 {
		t.Fatal("another conversation's receipts went with it")
	}
}

// Receipts that arrived while a conversation was still known only by its encrypted number move with its messages when
// the pairing arrives, instead of staying behind on a conversation that no longer exists.
func TestStrayReceiptsMoveWithTheirMessages(t *testing.T) {
	s := newStore()
	s.self = 1
	s.messages[70] = map[string]*message{"s1": {id: "s1", encrypted: true, sender: 1, timestamp: 500}}
	stray := s.get(70)
	stray.kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	stray.participants[1], stray.participants[2] = struct{}{}, struct{}{}
	s.mu.Lock()
	s.applyEncryptedReceipt(70, 2, receiptDelivered, []string{"s1"}, 7000)
	s.applyEncryptedReceipt(70, 2, receiptRead, []string{"s1", "s2"}, 8000)
	s.mu.Unlock()
	s.apply(&table.LSTable{LSVerifyHybridThreadExists: []*table.LSVerifyHybridThreadExists{
		{ThreadKey: 7, ThreadJID: 70, ThreadType: table.ENCRYPTED_OVER_WA_ONE_TO_ONE},
	}})
	moved := s.messages[7]["s1"]
	if moved == nil || !slices.Equal(s.readersOf(7, moved), []string{"2"}) || s.deliveredTo(7, moved) != 1 || s.seenAt(7, moved) != 8000 {
		t.Fatal("the receipts did not follow their message into the paired conversation")
	}
	if s.receipts.marks[70] != nil || s.receipts.waiting["s2"][0].thread != 7 || !s.receipts.secure[7] {
		t.Fatal("receipts were left under the encrypted number")
	}
	// The stray number's kept rows are removed at the next keep; the paired conversation's are written.
	watermarks, _, _ := s.receiptsToKeep()
	var gone, written bool
	for _, row := range watermarks {
		gone = gone || row.thread == 70 && row.gone
		written = written || row.thread == 7 && row.read == 500 && row.delivered == 500
	}
	if !gone || !written {
		t.Fatalf("kept %+v", watermarks)
	}
}

// Facebook's own member count reaches the app for a group, so a group whose participant rows have not all arrived is
// not taken to be complete. A change adds to a count Facebook has given, and alone says nothing.
func TestGroupMemberCountReachesTheChat(t *testing.T) {
	s := newStore()
	s.self = 1
	touched := s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: 7, ThreadType: table.GROUP_THREAD, ThreadName: "Synthetic", MemberCount: 30},
			{ThreadKey: 8, ThreadType: table.ONE_TO_ONE, MemberCount: 2},
		},
		LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{
			{ThreadKey: 7, ContactId: 1}, {ThreadKey: 7, ContactId: 2}, {ThreadKey: 7, ContactId: 3},
		},
	})
	if !slices.Contains(touched.Chats, "7") {
		t.Fatalf("the counted group was not announced: %+v", touched)
	}
	s.mu.Lock()
	group, direct := s.view(s.threads[7]), s.view(s.threads[8])
	s.mu.Unlock()
	if group.Members != 30 || len(group.Participants) != 3 || direct.Members != 0 {
		t.Fatalf("members group=%d listed=%d direct=%d", group.Members, len(group.Participants), direct.Members)
	}
	touched = s.apply(&table.LSTable{LSAddToMemberCount: []*table.LSAddToMemberCount{
		{ThreadKey: 7, IncrementCount: -1}, {ThreadKey: 9, IncrementCount: 4},
	}})
	s.mu.Lock()
	defer s.mu.Unlock()
	if !slices.Contains(touched.Chats, "7") || s.view(s.threads[7]).Members != 29 || s.receipts.members[9] != 0 {
		t.Fatalf("after a change: group=%d unknown=%d", s.view(s.threads[7]).Members, s.receipts.members[9])
	}
}
