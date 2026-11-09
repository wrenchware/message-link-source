package main

import (
	"context"
	"encoding/json"
	"errors"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// Archiving is DeleteThreadTask's remove type 1, and bringing a conversation back is the unarchive task of its own, as
// Messenger's web client sends them. Remove type 2 is never sent: that client files it under the archive too.
func TestFolderMovesSendMessengersOwnRequests(t *testing.T) {
	for _, tc := range []struct {
		archived, encrypted bool
		label, queue        string
		payload             string
	}{
		{true, false, "146", "7", `{"thread_key":7,"remove_type":1,"sync_group":1}`},
		{true, true, "146", "7", `{"thread_key":7,"remove_type":1,"sync_group":95}`},
		{false, false, "242", "unarchive_thread", `{"thread_id":"7","sync_group":1}`},
		{false, true, "242", "unarchive_thread", `{"thread_id":"7","sync_group":95}`},
	} {
		task := chatFolderTask(7, tc.archived, tc.encrypted)
		payload, queue := task.Create()
		encoded, err := json.Marshal(payload)
		if err != nil {
			t.Fatal(err)
		}
		if task.GetLabel() != tc.label || queue != tc.queue || string(encoded) != tc.payload {
			t.Fatalf("archived=%v encrypted=%v sent label=%s queue=%s payload=%s", tc.archived, tc.encrypted, task.GetLabel(), queue, encoded)
		}
		if remove, ok := task.(*socket.DeleteThreadTask); ok && remove.RemoveType != 1 {
			t.Fatalf("a folder move sent remove type %d", remove.RemoveType)
		}
	}
	if words := folderTaskWords(chatFolderTask(7, true, false)); words != "task=146 removeType=1" {
		t.Fatalf("archive logged as %q", words)
	}
	if words := folderTaskWords(chatFolderTask(7, false, false)); words != "task=242" {
		t.Fatalf("unarchive logged as %q", words)
	}
}

// Messenger names an archived conversation "archived" or "e2ee_cutover_archived", or clears the name under the archive's
// parent; "archive" is what this helper saved before 2026-10-17. Every one of them reads as archived, nothing else does.
func TestEveryArchivedFolderMessengerUsesReadsAsArchived(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
		{ThreadKey: 1, ThreadType: table.ONE_TO_ONE, FolderName: "archived"},
		{ThreadKey: 2, ThreadType: table.ONE_TO_ONE, FolderName: "e2ee_cutover_archived"},
		{ThreadKey: 3, ThreadType: table.ONE_TO_ONE, ParentThreadKey: archiveParent},
		{ThreadKey: 4, ThreadType: table.ONE_TO_ONE, FolderName: "archive"},
		{ThreadKey: 5, ThreadType: table.ONE_TO_ONE, FolderName: "inbox"},
		{ThreadKey: 6, ThreadType: table.ONE_TO_ONE, FolderName: "e2ee_cutover"},
		{ThreadKey: 7, ThreadType: table.ONE_TO_ONE, FolderName: "pending", ParentThreadKey: -1},
		{ThreadKey: 8, ThreadType: table.ONE_TO_ONE, FolderName: "inbox", ParentThreadKey: archiveParent},
	}})
	for key, want := range map[int64]bool{1: true, 2: true, 3: true, 4: true, 5: false, 6: false, 7: false, 8: false} {
		found, ok := s.chatByKey(key)
		if !ok || found.Archived != want {
			t.Fatalf("conversation %d archived=%v, want %v", key, found.Archived, want)
		}
	}
	// A partial row that names no folder says nothing about it; one under the archive's parent moves it there.
	s.apply(&table.LSTable{LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{{ThreadKey: 1, LastActivityTimestampMs: 1}}})
	if found, _ := s.chatByKey(1); !found.Archived {
		t.Fatal("a partial row without a folder took a conversation out of the archive")
	}
	s.apply(&table.LSTable{LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{{ThreadKey: 5, LastActivityTimestampMs: 1, ParentThreadKey: archiveParent}}})
	if found, _ := s.chatByKey(5); !found.Archived {
		t.Fatal("a partial row under the archive's parent did not archive the conversation")
	}
	// Messenger's own folder moves, from any device.
	s.apply(&table.LSTable{LSMoveThreadToArchivedFolder: []*table.LSMoveThreadToArchivedFolder{{ThreadKey: 6}},
		LSMoveThreadToInboxAndUpdateParent: []*table.LSMoveThreadToInboxAndUpdateParent{{ThreadKey: 2}}})
	archived, _ := s.chatByKey(6)
	inbox, _ := s.chatByKey(2)
	if !archived.Archived || inbox.Archived {
		t.Fatalf("folder moves were not applied: archived=%v inbox=%v", archived.Archived, inbox.Archived)
	}
	toArchive, toInbox, byName, byParent := folderRowCounts(&table.LSTable{
		LSMoveThreadToArchivedFolder:       []*table.LSMoveThreadToArchivedFolder{{ThreadKey: 6}},
		LSMoveThreadToInboxAndUpdateParent: []*table.LSMoveThreadToInboxAndUpdateParent{{ThreadKey: 2}, {ThreadKey: 3}},
		LSDeleteThenInsertThread:           []*table.LSDeleteThenInsertThread{{ThreadKey: 1, FolderName: "archived"}, {ThreadKey: 3, ParentThreadKey: archiveParent}},
		LSUpdateOrInsertThread:             []*table.LSUpdateOrInsertThread{{ThreadKey: 4, FolderName: "inbox"}},
	})
	if toArchive != 1 || toInbox != 2 || byName != 1 || byParent != 1 {
		t.Fatalf("folder rows counted toArchive=%d toInbox=%d byName=%d byParent=%d", toArchive, toInbox, byName, byParent)
	}
}

