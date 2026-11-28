package main

import (
	"bytes"
	"context"
	"encoding/json"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	waTypes "go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
)

// A conversation between this account (1) and one other person (2): two messages sent, one received between them.
func directStore(kind table.ThreadType) *store {
	s := newStore()
	s.self = 1
	s.messages[7] = map[string]*message{
		"a": {id: "a", sender: 1, timestamp: 100},
		"b": {id: "b", sender: 2, timestamp: 200},
		"c": {id: "c", sender: 1, timestamp: 300},
	}
	current := s.get(7)
	current.kind = kind
	current.participants[1] = struct{}{}
	current.participants[2] = struct{}{}
	return s
}

func TestDeliveryWatermarkCountsTheOwnMessagesItReached(t *testing.T) {
	s := readingStore()
	touched := s.apply(&table.LSTable{LSUpdateDeliveryReceipt: []*table.LSUpdateDeliveryReceipt{
		{ThreadKey: 7, ContactId: 2, DeliveredWatermarkTimestampMs: 300},
	}})
	// Only this account's own messages carry a count, and a delivery is no new message nor a change to the list row.
	if !slices.Equal(touched.Receipts["7"], []string{"a", "c"}) || len(touched.Messages) != 0 || len(touched.Chats) != 0 {
		t.Fatalf("delivery redrew %+v", touched)
	}
	if s.deliveredTo(7, s.messages[7]["a"]) != 1 || s.deliveredTo(7, s.messages[7]["c"]) != 1 || s.deliveredTo(7, s.messages[7]["b"]) != 0 {
		t.Fatal("delivered counts are wrong")
	}
	// Reading counts as reaching: person 3 never sent a delivery, but has read the first message.
	s.advanceRead(7, 3, 150)
	if s.deliveredTo(7, s.messages[7]["a"]) != 2 || s.deliveredTo(7, s.messages[7]["c"]) != 1 {
		t.Fatal("a reader was not counted as reached")
	}
	// A delivery covering what this person has already read changes no count, so it redraws nothing.
	if moved := s.advanceDelivered(7, 3, 150); len(moved) != 0 {
		t.Fatalf("a delivery behind the read watermark redrew %v", moved)
	}
	// Older watermarks are not news.
	if moved := s.advanceDelivered(7, 2, 200); len(moved) != 0 {
		t.Fatalf("an older delivery watermark moved %v", moved)
	}
	// Somebody who leaves no longer counts.
	s.apply(&table.LSTable{LSRemoveParticipantFromThread: []*table.LSRemoveParticipantFromThread{{ThreadKey: 7, ParticipantId: 2}}})
	if s.deliveredTo(7, s.messages[7]["c"]) != 0 {
		t.Fatal("a departed participant still counts as reached")
	}
}

func TestParticipantSnapshotCarriesDeliveryButNoReadTime(t *testing.T) {
	s := directStore(table.ONE_TO_ONE)
	s.apply(&table.LSTable{LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{
		{ThreadKey: 7, ContactId: 2, ReadWatermarkTimestampMs: 100, ReadActionTimestampMs: 900, DeliveredWatermarkTimestampMs: 300},
	}})
	if s.deliveredTo(7, s.messages[7]["c"]) != 1 {
		t.Fatal("the snapshot's delivery watermark was not applied")
	}
	// The snapshot says when this person last read anything, not when they read this message.
	if at := s.seenAt(7, s.messages[7]["a"]); at != 0 {
		t.Fatalf("a snapshot claimed a read time of %d", at)
	}
	view := s.viewOf(7, s.messages[7]["c"])
	if view.Delivered != 1 || view.SeenAt != 0 || len(view.SeenBy) != 0 {
		t.Fatalf("history carries %+v", view)
	}
}

func TestReadTimeComesOnlyFromAReadingWatchedLive(t *testing.T) {
	s := directStore(table.ONE_TO_ONE)
	s.apply(&table.LSTable{LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{
		{ThreadKey: 7, ContactId: 2, ReadWatermarkTimestampMs: 150, ReadActionTimestampMs: 900},
	}})
	s.apply(&table.LSTable{LSUpdateReadReceipt: []*table.LSUpdateReadReceipt{
		{ThreadKey: 7, ContactId: 2, ReadWatermarkTimestampMs: 300, ReadActionTimestampMs: 5000},
	}})
	if at := s.seenAt(7, s.messages[7]["c"]); at != 5000 {
		t.Fatalf("the message read live has read time %d", at)
	}
	// Read before this helper was watching: the time is not known, and none is claimed.
	if at := s.seenAt(7, s.messages[7]["a"]); at != 0 {
		t.Fatalf("an earlier reading claimed time %d", at)
	}
	if view := s.viewOf(7, s.messages[7]["c"]); view.SeenAt != 5000 || view.Delivered != 1 {
		t.Fatalf("history carries %+v", view)
	}
	// A live report with nothing known before it cannot say which messages it covered just now.
	fresh := directStore(table.ONE_TO_ONE)
	fresh.apply(&table.LSTable{LSUpdateReadReceipt: []*table.LSUpdateReadReceipt{
		{ThreadKey: 7, ContactId: 2, ReadWatermarkTimestampMs: 300, ReadActionTimestampMs: 5000},
	}})
	if at := fresh.seenAt(7, fresh.messages[7]["c"]); at != 0 {
		t.Fatalf("a first report claimed time %d", at)
	}
}

