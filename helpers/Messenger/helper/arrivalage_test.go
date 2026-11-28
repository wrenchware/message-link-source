package main

import (
	"strings"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func TestArrivalAgeCountsOnlyNewMessagesFromOthers(t *testing.T) {
	s := newStore()
	s.setSelf(9)
	s.apply(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{
		{ThreadKey: 7, MessageId: "known", Text: "SENTINEL-BODY", SenderId: 1, TimestampMs: 1000},
	}})
	now := time.UnixMilli(1_000_000_000)
	sentAgo := func(d time.Duration) int64 { return now.Add(-d).UnixMilli() }
	tbl := &table.LSTable{
		LSInsertMessage: []*table.LSInsertMessage{
			{ThreadKey: 7, MessageId: "live", SenderId: 1, TimestampMs: sentAgo(2 * time.Second)},
			{ThreadKey: 7, MessageId: "mine", SenderId: 9, TimestampMs: sentAgo(time.Second)},
		},
		LSUpsertMessage: []*table.LSUpsertMessage{
			{ThreadKey: 7, MessageId: "known", SenderId: 1, TimestampMs: 1000},
			{ThreadKey: 7, MessageId: "late", SenderId: 1, TimestampMs: sentAgo(22 * time.Minute)},
			{ThreadKey: 7, MessageId: "live", SenderId: 1, TimestampMs: sentAgo(2 * time.Second)},
			{ThreadKey: 8, MessageId: "other", SenderId: 2, TimestampMs: sentAgo(61 * time.Second)},
			{ThreadKey: 8, MessageId: "stamp", SenderId: 2},
		},
		LSExecuteFirstBlockForSyncTransaction: []*table.LSExecuteFirstBlockForSyncTransaction{{DatabaseID: 95}, {DatabaseID: 1}},
	}
	rows := s.arrivalRows(tbl)
	if len(rows) != 7 {
		t.Fatalf("rows = %d, want every insert and upsert row", len(rows))
	}
	group := syncGroups(tbl)
	if group != "1,95" {
		t.Fatalf("group = %q", group)
	}
	lines := arrivalLines(rows, "resync", group, now)
	if len(lines) != 3 {
		t.Fatalf("want two conversations and the batch, got %q", lines)
	}
	want := map[string]string{
		tag(7): "arrival age chat=" + tag(7) + " source=resync group=1,95 n=2 max_age_s=1320 over60=1 insert=1 upsert=1 dup=2 self=1 notime=0",
		tag(8): "arrival age chat=" + tag(8) + " source=resync group=1,95 n=1 max_age_s=61 over60=1 insert=0 upsert=1 dup=0 self=0 notime=1",
		"all":  "arrival age chat=all source=resync group=1,95 n=3 max_age_s=1320 over60=2 insert=1 upsert=2 dup=2 self=1 notime=1",
	}
	for _, line := range lines {
		name := strings.Fields(line)[2][len("chat="):]
		if want[name] != line {
			t.Errorf("%s:\n got %s\nwant %s", name, line, want[name])
		}
		delete(want, name)
	}
	if len(want) != 0 {
		t.Errorf("missing lines for %v", want)
	}
	for _, line := range lines {
		if strings.Contains(line, "SENTINEL") {
			t.Fatal("a message body reached the arrival line")
		}
	}
}

func TestArrivalAgeBatchOfRepeatsStillWritesTheBatchLine(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 7, MessageId: "known", SenderId: 1, TimestampMs: 1000}}})
	rows := s.arrivalRows(&table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "known", SenderId: 1, TimestampMs: 1000}}})
	lines := arrivalLines(rows, "live", "none", time.UnixMilli(5000))
	want := "arrival age chat=all source=live group=none n=0 max_age_s=0 over60=0 insert=0 upsert=0 dup=1 self=0 notime=0"
	if len(lines) != 1 || lines[0] != want {
		t.Fatalf("got %q", lines)
	}
}

func TestArrivalAgeIgnoresTablesWithoutMessagesAndClampsClockSkew(t *testing.T) {
	s := newStore()
	if s.arrivalRows(&table.LSTable{}) != nil || s.arrivalRows(nil) != nil || arrivalLines(nil, "live", "none", time.Now()) != nil {
		t.Fatal("a table without message rows should write nothing")
	}
	now := time.UnixMilli(10_000)
	rows := s.arrivalRows(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 3, MessageId: "future", SenderId: 1, TimestampMs: 20_000}}})
	lines := arrivalLines(rows, "live", "none", now)
	if len(lines) != 2 || !strings.Contains(lines[0], " n=1 max_age_s=0 over60=0 ") {
		t.Fatalf("a message stamped ahead of this PC should read as age zero: %q", lines)
	}
}

func TestArrivalSourceFollowsTheSocket(t *testing.T) {
	if arrivalSource(false) != "resync" || arrivalSource(true) != "live" {
		t.Fatal("source names changed")
	}
	a := newAdapter(nil)
	if a.socketLive.Load() {
		t.Fatal("a new adapter has not finished its opening synchronisation")
	}
}
