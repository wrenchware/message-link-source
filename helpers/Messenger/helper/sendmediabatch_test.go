package main

import (
	"bytes"
	"encoding/json"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waArmadilloApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
)

func batchStageRequest(t *testing.T, index, offset int, reset bool, data []byte) request {
	t.Helper()
	raw, err := json.Marshal(map[string]any{"index": index, "offset": offset, "reset": reset, "data": data})
	if err != nil {
		t.Fatal(err)
	}
	return request{Method: "ui.mediaBatchStage", Params: raw}
}

func TestPhotoBatchStagesInFileAndChunkOrder(t *testing.T) {
	a := newAdapter(&output{})
	for _, part := range []struct {
		index, offset int
		reset         bool
		data          string
	}{{0, 0, true, "first-"}, {0, 6, false, "photo"}, {1, 0, false, "second"}} {
		if _, code := a.mediaBatchStage(batchStageRequest(t, part.index, part.offset, part.reset, []byte(part.data))); code != "" {
			t.Fatalf("stage %d/%d failed: %s", part.index, part.offset, code)
		}
	}
	if len(a.batchUploads) != 2 || string(a.batchUploads[0]) != "first-photo" || string(a.batchUploads[1]) != "second" {
		t.Fatalf("staged batch was %#v", a.batchUploads)
	}
	if _, code := a.mediaBatchStage(batchStageRequest(t, 3, 0, false, []byte("gap"))); code == "" {
		t.Fatal("a skipped file index was accepted")
	}
	if _, code := a.mediaBatchStage(batchStageRequest(t, 0, 0, true, []byte("replacement"))); code != "" || len(a.batchUploads) != 1 || string(a.batchUploads[0]) != "replacement" {
		t.Fatal("reset did not discard the abandoned batch")
	}
	a.mediaBatchDiscard()
	if a.batchUploads != nil {
		t.Fatal("discard retained staged bytes")
	}
}

func TestOrdinaryPhotoBatchUsesOneMessageTask(t *testing.T) {
	tasks := mediaBatchTasks(7, 9, []int64{101, 102, 103}, 11)
	if len(tasks) != 2 {
		t.Fatalf("got %d tasks", len(tasks))
	}
	sent, ok := tasks[0].(*socket.SendMessageTask)
	if !ok || sent.ThreadId != 7 || sent.Otid != 9 || len(sent.AttachmentFBIds) != 3 || sent.AttachmentFBIds[2] != 103 {
		t.Fatalf("photo ids were not placed in one message task: %#v", tasks[0])
	}
}

func TestReceivedEncryptedGalleryRetainsEveryPhoto(t *testing.T) {
	gallery := &waArmadilloApplication.Armadillo_Content_ImageGalleryMessage{}
	images := make([]*whatsmeow.UploadResponse, 2)
	for index := range images {
		images[index] = &whatsmeow.UploadResponse{DirectPath: "/v/photo", ObjectID: "1", MediaKey: bytes.Repeat([]byte{1}, 32),
			FileEncSHA256: bytes.Repeat([]byte{2}, 32), FileSHA256: bytes.Repeat([]byte{3}, 32)}
	}
	if err := gallery.Set([]*waMediaTransport.ImageTransport{
		wrapEncryptedImageTransport(images[0], []byte("one"), "image/jpeg", 640, 480),
		wrapEncryptedImageTransport(images[1], []byte("two"), "image/png", 320, 700),
	}); err != nil {
		t.Fatal(err)
	}
	items := encryptedGallery(gallery, "message")
	if len(items) != 2 || items[0].item.id == items[1].item.id || items[0].item.width != 640 || items[1].item.height != 700 {
		t.Fatalf("gallery did not return two ordered photos: %#v", items)
	}
}

func TestEncryptedPhotoBatchMetadataLinksEveryNativeImage(t *testing.T) {
	first := mediaBatchMetadata("set", 4, 0)
	last := mediaBatchMetadata("set", 4, 3)
	if first.GetGroupID() != "set" || last.GetGroupID() != "set" || first.GetGroupSize() != 4 || last.GetGroupSize() != 4 ||
		first.GroupIndex == nil || first.GetGroupIndex() != 0 || last.GetGroupIndex() != 3 {
		t.Fatalf("invalid photo grouping metadata: first=%#v last=%#v", first, last)
	}
}
