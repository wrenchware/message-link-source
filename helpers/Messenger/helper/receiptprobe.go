package main

import (
	"context"
	"encoding/json"
	"strconv"
	"strings"
	"time"

	waTypes "go.mau.fi/whatsmeow/types"
)

// Opt-in diagnostic, disabled in the app and the receive-only observer. Exactly
// one explicitly named encrypted DM, one fresh incoming message, one attempt.
// All other user actions and device registration remain blocked by observer mode.
type receiptProbe struct {
	key        int64
	name, mode string
	started    time.Time
	before     map[string]bool
	used       bool
}

func (s *store) newReceiptProbe(name, mode string, now time.Time) (*receiptProbe, string) {
	if strings.TrimSpace(name) == "" || (mode != "single" && mode != "device" && mode != "batch" && mode != "thread" && mode != "app") {
		return nil, "invalid_input"
	}
	var matches []viewChat
	for _, chat := range s.chats(100000) {
		if strings.EqualFold(chat.Title, name) {
			matches = append(matches, chat)
		}
	}
	if len(matches) != 1 || !matches[0].Encrypted || matches[0].Group {
		return nil, "test_chat_not_unique_encrypted_dm"
	}
	key, err := strconv.ParseInt(matches[0].ID, 10, 64)
	if err != nil {
		return nil, "invalid_input"
	}
	p := &receiptProbe{key: key, name: name, mode: mode, started: now, before: map[string]bool{}}
	s.mu.Lock()
	defer s.mu.Unlock()
	for id := range s.messages[key] {
		p.before[id] = true
	}
	return p, ""
}

func (s *store) freshReceiptMessage(p *receiptProbe) *unreadMessage {
	s.mu.Lock()
	defer s.mu.Unlock()
	thread := s.threads[p.key]
	if thread == nil {
		return nil
	}
	var newest *unreadMessage
	for id, m := range s.messages[p.key] {
		if p.before[id] || !m.encrypted || m.unsent || m.sender == 0 || m.sender == s.self ||
			m.timestamp <= thread.deletedAt || m.timestamp < p.started.Add(-2*time.Second).UnixMilli() {
			continue
		}
		if newest == nil || m.timestamp > newest.at || (m.timestamp == newest.at && id > newest.id) {
			newest = &unreadMessage{id: id, sender: m.sender, at: m.timestamp}
		}
	}
	return newest
}

func (a *adapter) probeReadReceipt(ctx context.Context, r request) (any, string) {
	var args struct{ Name, Mode string }
	if json.Unmarshal(r.Params, &args) != nil {
		return nil, "invalid_input"
	}
	if !a.encryptedReady() {
		return map[string]any{"state": "connecting"}, ""
	}
	if a.receiptProbe == nil {
		p, code := a.chats.newReceiptProbe(args.Name, args.Mode, time.Now())
		if code != "" {
			return nil, code
		}
		a.receiptProbe = p
		logf("receipt probe armed chat=%s mode=%s", tag(p.key), p.mode)
		return map[string]any{"state": "armed", "chat": tag(p.key)}, ""
	}
	p := a.receiptProbe
	if args.Name != p.name || args.Mode != p.mode || time.Since(p.started) > 3*time.Minute {
		return nil, "test_scope_changed_or_expired"
	}
	if p.used {
		return nil, "test_receipt_already_attempted"
	}
	fresh := a.chats.freshReceiptMessage(p)
	if fresh == nil {
		return map[string]any{"state": "waiting"}, ""
	}
	to, ok := a.encryptedAddress(p.key)
	if !ok || to.Server != waTypes.MessengerServer || to.User != strconv.FormatInt(fresh.sender, 10) {
		return nil, "test_sender_does_not_match_dm"
	}
	if p.mode == "device" {
		original, found := a.receiptOrigins.Load(fresh.id)
		device, valid := original.(waTypes.JID)
		if !found || !valid || device.User != to.User || device.Server != to.Server || device.Device == 0 {
			return nil, "test_original_device_unavailable"
		}
		to = device
	}
	// Set before writing: a timeout or uncertain write must never cause a retry.
	p.used = true
	a.mu.Lock()
	crypto := a.crypto
	a.mu.Unlock()
	if crypto == nil {
		return nil, "e2ee_off"
	}
	var code string
	switch p.mode {
	case "single", "device":
		if err := crypto.client.MarkRead(ctx, []waTypes.MessageID{fresh.id}, time.Now(), to, waTypes.EmptyJID); err != nil {
			code = "mark_read_failed"
		}
	case "batch":
		_, code = a.sendEncryptedRead(ctx, p.key, fresh.at, to, func(ctx context.Context, ids []waTypes.MessageID, at time.Time, chat, sender waTypes.JID) error {
			return crypto.client.MarkRead(ctx, ids, at, chat, sender)
		})
	case "thread":
		// Diagnostic only: compare Facebook's thread API using actual viewing time,
		// without the second precision of the encrypted message timestamp.
		code = a.writeThreadRead(ctx, p.key, time.Now().UnixMilli())
	case "app":
		params, _ := json.Marshal(map[string]string{"chatId": strconv.FormatInt(p.key, 10), "messageId": fresh.id})
		_, code = a.markRead(ctx, request{Params: params})
	}
	logf("receipt probe attempted chat=%s id=%s mode=%s failed=%v", tag(p.key), tagOf(fresh.id), p.mode, code != "")
	if code != "" {
		return nil, code
	}
	return map[string]any{"state": "written", "chat": tag(p.key), "message": tagOf(fresh.id)}, ""
}
