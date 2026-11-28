package main

import (
	"fmt"
	"sort"
	"strconv"
	"strings"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// How old a message already was when this helper first heard of it, per conversation, for working out whether some
// conversations are only delivered when the socket resynchronises. Counts and seconds only: a conversation is its
// usual one-way tag, a sync group is its database number, and nothing a message says or who sent it reaches the log.
//
// Only messages the store had never seen count toward n, max_age_s and over60, because a resync re-delivers
// messages it already has and those say nothing about when a message first arrived. dup is how many were skipped
// for that; self is how many of the new ones were this account's own (a send's echo is always age zero and says
// nothing about delivery); notime is new rows with no timestamp.

// A message row reduced to what the age measurement needs. Built before the batch is applied, because whether a
// message is new is a question about the store as it was.
type arrivalRow struct {
	chat   int64
	sentMs int64
	fresh  bool
	self   bool
	upsert bool
}

// Beyond this many conversations in one batch only the batch line is written, which still carries every count.
const arrivalChatLines = 40

// Older than this is called late in the log. A minute is far beyond ordinary delivery and well short of a resync.
const arrivalLateAfter = 60 * time.Second

// Lightspeed upserts carry fetched history and inserts carry live arrivals (see changes.markHistory), so the two are
// counted separately: a group that only ever shows upserts is not being pushed live.
func (s *store) arrivalRows(tbl *table.LSTable) []arrivalRow {
	if tbl == nil || len(tbl.LSUpsertMessage)+len(tbl.LSInsertMessage) == 0 {
		return nil
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	rows := make([]arrivalRow, 0, len(tbl.LSUpsertMessage)+len(tbl.LSInsertMessage))
	seen := map[string]bool{}
	add := func(thread int64, id string, sent, sender int64, upsert bool) {
		key := thread
		if mapped, ok := s.hybrid[thread]; ok {
			key = mapped
		}
		known := id != "" && (s.messages[key][id] != nil || s.messages[thread][id] != nil)
		// The same message twice in one batch is one arrival.
		first := id == "" || !seen[id]
		seen[id] = true
		rows = append(rows, arrivalRow{chat: key, sentMs: sent, fresh: !known && first,
			self: s.self != 0 && sender == s.self, upsert: upsert})
	}
	for _, row := range tbl.LSInsertMessage {
		add(row.ThreadKey, row.MessageId, row.TimestampMs, row.SenderId, false)
	}
	for _, row := range tbl.LSUpsertMessage {
		add(row.ThreadKey, row.MessageId, row.TimestampMs, row.SenderId, true)
	}
	return rows
}

// The database numbers a table's sync transactions name, such as "1" or "1,95", or "none" for a table without one.
// Lightspeed answers a sync request with a transaction naming its database; whether a live push carries one too is
// part of what this measurement is meant to show.
func syncGroups(tbl *table.LSTable) string {
	if tbl == nil {
		return "none"
	}
	seen := map[int64]bool{}
	for _, block := range tbl.GetLSExecuteFirstBlockForSyncTransactionV4() {
		seen[block.DatabaseID] = true
	}
	if len(seen) == 0 {
		return "none"
	}
	ids := make([]int64, 0, len(seen))
	for id := range seen {
		ids = append(ids, id)
	}
	sort.Slice(ids, func(i, j int) bool { return ids[i] < ids[j] })
	names := make([]string, len(ids))
	for i, id := range ids {
		names[i] = strconv.FormatInt(id, 10)
	}
	return strings.Join(names, ",")
}

type arrivalTally struct {
	n, over, insert, upsert, dup, self, notime int
	maxAge                                     time.Duration
}

func (t *arrivalTally) add(row arrivalRow, now time.Time) {
	switch {
	case !row.fresh:
		t.dup++
		return
	case row.self:
		t.self++
		return
	case row.sentMs <= 0:
		t.notime++
		return
	}
	age := now.Sub(time.UnixMilli(row.sentMs))
	if age < 0 {
		age = 0
	}
	t.n++
	if age > arrivalLateAfter {
		t.over++
	}
	if age > t.maxAge {
		t.maxAge = age
	}
	if row.upsert {
		t.upsert++
	} else {
		t.insert++
	}
}

func (t *arrivalTally) line(chat, source, group string) string {
	return fmt.Sprintf("arrival age chat=%s source=%s group=%s n=%d max_age_s=%d over60=%d insert=%d upsert=%d dup=%d self=%d notime=%d",
		chat, source, group, t.n, int64(t.maxAge/time.Second), t.over, t.insert, t.upsert, t.dup, t.self, t.notime)
}

// One line per conversation that received a new message, then chat=all for the whole batch (written even when
// nothing in it was new, since a batch of nothing but repeats is also a finding). Lines come out in tag order.
func arrivalLines(rows []arrivalRow, source, group string, now time.Time) []string {
	if len(rows) == 0 {
		return nil
	}
	whole := &arrivalTally{}
	perChat := map[int64]*arrivalTally{}
	for _, row := range rows {
		whole.add(row, now)
		tally := perChat[row.chat]
		if tally == nil {
			tally = &arrivalTally{}
			perChat[row.chat] = tally
		}
		tally.add(row, now)
	}
	type tagged struct {
		name  string
		tally *arrivalTally
	}
	var chats []tagged
	for chat, tally := range perChat {
		if tally.n > 0 {
			chats = append(chats, tagged{tag(chat), tally})
		}
	}
	sort.Slice(chats, func(i, j int) bool { return chats[i].name < chats[j].name })
	lines := make([]string, 0, len(chats)+1)
	for i, chat := range chats {
		if i == arrivalChatLines {
			break
		}
		lines = append(lines, chat.tally.line(chat.name, source, group))
	}
	return append(lines, whole.line("all", source, group))
}

// What the socket was doing when a table arrived. Everything delivered before the connected or reconnected event is
// the synchronisation that opens the connection, so it is called resync; everything after is live. A live push that
// lands inside that opening window is called resync too, which is the one way this labelling can mislead.
func arrivalSource(live bool) string {
	if live {
		return "live"
	}
	return "resync"
}

func (a *adapter) logArrivalAge(tbl *table.LSTable, rows []arrivalRow) {
	for _, line := range arrivalLines(rows, arrivalSource(a.socketLive.Load()), syncGroups(tbl), time.Now()) {
		logf("%s", line)
	}
}
