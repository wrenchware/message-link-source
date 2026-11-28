package main

import (
	"slices"
	"sort"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func readingStore() *store {
	s := newStore()
	s.self = 1
	s.messages[7] = map[string]*message{
		"a": {id: "a", sender: 1, timestamp: 100},
		"b": {id: "b", sender: 2, timestamp: 200},
		"c": {id: "c", sender: 1, timestamp: 300},
	}
	current := s.get(7)
	current.kind = table.GROUP_THREAD
	current.participants[1] = struct{}{}
	current.participants[2] = struct{}{}
	current.participants[3] = struct{}{}
	return s
}

func TestAdvanceReadReportsNewlyReadAndPreviousMarker(t *testing.T) {
	s := readingStore()
	moved := s.advanceRead(7, 2, 150)
	if !slices.Equal(moved, []string{"a"}) {
		t.Fatalf("first watermark moved %v", moved)
	}
	// The old mark must be cleared even though that message remains read.
	moved = s.advanceRead(7, 2, 300)
	sort.Strings(moved)
	if !slices.Equal(moved, []string{"a", "c"}) {
		t.Fatalf("second watermark moved %v, wanted the old mark and newly covered messages", moved)
	}
	// "b" is this person's own message, so it is never something they read.
	if slices.Contains(moved, "b") {
		t.Fatal("a person was reported as reading their own message")
	}
	// Facebook resends older watermarks freely; one that goes backwards is not news.
	if moved := s.advanceRead(7, 2, 200); len(moved) != 0 {
		t.Fatalf("an older watermark moved %v", moved)
	}
	// This account reading its own conversation is not a receipt to draw.
	if moved := s.advanceRead(7, s.self, 300); len(moved) != 0 {
		t.Fatalf("the account's own reading moved %v", moved)
	}
}

func TestReadMarkLandsOnTheNewestMessageEachPersonRead(t *testing.T) {
	s := readingStore()
	// Person 2 has read everything. Their own message "b" sits between the others, and a rule that only compared a
	// message with the one after it would put a face on "a" as well, because "b" is not something they read.
	s.advanceRead(7, 2, 300)
	if marks := s.readMarkOf(7, s.messages[7]["a"]); len(marks) != 0 {
		t.Fatalf("a face was left behind on an older message: %v", marks)
	}
	if marks := s.readMarkOf(7, s.messages[7]["c"]); !slices.Equal(marks, []string{"2"}) {
		t.Fatalf("the newest message they read carries %v", marks)
	}
	// Someone who has only got as far as the first message is drawn there.
	s.advanceRead(7, 3, 150)
	if marks := s.readMarkOf(7, s.messages[7]["a"]); !slices.Equal(marks, []string{"3"}) {
		t.Fatalf("a partial reader is drawn at %v", marks)
	}
	// And never against a message they sent themselves.
	if marks := s.readMarkOf(7, s.messages[7]["b"]); slices.Contains(marks, "2") {
		t.Fatalf("a person was drawn against their own message: %v", marks)
	}
}

func TestReadersOfNamesEveryoneWhoHasReachedIt(t *testing.T) {
	s := readingStore()
	s.advanceRead(7, 2, 300)
	s.advanceRead(7, 3, 150)
	readers := s.readersOf(7, s.messages[7]["a"])
	sort.Strings(readers)
	if !slices.Equal(readers, []string{"2", "3"}) {
		t.Fatalf("the oldest message was read by %v", readers)
	}
	// The newest message has only reached the person whose watermark covers it.
	readers = s.readersOf(7, s.messages[7]["c"])
	if !slices.Equal(readers, []string{"2"}) {
		t.Fatalf("the newest message was read by %v", readers)
	}
	// A person is never listed against their own message.
	if readers := s.readersOf(7, s.messages[7]["b"]); slices.Contains(readers, "2") {
		t.Fatalf("the sender was listed as a reader of their own message: %v", readers)
	}
}

// Reading is not an answer to a request for history. The revision counter is what a waiting request watches, and a
// watermark arriving on its own used to be enough to report an answer that had not come.
func TestReadReceiptsRedrawWithoutCountingAsAnAnswer(t *testing.T) {
	s := readingStore()
	before := s.rev[7]
	touched := s.apply(&table.LSTable{LSUpdateReadReceipt: []*table.LSUpdateReadReceipt{
		{ThreadKey: 7, ContactId: 2, ReadWatermarkTimestampMs: 300},
	}})
	if s.rev[7] != before {
		t.Fatalf("a read receipt moved the revision from %d to %d", before, s.rev[7])
	}
	// Reading changes no conversation summary and is no new message: it travels as receipts alone.
	if slices.Contains(touched.Chats, "7") || len(touched.Messages) != 0 {
		t.Fatalf("a receipt was reported as a conversation or message change: %+v", touched)
	}
	ids := touched.Receipts["7"]
	sort.Strings(ids)
	if !slices.Equal(ids, []string{"a", "c"}) {
		t.Fatalf("the messages read were redrawn as %v", ids)
	}
}

// A message row and a receipt arriving together: the row still counts as Facebook answering, and neither set of ids
// is lost to the other.
func TestReadReceiptsMergeWithRealMessageRows(t *testing.T) {
	s := readingStore()
	before := s.rev[7]
	touched := s.apply(&table.LSTable{
		LSUpsertMessage:     []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "d", SenderId: 2, TimestampMs: 400, Text: "hello"}},
		LSUpdateReadReceipt: []*table.LSUpdateReadReceipt{{ThreadKey: 7, ContactId: 3, ReadWatermarkTimestampMs: 300}},
	})
	if s.rev[7] == before {
		t.Fatal("a message row did not move the revision")
	}
	// The message the row carried is a change; everything up to the watermark for this person, none of it theirs,
	// is a receipt. Neither set is lost to the other.
	if !slices.Equal(touched.Messages["7"], []string{"d"}) || !slices.Contains(touched.Chats, "7") {
		t.Fatalf("the batch changed %+v", touched)
	}
	ids := touched.Receipts["7"]
	sort.Strings(ids)
	if !slices.Equal(ids, []string{"a", "b", "c"}) {
		t.Fatalf("the batch redrew %v as receipts", ids)
	}
}

