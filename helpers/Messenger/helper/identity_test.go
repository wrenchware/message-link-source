package main

import (
	"bytes"
	"context"
	"encoding/json"
	"path/filepath"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	waTypes "go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
)

func identityAdapter(t *testing.T) (*adapter, *bytes.Buffer) {
	t.Helper()
	var wire bytes.Buffer
	a := newAdapter(&output{encoder: json.NewEncoder(&wire)})
	kept, err := openMessageStore(context.Background(), filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	a.kept = kept
	a.crypto = &encrypted{messages: kept}
	a.chats.setSelf(1)
	t.Cleanup(func() { a.stop(); _ = a.kept.db.Close() })
	return a, &wire
}

func identityRows(group bool) *table.LSTable {
	kind := table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	name := ""
	if group {
		kind = table.ENCRYPTED_OVER_WA_GROUP
		name = "Test group"
	}
	return &table.LSTable{
		LSDeleteThenInsertThread:        []*table.LSDeleteThenInsertThread{{ThreadKey: 7, ThreadName: name, ThreadType: kind, LastActivityTimestampMs: 100, ThreadPictureUrl: "https://example.test/group.jpg"}},
		LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{{ThreadKey: 7, ContactId: 1}, {ThreadKey: 7, ContactId: 555}},
		LSDeleteThenInsertContact:       []*table.LSDeleteThenInsertContact{{Id: 555, Name: "Test peer", ProfilePictureUrl: "https://example.test/peer.jpg"}},
		LSVerifyHybridThreadExists:      []*table.LSVerifyHybridThreadExists{{ThreadKey: 7, ThreadJID: 555, ThreadType: kind}},
		LSInsertMessage:                 []*table.LSInsertMessage{{ThreadKey: 7, MessageId: "old", SenderId: 555, Text: "old history", TimestampMs: 100}},
	}
}

func restartIdentityAdapter(t *testing.T, a *adapter) {
	t.Helper()
	var seq int
	var name, path string
	if err := a.kept.db.QueryRow(`PRAGMA database_list`).Scan(&seq, &name, &path); err != nil {
		t.Fatal(err)
	}
	if err := a.kept.db.Close(); err != nil {
		t.Fatal(err)
	}
	kept, err := openMessageStore(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	a.kept, a.crypto.messages = kept, kept
	a.chats = newStore()
	a.chats.setSelf(1)
	a.kept.restore(context.Background(), a.chats)
}

func encryptedEnvelope(id string, at int64, group bool, outgoing bool) *events.FBMessage {
	server := waTypes.MessengerServer
	if group {
		server = waTypes.GroupServer
	}
	sender := "555"
	if outgoing {
		sender = "1"
	}
	return &events.FBMessage{Info: waTypes.MessageInfo{
		MessageSource: waTypes.MessageSource{Chat: waTypes.NewJID("555", server), Sender: waTypes.NewJID(sender, waTypes.MessengerServer), IsFromMe: outgoing},
		ID:            id, Timestamp: time.UnixMilli(at),
	}, Message: &waConsumerApplication.ConsumerApplication{Payload: &waConsumerApplication.ConsumerApplication_Payload{Payload: &waConsumerApplication.ConsumerApplication_Payload_Content{Content: contentOf(&waConsumerApplication.ConsumerApplication_Content_MessageText{MessageText: &waCommon.MessageText{Text: proto.String("new message")}})}}}}
}

func TestDeletedIdentitySurvivesRestartAndEncryptedEnvelope(t *testing.T) {
	for _, group := range []bool{false, true} {
		t.Run(map[bool]string{false: "direct", true: "group"}[group], func(t *testing.T) {
			ctx := context.Background()
			a, _ := identityAdapter(t)
			a.applyTable(ctx, identityRows(group))
			a.forgetChat(ctx, 7)
			cutoff := a.chats.threads[7].deletedAt
			if len(a.chats.chats(0)) != 0 {
				t.Fatal("deleted conversation remained visible")
			}
			// Restart without any new Facebook thread records.
			restartIdentityAdapter(t, a)
			if len(a.chats.chats(0)) != 0 || a.chats.messageCount(7) != 0 {
				t.Fatal("restart restored deleted history or its row")
			}
			if key, ok := a.chats.threadForJID(555); !ok || key != 7 {
				t.Fatal("restart lost encrypted pairing")
			}
			// Neither old server metadata nor old encrypted/ordinary history can resurrect a deleted thread.
			a.applyTable(ctx, identityRows(group))
			a.applyEncrypted(encryptedEnvelope("replay", cutoff-1, group, false))
			if len(a.chats.chats(0)) != 0 || a.chats.messageCount(7) != 0 {
				t.Fatal("stale rows resurrected deleted thread")
			}
			a.applyEncrypted(encryptedEnvelope("new", cutoff+2000, group, true))
			chat, ok := a.chats.chatByKey(7)
			want := "Test peer"
			if group {
				want = "Test group"
			}
			if !ok || chat.Title != want || chat.Group != group || !chat.Encrypted || len(chat.Participants) != 2 || chat.AvatarURL == "" {
				t.Fatalf("identity did not recover: %+v", chat)
			}
			if a.chats.messageCount(7) != 1 || a.chats.messageCount(555) != 0 {
				t.Fatal("new message used wrong identity or old history returned")
			}
			if page := a.chats.history(7, 0); len(page) != 1 || page[0].ChatID != "7" || page[0].Text != "new message" {
				t.Fatal("recovered message did not reach UI history")
			}
			restartIdentityAdapter(t, a)
			chat, ok = a.chats.chatByKey(7)
			if !ok || chat.Title != want || chat.Group != group || !chat.Encrypted || a.chats.messageCount(7) != 1 {
				t.Fatal("recovered identity was not durable")
			}
		})
	}
}

func TestIdentitySnapshotsKeepMetadataOnlyThreadsAndRollbackOnFailure(t *testing.T) {
	a, _ := identityAdapter(t)
	ctx := context.Background()
	a.applyTable(ctx, &table.LSTable{LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 7, ThreadName: "Original group", ThreadType: table.ENCRYPTED_OVER_WA_GROUP, LastActivityTimestampMs: 100}}})
	restartIdentityAdapter(t, a)
	chat, ok := a.chats.chatByKey(7)
	if !ok || chat.Title != "Original group" || !chat.Group || !chat.Encrypted {
		t.Fatal("metadata-only thread did not survive restart")
	}
	// Sparse full rows cannot erase identity, or move activity backwards when history is absent.
	a.applyTable(ctx, &table.LSTable{LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 7}}})
	chat, ok = a.chats.chatByKey(7)
	if !ok || chat.Title != "Original group" || !chat.Encrypted || chat.At.UnixMilli() != 100 {
		t.Fatal("sparse full row erased known identity")
	}
	if _, err := a.kept.db.Exec(`CREATE TRIGGER fail_contact BEFORE INSERT ON thread_contact BEGIN SELECT RAISE(ABORT, 'synthetic_failure'); END`); err != nil {
		t.Fatal(err)
	}
	a.chats.get(7).name = "Unsaved group"
	a.chats.remember(42, "Unsaved peer", "", "")
	a.kept.saveIdentity(ctx, a.chats)
	restored := newStore()
	restored.setSelf(1)
	a.kept.restore(ctx, restored)
	chat, ok = restored.chatByKey(7)
	if !ok || chat.Title != "Original group" {
		t.Fatal("failed transaction discarded previously saved identity")
	}
}

