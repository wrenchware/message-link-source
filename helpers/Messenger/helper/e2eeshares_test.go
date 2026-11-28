package main

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/whatsmeow/proto/armadilloutil"
	"go.mau.fi/whatsmeow/proto/waArmadilloApplication"
	"go.mau.fi/whatsmeow/proto/waArmadilloXMA"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	"go.mau.fi/whatsmeow/proto/waMsgApplication"
	waTypes "go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
)

func armadilloPreview(t *testing.T, transport *waMediaTransport.WAMediaTransport) *waCommon.SubProtocol {
	t.Helper()
	preview, err := armadilloutil.Marshal(&waMediaTransport.ImageTransport{
		Integral:  &waMediaTransport.ImageTransport_Integral{Transport: transport},
		Ancillary: &waMediaTransport.ImageTransport_Ancillary{Width: proto.Uint32(9), Height: proto.Uint32(16)},
	}, waConsumerApplication.ImageTransportVersion)
	if err != nil {
		t.Fatal(err)
	}
	return preview
}

// Exercise the actual application envelope codec before dispatch; a Consumer-only fixture cannot expose
// the silent GetConsumerApplication() == nil failure that lost these messages.
func armadilloEvent(t *testing.T, share *waArmadilloXMA.ExtendedContentMessage) *events.FBMessage {
	t.Helper()
	sub := &waMsgApplication.MessageApplication_SubProtocolPayload_Armadillo{}
	if err := sub.Set(&waArmadilloApplication.Armadillo{
		Payload: &waArmadilloApplication.Armadillo_Payload{Payload: &waArmadilloApplication.Armadillo_Payload_Content{Content: &waArmadilloApplication.Armadillo_Content{
			Content: &waArmadilloApplication.Armadillo_Content_ExtendedContentMessage{ExtendedContentMessage: share},
		}}},
	}); err != nil {
		t.Fatal(err)
	}
	app := &waMsgApplication.MessageApplication{
		Payload: &waMsgApplication.MessageApplication_Payload{Content: &waMsgApplication.MessageApplication_Payload_SubProtocol{
			SubProtocol: &waMsgApplication.MessageApplication_SubProtocolPayload{SubProtocol: sub},
		}},
		Metadata: &waMsgApplication.MessageApplication_Metadata{
			QuotedMessage: &waMsgApplication.MessageApplication_Metadata_QuotedMessage{StanzaID: proto.String("quoted-synthetic")},
		},
	}
	raw, err := proto.Marshal(app)
	if err != nil {
		t.Fatal(err)
	}
	var restored waMsgApplication.MessageApplication
	if err := proto.Unmarshal(raw, &restored); err != nil {
		t.Fatal(err)
	}
	decoded, err := restored.GetPayload().GetSubProtocol().SubProtocol.(*waMsgApplication.MessageApplication_SubProtocolPayload_Armadillo).Decode()
	if err != nil {
		t.Fatal(err)
	}
	return &events.FBMessage{Info: waTypes.MessageInfo{
		MessageSource: waTypes.MessageSource{Chat: waTypes.JID{User: "7"}, Sender: waTypes.JID{User: "42"}},
		ID:            "synthetic-share", Timestamp: time.UnixMilli(1700),
	}, Message: decoded, FBApplication: &restored}
}

func TestArmadilloShareThroughHandlerRestartAndMediaRead(t *testing.T) {
	for _, target := range []waArmadilloXMA.ExtendedContentMessage_ExtendedContentType{
		waArmadilloXMA.ExtendedContentMessage_FB_SHORT,
		waArmadilloXMA.ExtendedContentMessage_FB_FEED_SHARE,
		waArmadilloXMA.ExtendedContentMessage_MSG_EXTERNAL_LINK_SHARE,
	} {
		t.Run(target.String(), func(t *testing.T) {
			data, transport := inlineThumbnail(t)
			share := &waArmadilloXMA.ExtendedContentMessage{TargetType: target.Enum(),
				TitleText: proto.String("Synthetic title"),
				Ctas:      []*waArmadilloXMA.ExtendedContentMessage_CTA{{NativeURL: proto.String("https://example.test/reel")}},
				Previews:  []*waCommon.SubProtocol{armadilloPreview(t, transport)},
			}
			evt := armadilloEvent(t, share)
			if evt.GetConsumerApplication() != nil {
				t.Fatal("fixture must use the application omitted by the old handler")
			}
			ctx := context.Background()
			path := filepath.Join(t.TempDir(), "messages.db")
			db, err := openMessageStore(ctx, path)
			if err != nil {
				t.Fatal(err)
			}
			defer db.db.Close()
			a := &adapter{chats: newStore(), crypto: &encrypted{messages: db}, out: &output{encoder: json.NewEncoder(io.Discard)}}
			a.applyEncrypted(evt)
			assertShare := func(s *store) {
				t.Helper()
				page := s.historyBefore(7, 0, 10)
				if len(page) != 1 || len(page[0].Media) != 1 {
					t.Fatal("encrypted share must appear with its thumbnail")
				}
				media := page[0].Media[0]
				if media.Link != "https://example.test/reel" || media.Mime != "image/jpeg" || media.Video || media.Animated {
					t.Fatal("share must be a thumbnail carrying the browser destination")
				}
				// A zero-value ordinary client only satisfies the signed-in guard. Inline mediaRead must not call it.
				offline := &adapter{chats: s, client: &messagix.Client{}}
				params, _ := json.Marshal(map[string]any{"id": media.ID, "offset": 0})
				result, code := offline.mediaRead(ctx, request{Params: params})
				if code != "" {
					t.Fatalf("thumbnail could not cross the helper media boundary: %s", code)
				}
				chunk := result.(map[string]any)
				if chunk["mime"] != "image/jpeg" || !bytes.Equal(chunk["data"].([]byte), data) {
					t.Fatal("helper must return the original embedded JPEG bytes")
				}
			}
			assertShare(a.chats)
			if err := db.db.Close(); err != nil {
				t.Fatal(err)
			}
			db, err = openMessageStore(ctx, path)
			if err != nil {
				t.Fatal(err)
			}
			defer db.db.Close()
			back := newStore()
			db.restore(ctx, back)
			assertShare(back)
		})
	}
}

