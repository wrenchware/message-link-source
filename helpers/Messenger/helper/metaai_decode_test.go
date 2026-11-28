package main

import (
	"context"
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	"go.mau.fi/whatsmeow/proto/waArmadilloApplication"
	"go.mau.fi/whatsmeow/proto/waMsgApplication"
	waTypes "go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
)

func aiReplyEvent(t *testing.T, text string, at int64) *events.FBMessage {
	t.Helper()
	sub := &waMsgApplication.MessageApplication_SubProtocolPayload_Armadillo{}
	if err := sub.Set(&waArmadilloApplication.Armadillo{Payload: &waArmadilloApplication.Armadillo_Payload{
		Payload: &waArmadilloApplication.Armadillo_Payload_ApplicationData{ApplicationData: &waArmadilloApplication.Armadillo_ApplicationData{
			ApplicationData: &waArmadilloApplication.Armadillo_ApplicationData_AiBotResponse{AiBotResponse: &waArmadilloApplication.Armadillo_ApplicationData_AIBotResponseMessage{
				MessageText: proto.String(text), SerializedExtras: proto.String("synthetic opaque extras"), SummonToken: proto.String("synthetic token"),
			}},
		}},
	}}); err != nil {
		t.Fatal(err)
	}
	app := &waMsgApplication.MessageApplication{
		Payload: &waMsgApplication.MessageApplication_Payload{Content: &waMsgApplication.MessageApplication_Payload_SubProtocol{
			SubProtocol: &waMsgApplication.MessageApplication_SubProtocolPayload{SubProtocol: sub},
		}},
		Metadata: &waMsgApplication.MessageApplication_Metadata{QuotedMessage: &waMsgApplication.MessageApplication_Metadata_QuotedMessage{StanzaID: proto.String("synthetic-quote")}},
	}
	wire, err := proto.Marshal(app)
	if err != nil {
		t.Fatal(err)
	}
	var decoded waMsgApplication.MessageApplication
	if err = proto.Unmarshal(wire, &decoded); err != nil {
		t.Fatal(err)
	}
	message, err := decoded.GetPayload().GetSubProtocol().SubProtocol.(*waMsgApplication.MessageApplication_SubProtocolPayload_Armadillo).Decode()
	if err != nil {
		t.Fatal(err)
	}
	return &events.FBMessage{Info: waTypes.MessageInfo{
		MessageSource: waTypes.MessageSource{Chat: waTypes.JID{User: "7", Server: waTypes.MessengerServer}, Sender: waTypes.JID{User: "156025504001094", Server: waTypes.MessengerServer}},
		ID:            "synthetic-ai", Timestamp: time.UnixMilli(at),
	}, Message: message, FBApplication: &decoded}
}

