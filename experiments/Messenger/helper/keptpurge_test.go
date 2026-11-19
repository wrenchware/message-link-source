package main

import (
	"context"
	"strings"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func keptRows(t *testing.T, kept *messageStore, query string, args ...any) int {
	t.Helper()
	var count int
	if err := kept.db.QueryRowContext(context.Background(), query, args...).Scan(&count); err != nil {
		t.Fatal(err)
	}
	return count
}

// A deleted conversation's purge, a taken-back message's forget and an import's deletions look rows up by thread and
// time or by message. Each used to scan its whole table, under the store lock, for every deleted conversation.
func TestKeptHistoryLookupsUseAnIndex(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, t.TempDir()+"/messages.db")
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	for _, query := range []string{
		`SELECT id FROM encrypted_message WHERE thread = 1 AND at <= 2`,
		`SELECT id FROM encrypted_media WHERE thread = 1 AND at <= 2`,
		`SELECT id FROM encrypted_media WHERE message = 'x'`,
		`SELECT id FROM message_attachment WHERE message = 'x'`,
	} {
		rows, err := kept.db.QueryContext(ctx, `EXPLAIN QUERY PLAN `+query)
		if err != nil {
			t.Fatal(err)
		}
		var plan []string
		for rows.Next() {
			var id, parent, unused int
			var detail string
			if err := rows.Scan(&id, &parent, &unused, &detail); err != nil {
				t.Fatal(err)
			}
			plan = append(plan, detail)
		}
		rows.Close()
		if joined := strings.Join(plan, "; "); !strings.Contains(joined, "USING INDEX") && !strings.Contains(joined, "USING COVERING INDEX") {
			t.Fatalf("%s scans: %s", query, joined)
		}
	}
}

// The purge of a deleted conversation runs once, not on every save, and runs again once a message may have landed
// inside its range.
func TestDeletedConversationPurgeRunsOncePerWrite(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, t.TempDir()+"/messages.db")
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	s := newStore()
	s.get(7).kind = table.ONE_TO_ONE
	s.get(7).deletedAt = 1000
	kept.remember(ctx, 7, "old", 1, "before", "", 500, false)
	kept.saveIdentity(ctx, s)
	if keptRows(t, kept, `SELECT COUNT(*) FROM encrypted_message WHERE thread = 7`) != 0 {
		t.Fatal("the deleted conversation's message was not purged")
	}
	// Written behind the store's back: nothing says the range changed, so a second save does not look again.
	if _, err := kept.db.ExecContext(ctx, `INSERT INTO encrypted_message (id, thread, sender, body, reply, at) VALUES ('raw', 7, 1, x'', x'', 600)`); err != nil {
		t.Fatal(err)
	}
	kept.saveIdentity(ctx, s)
	if keptRows(t, kept, `SELECT COUNT(*) FROM encrypted_message WHERE thread = 7`) != 1 {
		t.Fatal("an unchanged deleted conversation was purged again")
	}
	// A write through the store does say so.
	kept.remember(ctx, 7, "late", 1, "late", "", 700, false)
	kept.saveIdentity(ctx, s)
	if keptRows(t, kept, `SELECT COUNT(*) FROM encrypted_message WHERE thread = 7`) != 0 {
		t.Fatal("a message written into a purged range was kept")
	}
	// And a later deletion moves the range.
	kept.remember(ctx, 7, "newer", 1, "newer", "", 1500, false)
	kept.saveIdentity(ctx, s)
	s.get(7).deletedAt = 2000
	if _, err := kept.db.ExecContext(ctx, `INSERT INTO encrypted_message (id, thread, sender, body, reply, at) VALUES ('raw2', 7, 1, x'', x'', 1600)`); err != nil {
		t.Fatal(err)
	}
	kept.saveIdentity(ctx, s)
	if keptRows(t, kept, `SELECT COUNT(*) FROM encrypted_message WHERE thread = 7`) != 0 {
		t.Fatal("a conversation deleted again was not purged up to its new time")
	}
}

// A message taken back takes its ordinary attachments with it, and keeps them from coming back.
func TestForgottenMessageLosesItsAttachments(t *testing.T) {
	ctx := context.Background()
	path := t.TempDir() + "/messages.db"
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	kept.remember(ctx, 7, "m1", 1, "", "", 500, false)
	kept.rememberAttachmentsOn(ctx, kept.db, 7, "m1", []*attachment{{id: "a1", name: "photo.jpg"}}, nil)
	if keptRows(t, kept, `SELECT COUNT(*) FROM message_attachment WHERE message = 'm1'`) != 1 {
		t.Fatal("the attachment was not kept to begin with")
	}
	kept.forget(ctx, 7, "m1")
	if keptRows(t, kept, `SELECT COUNT(*) FROM message_attachment WHERE message = 'm1'`) != 0 {
		t.Fatal("a taken-back message's attachment stayed on disk")
	}
	kept.rememberAttachmentsOn(ctx, kept.db, 7, "m1", []*attachment{{id: "a1", name: "photo.jpg"}}, nil)
	if keptRows(t, kept, `SELECT COUNT(*) FROM message_attachment WHERE message = 'm1'`) != 0 {
		t.Fatal("a taken-back message's attachment was written again")
	}
	// One left behind by an earlier build goes at the next open.
	if _, err := kept.db.ExecContext(ctx, `INSERT INTO message_attachment (id, message, thread, name, mime, size, width, height, video, animated, source, preview, playable, previewSource)
		VALUES ('a2', 'm1', 7, x'', x'', 0, 0, 0, 0, 0, x'', x'', x'', x'')`); err != nil {
		t.Fatal(err)
	}
	kept.db.Close()
	kept, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	if keptRows(t, kept, `SELECT COUNT(*) FROM message_attachment WHERE message = 'm1'`) != 0 {
		t.Fatal("an attachment left by an earlier build was not cleared at open")
	}
}
