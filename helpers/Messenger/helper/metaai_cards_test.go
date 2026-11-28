package main

import (
	"context"
	"path/filepath"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func TestMetaAITextOnlyCardUpdatesAndSurvivesRestart(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "synthetic.db")
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	a := newAdapter(&output{})
	a.kept = kept
	a.chats.setSelf(1)
	apply := func(tbl *table.LSTable) { a.keepMessages(ctx, a.chats.apply(tbl)) }
	assert := func(s *store, expected string) {
		t.Helper()
		page := s.historyBefore(7, 0, 10)
		if len(page) != 1 || page[0].Text != expected || page[0].ID != "synthetic-ai" || page[0].ReplyTo != "synthetic-quote" || page[0].At.UnixMilli() != 1000 || len(page[0].Media) != 0 {
			t.Fatal("card must render supplied title in one stable quoted native history row")
		}
		_, body, _, _, _ := s.rawMessage(7, "synthetic-ai")
		if body != "" {
			t.Fatal("card projection overwrote raw provider body")
		}
	}
	// The observed live shape is a container with no FBID, picture, playable URL or body text.
	apply(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 7, MessageId: "synthetic-ai", SenderId: metaAIMessengerID, TimestampMs: 1000, ReplySourceId: "synthetic-quote"}},
		LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{{ThreadKey: 7, MessageId: "synthetic-ai", TitleText: "Synthetic AI answer 🌍\nsecond line"}},
	})
	assert(a.chats, "Synthetic AI answer 🌍\nsecond line")
	// Use only the observed container shape for replacement; no fragment relationship is invented.
	apply(&table.LSTable{LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{{ThreadKey: 7, MessageId: "synthetic-ai", TitleText: "Synthetic replacement answer"}}})
	assert(a.chats, "Synthetic replacement answer")
	// A late item containing the same title is not a second answer or duplicated text.
	apply(&table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{{ThreadKey: 7, MessageId: "synthetic-ai", TitleText: "Synthetic replacement answer"}}})
	assert(a.chats, "Synthetic replacement answer")
	apply(&table.LSTable{LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{{ThreadKey: 7, MessageId: "synthetic-ai", DefaultCtaId: 1}}})
	assert(a.chats, "Synthetic replacement answer")
	if err = kept.db.Close(); err != nil {
		t.Fatal(err)
	}
	kept, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	back := newStore()
	back.setSelf(1)
	kept.restore(ctx, back)
	a.chats, a.kept = back, kept
	assert(back, "Synthetic replacement answer")
	// A bare body refresh after restart cannot erase separately saved card text.
	apply(&table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "synthetic-ai", SenderId: metaAIMessengerID, TimestampMs: 1000}}})
	assert(back, "Synthetic replacement answer")
	apply(&table.LSTable{LSDeleteMessage: []*table.LSDeleteMessage{{ThreadKey: 7, MessageId: "synthetic-ai"}}})
	apply(&table.LSTable{LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{{ThreadKey: 7, MessageId: "synthetic-ai", TitleText: "Late deleted card"}},
		LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 7, MessageId: "synthetic-ai", SenderId: metaAIMessengerID, TimestampMs: 2000}},
	})
	if len(back.historyBefore(7, 0, 10)) != 0 {
		t.Fatal("late card resurrected deleted message")
	}
	restored := newStore()
	kept.restore(ctx, restored)
	if len(restored.historyBefore(7, 0, 10)) != 0 {
		t.Fatal("deleted card returned after restart")
	}
}

func TestMetaAICardBeforeMessageAndProjectionBoundaries(t *testing.T) {
	for _, mode := range []string{"ordinary", "real-body", "other-contact", "outgoing", "deleted", "picture", "playable-fallback"} {
		t.Run(mode, func(t *testing.T) {
			s := newStore()
			s.setSelf(1)
			card := &table.LSInsertXmaAttachment{ThreadKey: 7, MessageId: "synthetic-ai", TitleText: "Synthetic title"}
			row := &table.LSInsertMessage{ThreadKey: 7, MessageId: "synthetic-ai", SenderId: metaAIMessengerID, TimestampMs: 1000}
			switch mode {
			case "real-body":
				row.Text = "Actual synthetic body"
			case "other-contact":
				row.SenderId = 42
			case "outgoing":
				s.setSelf(metaAIMessengerID)
			case "deleted":
				row.IsUnsent = true
			case "picture":
				card.PreviewUrl = "https://example.test/synthetic.png"
			case "playable-fallback":
				card.PlayableUrlFallback = "https://example.test/synthetic.mp4"
			}
			s.apply(&table.LSTable{LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{card}})
			s.apply(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{row}})
			page := s.historyBefore(7, 0, 10)
			if mode == "ordinary" && (len(page) != 1 || page[0].Text != card.TitleText) {
				t.Fatal("early card lost when message arrived")
			}
			if mode == "real-body" && (len(page) != 1 || page[0].Text != row.Text) {
				t.Fatal("title overwrote real body")
			}
			if mode != "ordinary" && mode != "real-body" && len(page) > 0 && page[0].Text == card.TitleText {
				t.Fatal("title projection crossed identity/deletion/media boundary")
			}
		})
	}
}
