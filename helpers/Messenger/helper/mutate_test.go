package main

import (
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	waTypes "go.mau.fi/whatsmeow/types"
)

func editableStore() *store {
	s := newStore()
	s.self = 1
	s.messages[7] = map[string]*message{
		"a": {id: "a", sender: 1, text: "first", timestamp: 100},
		"b": {id: "b", sender: 2, text: "theirs", timestamp: 200},
	}
	current := s.get(7)
	current.lastActivity = 200
	current.snippet = "theirs"
	return s
}

func TestEditChangesTheWordsAndSaysWhatToRedraw(t *testing.T) {
	s := editableStore()
	touched := s.applyEdit(7, "a", "corrected", 300)
	if s.messages[7]["a"].text != "corrected" || s.messages[7]["a"].editedAt != 300 {
		t.Fatalf("the message reads %q edited=%d", s.messages[7]["a"].text, s.messages[7]["a"].editedAt)
	}
	if len(touched.Messages["7"]) != 1 {
		t.Fatalf("the edit asked to redraw %+v", touched)
	}
	// The same edit arriving again — Facebook repeats itself — is not a change and must not redraw the conversation.
	if again := s.applyEdit(7, "a", "corrected", 300); !again.empty() {
		t.Fatalf("a repeated edit redrew %+v", again)
	}
	// An edit for a message this build has never seen invents nothing.
	if unknown := s.applyEdit(7, "missing", "text", 400); !unknown.empty() {
		t.Fatalf("an unknown message was edited into existence: %+v", unknown)
	}
	// Correcting the newest message corrects what the conversation list shows for it.
	s.applyEdit(7, "b", "theirs, corrected", 500)
	if s.threads[7].snippet != "theirs, corrected" {
		t.Fatalf("the list still says %q", s.threads[7].snippet)
	}
}

// Facebook names the message and not its conversation, so the conversation has to be found by the message.
func TestIncomingEditFindsItsConversation(t *testing.T) {
	s := editableStore()
	touched := s.apply(&table.LSTable{LSEditMessage: []*table.LSEditMessage{{MessageID: "a", Text: "from the phone"}}})
	if s.messages[7]["a"].text != "from the phone" || s.messages[7]["a"].editedAt == 0 {
		t.Fatalf("the edit did not land: %q", s.messages[7]["a"].text)
	}
	if len(touched.Messages["7"]) == 0 {
		t.Fatalf("the conversation was not redrawn: %+v", touched)
	}
}

func TestRemovingAnEncryptedMessageSaysItIsGone(t *testing.T) {
	s := editableStore()
	s.attach("a", &attachment{id: "e2ee:a", name: "Photo"})
	s.files["e2ee:a"] = &secureMedia{kind: "image"}
	touched := s.removeEncrypted(7, "a")
	if _, still := s.messages[7]["a"]; still {
		t.Fatal("the message is still in the conversation")
	}
	if len(s.attached["a"]) != 0 || s.media["e2ee:a"] != nil || s.files["e2ee:a"] != nil {
		t.Fatal("the message went but its attachment stayed")
	}
	if len(touched.Deleted["7"]) != 1 {
		t.Fatalf("the removal was reported as %+v", touched)
	}
	// Taking back something already gone reports nothing rather than a phantom deletion.
	if again := s.removeEncrypted(7, "a"); !again.empty() {
		t.Fatalf("a second removal reported %+v", again)
	}
}

// The key that names an existing message. A group has to say whose message it is; a one-to-one must not.
func TestEncryptedMessageKeyNamesTheRightThings(t *testing.T) {
	direct := waTypes.JID{User: "500", Server: waTypes.MessengerServer}
	key := encryptedMessageKey(direct, viewMessage{ID: "m", Sender: "2", Outgoing: false})
	if key.GetID() != "m" || key.GetFromMe() || key.GetParticipant() != "" {
		t.Fatalf("a one-to-one key came out as %+v", key)
	}
	mine := encryptedMessageKey(direct, viewMessage{ID: "m", Sender: "1", Outgoing: true})
	if !mine.GetFromMe() {
		t.Fatal("a message this account sent was not marked as its own")
	}
	group := encryptedMessageKey(waTypes.JID{User: "900", Server: waTypes.GroupServer}, viewMessage{ID: "m", Sender: "2"})
	if group.GetParticipant() == "" {
		t.Fatal("a group key does not say whose message it is")
	}
}
