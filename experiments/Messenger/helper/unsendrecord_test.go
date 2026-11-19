package main

import (
	"context"
	"encoding/json"
	"io"
	"path/filepath"
	"sync/atomic"
	"testing"
	"time"

	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	waTypes "go.mau.fi/whatsmeow/types"
)

// An encrypted take-back Facebook accepted stays accepted when its deletion record cannot be written at once, and the
// record is written once it can: the revoke is never sent again. The failed record used to be dropped, and the stored
// copy put the message back at the next start.
func TestAcceptedEncryptedUnsendWritesItsRecordLater(t *testing.T) {
	previous := forgetRetryDelay
	forgetRetryDelay = 20 * time.Millisecond
	t.Cleanup(func() { forgetRetryDelay = previous })
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "synthetic.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	a := newAdapter(&output{encoder: json.NewEncoder(io.Discard)})
	defer a.stop()
	a.kept = kept
	a.chats.setSelf(1)
	a.chats.encryptedIdentity(7, 555, waTypes.MessengerServer)
	a.chats.mu.Lock()
	a.chats.hybrid[555] = 7
	a.chats.mu.Unlock()
	a.chats.addEncrypted(7, "m", 1, "synthetic", "", 1000)
	kept.remember(ctx, 7, "m", 1, "synthetic", "", 1000)
	a.crypto = &encrypted{messages: kept}
	var sends atomic.Int32
	a.revokeSend = func(context.Context, waTypes.JID, *waConsumerApplication.ConsumerApplication, string) error {
		sends.Add(1)
		return nil
	}
	if _, err := kept.db.ExecContext(ctx, `CREATE TRIGGER refuse BEFORE INSERT ON deleted_message BEGIN SELECT RAISE(ABORT, 'synthetic'); END`); err != nil {
		t.Fatal(err)
	}
	params, _ := json.Marshal(map[string]any{"chatId": "7", "messageId": "m", "everyone": true})
	result, code := a.deleteMessage(ctx, request{Params: params})
	if outcome, _ := result.(map[string]any); code != "" || outcome["outcome"] != "accepted" {
		t.Fatalf("an accepted take-back answered %v %q", result, code)
	}
	// A few tries fail before the store takes the record.
	time.Sleep(100 * time.Millisecond)
	if _, err := kept.db.ExecContext(ctx, `DROP TRIGGER refuse`); err != nil {
		t.Fatal(err)
	}
	for deadline := time.Now().Add(3 * time.Second); ; time.Sleep(20 * time.Millisecond) {
		var records, messages int
		_ = kept.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM deleted_message WHERE id = 'm'`).Scan(&records)
		_ = kept.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM encrypted_message WHERE id = 'm'`).Scan(&messages)
		if records == 1 && messages == 0 {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("the deletion record was never written: record=%d message=%d", records, messages)
		}
	}
	if sends.Load() != 1 {
		t.Fatalf("the take-back was sent %d times", sends.Load())
	}
	// And a restart does not bring it back.
	restored := newStore()
	kept.restore(ctx, restored)
	if restored.holdsMessage(7, "m") {
		t.Fatal("the taken-back message came back from the store")
	}
}