func TestExplicitGroupIdentityChangesSurviveDeletionAndRestart(t *testing.T) {
	a, _ := identityAdapter(t)
	ctx := context.Background()
	a.applyTable(ctx, identityRows(true))
	a.applyTable(ctx, &table.LSTable{
		LSSyncUpdateThreadName:        []*table.LSSyncUpdateThreadName{{ThreadKey: 7, ThreadName: "Renamed group"}},
		LSRemoveParticipantFromThread: []*table.LSRemoveParticipantFromThread{{ThreadKey: 7, ParticipantId: 555}},
		LSSetThreadImageURL:           []*table.LSSetThreadImageURL{{ThreadKey: 7}},
	})
	a.forgetChat(ctx, 7)
	cutoff := a.chats.threads[7].deletedAt
	restartIdentityAdapter(t, a)
	a.applyEncrypted(encryptedEnvelope("new", cutoff+2000, true, false))
	chat, ok := a.chats.chatByKey(7)
	if !ok || chat.Title != "Renamed group" || len(chat.Participants) != 1 || chat.AvatarURL != "" || !chat.Group || !chat.Encrypted {
		t.Fatalf("stale group identity returned: %+v", chat)
	}
}

func TestLatePairingMovesIdentityAndEveryPersistedMessagePart(t *testing.T) {
	for _, authority := range []bool{false, true} {
		t.Run(map[bool]string{false: "verify", true: "authority"}[authority], func(t *testing.T) {
			ctx := context.Background()
			a, _ := identityAdapter(t)
			a.chats.encryptedIdentity(555, 555, waTypes.MessengerServer)
			a.chats.remember(555, "Test peer", "", "")
			touched := a.chats.addEncrypted(555, "held", 555, "new history", "", 300)
			a.keepMessages(ctx, touched)
			a.kept.rememberMedia(ctx, 555, "held", 300, &attachment{id: "file", mime: "image/jpeg"}, &secureMedia{transport: []byte{}})
			a.kept.rememberAttachments(ctx, 555, "held", []*attachment{{id: "picture", mime: "image/jpeg"}}, nil)
			a.kept.rememberReaction(ctx, 555, "held", 1, "like")
			rows := &table.LSTable{}
			if authority {
				rows.LSUpdateThreadAuthorityAndMappingWithOTIDFromJID = []*table.LSUpdateThreadAuthorityAndMappingWithOTIDFromJID{{ThreadKey: 7, ThreadJID: 555}}
			} else {
				rows.LSVerifyHybridThreadExists = []*table.LSVerifyHybridThreadExists{{ThreadKey: 7, ThreadJID: 555, ThreadType: table.ENCRYPTED_OVER_WA_ONE_TO_ONE}}
			}
			touched = a.applyTable(ctx, rows)
			if len(touched.ChatsDeleted) != 1 || touched.ChatsDeleted[0] != "555" || len(touched.Messages["7"]) != 1 {
				t.Fatalf("UI move was not announced: %+v", touched)
			}
			chat, ok := a.chats.chatByKey(7)
			if !ok || chat.Title != "Test peer" || chat.At.UnixMilli() != 300 || chat.Preview != "new history" || chat.Group || !chat.Encrypted {
				t.Fatalf("late mapping lost identity/summary: %+v", chat)
			}
			for _, name := range []string{"encrypted_message", "encrypted_media", "message_attachment", "encrypted_reaction"} {
				var key int64
				if err := a.kept.db.QueryRowContext(ctx, `SELECT thread FROM `+name+` LIMIT 1`).Scan(&key); err != nil || key != 7 {
					t.Fatalf("%s stayed under temporary identity: %d %v", name, key, err)
				}
			}
			a.chats = newStore()
			a.chats.setSelf(1)
			a.kept.restore(ctx, a.chats)
			if len(a.chats.chats(0)) != 1 || a.chats.messageCount(7) != 1 || a.chats.messageCount(555) != 0 {
				t.Fatal("restart recreated temporary conversation")
			}
		})
	}
}