func TestArmadilloSharePreviewValidationAndLinkFallback(t *testing.T) {
	valid := armadilloPreview(t, carrier("/v/synthetic", "image/jpeg", 10))
	wrongVersion := proto.Clone(valid).(*waCommon.SubProtocol)
	wrongVersion.Version = proto.Int32(99)
	broken := &waCommon.SubProtocol{Version: proto.Int32(1), Payload: []byte{0xff}}
	share := &waArmadilloXMA.ExtendedContentMessage{Previews: []*waCommon.SubProtocol{wrongVersion, broken, valid, valid},
		Ctas: []*waArmadilloXMA.ExtendedContentMessage_CTA{{NativeURL: proto.String("fb://opaque"), ActionURL: proto.String("https://example.test/share")}},
	}
	items, _ := encryptedShare(share, "m")
	if len(items) != 2 || items[0].item.id == items[1].item.id || items[0].item.width != 9 || items[0].item.height != 16 {
		t.Fatal("valid previews must survive earlier invalid rows and retain distinct stable identities and size")
	}
	for _, item := range items {
		if item.item.link != "https://example.test/share" || item.held == nil {
			t.Fatal("each preview must have transport and web destination")
		}
	}
	share.Previews = share.Previews[:2]
	items, text := encryptedShare(share, "m")
	if len(items) != 0 || text != "https://example.test/share" {
		t.Fatal("undecodable pictures must preserve the supplied link as text")
	}
	share.Ctas[0].ActionURL = proto.String("javascript:invalid")
	_, text = encryptedShare(share, "m")
	if text == "" || strings.Contains(text, "javascript:") || strings.Contains(text, "fb://") {
		t.Fatal("missing pictures and unsupported destinations must not become a blank message or an unsafe link")
	}
}

func TestEncryptedShareDiagnosticsContainNoContent(t *testing.T) {
	path := filepath.Join(t.TempDir(), "synthetic.log")
	file, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	previous := diagnostics
	diagnostics = &logger{file: file}
	t.Cleanup(func() { closeLog(); diagnostics = previous })
	const secret = "PRIVATE_SYNTHETIC_SENTINEL"
	evt := armadilloEvent(t, &waArmadilloXMA.ExtendedContentMessage{
		TitleText: proto.String(secret), MessageText: proto.String(secret), TargetID: proto.String(secret),
		Ctas:     []*waArmadilloXMA.ExtendedContentMessage_CTA{{NativeURL: proto.String("https://example.test/" + secret)}},
		Previews: []*waCommon.SubProtocol{armadilloPreview(t, carrier("/v/"+secret, "image/jpeg", 10))},
	})
	evt.Info.ID = secret
	encryptedContents(evt)
	closeLog()
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Contains(raw, []byte("armadillo=true")) || !bytes.Contains(raw, []byte("thumbnails=1")) {
		t.Fatal("diagnostics must distinguish application dispatch and thumbnail assembly")
	}
	if bytes.Contains(raw, []byte(secret)) || bytes.Contains(raw, []byte("https://")) || bytes.Contains(raw, []byte("/v/")) {
		t.Fatal("diagnostics must not contain identifiers, text, titles or addresses")
	}
}

