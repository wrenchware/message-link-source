package main

import (
	"context"
	"encoding/json"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"strings"
	"testing"
)

func TestMediaMetadataKeepsKeysPrivateAndIdentityStable(t *testing.T) {
	c := &gmproto.MediaContent{MediaID: "media", MediaName: "photo.png", MimeType: "image/png", DecryptionKey: []byte("private-key"), Dimensions: &gmproto.Dimensions{Width: 640, Height: 480}}
	m := &gmproto.Message{ConversationID: "chat", MessageID: "message", MessageInfo: []*gmproto.MessageInfo{{Data: &gmproto.MessageInfo_MediaContent{MediaContent: c}}}}
	a := &adapter{}
	first := a.viewMessage(m)
	encoded, _ := json.Marshal(first)
	if len(first.Media) != 1 || first.Text != "" || first.Media[0].Width != 640 || strings.Contains(string(encoded), "private-key") || strings.Contains(string(encoded), "decryption") {
		t.Fatal("bad media metadata")
	}
	c.MediaName = "changed.png"
	if a.viewMessage(m).Media[0].ID != first.Media[0].ID {
		t.Fatal("metadata update changed media identity")
	}
	c.MediaID = "replacement"
	if a.viewMessage(m).Media[0].ID == first.Media[0].ID {
		t.Fatal("replacement reused identity")
	}
}
func TestMediaChunksAndStageBounds(t *testing.T) {
	a := &adapter{client: &libgm.Client{}, mediaRefs: map[string]*gmproto.MediaContent{"id": {MediaData: make([]byte, mediaChunk+9)}}}
	requestFor := func(method string, p any) request { b, _ := json.Marshal(p); return request{Method: method, Params: b} }
	result, code := a.mediaRead(context.Background(), requestFor("ui.mediaRead", map[string]any{"id": "id", "offset": 0}))
	if result.(map[string]any)["pending"] == true {
		<-a.mediaJobs["id"].done
		result, code = a.mediaRead(context.Background(), requestFor("ui.mediaRead", map[string]any{"id": "id", "offset": 0}))
	}
	if code != "" || result.(map[string]any)["next"] != mediaChunk {
		t.Fatal("first chunk")
	}
	result, code = a.mediaRead(context.Background(), requestFor("ui.mediaRead", map[string]any{"id": "id", "offset": mediaChunk}))
	if code != "" || len(result.(map[string]any)["data"].([]byte)) != 9 {
		t.Fatal("last chunk")
	}
	if _, code = a.mediaRead(context.Background(), requestFor("ui.mediaRead", map[string]any{"id": "id", "offset": mediaChunk + 10})); code == "" {
		t.Fatal("invalid offset")
	}
	if _, code = a.mediaStage(requestFor("ui.mediaStage", map[string]any{"offset": 0, "data": []byte("abc")})); code != "" {
		t.Fatal("stage")
	}
	if _, code = a.mediaStage(requestFor("ui.mediaStage", map[string]any{"offset": 5, "data": []byte("x")})); code == "" {
		t.Fatal("out of order stage")
	}
}
