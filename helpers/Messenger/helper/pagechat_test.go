package main

import (
	"slices"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// A business page's promotion (2026-11-25, #93) came as its messages alone: no thread, contact or participant row. The
// page's id is the thread key, so the conversation is that page's one-to-one, and the page is asked for its name and
// picture like any participant.
func TestKeyedConversationWithoutThreadRowIsOneToOne(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	s.apply(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{
		{ThreadKey: 900, MessageId: "notice", SenderId: 900, TimestampMs: 1000, Text: "Synthetic notice", IsAdminMessage: true},
		{ThreadKey: 900, MessageId: "line", SenderId: 900, TimestampMs: 2000, Text: "Synthetic line"},
	}})
	chat, ok := s.chatByKey(900)
	if !ok || chat.Group || len(chat.Participants) != 2 {
		t.Fatalf("a keyed conversation with no thread row must be a one-to-one with its key's person: group=%v people=%d", chat.Group, len(chat.Participants))
	}
	if !slices.Contains(s.missingPictures(5), 900) {
		t.Fatal("the page must be asked for its name and picture")
	}
	// The lookup's answer names the conversation.
	s.apply(&table.LSTable{LSDeleteThenInsertContact: []*table.LSDeleteThenInsertContact{{Id: 900, Name: "Synthetic Page"}}})
	if chat, _ := s.chatByKey(900); chat.Title != "Synthetic Page" {
		t.Fatalf("the page's name must title the conversation: %q", chat.Title)
	}
	if page := s.historyBefore(900, 0, 10); len(page) != 2 || !page[0].Notice {
		t.Fatal("the page's notice must stay a notice")
	}
}

// The same conversation's other sign: the page reads it, as a one-to-one's other person does.
func TestKeyedConversationAdoptedFromReadMark(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	s.apply(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 900, MessageId: "m", SenderId: 0, TimestampMs: 1000}}})
	if chat, _ := s.chatByKey(900); !chat.Group {
		t.Fatal("nothing has named the other person yet")
	}
	changed := s.apply(&table.LSTable{LSUpdateReadReceipt: []*table.LSUpdateReadReceipt{{ThreadKey: 900, ContactId: 900, ReadWatermarkTimestampMs: 1000}}})
	if chat, _ := s.chatByKey(900); chat.Group || len(chat.Participants) != 2 {
		t.Fatal("a read mark from the key's own id must make it a one-to-one")
	}
	if !slices.Contains(changed.Chats, "900") {
		t.Fatal("the app must hear that the conversation changed")
	}
}

// Nothing else is guessed: a sender other than the key (a group's member), a conversation Facebook described, this
// account's own conversation and a nameless encrypted arrival keep what they were.
func TestKeyedConversationGuessesNothingElse(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 800, ThreadType: table.GROUP_THREAD, ThreadName: "Synthetic group"}},
		LSInsertMessage: []*table.LSInsertMessage{
			{ThreadKey: 700, MessageId: "a", SenderId: 42, TimestampMs: 1000},
			{ThreadKey: 800, MessageId: "b", SenderId: 800, TimestampMs: 1000},
			{ThreadKey: 1, MessageId: "c", SenderId: 1, TimestampMs: 1000},
		},
	})
	for _, key := range []int64{700, 1} {
		if current := s.threads[key]; current.kind != table.UNKNOWN_THREAD_TYPE || len(current.participants) != 0 {
			t.Fatalf("conversation %d must not be guessed", key)
		}
	}
	if current := s.threads[800]; current.kind != table.GROUP_THREAD || len(current.participants) != 0 {
		t.Fatal("a described group must keep its kind")
	}
	s.get(600).unresolved = true
	s.apply(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 600, MessageId: "d", SenderId: 600, TimestampMs: 1000}}})
	if s.threads[600].kind != table.UNKNOWN_THREAD_TYPE {
		t.Fatal("an encrypted arrival waiting for its identity must not be guessed")
	}
}

// A conversation kept before this rule (as the one #93 found) is adopted on the next start from its kept messages.
func TestKeyedConversationAdoptedAfterRestore(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	s.get(900).lastActivity = 2000
	s.messages[900] = map[string]*message{
		"m": {id: "m", sender: 900, timestamp: 2000},
	}
	s.get(901).lastActivity = 2000
	s.messages[901] = map[string]*message{
		"e": {id: "e", sender: 901, timestamp: 2000, encrypted: true},
	}
	s.adoptKeyPeople()
	if chat, _ := s.chatByKey(900); chat.Group || len(chat.Participants) != 2 {
		t.Fatal("a kept keyed conversation must be adopted on restore")
	}
	if s.threads[901].kind != table.UNKNOWN_THREAD_TYPE {
		t.Fatal("an encrypted kept message must not adopt its conversation")
	}
}
