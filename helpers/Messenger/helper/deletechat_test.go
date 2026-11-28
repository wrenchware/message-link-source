package main

import (
	"bytes"
	"context"
	"path/filepath"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func TestConversationDeletionRequiresProviderConfirmation(t *testing.T) {
	if confirmedThreadDeletion(nil, 7) || confirmedThreadDeletion(&table.LSTable{}, 7) ||
		confirmedThreadDeletion(&table.LSTable{LSDeleteThread: []*table.LSDeleteThread{{ThreadKey: 8}}}, 7) {
		t.Fatal("an empty or unrelated acknowledgement cannot delete local history")
	}
	for name, rows := range map[string]*table.LSTable{
		"whole":            {LSDeleteThread: []*table.LSDeleteThread{{ThreadKey: 7}}},
		"for this account": {LSDeletePartialThread: []*table.LSDeletePartialThread{{ThreadKey: 7}}},
		"message request":  {LSDeleteMessageRequest: []*table.LSDeleteMessageRequest{{ThreadKey: 7}}},
	} {
		t.Run(name, func(t *testing.T) {
			if !confirmedThreadDeletion(rows, 7) {
				t.Fatal("the provider's matching deletion row must be accepted")
			}
		})
	}
	if confirmedThreadDeletion(&table.LSTable{
		LSDeletePartialThread:  []*table.LSDeletePartialThread{{ThreadKey: 7}},
		LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{{ThreadKey: 7}},
	}, 7) {
		t.Fatal("a thread replaced in the same sync must not be confirmed as deleted")
	}
}

func TestConversationDeletionAcceptsSocketConfirmationAroundEmptyReply(t *testing.T) {
	a := newAdapter(nil)
	t.Cleanup(a.stop)

	confirmed, stopWatching := a.watchThreadDeletion(7)
	a.noteThreadDeletion(7)
	if !waitForThreadDeletion(context.Background(), confirmed, time.Second) {
		t.Fatal("a socket confirmation that arrived before the request reply was lost")
	}
	stopWatching()

	confirmed, stopWatching = a.watchThreadDeletion(7)
	go func() {
		time.Sleep(10 * time.Millisecond)
		a.noteThreadDeletion(7)
	}()
	if !waitForThreadDeletion(context.Background(), confirmed, time.Second) {
		t.Fatal("a socket confirmation that arrived after the request reply was not observed")
	}
	stopWatching()
}

func TestConversationDeletionDoesNotAcceptUnrelatedSocketEvent(t *testing.T) {
	a := newAdapter(nil)
	t.Cleanup(a.stop)
	confirmed, stopWatching := a.watchThreadDeletion(7)
	defer stopWatching()
	a.noteThreadDeletion(8)
	if waitForThreadDeletion(context.Background(), confirmed, 20*time.Millisecond) {
		t.Fatal("an unrelated conversation deletion confirmed this request")
	}
}

func TestConversationDeletionConfirmationMapsEncryptedAddress(t *testing.T) {
	a := newAdapter(nil)
	t.Cleanup(a.stop)
	a.chats.hybrid[555] = 7
	if !a.confirmedThreadDeletion(&table.LSTable{
		LSDeletePartialThread: []*table.LSDeletePartialThread{{ThreadKey: 555}},
	}, 7) {
		t.Fatal("an authoritative deletion under the encrypted address did not confirm its canonical conversation")
	}
	if a.confirmedThreadDeletion(&table.LSTable{
		LSDeletePartialThread:  []*table.LSDeletePartialThread{{ThreadKey: 555}},
		LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{{ThreadKey: 7}},
	}, 7) {
		t.Fatal("a canonical replacement row did not supersede encrypted-address deletion")
	}
}

func TestProviderConversationDeletionRowsRemoveCanonicalConversation(t *testing.T) {
	for name, rows := range map[string]*table.LSTable{
		"whole":            {LSDeleteThread: []*table.LSDeleteThread{{ThreadKey: 7}}},
		"for this account": {LSDeletePartialThread: []*table.LSDeletePartialThread{{ThreadKey: 7}}},
		"message request":  {LSDeleteMessageRequest: []*table.LSDeleteMessageRequest{{ThreadKey: 7}}},
		"encrypted jid":    {LSDeletePartialThread: []*table.LSDeletePartialThread{{ThreadKey: 555}}},
		"duplicate forms": {
			LSDeleteThread:        []*table.LSDeleteThread{{ThreadKey: 7}},
			LSDeletePartialThread: []*table.LSDeletePartialThread{{ThreadKey: 7}},
		},
	} {
		t.Run(name, func(t *testing.T) {
			a, wire := identityAdapter(t)
			a.applyTable(context.Background(), identityRows(false))
			wire.Reset()
			a.applyTable(context.Background(), rows)
			if _, visible := a.chats.chatByKey(7); visible || a.chats.messageCount(7) != 0 || !a.chats.threads[7].deleted {
				t.Fatal("authoritative provider deletion did not remove the conversation and its history")
			}
			if mapped, ok := a.chats.threadForJID(555); !ok || mapped != 7 {
				t.Fatal("conversation deletion discarded the encrypted identity mapping")
			}
			if bytes.Count(wire.Bytes(), []byte(`"chatsDeleted":["7"]`)) != 1 {
				t.Fatalf("app was not notified of conversation deletion: %s", wire.String())
			}
		})
	}
}

func TestReplacementBatchDoesNotDeleteConversation(t *testing.T) {
	a, _ := identityAdapter(t)
	a.applyTable(context.Background(), identityRows(false))
	a.applyTable(context.Background(), &table.LSTable{
		LSDeletePartialThread:  []*table.LSDeletePartialThread{{ThreadKey: 7}},
		LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{{ThreadKey: 7, LastActivityTimestampMs: 200}},
	})
	if _, visible := a.chats.chatByKey(7); !visible || a.chats.messageCount(7) != 1 || a.chats.threads[7].deleted {
		t.Fatal("delete-then-replace sync batch hid a live conversation")
	}
}

// Deleting a conversation removes it and everything stored for it, and nothing belonging to any other conversation.
func TestForgettingAConversationWipesOnlyThatConversation(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = kept.db.Close() })
	kept.remember(ctx, 7, "gone1", 2, "one", "", 100)
	kept.remember(ctx, 7, "gone2", 2, "two", "", 200)
	kept.remember(ctx, 8, "stays", 3, "other", "", 300)
	kept.rememberReaction(ctx, 7, "gone1", 2, "👍")

	s := newStore()
	s.get(7)
	s.get(8)
	s.messages[7] = map[string]*message{"gone1": {id: "gone1"}, "gone2": {id: "gone2"}}
	s.messages[8] = map[string]*message{"stays": {id: "stays"}}
	s.hybrid[555] = 7

	if dropped := s.forgetThread(7); dropped != 2 {
		t.Fatalf("dropped %d messages from memory", dropped)
	}
	if _, ok := s.chatByKey(7); ok || s.messages[7] != nil || s.hybrid[555] != 7 || !s.threads[7].deleted {
		t.Fatal("deleted history must disappear while hidden identity and pairing remain")
	}
	if _, ok := s.threads[8]; !ok || len(s.messages[8]) != 1 {
		t.Fatal("another conversation was touched")
	}
	if removed := kept.forgetThread(ctx, 7); removed != 2 {
		t.Fatalf("removed %d stored messages", removed)
	}
	var left, reactions int
	_ = kept.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM encrypted_message`).Scan(&left)
	_ = kept.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM encrypted_reaction WHERE thread = 7`).Scan(&reactions)
	if left != 1 || reactions != 0 {
		t.Fatalf("stored messages left=%d reactions left=%d", left, reactions)
	}
}

// A conversation deleted in the same page that read its card: login names no card of it, as nothing else of it goes out
// (review, 2026-11-14).
func TestDeletedConversationSendsNoCardAtLogin(t *testing.T) {
	a, _ := identityAdapter(t)
	a.applyTable(context.Background(), &table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 9, ThreadType: table.MARKETPLACE, ThreadName: "Synthetic assistant",
			LastActivityTimestampMs: 2000}},
		LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 9, MessageId: "synthetic-question", SenderId: 42, TimestampMs: 2000}},
	})
	card := &table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 9, MessageId: "synthetic-question", SenderId: 42, TimestampMs: 2000}},
		LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
			{ThreadKey: 9, MessageId: "synthetic-question", AttachmentFbid: "synthetic-card", XmaDataclass: syntheticTemplate(t, syntheticQuestion)},
		},
	}
	if a.applyTable(context.Background(), card).cardChanges() == nil {
		t.Fatal("the card was not read, so this proves nothing")
	}
	card.LSDeleteThread = []*table.LSDeleteThread{{ThreadKey: 9}}
	if cards := a.applyTable(context.Background(), card).cardChanges(); cards != nil {
		t.Fatalf("a deleted conversation's card went out at login: %+v", cards)
	}
}
