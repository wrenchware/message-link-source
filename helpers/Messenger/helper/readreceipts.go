package main

import (
	"context"
	"slices"
	"sort"
	"strconv"

	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
)

// Who has read how far. Messenger draws a small face beside the newest message each person has seen, which needs one
// number per person rather than a single "read" flag for the conversation.
//
// The two connections say it differently. The ordinary one sends a watermark — everything up to this moment has been
// seen. The encrypted one names the messages. Both become the same watermark here, so the conversation does not have
// to care which connection a message arrived on.

// Call with the lock held. Returns the messages whose set of readers changed, so only those rows are redrawn.
func (s *store) advanceRead(key, contact, watermark int64) []string {
	if key == 0 || contact == 0 || watermark <= 0 || contact == s.self {
		return nil
	}
	current := s.get(key)
	if current.readBy == nil {
		current.readBy = map[int64]int64{}
	}
	previous := current.readBy[contact]
	// A watermark only ever moves forward. Facebook resends older ones freely, and treating one as news would redraw
	// the conversation every time it did.
	if watermark <= previous {
		return nil
	}
	// Moving a face changes its old row too, even though SeenBy stays true there.
	var oldMark string
	if old := s.latestReadMessage(key, previous); old != nil {
		oldMark = old.id
	}
	current.readBy[contact] = watermark
	s.logReadBoundary(key, contact, watermark)
	var moved []string
	for id, message := range s.messages[key] {
		// The messages this person had not read before and has now: the ones between the two watermarks. A person's
		// own message is not something they read.
		if message.sender != contact && message.timestamp > previous && message.timestamp <= watermark {
			moved = append(moved, id)
		}
	}
	if oldMark != "" && !slices.Contains(moved, oldMark) {
		moved = append(moved, oldMark)
	}
	if latest := s.latestReadMessage(key, watermark); latest != nil && !slices.Contains(moved, latest.id) {
		moved = append(moved, latest.id)
	}
	return moved
}

// Counts, hashes and relative offsets only. A locally assigned send timestamp
// just after Facebook's watermark can leave the face on the preceding message.
// Call with the store lock held; also used by the read-only history probe.
func (s *store) logReadBoundary(key, contact, watermark int64) {
	var next *message
	for _, candidate := range s.messages[key] {
		if !candidate.unsent && candidate.timestamp > watermark && (next == nil || candidate.timestamp < next.timestamp) {
			next = candidate
		}
	}
	if next != nil {
		logf("read boundary chat=%s person=%s next=%s nextOwn=%v gapMs=%d",
			tag(key), tag(contact), tagOf(next.id), next.sender == s.self, next.timestamp-watermark)
	}
}

// A face marks a position in the conversation, including the reader's own messages.
// This is separate from SeenBy: sending a message does not count as a recipient reading it.
// Call with the lock held.
func (s *store) readMarkOf(key int64, current *message) []string {
	thread, ok := s.threads[key]
	if !ok || len(thread.readBy) == 0 || current.unsent {
		return nil
	}
	var marks []string
	for contact, watermark := range thread.readBy {
		if contact == s.self || watermark < current.timestamp {
			continue
		}
		if latest := s.latestReadMessage(key, watermark); latest != nil && latest.id == current.id {
			marks = append(marks, strconv.FormatInt(contact, 10))
		}
	}
	sort.Strings(marks)
	return marks
}

// Every face in a conversation at once, by the message it sits on: what readMarkOf answers for each message, for one
// scan of the conversation per reader instead of one per reader for every message drawn. Call with the lock held.
func (s *store) readMarksLocked(key int64) map[string][]string {
	thread, ok := s.threads[key]
	if !ok || len(thread.readBy) == 0 {
		return nil
	}
	marks := map[string][]string{}
	for contact, watermark := range thread.readBy {
		if contact == s.self {
			continue
		}
		if latest := s.latestReadMessage(key, watermark); latest != nil {
			marks[latest.id] = append(marks[latest.id], strconv.FormatInt(contact, 10))
		}
	}
	for _, readers := range marks {
		sort.Strings(readers)
	}
	return marks
}

// A deterministic target shared by marker projection and incremental invalidation.
func (s *store) latestReadMessage(key, watermark int64) *message {
	var latest *message
	for _, candidate := range s.messages[key] {
		if candidate.unsent || candidate.timestamp > watermark {
			continue
		}
		if latest == nil || candidate.timestamp > latest.timestamp ||
			(candidate.timestamp == latest.timestamp && candidate.id > latest.id) {
			latest = candidate
		}
	}
	return latest
}

