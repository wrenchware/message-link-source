package main

import (
	"context"
	"slices"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func TestEncryptedSendTimeMatchesServerReadWatermarkAcrossRestart(t *testing.T) {
	ctx := context.Background()
	path := t.TempDir() + "/messages.db"
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	s := newStore()
	s.setSelf(1)
	server := time.Unix(1789420800, 0)
	// The computer clock can be ahead, and waiting for the ack takes time.
	dispatched := server.Add(3 * time.Second)
	at := encryptedSendTimestamp(server, dispatched)
	s.addEncrypted(7, "peer-before", 2, "before", "", server.Add(-time.Second).UnixMilli())
	s.addEncrypted(7, "sent", 1, "sent", "", at)
	kept.remember(ctx, 7, "sent", 1, "sent", "", at)
	kept.saveIdentity(ctx, s)
	kept.db.Close()
	kept, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	restored := newStore()
	restored.setSelf(1)
	if kept.restore(ctx, restored) != 1 {
		t.Fatal("send was not archived")
	}
	for _, current := range []*store{s, restored} {
		// Facebook's receipt covers the server send time, before local dispatch/ack time.
		current.apply(&table.LSTable{LSUpdateReadReceipt: []*table.LSUpdateReadReceipt{{
			ThreadKey: 7, ContactId: 2, ReadWatermarkTimestampMs: server.UnixMilli() + 200,
		}}})
		msg := current.messages[7]["sent"]
		if msg.timestamp != server.UnixMilli() || !slices.Contains(current.readersOf(7, msg), "2") ||
			!slices.Contains(current.readMarkOf(7, msg), "2") {
			t.Fatal("valid server receipt left the face before the sent message")
		}
	}
}

func TestEncryptedSendMissingServerTimeFallsBackToDispatch(t *testing.T) {
	dispatched := time.Unix(1789420800, 123000000)
	for _, absent := range []time.Time{{}, time.Unix(0, 0), time.Unix(-1, 0)} {
		if encryptedSendTimestamp(absent, dispatched) != dispatched.UnixMilli() {
			t.Fatal("missing server time did not preserve dispatch time")
		}
	}
}
