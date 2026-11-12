package main

import (
	"context"
	"path/filepath"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// The shape of a Marketplace buyer's first contact (2026-11-12): Facebook's own lines as admin messages from the buyer,
// and a "Message sent" card with a title, no picture and only Marketplace's own call-function buttons, which the
// Messenger phone app does not show (owner: hide it).
func TestMarketplaceNoticesSurviveRestartAndCardStaysHidden(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "synthetic.db")
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	a := newAdapter(&output{})
	a.kept = kept
	a.chats.setSelf(1)
	a.keepMessages(ctx, a.chats.apply(&table.LSTable{
		LSInsertMessage: []*table.LSInsertMessage{
			{ThreadKey: 7, MessageId: "synthetic-started", SenderId: 42, TimestampMs: 1000, Text: "Synthetic started this chat.", IsAdminMessage: true},
			{ThreadKey: 7, MessageId: "synthetic-question", SenderId: 42, TimestampMs: 2000, Text: "Synthetic question?"},
			{ThreadKey: 7, MessageId: "synthetic-card", SenderId: 42, TimestampMs: 3000},
		},
		LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{{ThreadKey: 7, MessageId: "synthetic-card", TitleText: "Message sent"}},
	}))
	check := func(s *store) {
		t.Helper()
		page := s.historyBefore(7, 0, 10)
		if len(page) != 2 {
			t.Fatalf("expected two rows, got %d", len(page))
		}
		if !page[0].Notice || page[0].Text != "Synthetic started this chat." {
			t.Fatal("an admin message must be a notice with its words")
		}
		if page[1].Notice || page[1].Text != "Synthetic question?" {
			t.Fatal("a person's message must not be a notice")
		}
		// Asked for by id, as after a change event, it comes back with nothing to draw; the app hides such a row.
		if card, ok := s.messageByID(7, "synthetic-card"); ok && (card.Text != "" || len(card.Media) != 0) {
			t.Fatal("the Marketplace card must carry nothing to draw")
		}
	}
	check(a.chats)
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
	check(back)
	// A later refresh of the same row keeps what Facebook says about it.
	a.chats, a.kept = back, kept
	a.keepMessages(ctx, back.apply(&table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{
		{ThreadKey: 7, MessageId: "synthetic-started", SenderId: 42, TimestampMs: 1000, Text: "Synthetic started this chat.", IsAdminMessage: true},
	}}))
	check(back)
}
