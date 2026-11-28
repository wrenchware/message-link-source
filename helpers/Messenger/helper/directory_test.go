package main

import (
	"context"
	"strconv"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func TestMessengerSearchTaskUsesPeoplePickerShape(t *testing.T) {
	task := messengerSearchTask("Alex", false)
	if task.Query != "Alex" || task.SurfaceType != 5 || task.Secondary || len(task.SupportedTypes) < 4 {
		t.Fatalf("unexpected search task: %+v", task)
	}
}

func TestMessengerSearchRejectsUnmessageableRows(t *testing.T) {
	valid := &table.LSInsertSearchResult{ResultId: "200", DisplayName: "Alex", ThreadType: table.ONE_TO_ONE, CanViewerMessage: true}
	if id, name, ok := messageableSearchResult(valid, 100); !ok || id != 200 || name != "Alex" {
		t.Fatalf("valid person was rejected: %d %q %t", id, name, ok)
	}
	for name, mutate := range map[string]func(*table.LSInsertSearchResult){
		"cannot message": func(row *table.LSInsertSearchResult) { row.CanViewerMessage = false },
		"restricted":     func(row *table.LSInsertSearchResult) { row.IsRestrictedByViewer = true },
		"blocked":        func(row *table.LSInsertSearchResult) { row.BlockedByViewerStatus = 1 },
		"self":           func(row *table.LSInsertSearchResult) { row.ResultId = "100" },
	} {
		t.Run(name, func(t *testing.T) {
			row := *valid
			mutate(&row)
			if _, _, ok := messageableSearchResult(&row, 100); ok {
				t.Fatal("ineligible picker row was offered")
			}
		})
	}
}

func TestActivatedDeletedDirectSurvivesRestart(t *testing.T) {
	a, _ := identityAdapter(t)
	a.chats.rememberSearchContact(200, "Alex", "")
	a.chats.get(200).deleted = true
	a.chats.get(200).deletedAt = 123
	a.chats.activateDirect(200, 500)
	a.kept.saveIdentity(context.Background(), a.chats)
	restartIdentityAdapter(t, a)
	a.chats.setSecure(true)
	chat, found := a.chats.directChat(200)
	if !found || chat.ReadOnly || a.chats.threads[200].deleted || a.chats.threads[200].deletedAt != 123 || a.chats.hybrid[200] != 200 {
		t.Fatalf("restarted direct was not active and writable: %+v", chat)
	}
}

func TestDirectCreationAndExistingLookup(t *testing.T) {
	tasks := directCreateTasks(200)
	if len(tasks) != 2 {
		t.Fatalf("expected both transports, got %d", len(tasks))
	}
	ordinary, ok := tasks[0].(*socket.CreateThreadTask)
	if !ok || ordinary.ThreadFBID != 200 || ordinary.SyncGroup != 1 {
		t.Fatalf("unexpected ordinary task: %+v", tasks[0])
	}
	secure, ok := tasks[1].(*socket.CreateWhatsAppThreadTask)
	if !ok || secure.WAJID != 200 || secure.ThreadType != table.ENCRYPTED_OVER_WA_ONE_TO_ONE || secure.FolderType != table.INBOX {
		t.Fatalf("unexpected encrypted task: %+v", tasks[1])
	}

	s := newStore()
	s.setSelf(100)
	s.rememberSearchContact(200, "Alex", "https://example.test/alex.jpg")
	s.setSecure(true)
	s.get(200).deleted = true
	s.get(200).deletedAt = 123
	s.get(200).cannotReply = true
	key := s.activateDirect(200, 500)
	chat, found := s.directChat(200)
	if key != 200 || !found || chat.ID != "200" || chat.Title != "Alex" || chat.Group || !chat.Encrypted || chat.ReadOnly || len(chat.Participants) != 2 {
		t.Fatalf("unexpected direct chat: %+v", chat)
	}
	if s.threads[200].deleted || s.threads[200].deletedAt != 123 || s.threads[200].lastActivity != 500 || s.hybrid[200] != 200 {
		t.Fatal("starting a deleted direct must retain its history cutoff and restore its encrypted address")
	}
}

// Live: a direct started with no known pairing opened under the person's address, and the pairing that followed named
// another thread. Both stayed in the list, the stand-in read-only.
func TestLatePairingReplacesDirectStandIn(t *testing.T) {
	for name, previous := range map[string]int64{"unpaired": 0, "paired to a deleted thread": 600} {
		t.Run(name, func(t *testing.T) {
			s := newStore()
			s.setSelf(100)
			s.setSecure(true)
			s.rememberSearchContact(200, "Alex", "")
			if previous != 0 {
				s.hybrid[200] = previous
				s.get(previous).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
				s.get(previous).deleted = true
				s.get(previous).deletedAt = 123
			}
			standIn := s.activateDirect(200, 500)
			asked := s.pairingCount(200)
			touched := s.apply(&table.LSTable{LSVerifyHybridThreadExists: []*table.LSVerifyHybridThreadExists{{ThreadKey: 700, ThreadJID: 200, ThreadType: table.ENCRYPTED_OVER_WA_ONE_TO_ONE}}})
			if s.pairingCount(200) == asked {
				t.Fatal("the pairing was not counted, so starting a chat would not see it")
			}
			if _, ok := s.chatByKey(standIn); ok {
				t.Fatal("the stand-in stayed in the list beside the real thread")
			}
			if len(touched.ChatsDeleted) != 1 || touched.ChatsDeleted[0] != strconv.FormatInt(standIn, 10) {
				t.Fatalf("the app was not told the stand-in is gone: %v", touched.ChatsDeleted)
			}
			chat, found := s.directChat(200)
			if !found || chat.ID != "700" || chat.ReadOnly || s.threads[700].lastActivity != 500 {
				t.Fatalf("the real thread did not take the stand-in's place: %+v", chat)
			}
			if previous != 0 && s.threads[700].deletedAt != 123 {
				t.Fatal("the deleted thread's history cutoff was not carried over")
			}
		})
	}
}

// A duplicate saved before this was fixed goes when Facebook repeats the pairing, as it does on every connect.
func TestRepeatedPairingRemovesSavedStandIn(t *testing.T) {
	s := newStore()
	s.setSelf(100)
	s.setSecure(true)
	s.rememberSearchContact(200, "Alex", "")
	standIn := s.activateDirect(200, 500)
	s.hybrid[200] = 700
	s.get(700).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	s.get(700).lastActivity = 900
	touched := s.apply(&table.LSTable{LSVerifyHybridThreadExists: []*table.LSVerifyHybridThreadExists{{ThreadKey: 700, ThreadJID: 200, ThreadType: table.ENCRYPTED_OVER_WA_ONE_TO_ONE}}})
	if _, ok := s.chatByKey(standIn); ok || len(touched.ChatsDeleted) != 1 {
		t.Fatalf("the saved stand-in was kept: %v", touched.ChatsDeleted)
	}
	if chat, found := s.directChat(200); !found || chat.ID != "700" {
		t.Fatalf("the real thread was lost: %+v", chat)
	}
}

// A conversation with messages in it is never hidden to make way for a pairing.
func TestPairingKeepsSupersededThreadWithMessages(t *testing.T) {
	s := newStore()
	s.setSelf(100)
	s.rememberSearchContact(200, "Alex", "")
	s.hybrid[200] = 600
	s.get(600).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	s.get(600).participants[200] = struct{}{}
	s.messages[600] = map[string]*message{"m": {id: "m", timestamp: 50}}
	touched := s.apply(&table.LSTable{LSVerifyHybridThreadExists: []*table.LSVerifyHybridThreadExists{{ThreadKey: 700, ThreadJID: 200, ThreadType: table.ENCRYPTED_OVER_WA_ONE_TO_ONE}}})
	if s.threads[600].deleted || len(touched.ChatsDeleted) != 0 {
		t.Fatal("a conversation holding messages was hidden")
	}
}

func TestDirectActivationUsesAuthoritativeHybridThread(t *testing.T) {
	s := newStore()
	s.setSelf(100)
	s.setSecure(true)
	s.rememberSearchContact(200, "Alex", "https://example.test/alex.jpg")
	s.hybrid[200] = 700
	s.get(700).cannotReply = true
	if key := s.activateDirect(200, 500); key != 700 {
		t.Fatalf("activated temporary key %d instead of provider thread", key)
	}
	chat, found := s.directChat(200)
	if !found || chat.ID != "700" || chat.ReadOnly || s.hybrid[200] != 700 {
		t.Fatalf("authoritative direct was not writable: %+v", chat)
	}
	if s.media["avatar:200"] == nil {
		t.Fatal("searched contact avatar was not exposed through authenticated media")
	}
}
