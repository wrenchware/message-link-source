package main

import (
	"context"
	"fmt"
	"path/filepath"
	"sync"
	"testing"
)

// An attachment kept for a message while that message is being taken back never outlives the deletion record. The
// deletes and the record were separate statements, and an attachment written between them stayed for good.
func TestAttachmentWrittenDuringForgetDoesNotOutliveIt(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "synthetic.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	for round := 0; round < 150; round++ {
		id := fmt.Sprintf("m%d", round)
		kept.remember(ctx, 7, id, 42, "synthetic", "", int64(1000+round))
		var writers sync.WaitGroup
		start := make(chan struct{})
		for writer := 0; writer < 2; writer++ {
			writers.Add(1)
			go func() {
				defer writers.Done()
				<-start
				for again := 0; again < 4; again++ {
					kept.rememberAttachments(ctx, 7, id, []*attachment{{id: fmt.Sprintf("a%d-%d", round, writer), mime: "image/png"}}, nil)
				}
			}()
		}
		close(start)
		if err := kept.forget(ctx, 7, id); err != nil {
			t.Fatal(err)
		}
		writers.Wait()
		var left int
		if err := kept.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM message_attachment WHERE message = ?`, id).Scan(&left); err != nil {
			t.Fatal(err)
		}
		if left != 0 {
			t.Fatalf("round %d: %d attachment(s) of a taken-back message stayed", round, left)
		}
	}
}

// A statement that fails takes the others back with it and is reported, rather than leaving half a deletion.
func TestForgetIsAllOrNothing(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "synthetic.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	kept.remember(ctx, 7, "m", 42, "synthetic", "", 1000)
	if _, err := kept.db.ExecContext(ctx, `CREATE TRIGGER refuse BEFORE DELETE ON encrypted_reaction BEGIN SELECT RAISE(ABORT, 'synthetic'); END`); err != nil {
		t.Fatal(err)
	}
	if _, err := kept.db.ExecContext(ctx, `INSERT INTO encrypted_reaction (message, actor, thread, emoji) VALUES ('m', 1, 7, x'00')`); err != nil {
		t.Fatal(err)
	}
	if kept.forget(ctx, 7, "m") == nil {
		t.Fatal("a failed deletion was not reported")
	}
	var messages, records int
	_ = kept.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM encrypted_message WHERE id = 'm'`).Scan(&messages)
	_ = kept.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM deleted_message WHERE id = 'm'`).Scan(&records)
	if messages != 1 || records != 0 {
		t.Fatalf("a failed deletion left half its work: message=%d record=%d", messages, records)
	}
}