func TestEncryptedIdentityWithoutThreadRecordsDoesNotInventAGroup(t *testing.T) {
	a, _ := identityAdapter(t)
	// Outgoing echoes must use the chat peer rather than our account as the title/participant.
	a.chats.remember(555, "Test peer", "", "")
	a.applyEncrypted(encryptedEnvelope("direct", 300, false, true))
	chat, ok := a.chats.chatByKey(555)
	if !ok || chat.Title != "Test peer" || chat.Group || !chat.Encrypted {
		t.Fatal("direct envelope failed to recover known peer identity")
	}
	b, _ := identityAdapter(t)
	b.chats.remember(555, "Test sender", "", "")
	b.applyEncrypted(encryptedEnvelope("group", 300, true, false))
	if len(b.chats.chats(0)) != 0 || b.chats.messageCount(555) != 1 {
		t.Fatal("group sender was used as group identity or message was lost")
	}
	b.chats = newStore()
	b.chats.setSelf(1)
	b.kept.restore(context.Background(), b.chats)
	if len(b.chats.chats(0)) != 0 || b.chats.messageCount(555) != 1 {
		t.Fatal("restart exposed unresolved group or lost message")
	}
	b.applyTable(context.Background(), &table.LSTable{LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{{ThreadKey: 555, ThreadName: "Recovered group", ThreadType: table.ENCRYPTED_OVER_WA_GROUP}}})
	chat, ok = b.chats.chatByKey(555)
	if !ok || chat.Title != "Recovered group" || !chat.Group || !chat.Encrypted {
		t.Fatal("late group identity did not reveal retained message")
	}
}

