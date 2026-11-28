package main

import (
	"bytes"
	"context"
	"image"
	"image/jpeg"
	"path/filepath"
	"testing"

	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	"google.golang.org/protobuf/proto"
)

func inlineThumbnail(t *testing.T) ([]byte, *waMediaTransport.WAMediaTransport) {
	t.Helper()
	var encoded bytes.Buffer
	if err := jpeg.Encode(&encoded, image.NewRGBA(image.Rect(0, 0, 4, 3)), nil); err != nil {
		t.Fatal(err)
	}
	data := encoded.Bytes()
	return data, &waMediaTransport.WAMediaTransport{
		Ancillary: &waMediaTransport.WAMediaTransport_Ancillary{
			Thumbnail: &waMediaTransport.WAMediaTransport_Ancillary_Thumbnail{JPEGThumbnail: data},
		},
	}
}

func TestEncryptedInlineLinkPreviewSurvivesRestartWithoutConnection(t *testing.T) {
	data, transport := inlineThumbnail(t)
	preview := &waConsumerApplication.ConsumerApplication_ExtendedTextMessage{
		CanonicalURL: proto.String("https://example.test/share"),
		Title:        proto.String("Shared link"),
	}
	if err := preview.SetThumbnail(&waMediaTransport.ImageTransport{
		Integral:  &waMediaTransport.ImageTransport_Integral{Transport: transport},
		Ancillary: &waMediaTransport.ImageTransport_Ancillary{Width: proto.Uint32(4), Height: proto.Uint32(3)},
	}); err != nil {
		t.Fatal(err)
	}
	item, held, _ := encryptedMedia(contentOf(&waConsumerApplication.ConsumerApplication_Content_ExtendedTextMessage{
		ExtendedTextMessage: preview,
	}), "inline-share")
	if item == nil || held == nil {
		t.Fatal("an inline link thumbnail must remain an addressable attachment")
	}
	if item.mime != "image/jpeg" || item.size != int64(len(data)) || item.link != "https://example.test/share" {
		t.Fatal("inline preview metadata must describe the actual JPEG and retain its browser destination")
	}
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "messages.db")
	db, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	db.remember(ctx, 7, "inline-share", 42, "", "", 1700)
	db.rememberMedia(ctx, 7, "inline-share", 1700, item, held)
	if err := db.db.Close(); err != nil {
		t.Fatal(err)
	}
	db, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.db.Close()
	s := newStore()
	db.restore(ctx, s)
	page := s.historyBefore(7, 0, 10)
	if len(page) != 1 || len(page[0].Media) != 1 || page[0].Media[0].Link != "https://example.test/share" {
		t.Fatal("the inline share and browser destination must return after reopening storage")
	}
	restored, ok := s.secureFor(item.id)
	if !ok {
		t.Fatal("the restored thumbnail must still have its stored bytes")
	}
	var offline adapter
	actual, mime, ok := offline.fetchSecure(ctx, restored)
	if !ok || mime != "image/jpeg" || !bytes.Equal(actual, data) {
		t.Fatal("inline thumbnail must be served without an encrypted connection")
	}
}

func TestEncryptedInlineThumbnailRejectsInvalidData(t *testing.T) {
	for _, data := range [][]byte{nil, []byte("not a JPEG"), {0xff, 0xd8, 0xff}} {
		transport := &waMediaTransport.WAMediaTransport{
			Ancillary: &waMediaTransport.WAMediaTransport_Ancillary{
				Thumbnail: &waMediaTransport.WAMediaTransport_Ancillary_Thumbnail{JPEGThumbnail: data},
			},
		}
		if directions(transport, "image") != nil {
			t.Fatal("invalid inline image bytes must not make an unavailable attachment addressable")
		}
		raw, err := proto.Marshal(transport)
		if err != nil {
			t.Fatal(err)
		}
		var offline adapter
		if _, _, ok := offline.fetchSecure(context.Background(), &secureMedia{kind: "image", transport: raw}); ok {
			t.Fatal("restored malformed thumbnails must not be served as images")
		}
	}
}

func TestEncryptedInlineThumbnailDoesNotReplaceOtherMedia(t *testing.T) {
	_, transport := inlineThumbnail(t)
	for _, kind := range []string{"document", "video", "audio"} {
		if directions(transport, kind) != nil {
			t.Fatalf("a %s cover image must not be offered as the file", kind)
		}
		raw, err := proto.Marshal(transport)
		if err != nil {
			t.Fatal(err)
		}
		var offline adapter
		if _, _, ok := offline.fetchSecure(context.Background(), &secureMedia{kind: kind, transport: raw}); ok {
			t.Fatalf("a stored %s cover image must not be served as the file", kind)
		}
	}
}

func TestEncryptedInlineThumbnailLeavesFullImageDownloadIntact(t *testing.T) {
	_, transport := inlineThumbnail(t)
	transport.Integral = &waMediaTransport.WAMediaTransport_Integral{DirectPath: proto.String("/full-image")}
	transport.Ancillary.Mimetype = proto.String("image/png")
	transport.Ancillary.FileLength = proto.Uint64(9876)
	item := describe("full-image", "Photo", transport)
	if item.mime != "image/png" || item.size != 9876 {
		t.Fatal("downloadable full image metadata must not be replaced with thumbnail metadata")
	}
	var offline adapter
	if _, _, ok := offline.fetchSecure(context.Background(), directions(transport, "image")); ok {
		t.Fatal("the full image path must still require a connection")
	}
}
