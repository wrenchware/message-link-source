package main

import (
	"testing"

	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	"google.golang.org/protobuf/proto"
)

func carrier(path, mime string, length uint64) *waMediaTransport.WAMediaTransport {
	integral := &waMediaTransport.WAMediaTransport_Integral{
		MediaKey:      []byte("key"),
		FileSHA256:    []byte("sha"),
		FileEncSHA256: []byte("enc"),
	}
	if path != "" {
		integral.DirectPath = proto.String(path)
	}
	return &waMediaTransport.WAMediaTransport{
		Integral:  integral,
		Ancillary: &waMediaTransport.WAMediaTransport_Ancillary{Mimetype: proto.String(mime), FileLength: proto.Uint64(length)},
	}
}

func contentOf(inner waConsumerApplication.ConsumerApplication_Content_Content) *waConsumerApplication.ConsumerApplication_Content {
	return &waConsumerApplication.ConsumerApplication_Content{Content: inner}
}

func TestEncryptedImageCarriesItsCaption(t *testing.T) {
	image := &waConsumerApplication.ConsumerApplication_ImageMessage{
		Caption: &waCommon.MessageText{Text: proto.String("at the lake")},
	}
	if err := image.Set(&waMediaTransport.ImageTransport{
		Integral:  &waMediaTransport.ImageTransport_Integral{Transport: carrier("/v/t1", "image/jpeg", 1234)},
		Ancillary: &waMediaTransport.ImageTransport_Ancillary{Width: proto.Uint32(800), Height: proto.Uint32(600)},
	}); err != nil {
		t.Fatalf("could not build the message: %v", err)
	}
	item, held, caption := encryptedMedia(contentOf(&waConsumerApplication.ConsumerApplication_Content_ImageMessage{ImageMessage: image}), "m1")
	if item == nil || held == nil {
		t.Fatalf("a picture with a path should be addressable, got item=%v held=%v", item, held)
	}
	if item.id != "e2ee:m1" || item.mime != "image/jpeg" || item.size != 1234 || item.width != 800 || item.height != 600 {
		t.Errorf("unexpected attachment: %+v", item)
	}
	if item.video || item.animated {
		t.Errorf("a photo is neither a video nor animated: %+v", item)
	}
	if caption != "at the lake" {
		t.Errorf("caption = %q", caption)
	}
	if held.kind != "image" || len(held.transport) == 0 {
		t.Errorf("unexpected directions: kind=%q bytes=%d", held.kind, len(held.transport))
	}
}

func TestEncryptedGifIsAnimatedRatherThanVideo(t *testing.T) {
	video := &waConsumerApplication.ConsumerApplication_VideoMessage{}
	if err := video.Set(&waMediaTransport.VideoTransport{
		Integral:  &waMediaTransport.VideoTransport_Integral{Transport: carrier("/v/t2", "video/mp4", 99)},
		Ancillary: &waMediaTransport.VideoTransport_Ancillary{GifPlayback: proto.Bool(true), Width: proto.Uint32(320), Height: proto.Uint32(240)},
	}); err != nil {
		t.Fatalf("could not build the message: %v", err)
	}
	item, held, _ := encryptedMedia(contentOf(&waConsumerApplication.ConsumerApplication_Content_VideoMessage{VideoMessage: video}), "m2")
	if item == nil || held == nil {
		t.Fatalf("a gif with a path should be addressable")
	}
	if !item.animated || item.video {
		t.Errorf("a gif plays by itself and is not a video: %+v", item)
	}
	if held.kind != "video" {
		t.Errorf("a gif is still fetched as a video: kind=%q", held.kind)
	}
}

func TestEncryptedMediaWithNoPathIsNotOffered(t *testing.T) {
	document := &waConsumerApplication.ConsumerApplication_DocumentMessage{FileName: proto.String("notes.pdf")}
	if err := document.Set(&waMediaTransport.DocumentTransport{
		Integral:  &waMediaTransport.DocumentTransport_Integral{Transport: carrier("", "application/pdf", 10)},
		Ancillary: &waMediaTransport.DocumentTransport_Ancillary{},
	}); err != nil {
		t.Fatalf("could not build the message: %v", err)
	}
	item, held, _ := encryptedMedia(contentOf(&waConsumerApplication.ConsumerApplication_Content_DocumentMessage{DocumentMessage: document}), "m3")
	if held != nil {
		t.Errorf("nothing can be fetched without a path, so no directions should be returned")
	}
	if item != nil && item.name != "notes.pdf" {
		t.Errorf("a document keeps its own name: %+v", item)
	}
}

func TestPlainTextCarriesNoAttachment(t *testing.T) {
	text := &waConsumerApplication.ConsumerApplication_Content_MessageText{
		MessageText: &waCommon.MessageText{Text: proto.String("hello")},
	}
	item, held, caption := encryptedMedia(contentOf(text), "m4")
	if item != nil || held != nil || caption != "" {
		t.Errorf("a text message has no attachment: item=%v held=%v caption=%q", item, held, caption)
	}
}

// A restart must not cost an encrypted conversation its pictures: what is written down has to come back as a real
// attachment on the message it belongs to, fetchable again.
func TestKeptAttachmentComesBackOnItsMessage(t *testing.T) {
	s := newStore()
	s.addEncrypted(7, "m5", 42, "", "", 1700)
	item := &attachment{id: "e2ee:m5", name: "Photo", mime: "image/jpeg", size: 10, width: 4, height: 3}
	s.attachSecure(7, "m5", 1700, item, &secureMedia{transport: []byte("directions"), kind: "image"})

	held, ok := s.secureFor("e2ee:m5")
	if !ok || held.kind != "image" {
		t.Fatalf("the directions should be found by attachment id, got %v %v", held, ok)
	}
	history := s.historyBefore(7, 0, 10)
	if len(history) != 1 {
		t.Fatalf("the message should be in the conversation, got %d", len(history))
	}
	if len(history[0].Media) != 1 || history[0].Media[0].ID != "e2ee:m5" {
		t.Fatalf("the attachment should hang on the message: %+v", history[0].Media)
	}
	// A conversation whose newest message is only a picture would otherwise read as blank in the list.
	chat, ok := s.chatByKey(7)
	if !ok || chat.Preview != "Photo" {
		t.Errorf("preview = %q", chat.Preview)
	}
}