func TestGroupMetadataRecoveryUsesCurrentPairingAndCannotReviveDeletion(t *testing.T) {
	for _, deleted := range []bool{false, true} {
		t.Run(map[bool]string{false: "paired", true: "deleted"}[deleted], func(t *testing.T) {
			a, _ := identityAdapter(t)
			started, release, done := make(chan struct{}), make(chan struct{}), make(chan struct{})
			a.groupInfo = func(ctx context.Context, address waTypes.JID) (*waTypes.GroupInfo, error) {
				close(started)
				select {
				case <-release:
				case <-ctx.Done():
					return nil, ctx.Err()
				}
				info := &waTypes.GroupInfo{}
				info.Name = "Fetched group"
				info.Participants = []waTypes.GroupParticipant{{JID: waTypes.NewJID("1", waTypes.MessengerServer)}, {JID: waTypes.NewJID("42", waTypes.MessengerServer)}}
				close(done)
				return info, nil
			}
			a.applyEncrypted(encryptedEnvelope("group", 300, true, false))
			select {
			case <-started:
			case <-time.After(time.Second):
				t.Fatal("metadata recovery did not start")
			}
			a.applyTable(context.Background(), &table.LSTable{LSVerifyHybridThreadExists: []*table.LSVerifyHybridThreadExists{{ThreadKey: 7, ThreadJID: 555, ThreadType: table.ENCRYPTED_OVER_WA_GROUP}}})
			if deleted {
				a.forgetChat(context.Background(), 7)
			}
			close(release)
			select {
			case <-done:
			case <-time.After(time.Second):
				t.Fatal("metadata recovery did not finish")
			}
			// Wait for the callback's state update, without reading its output buffer concurrently.
			deadline := time.Now().Add(time.Second)
			for time.Now().Before(deadline) {
				a.chats.mu.Lock()
				name := a.chats.threads[7].name
				a.chats.mu.Unlock()
				if name == "Fetched group" {
					break
				}
				time.Sleep(time.Millisecond)
			}
			a.state.Lock()
			defer a.state.Unlock()
			chat, ok := a.chats.chatByKey(7)
			if deleted {
				if ok || a.chats.messageCount(7) != 0 {
					t.Fatal("late group metadata resurrected deletion")
				}
			} else {
				if !ok || chat.Title != "Fetched group" || !chat.Group || !chat.Encrypted || len(chat.Participants) != 2 {
					t.Fatalf("group metadata did not resolve current identity: %+v", chat)
				}
				restored := newStore()
				restored.setSelf(1)
				a.kept.restore(context.Background(), restored)
				chat, ok = restored.chatByKey(7)
				if !ok || chat.Title != "Fetched group" || restored.messageCount(555) != 0 {
					t.Fatal("fetched group identity did not persist")
				}
			}
		})
	}
}
