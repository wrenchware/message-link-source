package main

import (
	"context"
	"path/filepath"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	"go.mau.fi/whatsmeow/proto/armadilloutil"
	"go.mau.fi/whatsmeow/proto/waArmadilloXMA"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
)

func retainedShareFixture(t *testing.T) *waArmadilloXMA.ExtendedContentMessage {
	t.Helper()
	preview, err := armadilloutil.Marshal(&waMediaTransport.ImageTransport{
		Integral: &waMediaTransport.ImageTransport_Integral{Transport: carrier("/v/synthetic", "image/jpeg", 123)},
		Ancillary: &waMediaTransport.ImageTransport_Ancillary{
			Width: proto.Uint32(9), Height: proto.Uint32(16),
		},
	}, waConsumerApplication.ImageTransportVersion)
	if err != nil {
		t.Fatal("could not construct synthetic share preview")
	}
	return &waArmadilloXMA.ExtendedContentMessage{
		TitleText:   proto.String("Synthetic shared reel"),
		MessageText: proto.String("Synthetic share caption"),
		Ctas: []*waArmadilloXMA.ExtendedContentMessage_CTA{
			{NativeURL: proto.String("https://example.test/reel")},
		},
		Previews: []*waCommon.SubProtocol{preview},
	}
}

func TestEncryptedShareReprocessesRetainedPayloadAfterReopen(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "messages.db")
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	// Simulate an earlier decoder that retained the received share but extracted no text or thumbnail.
	if err := kept.rememberChecked(ctx, 555, "retained-share", 42, "", "quoted-message", 1700); err != nil {
		t.Fatal(err)
	}
	content := retainedShareFixture(t)
	unknown := protowire.AppendTag(nil, 999, protowire.VarintType)
	unknown = protowire.AppendVarint(unknown, 7)
	content.ProtoReflect().SetUnknown(unknown)
	kept.rememberShare(ctx, "retained-share", content)
	if err := kept.db.Close(); err != nil {
		t.Fatal(err)
	}
	kept, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	var payload []byte
	if err := kept.db.QueryRowContext(ctx, `SELECT payload FROM encrypted_share WHERE message = 'retained-share'`).Scan(&payload); err != nil {
		t.Fatal(err)
	}
	payload, err = unprotectLocal(payload)
	if err != nil {
		t.Fatal("retained share payload was not protected")
	}
	var decoded waArmadilloXMA.ExtendedContentMessage
	if proto.Unmarshal(payload, &decoded) != nil || !proto.Equal(content, &decoded) {
		t.Fatal("retention must preserve the received schema, including unknown fields")
	}
	back := newStore()
	if kept.restore(ctx, back) != 1 {
		t.Fatal("the retained share's parent message was lost")
	}
	page := back.history(555, 0)
	if len(page) != 1 || len(page[0].Media) != 1 || page[0].Text != content.GetMessageText() || page[0].ReplyTo != "quoted-message" {
		t.Fatal("reprocessing the retained payload did not repair the blank message")
	}
	media := page[0].Media[0]
	if media.Link != "https://example.test/reel" || media.ID != "e2ee:retained-share:share:0" || media.Width != 9 || media.Height != 16 {
		t.Fatal("the restored share lost its thumbnail dimensions or browser destination")
	}
	if held, ok := back.secureFor(media.ID); !ok || held.kind != "image" || len(held.transport) == 0 {
		t.Fatal("the restored share lost its encrypted thumbnail directions")
	}
	// Pairing can still arrive after the encrypted messages are restored. The payload joins its existing parent
	// message, so moving that message into its ordinary thread carries the decoded attachment with it.
	back.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: 7, LastActivityTimestampMs: 1700, ThreadType: table.ENCRYPTED_OVER_WA_ONE_TO_ONE},
		},
		LSVerifyHybridThreadExists: []*table.LSVerifyHybridThreadExists{{ThreadKey: 7, ThreadJID: 555}},
	})
	page = back.history(7, 0)
	if len(page) != 1 || len(page[0].Media) != 1 || page[0].Media[0].ID != media.ID || len(back.history(555, 0)) != 0 {
		t.Fatal("restored share failed to move with its message when the thread pairing arrived")
	}
}

func TestEncryptedShareRetentionUpdatesExistingMediaWithoutDuplicating(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	kept.remember(ctx, 7, "share", 42, "", "", 1700)
	content := retainedShareFixture(t)
	items, _ := encryptedShare(content, "share")
	if len(items) != 1 {
		t.Fatal("synthetic share must produce one thumbnail")
	}
	kept.rememberMedia(ctx, 7, "share", 1700, items[0].item, items[0].held)
	kept.rememberShare(ctx, "share", content)
	content.Ctas[0].NativeURL = proto.String("https://example.test/reel-updated")
	content.Ctas[0].Title = proto.String(" View listing ")
	kept.rememberShare(ctx, "share", content)
	back := newStore()
	kept.restore(ctx, back)
	page := back.history(7, 0)
	if len(page) != 1 || len(page[0].Media) != 1 || page[0].Media[0].Link != content.Ctas[0].GetNativeURL() {
		t.Fatal("re-decoding retained share must update the earlier thumbnail in place")
	}
	if got := page[0].Media[0].ActionTitle; got != "View listing" {
		t.Fatalf("an already kept share did not gain its button's words: %q", got)
	}
}

func TestEncryptedShareRetentionHonorsDeletionsAndOrphans(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	content := retainedShareFixture(t)
	kept.remember(ctx, 7, "deleted-share", 42, "", "", 1700)
	kept.rememberShare(ctx, "deleted-share", content)
	kept.forget(ctx, 7, "deleted-share")
	var count int
	if err := kept.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM encrypted_share`).Scan(&count); err != nil || count != 0 {
		t.Fatal("forgetting a message must remove its retained share")
	}
	kept.rememberShare(ctx, "deleted-share", content)
	if err := kept.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM encrypted_share`).Scan(&count); err != nil || count != 0 {
		t.Fatal("a repeated event must not retain a deleted message's payload again")
	}
	// An orphaned payload and a tombstoned parent must not manufacture messages during reprocessing.
	kept.rememberShare(ctx, "orphan", content)
	kept.remember(ctx, 7, "deleted-share", 42, "", "", 1700)
	payload, err := proto.Marshal(content)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := kept.db.ExecContext(ctx, `INSERT INTO encrypted_share VALUES ('deleted-share', ?)`, payload); err != nil {
		t.Fatal(err)
	}
	back := newStore()
	kept.restore(ctx, back)
	if len(back.history(7, 0)) != 0 {
		t.Fatal("retained payloads resurrected an orphan or deleted share")
	}
	if _, ok := back.secureFor("e2ee:deleted-share:share:0"); ok {
		t.Fatal("a tombstoned payload must not be decoded into media")
	}
}

func TestEncryptedShareRetentionMalformedPayloadKeepsExistingMessage(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	kept.remember(ctx, 7, "malformed-share", 42, "existing synthetic caption", "", 1700)
	if _, err := kept.db.ExecContext(ctx, `INSERT INTO encrypted_share VALUES ('malformed-share', ?)`, []byte{0xff}); err != nil {
		t.Fatal(err)
	}
	back := newStore()
	kept.restore(ctx, back)
	page := back.history(7, 0)
	if len(page) != 1 || page[0].Text != "existing synthetic caption" {
		t.Fatal("an undecodable retained share must not discard its previously decoded message")
	}
}