func TestArmadilloShareWithNoPictureBecomesACardOfWords(t *testing.T) {
	const link = "https://www.facebook.com/share/p/synthetic/"
	share := &waArmadilloXMA.ExtendedContentMessage{
		TitleText: proto.String("Synthetic post words"), HeaderTitle: proto.String("Synthetic author"),
		Ctas: []*waArmadilloXMA.ExtendedContentMessage_CTA{{ActionURL: proto.String(link)}},
	}
	items, text := encryptedShare(share, "m")
	if text != link {
		t.Fatal("the link must stay the message's words, so lists and notifications still read it")
	}
	if len(items) != 1 || items[0].held != nil {
		t.Fatal("a share with words and no picture must become one card with nothing to download")
	}
	card := items[0].item
	if card.name != "Synthetic post words" || card.description != "Synthetic author" || card.link != link || card.mime != "" {
		t.Fatal("the card must carry the share's title, its other line and its destination, and no media type")
	}
	if !pictureless(card.id, card.link, card.mime) || cardCovers(card.id, card.link, card.mime, link) {
		t.Fatal("a card of words must leave room for a card made from the page")
	}
	if title, description := shareCardWords(&waArmadilloXMA.ExtendedContentMessage{SubtitleText: proto.String("Only line"), HeaderTitle: proto.String("Only line")}); title != "Only line" || description != "" {
		t.Fatal("a line repeated across fields must not be drawn twice")
	}
	if items, _ := encryptedShare(&waArmadilloXMA.ExtendedContentMessage{Ctas: share.Ctas}, "m"); len(items) != 0 {
		t.Fatal("a share with no words must stay a plain link")
	}
	if items, _ := encryptedShare(&waArmadilloXMA.ExtendedContentMessage{TitleText: share.TitleText}, "m"); len(items) != 0 {
		t.Fatal("a card needs somewhere to lead")
	}

	// Whichever arrives first, the card made from the page (with its picture) is the one card left, and a card with a
	// picture from Facebook still wins over one made from the page.
	for _, wordsFirst := range []bool{true, false} {
		s := newStore()
		s.addEncrypted(7, "m", 4, text, "", 1700)
		if wordsFirst {
			s.attachSecure(7, "m", 1700, card, nil)
			if s.attachLinkPreview(7, "m", text, fixturePreview()) == nil {
				t.Fatal("a card of words must not stop the page's card")
			}
		} else {
			if s.attachLinkPreview(7, "m", text, fixturePreview()) == nil {
				t.Fatal("the page's card must attach")
			}
			s.attachSecure(7, "m", 1700, card, nil)
		}
		views := s.history(7, 0)
		if len(views) != 1 || len(views[0].Media) != 1 || !generatedLinkPreview(views[0].Media[0].ID) {
			t.Fatalf("wordsFirst=%v: expected only the page's card", wordsFirst)
		}
	}
	s := newStore()
	s.addEncrypted(7, "m", 4, text, "", 1700)
	s.attachSecure(7, "m", 1700, &attachment{id: "native-card", name: "Native share", mime: "image/jpeg", source: "https://example.test/native.jpg", link: link}, nil)
	if s.attachLinkPreview(7, "m", text, fixturePreview()) != nil {
		t.Fatal("a card with Facebook's own picture must still stop the page's card")
	}
}

func TestEncryptedShareCardOfWordsDiagnosticsContainNoContent(t *testing.T) {
	path := filepath.Join(t.TempDir(), "synthetic.log")
	file, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	previous := diagnostics
	diagnostics = &logger{file: file}
	t.Cleanup(func() { closeLog(); diagnostics = previous })
	const secret = "PRIVATE_SYNTHETIC_SENTINEL"
	encryptedShare(&waArmadilloXMA.ExtendedContentMessage{
		TitleText: proto.String(secret), HeaderTitle: proto.String(secret), SubtitleText: proto.String(secret),
		Ctas: []*waArmadilloXMA.ExtendedContentMessage_CTA{{ActionURL: proto.String("https://example.test/" + secret)}},
	}, secret)
	closeLog()
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Contains(raw, []byte("thumbnails=0")) || !bytes.Contains(raw, []byte("carded=true")) || !bytes.Contains(raw, []byte("header=true")) {
		t.Fatal("diagnostics must say a card of words was made and which fields were present")
	}
	if bytes.Contains(raw, []byte(secret)) || bytes.Contains(raw, []byte("https://")) {
		t.Fatal("diagnostics must not contain identifiers, text or addresses")
	}
}

func TestCardOfWordsReturnsWhenThePagesCardGoes(t *testing.T) {
	const link = "https://www.facebook.com/share/p/synthetic/"
	s := newStore()
	s.addEncrypted(7, "m", 4, link, "", 1700)
	s.attachSecure(7, "m", 1700, &attachment{id: "e2ee:m:share:card", name: "Synthetic post", link: link}, nil)
	generated := s.attachLinkPreview(7, "m", link, fixturePreview())
	if generated == nil {
		t.Fatal("the page's card must attach")
	}
	// What retryLinkPreview does when the page's picture cannot be read.
	list := s.attached["m"]
	for index, item := range list {
		if item.id == generated.id {
			s.attached["m"] = append(list[:index], list[index+1:]...)
			delete(s.media, generated.id)
			break
		}
	}
	views := s.history(7, 0)
	if len(views) != 1 || len(views[0].Media) != 1 || views[0].Media[0].ID != "e2ee:m:share:card" {
		t.Fatal("the card of words must show again once the page's card is gone")
	}
	if title, description := shareCardWords(&waArmadilloXMA.ExtendedContentMessage{SubtitleText: proto.String("Site"), HeaderTitle: proto.String("Author")}); title != "Author" || description != "Site" {
		t.Fatal("a header comes before a subtitle in the title")
	}
}
