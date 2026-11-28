package main

import (
	"encoding/json"
	"reflect"
	"strings"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func TestHistoryActivityIsMarkedPerMessage(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{
		{ThreadKey: 7, MessageId: "old", Text: "synthetic", SenderId: 1, TimestampMs: 1000},
		{ThreadKey: 7, MessageId: "other-old", Text: "synthetic", SenderId: 1, TimestampMs: 900},
	}})
	changed := s.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "old", Text: "HISTORY-BODY-SENTINEL", SenderId: 1, TimestampMs: 1000}},
		LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 7, MessageId: "new", Text: "synthetic", SenderId: 1, TimestampMs: 2000}},
		LSUpsertReaction: []*table.LSUpsertReaction{
			{ThreadKey: 7, MessageId: "old", ActorId: 1, Reaction: "👍"},
			{ThreadKey: 7, MessageId: "other-old", ActorId: 2, Reaction: "❤"},
		},
	})
	if !reflect.DeepEqual(changed.History["7"], []string{"old"}) || len(changed.Messages["7"]) != 3 {
		t.Fatalf("snapshot and unrelated live ids not separated: %+v", changed)
	}
	page := s.historyBefore(7, 0, 50)
	if len(page) != 3 || len(page[1].Reactions) != 1 || len(page[0].Reactions) != 1 {
		t.Fatal("history reactions did not reach the stored message snapshots")
	}
	wire, err := json.Marshal(changed)
	if err != nil || !strings.Contains(string(wire), `"history":{"7":["old"]}`) || strings.Contains(string(wire), "SENTINEL") {
		t.Fatalf("history origin did not stay an ids-only event: %s, %v", wire, err)
	}
	// The original message's age never suppresses a subsequent live reaction, including its removal.
	for _, reaction := range []string{"😂", ""} {
		live := s.apply(&table.LSTable{LSUpsertReaction: []*table.LSUpsertReaction{
			{ThreadKey: 7, MessageId: "old", ActorId: 2, Reaction: reaction},
		}})
		if len(live.History) != 0 || !reflect.DeepEqual(live.Messages["7"], []string{"old"}) {
			t.Fatalf("live reaction was labelled history: %+v", live)
		}
	}
}

func TestExplicitMutationsRemainLiveBesideHistory(t *testing.T) {
	c := &changes{Messages: map[string][]string{"7": {"insert", "edit", "old"}}, Deleted: map[string][]string{"7": {"delete", "unsend", "historical-unsend"}}}
	c.markHistory(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{
			{MessageId: "insert"}, {MessageId: "edit"}, {MessageId: "old"},
			{MessageId: "delete"}, {MessageId: "unsend"}, {MessageId: "historical-unsend", IsUnsent: true},
		},
		LSInsertMessage:           []*table.LSInsertMessage{{MessageId: "insert"}},
		LSEditMessage:             []*table.LSEditMessage{{MessageID: "edit"}},
		LSDeleteMessage:           []*table.LSDeleteMessage{{MessageId: "delete"}},
		LSDeleteThenInsertMessage: []*table.LSDeleteThenInsertMessage{{MessageId: "unsend", IsUnsent: true}},
	})
	if !reflect.DeepEqual(c.History["7"], []string{"old", "historical-unsend"}) {
		t.Fatalf("explicit live mutations swallowed by a mixed history table: %+v", c.History)
	}
}
