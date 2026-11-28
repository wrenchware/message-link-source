package main

import (
	"slices"
	"sort"
	"strconv"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// Everything about receipts beyond the read watermark each thread keeps: how far each person has received, when their
// reading last moved, and encrypted receipts that named a message this helper has not loaded yet.
//
// Delivery is a watermark like reading: Facebook reports, per person, the moment up to which everything has reached
// them. Reading implies it, so a message counts as reaching someone once either watermark covers it.
//
// A read time is only ever the moment Facebook gave for a reading this helper watched happen. The participant
// snapshot at sign-in says how far someone has read and when they last read anything, but not when they read any
// particular message, so it moves the watermark without claiming a time.
type receiptBook struct {
	marks map[int64]map[int64]*receiptMark
	// Encrypted-connection receipts naming a message not loaded yet, by message id. They apply when it arrives.
	waiting map[string][]waitingReceipt
	// What has to be written to (or removed from) the local store at the next keep. Cleared message ids are those
	// whose waiting receipts left memory — applied, given up on, or gone with their conversation — and are removed
	// before parked ones are written, so a message cleared and then named again is kept.
	dirty   map[receiptKey]bool
	parked  map[string]bool
	cleared map[string]bool
	// Conversations whose receipts came over the encrypted connection. Their watermarks are kept across restarts;
	// ordinary conversations are sent a fresh snapshot by Facebook every time.
	secure map[int64]bool
	// Send status values seen so far, with their direction, so each distinct one is logged once.
	statuses map[int64]bool
	// Facebook's own count of each group's members (applyMemberCounts).
	members map[int64]int64
}

type receiptMark struct {
	delivered int64
	// The read watermark before its last move, or -1 when that is not known.
	readFrom int64
	// When that move happened, as Facebook reported it. Zero when it did not say.
	readAt int64
}

type receiptKey struct{ thread, contact int64 }

type receiptKind int64

const (
	receiptRead      receiptKind = 1
	receiptDelivered receiptKind = 2
)

type waitingReceipt struct {
	// The conversation the receipt named, so deleting it takes the receipt too.
	thread  int64
	contact int64
	kind    receiptKind
	at      int64
	// When this helper began keeping it, which is what decides how long it may wait.
	since int64
}

// How many messages may have encrypted receipts waiting for them, and for how long. Some never arrive here — a
// reaction, an edit or a message that could not be decrypted is named like any other — so a receipt that has waited a
// week is given up, and when there is no room the one that has waited longest makes way for the new one. Otherwise
// receipts that will never settle would fill the room for good and every later one would be refused.
const (
	maxWaitingReceipts = 2000
	waitingReceiptAge  = int64(7 * 24 * time.Hour / time.Millisecond)
)

// The time a waiting receipt is stamped with. A variable so the tests can move it.
var receiptClock = func() int64 { return time.Now().UnixMilli() }

// Call with the lock held.
func (s *store) book() *receiptBook {
	if s.receipts == nil {
		s.receipts = &receiptBook{marks: map[int64]map[int64]*receiptMark{}, waiting: map[string][]waitingReceipt{},
			dirty: map[receiptKey]bool{}, parked: map[string]bool{}, cleared: map[string]bool{}, secure: map[int64]bool{},
			statuses: map[int64]bool{}, members: map[int64]int64{}}
	}
	return s.receipts
}

// Call with the lock held.
func (s *store) markOf(key, contact int64) *receiptMark {
	book := s.book()
	marks, ok := book.marks[key]
	if !ok {
		marks = map[int64]*receiptMark{}
		book.marks[key] = marks
	}
	mark, ok := marks[contact]
	if !ok {
		mark = &receiptMark{readFrom: -1}
		marks[contact] = mark
	}
	return mark
}

// Somebody who has left the conversation no longer counts towards who a message reached. Call with the lock held.
func (s *store) forgetReceipts(key, contact int64) {
	if s.receipts == nil {
		return
	}
	if _, ok := s.receipts.marks[key][contact]; ok {
		delete(s.receipts.marks[key], contact)
		s.receipts.dirty[receiptKey{key, contact}] = true
	}
}

// The ordinary connection's receipts in a batch: the participant snapshot, which carries each person's read and
// delivery watermarks, and the live read and delivery rows; and how many members each group has. Call with the lock
// held.
func (s *store) applyReceipts(tbl *table.LSTable, touch func(int64), noteSeen func(int64, string)) {
	s.applyMemberCounts(tbl, touch)
	for _, row := range tbl.LSAddParticipantIdToGroupThread {
		for _, id := range s.advanceReadAt(row.ThreadKey, row.ContactId, row.ReadWatermarkTimestampMs, 0, 0, false) {
			noteSeen(row.ThreadKey, id)
		}
		for _, id := range s.advanceDelivered(row.ThreadKey, row.ContactId, row.DeliveredWatermarkTimestampMs) {
			noteSeen(row.ThreadKey, id)
		}
	}
	for _, row := range tbl.LSUpdateReadReceipt {
		if s.adoptKeyPersonLocked(row.ThreadKey, row.ContactId) {
			touch(row.ThreadKey)
		}
		moved := s.advanceReadAt(row.ThreadKey, row.ContactId, row.ReadWatermarkTimestampMs, 0, row.ReadActionTimestampMs, true)
		// Whether Facebook sends these at all, and whether they land on a conversation we hold messages for, is the
		// only way to tell a silent protocol from a silent bug. Counts and one-way tags only.
		logf("read receipt chat=%s person=%s self=%v mark=%v when=%v loaded=%d moved=%d", tag(row.ThreadKey), tag(row.ContactId),
			row.ContactId == s.self, row.ReadWatermarkTimestampMs > 0, row.ReadActionTimestampMs > 0, len(s.messages[row.ThreadKey]), len(moved))
		for _, id := range moved {
			noteSeen(row.ThreadKey, id)
		}
	}
	if len(tbl.LSUpdateDeliveryReceipt) == 0 {
		return
	}
	moved := 0
	for _, row := range tbl.LSUpdateDeliveryReceipt {
		ids := s.advanceDelivered(row.ThreadKey, row.ContactId, row.DeliveredWatermarkTimestampMs)
		moved += len(ids)
		for _, id := range ids {
			noteSeen(row.ThreadKey, id)
		}
	}
	// One line per batch rather than per row: every message sent is delivered to everyone in the conversation.
	logf("delivery receipts rows=%d moved=%d", len(tbl.LSUpdateDeliveryReceipt), moved)
}

// advanceRead, remembering when the reading happened where that is known. A live report says when this person read
// everything between their previous watermark and the new one. With no previous watermark that span is unknown, except
// as far back as from, the oldest message an encrypted receipt named. Call with the lock held.
func (s *store) advanceReadAt(key, contact, watermark, from, at int64, live bool) []string {
	previous := int64(0)
	if current, ok := s.threads[key]; ok {
		previous = current.readBy[contact]
	}
	moved := s.advanceRead(key, contact, watermark)
	if current, ok := s.threads[key]; !ok || watermark <= previous || current.readBy[contact] != watermark {
		return moved
	}
	mark := s.markOf(key, contact)
	switch {
	case !live:
		mark.readFrom, mark.readAt = -1, 0
	case previous > 0:
		mark.readFrom, mark.readAt = previous, max(at, 0)
	case from > 0:
		mark.readFrom, mark.readAt = from-1, max(at, 0)
	default:
		mark.readFrom, mark.readAt = -1, 0
	}
	s.book().dirty[receiptKey{key, contact}] = true
	return moved
}

// A delivery watermark only moves forward. Returns the messages this account sent whose count of people reached
// changed: those between the two watermarks that this person had not already read. Call with the lock held.
func (s *store) advanceDelivered(key, contact, watermark int64) []string {
	if key == 0 || contact == 0 || watermark <= 0 || contact == s.self {
		return nil
	}
	mark := s.markOf(key, contact)
	previous := mark.delivered
	if watermark <= previous {
		return nil
	}
	mark.delivered = watermark
	s.book().dirty[receiptKey{key, contact}] = true
	read := int64(0)
	if current, ok := s.threads[key]; ok {
		read = current.readBy[contact]
	}
	var moved []string
	for id, message := range s.messages[key] {
		if s.self != 0 && message.sender == s.self && message.timestamp > previous && message.timestamp <= watermark &&
			message.timestamp > read {
			moved = append(moved, id)
		}
	}
	sort.Strings(moved)
	return moved
}

// How many of the other people a message this account sent has reached. Call with the lock held.
func (s *store) deliveredTo(key int64, current *message) int {
	if s.self == 0 || current.sender != s.self || current.unsent {
		return 0
	}
	reached := map[int64]bool{}
	if thread, ok := s.threads[key]; ok {
		for contact, watermark := range thread.readBy {
			if contact != s.self && watermark >= current.timestamp {
				reached[contact] = true
			}
		}
	}
	if s.receipts != nil {
		for contact, mark := range s.receipts.marks[key] {
			if contact != s.self && mark.delivered >= current.timestamp {
				reached[contact] = true
			}
		}
	}
	return len(reached)
}

// When a message this account sent was read: the latest of its readers' reading times, and only when every one of
// them is known. One reader whose time is not known makes the whole answer unknown. Call with the lock held.
func (s *store) seenAt(key int64, current *message) int64 {
	if s.self == 0 || current.sender != s.self || current.unsent || s.receipts == nil {
		return 0
	}
	thread, ok := s.threads[key]
	if !ok {
		return 0
	}
	latest := int64(0)
	for contact, watermark := range thread.readBy {
		if contact == s.self || watermark < current.timestamp {
			continue
		}
		mark := s.receipts.marks[key][contact]
		if mark == nil || mark.readAt <= 0 || mark.readFrom < 0 || current.timestamp <= mark.readFrom {
			return 0
		}
		latest = max(latest, mark.readAt)
	}
	return latest
}

// An encrypted receipt, which names messages rather than a moment. The moment is the newest named message this helper
// holds; a named message it does not hold waits for that message to arrive. Returns the messages to redraw, how many
// named messages were found, how many now wait, and how many older waiting receipts were given up to make room. Call
// with the lock held.
func (s *store) applyEncryptedReceipt(key, contact int64, kind receiptKind, ids []string, at int64) (moved []string, matched, waiting, dropped int) {
	if key == 0 || contact == 0 || contact == s.self {
		return nil, 0, 0, 0
	}
	book := s.book()
	book.secure[key] = true
	newest, oldest := int64(0), int64(0)
	for _, id := range ids {
		found, ok := s.messages[key][id]
		if !ok {
			dropped += s.park(id, waitingReceipt{thread: key, contact: contact, kind: kind, at: at, since: receiptClock()})
			waiting++
			continue
		}
		matched++
		newest = max(newest, found.timestamp)
		if oldest == 0 || found.timestamp < oldest {
			oldest = found.timestamp
		}
	}
	if newest == 0 {
		return nil, matched, waiting, dropped
	}
	if kind == receiptDelivered {
		return s.advanceDelivered(key, contact, newest), matched, waiting, dropped
	}
	return s.advanceReadAt(key, contact, newest, oldest, at, true), matched, waiting, dropped
}

// Keeps a receipt until its message arrives, making room first when every place is taken: receipts past their age
// go, and then, if that was not enough, whichever has waited longest. Returns how many were given up. Call with the
// lock held.
func (s *store) park(id string, receipt waitingReceipt) (dropped int) {
	book := s.book()
	if book.waiting[id] == nil && len(book.waiting) >= maxWaitingReceipts {
		oldest, first := "", int64(0)
		for waitingID, receipts := range book.waiting {
			since := receipts[0].since
			if since < receipt.since-waitingReceiptAge {
				s.dropWaiting(waitingID)
				dropped++
				continue
			}
			if oldest == "" || since < first || since == first && waitingID < oldest {
				oldest, first = waitingID, since
			}
		}
		if len(book.waiting) >= maxWaitingReceipts && oldest != "" {
			s.dropWaiting(oldest)
			dropped++
		}
	}
	book.waiting[id] = append(book.waiting[id], receipt)
	book.parked[id] = true
	return dropped
}

// Call with the lock held.
func (s *store) dropWaiting(id string) {
	book := s.book()
	delete(book.waiting, id)
	delete(book.parked, id)
	book.cleared[id] = true
}

// A deleted conversation takes its receipts with it: the positions, and receipts waiting for its messages. The rows
// already kept for it go with the rest of its stored history (messageStore.forgetThread). Call with the lock held.
func (s *store) forgetThreadReceipts(key int64) {
	book := s.receipts
	if book == nil {
		return
	}
	delete(book.marks, key)
	delete(book.secure, key)
	for pending := range book.dirty {
		if pending.thread == key {
			delete(book.dirty, pending)
		}
	}
	for id, receipts := range book.waiting {
		kept := slices.DeleteFunc(slices.Clone(receipts), func(receipt waitingReceipt) bool { return receipt.thread == key })
		if len(kept) == len(receipts) {
			continue
		}
		s.dropWaiting(id)
		if len(kept) > 0 {
			book.waiting[id], book.parked[id] = kept, true
		}
	}
}

// Messages the encrypted connection delivered before a conversation's pairing was known sit under the number they
// arrived with, and so do the receipts for them. When the two turn out to be one conversation the messages move
// (store.apply), and the receipts move with them rather than staying behind on a conversation that is gone. Call with
// the lock held, before the stray conversation is removed.
func (s *store) adoptReceipts(stray *thread, from, into int64) {
	current := s.get(into)
	book := s.book()
	strays := book.marks[from]
	if stray != nil {
		for contact, watermark := range stray.readBy {
			if watermark <= current.readBy[contact] {
				continue
			}
			if current.readBy == nil {
				current.readBy = map[int64]int64{}
			}
			current.readBy[contact] = watermark
			// A read time belongs to the reading it came with, so it moves only with the further watermark.
			target := s.markOf(into, contact)
			target.readFrom, target.readAt = -1, 0
			if mark := strays[contact]; mark != nil {
				target.readFrom, target.readAt = mark.readFrom, mark.readAt
			}
			book.dirty[receiptKey{into, contact}] = true
		}
	}
	for contact, mark := range strays {
		if mark.delivered > 0 {
			target := s.markOf(into, contact)
			target.delivered = max(target.delivered, mark.delivered)
			book.dirty[receiptKey{into, contact}] = true
		}
		// Removed from the store at the next keep, since nothing is left under this number.
		book.dirty[receiptKey{from, contact}] = true
	}
	delete(book.marks, from)
	if book.secure[from] {
		book.secure[into] = true
	}
	for id, receipts := range book.waiting {
		for index := range receipts {
			if receipts[index].thread == from {
				receipts[index].thread = into
				book.parked[id] = true
			}
		}
	}
}

// Facebook's own count of a group's members. Its participant rows can arrive a few at a time, and a group whose every
// listed member has read is not read by everyone while some are still unlisted: the app says Read only once the list
// is as long as this count. A change to the count adds to one Facebook has given; alone it says nothing about the
// total. Call with the lock held.
func (s *store) applyMemberCounts(tbl *table.LSTable, touch func(int64)) {
	counted, changed := 0, 0
	for _, row := range tbl.LSDeleteThenInsertThread {
		if row.ThreadKey == 0 || row.MemberCount <= 0 {
			continue
		}
		counted++
		if book := s.book(); book.members[row.ThreadKey] != row.MemberCount {
			book.members[row.ThreadKey] = row.MemberCount
			touch(row.ThreadKey)
		}
	}
	for _, row := range tbl.LSAddToMemberCount {
		if row.ThreadKey == 0 || row.IncrementCount == 0 {
			continue
		}
		changed++
		if count, ok := s.book().members[row.ThreadKey]; ok {
			s.receipts.members[row.ThreadKey] = max(count+row.IncrementCount, 0)
			touch(row.ThreadKey)
		}
	}
	if counted+changed > 0 {
		logf("member counts chats=%d changes=%d", counted, changed)
	}
}

// How many members Facebook says a group has, or 0 when it has not said. Call with the lock held.
func (s *store) membersOf(current *thread) int {
	if s.receipts == nil || current.kind.IsOneToOne() {
		return 0
	}
	return int(s.receipts.members[current.key])
}

// Receipts that named this message before it arrived apply now that it has, and the rows they move are redrawn with
// it. Call with the lock held.
func (s *store) settleWaitingReceipts(key int64, current *message, touched *changes) *changes {
	if s.receipts == nil || len(s.receipts.waiting[current.id]) == 0 {
		return touched
	}
	moved := s.settleWaiting(key, current)
	chat := strconv.FormatInt(key, 10)
	for _, id := range moved {
		if id == current.id || slices.Contains(touched.Receipts[chat], id) {
			continue
		}
		if touched.Receipts == nil {
			touched.Receipts = map[string][]string{}
		}
		touched.Receipts[chat] = append(touched.Receipts[chat], id)
	}
	return touched
}

// Call with the lock held.
func (s *store) settleWaiting(key int64, current *message) []string {
	book := s.book()
	waiting := book.waiting[current.id]
	s.dropWaiting(current.id)
	book.secure[key] = true
	var moved []string
	for _, receipt := range waiting {
		var ids []string
		if receipt.kind == receiptDelivered {
			ids = s.advanceDelivered(key, receipt.contact, current.timestamp)
		} else {
			ids = s.advanceReadAt(key, receipt.contact, current.timestamp, current.timestamp, receipt.at, true)
		}
		for _, id := range ids {
			if !slices.Contains(moved, id) {
				moved = append(moved, id)
			}
		}
	}
	return moved
}

// Facebook's own send status numbers are not documented anywhere this build can check, so none of them is shown as a
// failure. Each distinct value is recorded once, with whether it was on a message this account sent, so a live session
// can say which numbers appear and when. Call with the lock held.
func (s *store) noteSendStatus(value int64, outgoing bool) {
	book := s.book()
	seen := value << 1
	if outgoing {
		seen |= 1
	}
	if book.statuses[seen] {
		return
	}
	book.statuses[seen] = true
	logf("send status seen value=%d outgoing=%v", value, outgoing)
}
