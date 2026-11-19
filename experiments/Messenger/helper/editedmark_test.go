package main

import (
	"context"
	"path/filepath"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// An ordinary message edited on another device stays marked edited when a history refresh brings its row again, and
// after a restart. The upsert rebuilt the message without its mark, and the mark was never written down.
func TestOrdinaryEditKeepsItsMarkThroughRefreshAndRestart(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "synthetic.db")
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	a := newAdapter(&output{})
	a.kept = kept
	a.chats.setSelf(1)
	a.keepMessages(ctx, a.chats.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 7, ThreadType: table.ONE_TO_ONE}},
		LSInsertMessage:          []*table.LSInsertMessage{{ThreadKey: 7, MessageId: "m", SenderId: 42, TimestampMs: 1000, Text: "first"}},
	}))
	a.keepMessages(ctx, a.chats.apply(&table.LSTable{LSEditMessage: []*table.LSEditMessage{{MessageID: "m", Text: "second", EditCount: 1}}}))
	edited := func(s *store) int64 {
		t.Helper()
		s.mu.Lock()
		defer s.mu.Unlock()
		held := s.messages[7]["m"]
		if held == nil || held.text != "second" {
			t.Fatal("the edited words are missing")
		}
		return held.editedAt
	}
	if edited(a.chats) == 0 {
		t.Fatal("the edit was not marked")
	}
	// The refresh's row carries the corrected words and its edit count.
	a.keepMessages(ctx, a.chats.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "m", SenderId: 42, TimestampMs: 1000, Text: "second", EditCount: 1}},
	}))
	if edited(a.chats) == 0 {
		t.Fatal("a refresh of the message cleared its edit mark")
	}
	kept.db.Close()
	reopened, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer reopened.db.Close()
	restored := newStore()
	reopened.restore(ctx, restored)
	if edited(restored) == 0 {
		t.Fatal("the edit mark did not survive a restart")
	}
	// A message first seen already edited says so too.
	fresh := newStore()
	fresh.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 7, ThreadType: table.ONE_TO_ONE}},
		LSUpsertMessage:          []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "m", SenderId: 42, TimestampMs: 1000, Text: "second", EditCount: 2}},
	})
	if edited(fresh) == 0 {
		t.Fatal("an edit count did not mark the message edited")
	}
	// And a stored copy restored over the refreshed message keeps the corrected words and mark.
	fresh.addEncrypted(7, "m", 42, "first", "", 1000, false)
	if edited(fresh) == 0 {
		t.Fatal("re-adding the message dropped its edit")
	}
}

// A history page captured before an edit brings the message's row with its first words and no edit count. Those words
// used to replace the corrected ones, under the Edited label kept from the edit. A row naming a later edit still lands.
func TestOlderRowDoesNotUndoAnEdit(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 7, ThreadType: table.ONE_TO_ONE}},
		LSInsertMessage:          []*table.LSInsertMessage{{ThreadKey: 7, MessageId: "m", SenderId: 42, TimestampMs: 1000, Text: "first"}},
	})
	s.apply(&table.LSTable{LSEditMessage: []*table.LSEditMessage{{MessageID: "m", Text: "second", EditCount: 1}}})
	held := func() (string, int64) {
		t.Helper()
		s.mu.Lock()
		defer s.mu.Unlock()
		current := s.messages[7]["m"]
		return current.text, current.editedAt
	}
	stale := &table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "m", SenderId: 42, TimestampMs: 1000, Text: "first"}}}
	s.apply(stale)
	if text, at := held(); text != "second" || at == 0 {
		t.Fatalf("an older row left %q (edited=%v)", text, at != 0)
	}
	// The same edit delivered again is not a second one.
	s.apply(&table.LSTable{LSEditMessage: []*table.LSEditMessage{{MessageID: "m", Text: "second", EditCount: 1}}})
	s.apply(&table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "m", SenderId: 42, TimestampMs: 1000, Text: "third", EditCount: 2}}})
	if text, _ := held(); text != "third" {
		t.Fatalf("a row naming a later edit left %q", text)
	}
	// After a restart only the mark is known: an older row still does not undo it.
	s.mu.Lock()
	s.messages[7]["m"].edits = 0
	s.mu.Unlock()
	s.apply(stale)
	if text, at := held(); text != "third" || at == 0 {
		t.Fatalf("after a restart an older row left %q (edited=%v)", text, at != 0)
	}
}

// The edit count is kept with the mark. After a restart a history row captured between two edits named one, which is
// more than the mark alone stands for, and put the first edit's words back over the second's.
func TestRowBetweenTwoEditsDoesNotUndoTheLaterOneAfterRestart(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "synthetic.db")
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	a := newAdapter(&output{})
	a.kept = kept
	a.chats.setSelf(1)
	a.keepMessages(ctx, a.chats.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 7, ThreadType: table.ONE_TO_ONE}},
		LSInsertMessage:          []*table.LSInsertMessage{{ThreadKey: 7, MessageId: "m", SenderId: 42, TimestampMs: 1000, Text: "first"}},
	}))
	a.keepMessages(ctx, a.chats.apply(&table.LSTable{LSEditMessage: []*table.LSEditMessage{{MessageID: "m", Text: "second", EditCount: 1}}}))
	a.keepMessages(ctx, a.chats.apply(&table.LSTable{LSEditMessage: []*table.LSEditMessage{{MessageID: "m", Text: "third", EditCount: 2}}}))
	kept.db.Close()
	reopened, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer reopened.db.Close()
	restored := newStore()
	restored.setSelf(1)
	reopened.restore(ctx, restored)
	restored.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "m", SenderId: 42, TimestampMs: 1000, Text: "second", EditCount: 1}},
	})
	restored.mu.Lock()
	held := restored.messages[7]["m"]
	text, editedAt := held.text, held.editedAt
	restored.mu.Unlock()
	if text != "third" || editedAt == 0 {
		t.Fatalf("after a restart a row naming the first edit left %q (edited=%v)", text, editedAt != 0)
	}
}
