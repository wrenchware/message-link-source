package main

import (
	"context"
	"encoding/json"
	"slices"
	"sort"
	"strconv"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	waTypes "go.mau.fi/whatsmeow/types"
)

// Telling the other people in a conversation that this account has read it. The app calls this when the person is
// actually looking at the newest messages, never for history walked in the background.
//
// The two connections say it differently, as they do for receiving it: the ordinary one takes a watermark for the
// whole conversation, the encrypted one wants the ids of the messages read, grouped by who sent them.

// How many message ids go in each sender's encrypted receipt. Keep the newest per sender, so an older group speaker
// still receives a receipt when someone else has sent many newer messages.
const readBatch = 50

func (a *adapter) markRead(ctx context.Context, r request) (any, string) {
	var p struct {
		ChatID    string `json:"chatId"`
		MessageID string `json:"messageId"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	key, err := strconv.ParseInt(p.ChatID, 10, 64)
	if err != nil || p.MessageID == "" {
		return nil, "invalid_input"
	}
	target, known := a.chats.messageByID(key, p.MessageID)
	if !known {
		return nil, "message_unavailable"
	}
	watermark := target.At.UnixMilli()
	encrypted, _ := a.chats.encryptedThread(key)
	// Facebook keeps a conversation unread while the watermark it holds is short of the thread's own activity time, and
	// that time can be a few milliseconds after the newest message's. Lifting only our own copy showed one read here
	// while Messenger on the web, and this helper after the nightly restart, kept it unread (2026-10-22).
	through, activity := a.chats.readToTell(key, watermark, time.Now().UnixMilli())
	if through != watermark {
		logf("mark read chat=%s watermark lifted ms=%d activity_ms=%d", tag(key), through-watermark, activity)
	}
	return a.acknowledgeRead(key, through, encrypted,
		func() (any, string) { return a.markReadEncrypted(ctx, key, watermark) },
		func() string { return a.writeThreadRead(ctx, key, through) })
}

// Encrypted messages need their named receipts and the Lightspeed thread watermark used by Facebook's chat UI.
// A successful socket write alone must not suppress the thread update or clear local unread state.
func (a *adapter) acknowledgeRead(key, watermark int64, encrypted bool, writeEncrypted func() (any, string), writeThread func() string) (any, string) {
	var result any = map[string]any{"outcome": "accepted"}
	if encrypted {
		var code string
		result, code = writeEncrypted()
		// With the encrypted connection down the named receipts cannot go, but the thread watermark travels on the
		// ordinary connection and is what clears the conversation on Facebook's web and phone. Nothing at all was sent
		// before, and the conversation stayed unread there for as long as the encrypted connection was away.
		if code == "e2ee_off" || code == "e2ee_offline" {
			logf("mark read chat=%s encrypted offline thread only", tag(key))
			result, code = map[string]any{"outcome": "accepted", "receiptsSent": 0}, ""
		}
		if code != "" {
			return nil, code
		}
	}
	if code := writeThread(); code != "" {
		return nil, code
	}
	a.readDone(key, watermark)
	return result, ""
}

func (a *adapter) writeThreadRead(ctx context.Context, key, watermark int64) string {
	if a.client == nil {
		return "login_required"
	}
	if !a.readyToSend(ctx) {
		return "not_connected"
	}
	task := a.chats.threadReadTask(key, watermark)
	tbl, err := a.request(ctx, func() []socket.Task {
		return []socket.Task{task}
	})
	if err != nil || tbl == nil || len(tbl.LSHandleFailedTask) > 0 {
		logf("mark read chat=%s failed", tag(key))
		return "mark_read_failed"
	}
	logf("mark read chat=%s thread watermark accepted group=%d", tag(key), task.SyncGroup)
	return ""
}

func (s *store) threadReadTask(key, watermark int64) *socket.ThreadMarkReadTask {
	group := int64(1)
	// Hybrid encrypted threads live in the same sync group used by DeleteThreadTask.
	if encrypted, _ := s.encryptedThread(key); encrypted {
		group = 95
	}
	return &socket.ThreadMarkReadTask{ThreadId: key, SyncGroup: group, LastReadWatermarkTs: watermark}
}

func (a *adapter) markReadEncrypted(ctx context.Context, key, watermark int64) (any, string) {
	a.mu.Lock()
	crypto := a.crypto
	a.mu.Unlock()
	if crypto == nil {
		return nil, "e2ee_off"
	}
	if !a.encryptedReady() {
		return nil, "e2ee_offline"
	}
	to, ok := a.encryptedAddress(key)
	if !ok {
		return nil, "mark_read_failed"
	}
	return a.sendEncryptedRead(ctx, key, watermark, to, func(ctx context.Context, ids []waTypes.MessageID, at time.Time, chat, sender waTypes.JID) error {
		return crypto.client.MarkRead(ctx, ids, at, chat, sender)
	})
}

// Offline tests capture exactly the payload passed to the pinned library, without opening a network connection.
func (a *adapter) sendEncryptedRead(ctx context.Context, key, watermark int64, to waTypes.JID,
	send func(context.Context, []waTypes.MessageID, time.Time, waTypes.JID, waTypes.JID) error) (any, string) {
	unread := a.chats.unreadUpTo(key, watermark)
	if len(unread) == 0 {
		// There may still be a thread watermark to write, even with no eligible encrypted message IDs.
		logf("mark read chat=%s encrypted nothing unread", tag(key))
		return map[string]any{"outcome": "accepted", "receiptsSent": 0}, ""
	}
	// A group needs to know whose messages are being acknowledged; a conversation between two people names nobody.
	bySender := map[waTypes.JID][]waTypes.MessageID{}
	for _, item := range unread {
		var sender waTypes.JID
		if to.Server == waTypes.GroupServer {
			sender = waTypes.JID{User: strconv.FormatInt(item.sender, 10), Server: waTypes.MessengerServer}
		}
		bySender[sender] = append(bySender[sender], waTypes.MessageID(item.id))
	}
	// The library's timestamp is when the reading happened, not the timestamp of the message being acknowledged.
	// Keep the message watermark separately for our local unread state.
	at := time.Now()
	logf("mark read chat=%s boundary ageSeconds=%d batchSpanSeconds=%d",
		tag(key), (at.UnixMilli()-watermark)/1000, (unread[len(unread)-1].at-unread[0].at)/1000)
	failed := 0
	for sender, ids := range bySender {
		// MarkRead puts ids[0] in the primary receipt/ack ID and the remainder
		// in a secondary list. Advance that primary ID with the visible boundary;
		// restored history must not keep every receipt anchored to the oldest ID.
		slices.Reverse(ids)
		if err := send(ctx, ids, at, to, sender); err != nil {
			failed++
			logf("mark read chat=%s encrypted part failed: %s", tag(key), scrub(err.Error()))
		}
	}
	if failed > 0 {
		logf("mark read chat=%s encrypted incomplete failed=%d senders=%d", tag(key), failed, len(bySender))
		return nil, "mark_read_failed"
	}
	logf("mark read chat=%s encrypted written messages=%d senders=%d", tag(key), len(unread), len(bySender))
	return map[string]any{"outcome": "accepted", "receiptsSent": len(unread)}, ""
}

type unreadMessage struct {
	id     string
	sender int64
	at     int64
}

// Other people's messages at the visible read boundary, newest last and capped per sender. The thread's readAt is
// local/list state: a reply or a synchronized thread row advances it without writing encrypted read receipts.
// Explicit viewing must still acknowledge these IDs. The view deduplicates successful boundaries while open;
// reopening after restart can repeat the bounded receipts, but background history never calls this path.
func (s *store) unreadUpTo(key, watermark int64) []unreadMessage {
	s.mu.Lock()
	defer s.mu.Unlock()
	thread, ok := s.threads[key]
	if !ok {
		return nil
	}
	var found []unreadMessage
	for id, current := range s.messages[key] {
		if !current.encrypted || current.unsent || current.sender == s.self || current.sender == 0 {
			continue
		}
		if current.timestamp > watermark || current.timestamp <= thread.deletedAt {
			continue
		}
		found = append(found, unreadMessage{id: id, sender: current.sender, at: current.timestamp})
	}
	sort.Slice(found, func(i, j int) bool {
		if found[i].at != found[j].at {
			return found[i].at < found[j].at
		}
		return found[i].id < found[j].id
	})
	if len(found) > readBatch {
		counts := map[int64]int{}
		kept := make([]unreadMessage, 0, len(found))
		for i := len(found) - 1; i >= 0; i-- {
			if counts[found[i].sender] >= readBatch {
				continue
			}
			counts[found[i].sender]++
			kept = append(kept, found[i])
		}
		slices.Reverse(kept)
		found = kept
	}
	return found
}

// The conversation is read up to here, and the list is told so it drops the unread mark.
func (a *adapter) readDone(key, watermark int64) {
	if a.chats.markReadUpTo(key, watermark) && a.out != nil {
		a.out.write(response{Event: "changed", Result: &changes{Chats: []string{strconv.FormatInt(key, 10)}}})
	}
}

// Reports whether the unread state changed. When nothing is newer than the watermark, the whole conversation is read:
// its activity time comes from Facebook's thread row, which can be a few milliseconds after the newest message's own
// timestamp, and a watermark short of it by that much left the conversation marked unread forever.
func (s *store) markReadUpTo(key, watermark int64) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.threads[key]
	if !ok {
		return false
	}
	watermark = s.readThroughLocked(key, watermark)
	if watermark <= current.readAt {
		return false
	}
	current.readAt = watermark
	return true
}

// How far reading through watermark reads: the thread's activity time when no message is newer than the watermark.
func (s *store) readThrough(key, watermark int64) int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.readThroughLocked(key, watermark)
}

func (s *store) readThroughLocked(key, watermark int64) int64 {
	current, ok := s.threads[key]
	if !ok || s.newerMessageLocked(key, watermark) {
		return watermark
	}
	return max(watermark, current.lastActivity)
}

func (s *store) newerMessageLocked(key, watermark int64) bool {
	for _, message := range s.messages[key] {
		// A hidden Marketplace receipt can never be read up to, so it must not hold the read short of the conversation's
		// newest moment (2026-11-12). Anything else not drawn here (an encrypted poll this build cannot read) still does:
		// reading here must not tell Facebook it was seen.
		if !message.unsent && message.timestamp > watermark && !(message.receipt && message.text == "") {
			return true
		}
	}
	return false
}

// How far past the newest message a read is told to Facebook, in milliseconds. An encrypted message brings its own time
// and not the thread's, which only a thread row says, and usually not before the next connect: the read went out short
// of it, was accepted, and came back unread after the nightly restart (2026-11-01; the gaps seen ran 291-809 ms).
const readMargin = 2000

// What to tell Facebook for a read through watermark, never past now, and how far the activity time this copy knows
// is past the watermark. Our own copy takes the same value, so a thread row bringing the real time finds it read.
func (s *store) readToTell(key, watermark, now int64) (through, activity int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.threads[key]
	if !ok || s.newerMessageLocked(key, watermark) {
		return watermark, 0
	}
	return max(watermark, current.lastActivity, min(watermark+readMargin, now)), max(current.lastActivity-watermark, 0)
}

// Replying in a conversation reads it, as Messenger's own apps do. The ordinary connection sends that with the
// message; an encrypted send has no room for it, so Facebook kept every conversation answered from here unread on the
// web while this helper showed it read (2026-10-22). Told after the send, off its path: the message has gone either
// way, and a lost read marker is only a dot on another device.
func (a *adapter) readAfterSend(key, at int64) {
	go func() {
		ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
		defer cancel()
		a.writeThreadRead(ctx, key, a.chats.readThrough(key, at))
	}()
}