func TestReadMarkSkipsDeletedMessagesAndBreaksTimestampTies(t *testing.T) {
	s := readingStore()
	s.messages[7]["d"] = &message{id: "d", sender: 1, timestamp: 300}
	s.messages[7]["deleted"] = &message{id: "deleted", sender: 1, timestamp: 400, unsent: true}
	s.advanceRead(7, 2, 500)
	for id, msg := range s.messages[7] {
		marked := slices.Contains(s.readMarkOf(7, msg), "2")
		if marked != (id == "d") {
			t.Fatalf("unexpected mark at %s: %v", id, marked)
		}
	}
}

func TestIncrementalReceiptClearsOldFace(t *testing.T) {
	s := readingStore()
	s.advanceRead(7, 2, 150)
	displayed := map[string][]string{}
	for id, msg := range s.messages[7] {
		displayed[id] = s.readMarkOf(7, msg)
	}
	changes := s.apply(&table.LSTable{LSUpdateReadReceipt: []*table.LSUpdateReadReceipt{
		{ThreadKey: 7, ContactId: 2, ReadWatermarkTimestampMs: 300},
	}})
	// Mimic the app: only message ids named by the event are resolved again.
	for _, id := range changes.Receipts["7"] {
		displayed[id] = s.readMarkOf(7, s.messages[7][id])
	}
	count := 0
	for _, marks := range displayed {
		if slices.Contains(marks, "2") {
			count++
		}
	}
	if count != 1 || !slices.Contains(displayed["c"], "2") {
		t.Fatalf("receipt left repeated/stale faces: %v", displayed)
	}
}

// Match the supplied screenshot: two people stop at the final incoming message
// (including its author), while another has reached the later outgoing reply.
func TestParticipantSnapshotPlacesFacesIncludingMessageAuthor(t *testing.T) {
	s := readingStore()
	s.messages[7]["c"].sender = 2
	s.messages[7]["reply"] = &message{id: "reply", sender: 1, timestamp: 900}
	change := s.apply(&table.LSTable{LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{
		{ThreadKey: 7, ContactId: 2, ReadWatermarkTimestampMs: 300},
		{ThreadKey: 7, ContactId: 3, ReadWatermarkTimestampMs: 300},
		{ThreadKey: 7, ContactId: 4, ReadWatermarkTimestampMs: 900},
		{ThreadKey: 7, ContactId: 1, ReadWatermarkTimestampMs: 900},
	}})
	if !slices.Equal(s.readMarkOf(7, s.messages[7]["c"]), []string{"2", "3"}) {
		t.Fatal("read positions excluded the message author")
	}
	if !slices.Equal(s.readMarkOf(7, s.messages[7]["reply"]), []string{"4"}) {
		t.Fatal("participant snapshot did not mark the later reply")
	}
	if slices.Contains(s.readersOf(7, s.messages[7]["c"]), "2") {
		t.Fatal("position marker incorrectly counted the sender as a recipient")
	}
	if !slices.Contains(change.Receipts["7"], "c") || !slices.Contains(change.Receipts["7"], "reply") {
		t.Fatal("snapshot marker changes were not published")
	}
	s.apply(&table.LSTable{LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{
		{ThreadKey: 7, ContactId: 4, ReadWatermarkTimestampMs: 100},
	}})
	if !slices.Equal(s.readMarkOf(7, s.messages[7]["reply"]), []string{"4"}) {
		t.Fatal("older snapshot moved a receipt backward")
	}
}

func TestReceiptMovesOntoOwnMessageAndClearsPreviousPosition(t *testing.T) {
	s := readingStore()
	s.advanceRead(7, 2, 100)
	moved := s.advanceRead(7, 2, 200)
	if !slices.Contains(moved, "a") || !slices.Contains(moved, "b") {
		t.Fatalf("move must invalidate both rows: %v", moved)
	}
	if len(s.readMarkOf(7, s.messages[7]["a"])) != 0 || !slices.Equal(s.readMarkOf(7, s.messages[7]["b"]), []string{"2"}) {
		t.Fatal("face did not move to own message")
	}
}

func TestParticipantSnapshotBeforeHistoryAndInSameBatch(t *testing.T) {
	for _, sameBatch := range []bool{false, true} {
		s := newStore()
		s.self = 1
		tbl := &table.LSTable{LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{
			{ThreadKey: 7, ContactId: 2, ReadWatermarkTimestampMs: 300},
		}}
		if !sameBatch {
			s.apply(tbl)
			tbl = &table.LSTable{}
		}
		tbl.LSUpsertMessage = []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "m", SenderId: 2, TimestampMs: 300}}
		s.apply(tbl)
		if !slices.Equal(s.readMarkOf(7, s.messages[7]["m"]), []string{"2"}) {
			t.Fatalf("snapshot lost with sameBatch=%v", sameBatch)
		}
	}
}