func TestAIReplyWireDecodeUpdatePersistenceAndDeletion(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "synthetic.db")
	db, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	a := &adapter{chats: newStore(), crypto: &encrypted{messages: db}, out: &output{encoder: json.NewEncoder(io.Discard)}}
	a.chats.setSelf(1)
	assertReply := func(s *store, expected string) {
		t.Helper()
		page := s.historyBefore(7, 0, 10)
		if len(page) != 1 || page[0].Text != expected || page[0].At.UnixMilli() != 1000 || page[0].ReplyTo != "synthetic-quote" {
			t.Fatal("AI answer must occupy one stable quoted row with its original timestamp")
		}
	}
	a.applyEncrypted(aiReplyEvent(t, " \n", 1000))
	assertReply(a.chats, metaAIUnavailableReply)
	a.applyEncrypted(aiReplyEvent(t, "Synthetic first answer", 1000))
	assertReply(a.chats, "Synthetic first answer")
	next := aiReplyEvent(t, "Synthetic complete answer\nSecond line", 2000)
	next.FBApplication.Metadata = nil // a partial delivery does not erase the original quote
	a.applyEncrypted(next)
	assertReply(a.chats, "Synthetic complete answer\nSecond line")
	for _, evt := range []*events.FBMessage{aiReplyEvent(t, "Older", 1500), aiReplyEvent(t, "", 3000), aiReplyEvent(t, "Equal conflicting", 2000), next} {
		a.applyEncrypted(evt)
		assertReply(a.chats, "Synthetic complete answer\nSecond line")
	}
	a.chats.apply(&table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "synthetic-ai", SenderId: metaAIMessengerID, TimestampMs: 1000}}})
	assertReply(a.chats, "Synthetic complete answer\nSecond line")
	if err = db.db.Close(); err != nil {
		t.Fatal(err)
	}
	db, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.db.Close()
	back := newStore()
	back.setSelf(1)
	db.restore(ctx, back)
	a.chats, a.crypto.messages = back, db
	assertReply(back, "Synthetic complete answer\nSecond line")
	a.applyEncrypted(aiReplyEvent(t, "Stale after restart", 1500))
	assertReply(back, "Synthetic complete answer\nSecond line")
	back.applyEdit(7, "synthetic-ai", "Synthetic edited answer", 3000)
	db.rememberEdit(ctx, "synthetic-ai", "Synthetic edited answer", 3000)
	back.applyEdit(7, "synthetic-ai", "Stale edit", 2500)
	db.rememberEdit(ctx, "synthetic-ai", "Stale edit", 2500)
	assertReply(back, "Synthetic edited answer")
	edited := newStore()
	db.restore(ctx, edited)
	a.chats = edited
	a.applyEncrypted(aiReplyEvent(t, "Late original after edit", 4000))
	assertReply(edited, "Synthetic edited answer")
	back = edited
	back.removeEncrypted(7, "synthetic-ai")
	db.forget(ctx, 7, "synthetic-ai")
	a.applyEncrypted(aiReplyEvent(t, "Late deleted answer", 4000))
	if len(back.historyBefore(7, 0, 10)) != 0 {
		t.Fatal("late delivery resurrected deleted AI reply")
	}
	restored := newStore()
	db.restore(ctx, restored)
	a.chats = restored
	a.applyEncrypted(aiReplyEvent(t, "Late after deleted restart", 5000))
	if len(restored.historyBefore(7, 0, 10)) != 0 {
		t.Fatal("restart resurrected deleted AI reply")
	}
}

func TestAIReplyDecoderPreservesTextAndIgnoresOpaqueExtras(t *testing.T) {
	for _, text := range []string{"", " \n\t", "Synthetic answer 🌍\nsecond line"} {
		items, decoded := encryptedContents(aiReplyEvent(t, text, 1000))
		if decoded != text || len(items) != 0 {
			t.Fatal("AI decoding must preserve only defined messageText")
		}
	}
}

func TestAIShapeDiagnosticsContainNoPayloadValues(t *testing.T) {
	path := filepath.Join(t.TempDir(), "synthetic.log")
	t.Setenv("MESSAGELINK_MESSENGER_LOG", path)
	openLog()
	defer closeLog()
	evt := aiReplyEvent(t, "Private synthetic answer", 1000)
	encryptedContents(evt)
	s := newStore()
	s.setSelf(1)
	s.addEncrypted(7, evt.Info.ID, metaAIMessengerID, "Private synthetic answer", "", 1000)
	s.addEncrypted(7, evt.Info.ID, metaAIMessengerID, "Private synthetic updated answer", "", 2000)
	s.apply(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 7, MessageId: "private-ordinary-id", SenderId: metaAIMessengerID, BotResponseID: 987654321, Text: "Private ordinary answer"}}})
	closeLog()
	log, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	for _, value := range []string{"Private", evt.Info.ID, "private-ordinary-id", "156025504001094", "987654321", "synthetic opaque extras", "synthetic token"} {
		if strings.Contains(string(log), value) {
			t.Fatal("diagnostic recorded a payload value")
		}
	}
	for _, value := range []string{"connection=encrypted", "connection=lightspeed", "text=true", "extras=true", "accepted=true", "botResponse=true"} {
		if !strings.Contains(string(log), value) {
			t.Fatal("diagnostic omitted shape/update presence")
		}
	}
}
