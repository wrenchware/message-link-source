package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"testing"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

func TestPhotoIsStagedInOrderAndSentOnceWithItsCaption(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	call := func(method string, params any) (any, string) {
		raw, _ := json.Marshal(params)
		return a.callContext(context.Background(), request{Method: method, Params: raw})
	}
	stage := func(offset int, data []byte) string {
		_, code := call("ui.mediaStage", map[string]any{"offset": offset, "data": base64.StdEncoding.EncodeToString(data)})
		return code
	}
	if code := stage(0, []byte("x")); code != "login_required" {
		t.Fatalf("staging needs a signed-in client, got %q", code)
	}
	a.client = libgv.NewClient(map[string]string{"SID": "synthetic"})
	var sent []*gvproto.ReqSendSMS
	a.sendSMS = func(ctx context.Context, _ *libgv.Client, req *gvproto.ReqSendSMS) (*gvproto.RespSendSMS, error) {
		sent = append(sent, req)
		attempt := ctx.Value(sendAttemptKey{}).(*sendAttempt)
		attempt.attempts, attempt.status = 1, 200
		return &gvproto.RespSendSMS{ThreadItemID: "photo1"}, nil
	}
	photo := bytes.Repeat([]byte{7}, mediaChunk+10)
	if stage(0, photo[:mediaChunk]) != "" || stage(mediaChunk+1, photo[mediaChunk:]) != "invalid_input" {
		t.Fatal("a chunk that does not follow on must be refused")
	}
	if stage(0, photo[:mediaChunk]) != "" || stage(mediaChunk, photo[mediaChunk:]) != "" {
		t.Fatal("ordered chunks must stage")
	}
	if _, code := call("ui.sendMedia", map[string]any{"chatId": "t1", "mime": "image/webp", "text": ""}); code != "invalid_input" || len(sent) != 0 {
		t.Fatalf("a format Voice does not send must be refused before anything is sent: %q", code)
	}
	if _, code := call("ui.sendMedia", map[string]any{"chatId": "t1", "mime": "image/jpeg", "text": "caption"}); code != "invalid_input" || len(sent) != 0 {
		t.Fatalf("a refused send lets the staged photo go, so nothing is left to send: %q", code)
	}
	_ = stage(0, photo[:mediaChunk])
	_ = stage(mediaChunk, photo[mediaChunk:])
	result, code := call("ui.sendMedia", map[string]any{"chatId": "t1", "mime": "image/jpeg", "text": "caption"})
	if code != "" || result.(map[string]any)["id"] != "photo1" || len(sent) != 1 {
		t.Fatalf("expected one accepted photo: %v %q", result, code)
	}
	got := sent[0]
	if got.GetThreadID() != "t1" || got.GetText() != "caption" || got.GetMedia().GetType() != gvproto.ReqSendSMS_Media_JPEG ||
		!bytes.Equal(got.GetMedia().GetData(), photo) || got.GetTransactionID().GetID() == 0 {
		t.Fatalf("unexpected request: thread %q text %q type %v bytes %d", got.GetThreadID(), got.GetText(), got.GetMedia().GetType(), len(got.GetMedia().GetData()))
	}
	if _, code := call("ui.sendMedia", map[string]any{"chatId": "t1", "mime": "image/jpeg", "text": ""}); code != "invalid_input" || len(sent) != 1 {
		t.Fatalf("a sent photo must not be sendable again: %q", code)
	}
	big := bytes.Repeat([]byte{1}, mediaChunk)
	for offset := 0; offset+mediaChunk <= photoLimit; offset += mediaChunk {
		if stage(offset, big) != "" {
			t.Fatal("up to 2 MB must stage")
		}
	}
	if stage(photoLimit, []byte{1}) != "invalid_input" {
		t.Fatal("more than 2 MB must be refused")
	}
	for _, method := range []string{"ui.mediaStage", "ui.sendMedia"} {
		if !mutationMethod(method) {
			t.Fatalf("%s runs in order with other account changes", method)
		}
	}
}