// One archived and one inbox conversation, with a stand-in socket that answers each request with reply().
func folderAdapter(t *testing.T, reply func(task socket.Task) (*table.LSTable, error)) (*adapter, *[]socket.Task) {
	t.Helper()
	a, _ := identityAdapter(t)
	a.applyTable(context.Background(), &table.LSTable{LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
		{ThreadKey: 40, ThreadName: "Archived", ThreadType: table.GROUP_THREAD, LastActivityTimestampMs: 100, FolderName: "archived"},
		{ThreadKey: 41, ThreadName: "Inbox", ThreadType: table.GROUP_THREAD, LastActivityTimestampMs: 100, FolderName: "inbox"},
	}})
	var sent []socket.Task
	a.executeTasks = func(_ context.Context, tasks ...socket.Task) (*table.LSTable, error) {
		sent = append(sent, tasks...)
		return reply(tasks[0])
	}
	previous := folderEchoWait
	folderEchoWait = 150 * time.Millisecond
	t.Cleanup(func() { folderEchoWait = previous })
	return a, &sent
}

func archivedNow(t *testing.T, a *adapter, key int64) bool {
	t.Helper()
	archived, known := a.chats.archivedNow(key)
	if !known {
		t.Fatalf("conversation %d is not held", key)
	}
	return archived
}

// A move back to the inbox shows once Messenger states it or acknowledges the request: it states nothing for an encrypted
// conversation, which would otherwise stay under Archived with its new message. A refusal or a failed request moves nothing.
func TestAFolderMoveBackIsShownWhenMessengerStatesOrAcknowledgesIt(t *testing.T) {
	inboxRow := func(key int64) *table.LSTable {
		return &table.LSTable{LSMoveThreadToInboxAndUpdateParent: []*table.LSMoveThreadToInboxAndUpdateParent{{ThreadKey: key}}}
	}
	for _, tc := range []struct {
		name     string
		reply    *table.LSTable
		err      error
		socket   *table.LSTable // applied as a socket table while the move waits
		code     string
		archived bool // the conversation afterwards
	}{
		{name: "stated in the reply", reply: inboxRow(40), code: "", archived: false},
		{name: "stated as a whole row", reply: &table.LSTable{LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: 40, ThreadType: table.GROUP_THREAD, LastActivityTimestampMs: 100, FolderName: "inbox"}}}, code: "", archived: false},
		{name: "stated on the socket", reply: &table.LSTable{}, socket: inboxRow(40), code: "", archived: false},
		{name: "bare acknowledgement", reply: &table.LSTable{}, code: "", archived: false},
		{name: "no reply table", reply: nil, code: "", archived: false},
		{name: "the old folder restated", reply: &table.LSTable{LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: 40, ThreadType: table.GROUP_THREAD, LastActivityTimestampMs: 100, FolderName: "archived"}}}, code: "", archived: false},
		{name: "another conversation moved", reply: inboxRow(41), code: "", archived: false},
		{name: "refused", reply: &table.LSTable{LSHandleFailedTask: []*table.LSHandleFailedTask{{}}}, code: "update_rejected", archived: true},
		{name: "request failed", err: errors.New("socket closed"), code: "update_failed", archived: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			var a *adapter
			socketDone := make(chan struct{})
			a, sent := folderAdapter(t, func(socket.Task) (*table.LSTable, error) {
				go func() {
					defer close(socketDone)
					if tc.socket != nil {
						time.Sleep(20 * time.Millisecond)
						a.applyTable(context.Background(), tc.socket)
					}
				}()
				return tc.reply, tc.err
			})
			result, code := call(a, "ui.updateChat", `{"chatId":"40","archived":false}`)
			<-socketDone
			if code != tc.code {
				t.Fatalf("answered %q, want %q", code, tc.code)
			}
			if len(*sent) != 1 {
				t.Fatalf("sent %d requests", len(*sent))
			}
			if _, ok := (*sent)[0].(*unarchiveThreadTask); !ok {
				t.Fatalf("sent %T to bring the conversation back", (*sent)[0])
			}
			if got := archivedNow(t, a, 40); got != tc.archived {
				t.Fatalf("archived=%v afterwards, want %v", got, tc.archived)
			}
			if code == "" {
				if chat, ok := result.(viewChat); !ok || chat.Archived {
					t.Fatalf("a confirmed move answered %+v", result)
				}
			}
			// What is saved is what is shown: a restart reads back the folder Messenger stated.
			restartIdentityAdapter(t, a)
			if got := archivedNow(t, a, 40); got != tc.archived {
				t.Fatalf("archived=%v after a restart, want %v", got, tc.archived)
			}
		})
	}
}