func TestGroupReadTimeNeedsEveryReadersTime(t *testing.T) {
	s := readingStore()
	s.advanceReadAt(7, 2, 100, 0, 0, false)
	s.advanceReadAt(7, 3, 100, 0, 0, false)
	s.advanceReadAt(7, 2, 300, 0, 4000, true)
	// Person 3 has not read "c", so its time is person 2's; once 3 reads it live, the later of the two.
	if at := s.seenAt(7, s.messages[7]["c"]); at != 4000 {
		t.Fatalf("read time %d", at)
	}
	s.advanceReadAt(7, 3, 300, 0, 6000, true)
	if at := s.seenAt(7, s.messages[7]["c"]); at != 6000 {
		t.Fatalf("read time %d after everyone read", at)
	}
}

func TestEncryptedReceiptWaitsForItsMessage(t *testing.T) {
	s := directStore(table.ENCRYPTED_OVER_WA_ONE_TO_ONE)
	s.mu.Lock()
	moved, matched, waiting, _ := s.applyEncryptedReceipt(7, 2, receiptRead, []string{"later"}, 7000)
	s.mu.Unlock()
	if len(moved) != 0 || matched != 0 || waiting != 1 {
		t.Fatalf("a receipt for an unloaded message moved=%v matched=%d waiting=%d", moved, matched, waiting)
	}
	s.mu.Lock()
	s.applyEncryptedReceipt(7, 2, receiptDelivered, []string{"later"}, 6000)
	s.mu.Unlock()
	touched := s.addEncrypted(7, "later", 1, "synthetic", "", 400)
	later := s.messages[7]["later"]
	if !slices.Contains(s.readersOf(7, later), "2") || s.deliveredTo(7, later) != 1 || s.seenAt(7, later) != 7000 {
		t.Fatal("the waiting receipt did not apply when its message arrived")
	}
	// Everything before it that this person had not read is redrawn with it, as receipts.
	if !slices.Equal(touched.Messages["7"], []string{"later"}) || !slices.Contains(touched.Receipts["7"], "a") ||
		slices.Contains(touched.Receipts["7"], "later") {
		t.Fatalf("arrival redrew %+v", touched)
	}
	if s.receipts.waiting["later"] != nil || !s.receipts.cleared["later"] {
		t.Fatal("the settled receipt is still waiting")
	}
}

