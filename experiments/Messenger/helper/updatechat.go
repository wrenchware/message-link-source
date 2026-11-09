package main

import (
	"context"
	"encoding/json"
	"strconv"
	"strings"
	"sync"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// Archiving and bringing a conversation back are two different requests in Messenger's own web client (its
// LSOptimisticRemoveThread and LSOptimisticUnarchiveThread procedures, read 2026-10-17; Texts' Meta client sent the
// same two): DeleteThreadTask with remove type 1 archives, and a task of its own, label 242 on the unarchive_thread
// queue, brings one back. Remove type 2, which this helper sent to bring one back until 2026-10-17, is filed under the
// archive in that client too. That fits what was seen on 2026-10-17: Messenger acknowledged an unarchive sent as type
// 2 and kept the conversation archived. The archive was seen working on 2026-10-30 (an encrypted chat, group 95):
// Messenger filed it under Archived chats and said nothing about it to this device. The unarchive has not been seen.
const archiveRemoveType int64 = 1

const unarchiveThreadLabel = "242"

// messagix has no task for bringing a conversation back, so it is described here as Messenger's web client sends it:
// the thread as a string, and the sync group the conversation lives in.
type unarchiveThreadTask struct {
	ThreadID  string `json:"thread_id"`
	SyncGroup int64  `json:"sync_group"`
}

func (t *unarchiveThreadTask) GetLabel() string      { return unarchiveThreadLabel }
func (t *unarchiveThreadTask) Create() (any, string) { return t, "unarchive_thread" }

// How long a move waits for Messenger to say the conversation is in its new folder, before its acknowledgement is taken
// instead (PROVIDERS.md). A statement arriving after this is still applied, as any other folder change is.
var folderEchoWait = 3 * time.Second

// An encrypted conversation's mailbox tasks go to its own sync group, as marking read and deleting do.
func chatFolderGroup(encrypted bool) int64 {
	if encrypted {
		return 95
	}
	return 1
}

func chatFolderTask(key int64, archived bool, encrypted bool) socket.Task {
	group := chatFolderGroup(encrypted)
	if archived {
		return &socket.DeleteThreadTask{ThreadKey: key, RemoveType: archiveRemoveType, SyncGroup: group}
	}
	return &unarchiveThreadTask{ThreadID: strconv.FormatInt(key, 10), SyncGroup: group}
}

// The request as a log line reads it: its label and, for the remove task, its type. Nothing about the conversation.
func folderTaskWords(task socket.Task) string {
	if remove, ok := task.(*socket.DeleteThreadTask); ok {
		return "task=" + task.GetLabel() + " removeType=" + strconv.FormatInt(remove.RemoveType, 10)
	}
	return "task=" + task.GetLabel()
}

func folderWord(archived bool) string {
	if archived {
		return "archive"
	}
	return "inbox"
}

func (a *adapter) updateChat(ctx context.Context, r request) (any, string) {
	var p struct {
		ChatID   string `json:"chatId"`
		Archived *bool  `json:"archived"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.Archived == nil {
		return nil, "invalid_input"
	}
	key, err := strconv.ParseInt(p.ChatID, 10, 64)
	if err != nil {
		return nil, "invalid_input"
	}
	if _, known := a.chats.chatByKey(key); !known {
		return nil, "chat_unavailable"
	}
	// The offline tests stand in for the socket, as the group changes' do.
	if a.client == nil && a.executeTasks == nil {
		return nil, "login_required"
	}
	if a.executeTasks == nil && !a.readyToSend(ctx) {
		return nil, "not_connected"
	}
	encrypted, _ := a.chats.encryptedThread(key)
	task := chatFolderTask(key, *p.Archived, encrypted)
	to := folderWord(*p.Archived)
	// Watched from before the request leaves: Messenger's statement can arrive on the socket ahead of the reply.
	confirmed, stopWatching := a.watchFolderMove(key, *p.Archived)
	defer stopWatching()
	started := time.Now()
	logf("folder move chat=%s to=%s %s group=%d sent", tag(key), to, folderTaskWords(task), chatFolderGroup(encrypted))
	tbl, err := a.request(ctx, func() []socket.Task { return []socket.Task{task} })
	if err != nil {
		logf("folder move chat=%s to=%s outcome=failed after=%dms", tag(key), to, time.Since(started).Milliseconds())
		return nil, "update_failed"
	}
	if tbl != nil && (len(tbl.LSHandleFailedTask) > 0 || len(tbl.LSIssueNewError) > 0) {
		logf("folder move chat=%s to=%s outcome=refused after=%dms", tag(key), to, time.Since(started).Milliseconds())
		return nil, "update_rejected"
	}
	if tbl != nil {
		logRows("folder move reply", tbl, a.applyTable(ctx, tbl))
	}
	// A move Messenger acknowledged is applied as its own statement would be. It archives and never says so to the
	// device that asked (2026-10-30), and an encrypted conversation's arrival states no folder either, so waiting for a
	// statement left a conversation with a new message under Archived until the next full read (2026-11-08). A later
	// statement from Messenger still replaces this. A move given up on (ctx) is unconfirmed, and never sent again.
	if !waitForFolderMove(ctx, confirmed, folderEchoWait) {
		if ctx.Err() != nil {
			logf("folder move chat=%s to=%s outcome=unconfirmed after=%dms", tag(key), to, time.Since(started).Milliseconds())
			return nil, "update_unconfirmed"
		}
		if *p.Archived {
			a.applyTable(ctx, &table.LSTable{LSMoveThreadToArchivedFolder: []*table.LSMoveThreadToArchivedFolder{{ThreadKey: key}}})
		} else {
			a.applyTable(ctx, &table.LSTable{LSMoveThreadToInboxAndUpdateParent: []*table.LSMoveThreadToInboxAndUpdateParent{{ThreadKey: key}}})
		}
		logf("folder move chat=%s to=%s outcome=acknowledged after=%dms", tag(key), to, time.Since(started).Milliseconds())
	} else {
		logf("folder move chat=%s to=%s outcome=confirmed after=%dms", tag(key), to, time.Since(started).Milliseconds())
	}
	found, ok := a.chats.chatByKey(key)
	if !ok {
		return nil, "chat_unavailable"
	}
	return found, ""
}

type folderWatch struct {
	archived bool
	done     chan struct{}
}

type folderWatches struct {
	mu      sync.Mutex
	pending map[int64]folderWatch
}

func (a *adapter) watchFolderMove(key int64, archived bool) (<-chan struct{}, func()) {
	a.folders.mu.Lock()
	defer a.folders.mu.Unlock()
	if a.folders.pending == nil {
		a.folders.pending = map[int64]folderWatch{}
	}
	watch := folderWatch{archived: archived, done: make(chan struct{})}
	a.folders.pending[key] = watch
	return watch.done, func() {
		a.folders.mu.Lock()
		if current, ok := a.folders.pending[key]; ok && current.done == watch.done {
			delete(a.folders.pending, key)
		}
		a.folders.mu.Unlock()
	}
}

// Called by applyTable once a table's rows are in the store: a move is confirmed when Messenger stated the folder of
// that conversation and the store now shows the folder that was asked for. A statement of the old folder (a row sent
// before Messenger acted) leaves the move waiting.
func (a *adapter) noteFolderMoves(tbl *table.LSTable) {
	if tbl == nil {
		return
	}
	a.folders.mu.Lock()
	defer a.folders.mu.Unlock()
	if len(a.folders.pending) == 0 {
		return
	}
	for key := range folderStatements(tbl) {
		watch, ok := a.folders.pending[key]
		if !ok {
			continue
		}
		if archived, known := a.chats.archivedNow(key); known && archived == watch.archived {
			close(watch.done)
			delete(a.folders.pending, key)
		}
	}
}

func waitForFolderMove(ctx context.Context, confirmed <-chan struct{}, timeout time.Duration) bool {
	timer := time.NewTimer(timeout)
	defer timer.Stop()
	select {
	case <-confirmed:
		return true
	case <-ctx.Done():
		return false
	case <-timer.C:
		return false
	}
}

// The conversations whose folder a table states: the two folder moves, a whole conversation row, and a partial row that
// names a folder.
func folderStatements(tbl *table.LSTable) map[int64]bool {
	stated := map[int64]bool{}
	if tbl == nil {
		return stated
	}
	for _, row := range tbl.LSMoveThreadToArchivedFolder {
		stated[row.ThreadKey] = true
	}
	for _, row := range tbl.LSMoveThreadToInboxAndUpdateParent {
		stated[row.ThreadKey] = true
	}
	for _, row := range tbl.LSDeleteThenInsertThread {
		stated[row.ThreadKey] = true
	}
	for _, row := range tbl.LSUpdateOrInsertThread {
		if rowFolder(row.FolderName, row.ParentThreadKey) != "" {
			stated[row.ThreadKey] = true
		}
	}
	delete(stated, 0)
	return stated
}

// The folder names that mean archived. "archive" is this helper's own word, which saved identities written before
// 2026-10-17 hold; "archived" and "e2ee_cutover_archived" are Messenger's (the pinned connector's events.go).
func archivedFolder(folder string) bool {
	switch strings.ToLower(folder) {
	case "archive", "archived", "e2ee_cutover_archived":
		return true
	}
	return false
}

// Messenger's web client files an archived conversation under this parent with its folder name cleared (its
// moveThreadToArchivedFolder procedure), and Texts' Meta client read a conversation as archived by this parent alone.
const archiveParent int64 = -10

// The folder a conversation row states. An empty name under the archive's parent is the archive; any other empty name
// states nothing.
func rowFolder(name string, parent int64) string {
	if name == "" && parent == archiveParent {
		return "archived"
	}
	return name
}

func applyFolderRows(s *store, tbl *table.LSTable, touch func(int64)) {
	for _, row := range tbl.LSMoveThreadToArchivedFolder {
		if row.ThreadKey != 0 {
			touch(row.ThreadKey)
			s.get(row.ThreadKey).folder = "archived"
		}
	}
	for _, row := range tbl.LSMoveThreadToInboxAndUpdateParent {
		if row.ThreadKey != 0 {
			touch(row.ThreadKey)
			s.get(row.ThreadKey).folder = "inbox"
		}
	}
}

// Logged per table: how many rows stated a folder, and in which form, so a live move shows how Messenger answered it.
func folderRowCounts(tbl *table.LSTable) (toArchive, toInbox, byName, byParent int) {
	if tbl == nil {
		return
	}
	toArchive, toInbox = len(tbl.LSMoveThreadToArchivedFolder), len(tbl.LSMoveThreadToInboxAndUpdateParent)
	count := func(name string, parent int64) {
		switch {
		case name != "" && archivedFolder(name):
			byName++
		case name == "" && parent == archiveParent:
			byParent++
		}
	}
	for _, row := range tbl.LSDeleteThenInsertThread {
		count(row.FolderName, row.ParentThreadKey)
	}
	for _, row := range tbl.LSUpdateOrInsertThread {
		count(row.FolderName, row.ParentThreadKey)
	}
	return
}