func TestArchivingIsConfirmedByEitherFormOfMessengersStatement(t *testing.T) {
	for name, statement := range map[string]*table.LSTable{
		"folder move":  {LSMoveThreadToArchivedFolder: []*table.LSMoveThreadToArchivedFolder{{ThreadKey: 41}}},
		"archive name": {LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{{ThreadKey: 41, LastActivityTimestampMs: 100, FolderName: "archived"}}},
		"archive parent": {LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: 41, ThreadType: table.GROUP_THREAD, LastActivityTimestampMs: 100, ParentThreadKey: archiveParent}}},
	} {
		t.Run(name, func(t *testing.T) {
			a, sent := folderAdapter(t, func(socket.Task) (*table.LSTable, error) { return statement, nil })
			result, code := call(a, "ui.updateChat", `{"chatId":"41","archived":true}`)
			if code != "" {
				t.Fatalf("answered %q", code)
			}
			if remove, ok := (*sent)[0].(*socket.DeleteThreadTask); !ok || remove.RemoveType != 1 || remove.ThreadKey != 41 || remove.SyncGroup != 1 {
				t.Fatalf("archived with %#v", (*sent)[0])
			}
			if chat, ok := result.(viewChat); !ok || !chat.Archived {
				t.Fatalf("a confirmed archive answered %+v", result)
			}
		})
	}
}

// Messenger archives on request and never says so to the device that asked (2026-10-30), so an archive it acknowledged
// is applied and saved as its own statement would be. A refusal or a failed request still moves nothing.
func TestAnAcknowledgedArchiveIsApplied(t *testing.T) {
	for _, tc := range []struct {
		name     string
		reply    *table.LSTable
		err      error
		code     string
		archived bool
	}{
		{name: "bare acknowledgement", reply: &table.LSTable{}, code: "", archived: true},
		{name: "no reply table", reply: nil, code: "", archived: true},
		{name: "refused", reply: &table.LSTable{LSHandleFailedTask: []*table.LSHandleFailedTask{{}}}, code: "update_rejected", archived: false},
		{name: "request failed", err: errors.New("socket closed"), code: "update_failed", archived: false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			a, sent := folderAdapter(t, func(socket.Task) (*table.LSTable, error) { return tc.reply, tc.err })
			result, code := call(a, "ui.updateChat", `{"chatId":"41","archived":true}`)
			if code != tc.code {
				t.Fatalf("answered %q, want %q", code, tc.code)
			}
			if len(*sent) != 1 {
				t.Fatalf("sent %d requests", len(*sent))
			}
			if got := archivedNow(t, a, 41); got != tc.archived {
				t.Fatalf("archived=%v afterwards, want %v", got, tc.archived)
			}
			if code == "" {
				if chat, ok := result.(viewChat); !ok || !chat.Archived {
					t.Fatalf("an acknowledged archive answered %+v", result)
				}
			}
			if len(a.folders.pending) != 0 {
				t.Fatal("a finished move left its watch behind")
			}
			restartIdentityAdapter(t, a)
			if got := archivedNow(t, a, 41); got != tc.archived {
				t.Fatalf("archived=%v after a restart, want %v", got, tc.archived)
			}
		})
	}
}

// Messenger's statement after the move stopped waiting is still believed, and the watch it no longer has is not in the
// way of the next move.
func TestALateStatementStillMovesTheConversation(t *testing.T) {
	a, sent := folderAdapter(t, func(socket.Task) (*table.LSTable, error) { return &table.LSTable{}, nil })
	if _, code := call(a, "ui.updateChat", `{"chatId":"40","archived":false}`); code != "" {
		t.Fatalf("answered %q", code)
	}
	a.applyTable(context.Background(), &table.LSTable{LSMoveThreadToArchivedFolder: []*table.LSMoveThreadToArchivedFolder{{ThreadKey: 40}}})
	if !archivedNow(t, a, 40) {
		t.Fatal("Messenger's late statement was not applied")
	}
	if len(a.folders.pending) != 0 {
		t.Fatal("a finished move left its watch behind")
	}
	if len(*sent) != 1 {
		t.Fatalf("an acknowledged move was sent %d times", len(*sent))
	}
}
