package main

import (
	"context"
	"encoding/json"
	"strconv"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	waTypes "go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
)

// Typing, in both directions and on both connections. Nothing about it is stored: a typing event that is missed is
// simply not shown, and the app expires anyone who stops being reported.

type typingEvent struct {
	ChatID string `json:"chatId"`
	Sender string `json:"sender"`
	Typing bool   `json:"typing"`
}

func (a *adapter) publishTyping(key, sender int64, typing bool) {
	if key == 0 || sender == 0 || sender == a.chats.viewer() || a.out == nil {
		return
	}
	a.out.write(response{Event: "typing", Result: typingEvent{strconv.FormatInt(key, 10), strconv.FormatInt(sender, 10), typing}})
}

// The ordinary connection reports typing as rows of their own.
func (a *adapter) ordinaryTyping(tbl *table.LSTable) {
	for _, row := range tbl.LSUpdateTypingIndicator {
		a.publishTyping(row.ThreadKey, row.SenderId, row.IsTyping)
	}
}

// The encrypted connection reports it as chat presence — and only to a client that has said it is available, which
// is why presence is sent once that connection is up.
func (a *adapter) encryptedTyping(evt *events.ChatPresence) {
	jid, err := strconv.ParseInt(evt.Chat.User, 10, 64)
	if err != nil {
		return
	}
	key, known := a.chats.threadForJID(jid)
	if !known {
		key = jid
	}
	sender, err := strconv.ParseInt(evt.Sender.User, 10, 64)
	if err != nil {
		return
	}
	a.publishTyping(key, sender, evt.State == waTypes.ChatPresenceComposing)
}

// This account typing, or stopping. Best effort throughout: typing is never worth waiting for, never retried, and
// skipped outright when another request holds the Facebook connection.
func (a *adapter) sendTyping(ctx context.Context, r request) (any, string) {
	var p struct {
		ChatID string `json:"chatId"`
		Typing bool   `json:"typing"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	key, err := strconv.ParseInt(p.ChatID, 10, 64)
	if err != nil {
		return nil, "invalid_input"
	}
	if encrypted, _ := a.chats.encryptedThread(key); encrypted {
		a.mu.Lock()
		crypto := a.crypto
		a.mu.Unlock()
		if crypto == nil || !a.encryptedReady() {
			return map[string]any{"sent": false}, ""
		}
		to, ok := a.encryptedAddress(key)
		if !ok {
			return map[string]any{"sent": false}, ""
		}
		state := waTypes.ChatPresencePaused
		if p.Typing {
			state = waTypes.ChatPresenceComposing
		}
		if err := crypto.client.SendChatPresence(ctx, to, state, waTypes.ChatPresenceMediaText); err != nil {
			return map[string]any{"sent": false}, ""
		}
		return map[string]any{"sent": true}, ""
	}
	if a.client == nil || ctx.Err() != nil {
		return map[string]any{"sent": false}, ""
	}
	group := int64(1)
	number, _ := a.chats.kindOf(key)
	kind := table.ThreadType(number)
	if kind.IsOneToOne() {
		group = 0
	}
	typing := int64(0)
	if p.Typing {
		typing = 1
	}
	err = a.client.ExecuteStatelessTask(ctx, &socket.UpdatePresenceTask{
		ThreadKey: key, IsGroupThread: group, IsTyping: typing, SyncGroup: 1, ThreadType: int64(kind),
	})
	return map[string]any{"sent": err == nil}, ""
}