func TestEncryptedReceiptsSurviveARestart(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "messages.db")
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	s := directStore(table.ENCRYPTED_OVER_WA_ONE_TO_ONE)
	// An ordinary conversation beside it, whose watermarks Facebook sends again and which are not kept.
	s.messages[8] = map[string]*message{"o": {id: "o", sender: 1, timestamp: 100}}
	s.get(8).kind = table.ONE_TO_ONE
	s.advanceReadAt(7, 2, 100, 0, 0, false)
	s.mu.Lock()
	s.applyEncryptedReceipt(7, 2, receiptRead, []string{"c"}, 8000)
	s.applyEncryptedReceipt(7, 2, receiptDelivered, []string{"c"}, 7000)
	s.applyEncryptedReceipt(7, 2, receiptRead, []string{"arrives-later"}, 9000)
	s.mu.Unlock()
	s.apply(&table.LSTable{LSUpdateReadReceipt: []*table.LSUpdateReadReceipt{{ThreadKey: 8, ContactId: 2, ReadWatermarkTimestampMs: 100}}})
	watermarks, waiting, settled := s.receiptsToKeep()
	if len(watermarks) != 1 || watermarks[0].thread != 7 || len(waiting) != 1 || len(settled) != 0 {
		t.Fatalf("kept watermarks=%+v waiting=%+v settled=%v", watermarks, waiting, settled)
	}
	if err := kept.saveReceipts(ctx, watermarks, waiting, settled); err != nil {
		t.Fatal(err)
	}
	// Nothing left to write until something changes.
	if w, p, d := s.receiptsToKeep(); len(w)+len(p)+len(d) != 0 {
		t.Fatal("receipts were handed out twice")
	}
	if err := kept.db.Close(); err != nil {
		t.Fatal(err)
	}

	kept, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	restored := directStore(table.ENCRYPTED_OVER_WA_ONE_TO_ONE)
	restored.messages[8] = map[string]*message{"o": {id: "o", sender: 1, timestamp: 100}}
	restored.get(8).kind = table.ONE_TO_ONE
	w, p, d := kept.restoreReceipts(ctx, restored)
	if w != 1 || p != 1 || d != 0 {
		t.Fatalf("restored watermarks=%d waiting=%d settled=%d", w, p, d)
	}
	c := restored.messages[7]["c"]
	if !slices.Equal(restored.readersOf(7, c), []string{"2"}) || !slices.Equal(restored.readMarkOf(7, c), []string{"2"}) ||
		restored.deliveredTo(7, c) != 1 || restored.seenAt(7, c) != 8000 {
		t.Fatal("the encrypted conversation's receipts did not come back")
	}
	if len(restored.readersOf(8, restored.messages[8]["o"])) != 0 {
		t.Fatal("an ordinary conversation's watermark was kept")
	}
	// The receipt that was waiting applies when its message finally arrives, after the restart.
	restored.addEncrypted(7, "arrives-later", 1, "synthetic", "", 500)
	if !slices.Contains(restored.readersOf(7, restored.messages[7]["arrives-later"]), "2") {
		t.Fatal("a kept waiting receipt did not apply when its message arrived")
	}
	_, _, settled = restored.receiptsToKeep()
	if err := kept.saveReceipts(ctx, nil, nil, settled); err != nil || !slices.Equal(settled, []string{"arrives-later"}) {
		t.Fatalf("settled %v: %v", settled, err)
	}
	var left int
	if err := kept.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM receipt_waiting`).Scan(&left); err != nil || left != 0 {
		t.Fatalf("waiting rows left=%d: %v", left, err)
	}
	// A kept position never names a conversation the kept identity did not bring back.
	empty := newStore()
	empty.self = 1
	if w, _, _ := kept.restoreReceipts(ctx, empty); w != 0 || len(empty.threads) != 0 {
		t.Fatal("a kept position created a conversation")
	}
}

// The whole encrypted path: a delivery receipt, then a read receipt, arriving on the encrypted connection are applied,
// announced as receipts rather than messages, and written down.
func TestEncryptedReceiptEventsAreAnnouncedAsReceiptsAndKept(t *testing.T) {
	ctx := context.Background()
	var written bytes.Buffer
	a := newAdapter(&output{encoder: json.NewEncoder(&written)})
	defer a.stop()
	a.chats = directStore(table.ENCRYPTED_OVER_WA_ONE_TO_ONE)
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	a.kept = kept
	source := waTypes.MessageSource{Chat: waTypes.JID{User: "7", Server: waTypes.MessengerServer},
		Sender: waTypes.JID{User: "2", Server: waTypes.MessengerServer}}
	a.encryptedRead(&events.Receipt{MessageSource: source, MessageIDs: []waTypes.MessageID{"c"}, Type: waTypes.ReceiptTypeDelivered})
	a.encryptedRead(&events.Receipt{MessageSource: source, MessageIDs: []waTypes.MessageID{"c"}, Type: waTypes.ReceiptTypeRead,
		Timestamp: time.UnixMilli(8000)})
	lines := strings.Split(strings.TrimSpace(written.String()), "\n")
	if len(lines) != 2 || !strings.Contains(lines[0], `"receipts":{"7":["a","c"]}`) || !strings.Contains(lines[1], `"receipts":{"7":`) ||
		strings.Contains(written.String(), `"messages"`) || strings.Contains(written.String(), `"chats"`) {
		t.Fatalf("encrypted receipts were announced as %q", written.String())
	}
	c := a.chats.messages[7]["c"]
	if a.chats.deliveredTo(7, c) != 1 || !slices.Contains(a.chats.readersOf(7, c), "2") {
		t.Fatal("the encrypted receipts were not applied")
	}
	var read, delivered int64
	if err := kept.db.QueryRowContext(ctx, `SELECT read_mark, delivered FROM receipt_watermark WHERE thread = 7 AND contact = 2`).
		Scan(&read, &delivered); err != nil || read != 300 || delivered != 300 {
		t.Fatalf("kept read=%d delivered=%d: %v", read, delivered, err)
	}
}

// Receipts cross the pipe under their own name, so the app can redraw them without treating them as new messages.
func TestReceiptChangesTravelApartFromMessages(t *testing.T) {
	raw, err := json.Marshal(&changes{Receipts: map[string][]string{"7": {"a"}}})
	if err != nil {
		t.Fatal(err)
	}
	if text := string(raw); text != `{"receipts":{"7":["a"]}}` {
		t.Fatalf("receipt change serialized as %s", text)
	}
	if (&changes{Receipts: map[string][]string{"7": {"a"}}}).empty() {
		t.Fatal("a receipt-only change was treated as nothing")
	}
}

// Facebook's send status numbers are not documented upstream, so none is turned into a failure; each distinct one is
// recorded once, with its direction, for a live session to identify.
func TestSendStatusIsRecordedNotInterpreted(t *testing.T) {
	s := readingStore()
	s.apply(&table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{
		{ThreadKey: 7, MessageId: "d", SenderId: 1, TimestampMs: 400, Text: "synthetic", SendStatusV2: 5},
		{ThreadKey: 7, MessageId: "e", SenderId: 1, TimestampMs: 500, Text: "synthetic", SendStatusV2: 5},
	}})
	if len(s.receipts.statuses) != 1 || !s.receipts.statuses[5<<1|1] {
		t.Fatalf("statuses recorded %v", s.receipts.statuses)
	}
	if view := s.viewOf(7, s.messages[7]["d"]); view.Status != 5 || view.Deleted {
		t.Fatalf("a send status changed the message: %+v", view)
	}
}