// The message each reader's face is on, for readers whose watermark is at or after a moment: a message put in at that
// moment, older than their watermark, can take their face over. Taken before the message goes in, and compared after
// by facesMoved, so the row the face leaves is redrawn too.
func (s *store) facesFrom(key, at int64) map[int64]string {
	s.mu.Lock()
	defer s.mu.Unlock()
	thread, ok := s.threads[key]
	if !ok {
		return nil
	}
	var faces map[int64]string
	for contact, watermark := range thread.readBy {
		if contact == s.self || watermark < at {
			continue
		}
		if latest := s.latestReadMessage(key, watermark); latest != nil {
			if faces == nil {
				faces = map[int64]string{}
			}
			faces[contact] = latest.id
		}
	}
	return faces
}

// The messages that no longer carry a face they carried in faces (facesFrom).
func (s *store) facesMoved(key int64, faces map[int64]string) []string {
	if len(faces) == 0 {
		return nil
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	var moved []string
	thread := s.threads[key]
	for contact, id := range faces {
		var latest *message
		if thread != nil {
			latest = s.latestReadMessage(key, thread.readBy[contact])
		}
		if (latest == nil || latest.id != id) && !slices.Contains(moved, id) {
			moved = append(moved, id)
		}
	}
	sort.Strings(moved)
	return moved
}

// Everyone whose reading has reached this message, which is a different question: it is what says "Read" in words on
// a message this account sent, and stays true for every message behind the watermark.
// Call with the lock held.
func (s *store) readersOf(key int64, current *message) []string {
	thread, ok := s.threads[key]
	if !ok || len(thread.readBy) == 0 {
		return nil
	}
	var readers []string
	for contact, watermark := range thread.readBy {
		if contact == current.sender || contact == s.self || watermark < current.timestamp {
			continue
		}
		readers = append(readers, strconv.FormatInt(contact, 10))
	}
	return readers
}

// A read or delivery receipt from the encrypted connection. It names messages rather than a moment, so the moment is
// taken from the newest message it names that this helper holds; a named message not loaded yet keeps the receipt
// waiting until it arrives (receiptbook.go).
func (a *adapter) encryptedRead(evt *events.Receipt) {
	if evt == nil {
		return
	}
	// Which kinds of receipt Facebook actually sends over this connection is not documented anywhere we can check,
	// and every kind but reading and delivery is dropped here — so the kind is recorded even when nothing is done with it.
	var kind receiptKind
	switch evt.Type {
	case types.ReceiptTypeRead, types.ReceiptTypeReadSelf:
		kind = receiptRead
	case types.ReceiptTypeDelivered:
		kind = receiptDelivered
	default:
		logf("e2ee receipt ignored kind=%q messages=%d", string(evt.Type), len(evt.MessageIDs))
		return
	}
	jid, err := strconv.ParseInt(evt.Chat.User, 10, 64)
	if err != nil {
		logf("e2ee receipt chat address not understood")
		return
	}
	// The same pairing the messages themselves go through: an encrypted conversation is numbered differently on the
	// ordinary side, and a receipt filed under the wrong number marks nothing.
	key, known := a.chats.threadForJID(jid)
	if !known {
		key = jid
	}
	contact, err := strconv.ParseInt(evt.Sender.User, 10, 64)
	if err != nil {
		return
	}
	at := int64(0)
	if !evt.Timestamp.IsZero() {
		at = evt.Timestamp.UnixMilli()
	}
	ids := make([]string, 0, len(evt.MessageIDs))
	for _, id := range evt.MessageIDs {
		ids = append(ids, string(id))
	}
	a.chats.mu.Lock()
	loaded := len(a.chats.messages[key])
	moved, matched, waiting, dropped := a.chats.applyEncryptedReceipt(key, contact, kind, ids, at)
	a.chats.mu.Unlock()
	name := "read"
	if kind == receiptDelivered {
		name = "delivered"
	}
	logf("e2ee %s chat=%s person=%s paired=%v named=%d matched=%d waiting=%d dropped=%d loaded=%d moved=%d",
		name, tag(key), tag(contact), known, len(evt.MessageIDs), matched, waiting, dropped, loaded, len(moved))
	a.keepReceipts(context.Background())
	if len(moved) == 0 {
		return
	}
	chat := strconv.FormatInt(key, 10)
	a.out.write(response{Event: "changed", Result: &changes{Receipts: map[string][]string{chat: moved}}})
}
