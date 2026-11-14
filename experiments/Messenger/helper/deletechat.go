package main

import (
	"context"
	"encoding/json"
	"strconv"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// Deleting a whole conversation starts its history fresh. Hidden identity remains so later encrypted messages can
// recover the conversation even when Facebook does not repeat its ordinary thread record.

func (a *adapter) deleteChat(ctx context.Context, r request) (any, string) {
	var p struct {
		ChatID string `json:"chatId"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	key, err := strconv.ParseInt(p.ChatID, 10, 64)
	if err != nil {
		return nil, "invalid_input"
	}
	if _, known := a.chats.chatByKey(key); !known {
		return nil, "chat_unavailable"
	}
	if a.client == nil {
		return nil, "login_required"
	}
	if !a.readyToSend(ctx) {
		return nil, "not_connected"
	}
	confirmed, stopWatching := a.watchThreadDeletion(key)
	defer stopWatching()
	// An encrypted conversation is deleted through its ordinary-side thread in a different sync group, as upstream does.
	group := int64(1)
	if encrypted, _ := a.chats.encryptedThread(key); encrypted {
		group = 95
	}
	tbl, err := a.request(ctx, func() []socket.Task {
		return []socket.Task{&socket.DeleteThreadTask{ThreadKey: key, RemoveType: 0, SyncGroup: group}}
	})
	if err != nil {
		logf("delete chat=%s failed", tag(key))
		return nil, "delete_failed"
	}
	if tbl == nil || len(tbl.LSHandleFailedTask) > 0 || len(tbl.LSIssueNewError) > 0 {
		logf("delete chat=%s refused", tag(key))
		return nil, "delete_rejected"
	}
	touched := a.applyTable(ctx, tbl)
	logRows("delete reply", tbl, touched)
	if !a.confirmedThreadDeletion(tbl, key) && !waitForThreadDeletion(ctx, confirmed, 2*time.Second) {
		// An empty task acknowledgement only proves that Meta received the request. Treating it as deletion used to
		// wipe the local copy even when the phone still retained the chat. The matching socket event is authoritative,
		// but may arrive just before or shortly after this otherwise-empty reply.
		logf("delete chat=%s unconfirmed group=%d", tag(key), group)
		return nil, "delete_unconfirmed"
	}
	logf("delete chat=%s accepted group=%d", tag(key), group)
	return map[string]any{"outcome": "accepted"}, ""
}

func (a *adapter) watchThreadDeletion(key int64) (<-chan struct{}, func()) {
	a.deleteMu.Lock()
	confirmed := make(chan struct{})
	a.deleteWatches[key] = confirmed
	a.deleteMu.Unlock()
	return confirmed, func() {
		a.deleteMu.Lock()
		if a.deleteWatches[key] == confirmed {
			delete(a.deleteWatches, key)
		}
		a.deleteMu.Unlock()
	}
}

func (a *adapter) noteThreadDeletion(key int64) {
	a.deleteMu.Lock()
	if confirmed := a.deleteWatches[key]; confirmed != nil {
		close(confirmed)
		delete(a.deleteWatches, key)
	}
	a.deleteMu.Unlock()
}

func waitForThreadDeletion(ctx context.Context, confirmed <-chan struct{}, timeout time.Duration) bool {
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

func confirmedThreadDeletion(tbl *table.LSTable, key int64) bool {
	return confirmedThreadDeletionMapped(tbl, key, func(raw int64) int64 { return raw })
}

func (a *adapter) confirmedThreadDeletion(tbl *table.LSTable, key int64) bool {
	return confirmedThreadDeletionMapped(tbl, key, func(raw int64) int64 {
		if mapped, ok := a.chats.threadForJID(raw); ok {
			return mapped
		}
		return raw
	})
}

func confirmedThreadDeletionMapped(tbl *table.LSTable, key int64, canonical func(int64) int64) bool {
	if tbl == nil {
		return false
	}
	active := map[int64]bool{}
	for raw := range activeThreadRows(tbl) {
		active[canonical(raw)] = true
	}
	if active[key] {
		return false
	}
	for _, deleted := range deletedThreadKeys(tbl) {
		if canonical(deleted) == key {
			return true
		}
	}
	return false
}

// Messenger uses deletePartialThread when a person deletes a chat for themselves. Older clients and some sync paths
// still emit deleteThread, while pending requests have their own equally authoritative deletion row.
func deletedThreadKeys(tbl *table.LSTable) []int64 {
	if tbl == nil {
		return nil
	}
	keys := make([]int64, 0, len(tbl.LSDeleteThread)+len(tbl.LSDeletePartialThread)+len(tbl.LSDeleteMessageRequest))
	for _, row := range tbl.LSDeleteThread {
		keys = append(keys, row.ThreadKey)
	}
	for _, row := range tbl.LSDeletePartialThread {
		keys = append(keys, row.ThreadKey)
	}
	for _, row := range tbl.LSDeleteMessageRequest {
		keys = append(keys, row.ThreadKey)
	}
	return keys
}

// A delete followed by a current thread row in the same sync is a replacement, not removal. This mirrors the pinned
// connector's event projection and prevents a mixed sync batch from hiding a conversation that still exists.
func activeThreadRows(tbl *table.LSTable) map[int64]bool {
	active := map[int64]bool{}
	if tbl == nil {
		return active
	}
	for _, row := range tbl.LSVerifyThreadExists {
		active[row.ThreadKey] = true
	}
	for _, row := range tbl.LSUpdateOrInsertThread {
		active[row.ThreadKey] = true
	}
	for _, row := range tbl.LSDeleteThenInsertThread {
		active[row.ThreadKey] = true
	}
	for _, row := range tbl.LSInsertNewMessageRange {
		active[row.ThreadKey] = true
	}
	return active
}

// Hide the conversation, remove its history from memory/disk, retain identity, and tell the app.
func (a *adapter) forgetChat(ctx context.Context, key int64) {
	a.state.Lock()
	defer a.state.Unlock()
	a.forgetChatLocked(ctx, key)
}

func (a *adapter) forgetChatLocked(ctx context.Context, key int64) {
	messages := a.chats.forgetThread(key)
	a.mu.Lock()
	kept := a.kept
	a.mu.Unlock()
	kept.saveIdentity(ctx, a.chats)
	rows := kept.forgetThread(ctx, key)
	a.mu.Lock()
	delete(a.asked, key)
	delete(a.refreshing, key)
	a.mu.Unlock()
	logf("chat removed chat=%s messages=%d stored=%d", tag(key), messages, rows)
	if a.out != nil {
		a.out.write(response{Event: "changed", Result: &changes{ChatsDeleted: []string{strconv.FormatInt(key, 10)}}})
	}
}

// Call without the lock. Returns how many messages were dropped.
func (s *store) forgetThread(key int64) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	if current := s.threads[key]; current != nil && current.deleted {
		return 0
	}
	thread := s.messages[key]
	for id := range thread {
		for _, item := range s.attached[id] {
			delete(s.media, item.id)
			delete(s.files, item.id)
		}
		delete(s.attached, id)
		delete(s.reactions, id)
	}
	delete(s.messages, key)
	current := s.get(key)
	current.deletedAt = max(current.deletedAt, current.lastActivity, time.Now().UnixMilli())
	current.deleted = true
	current.snippet, current.lastActivity, current.readAt = "", 0, 0
	current.previewOutgoing = false
	current.folder = ""
	current.anchorAt, current.anchorID = 0, ""
	current.readBy = map[int64]int64{}
	s.forgetThreadReceipts(key)
	delete(s.fromStore, key)
	delete(s.pages, key)
	delete(s.rev, key)
	for pair := range s.shares {
		if pair.thread == key {
			delete(s.shares, pair)
		}
	}
	for otid, id := range s.sent {
		if thread[id] != nil {
			delete(s.sent, otid)
		}
	}
	return len(thread)
}

// Everything written down for the conversation. Returns the number of stored messages removed.
func (m *messageStore) forgetThread(ctx context.Context, key int64) int64 {
	if m == nil {
		return 0
	}
	// What hangs off the messages first, found through them, then the messages themselves.
	for _, statement := range []string{
		`DELETE FROM encrypted_share WHERE message IN (SELECT id FROM encrypted_message WHERE thread = ?)`,
		`DELETE FROM encrypted_media WHERE thread = ?`,
		`DELETE FROM message_attachment WHERE thread = ?`,
		`DELETE FROM encrypted_reaction WHERE thread = ?`,
		`DELETE FROM deleted_message WHERE thread = ?`,
		forgetThreadWatermarks,
		forgetThreadWaiting,
	} {
		_, _ = m.db.ExecContext(ctx, statement, key)
	}
	removed := int64(0)
	if result, err := m.db.ExecContext(ctx, `DELETE FROM encrypted_message WHERE thread = ?`, key); err == nil {
		removed, _ = result.RowsAffected()
	} else {
		logf("a deleted conversation could not be removed from the store")
	}
	_, _ = m.db.ExecContext(ctx, `DELETE FROM archived_v3 WHERE thread = ?`, key)
	return removed
}
