package main

import (
	"encoding/json"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix"
)

func stageRequest(t *testing.T, offset int, data []byte) request {
	t.Helper()
	raw, err := json.Marshal(map[string]any{"offset": offset, "data": data})
	if err != nil {
		t.Fatal(err)
	}
	return request{Method: "ui.mediaStage", Params: raw}
}

// The bytes of a file arrive a chunk at a time. They have to arrive in order and add up, or what gets sent is a
// muddle of two files rather than either of them.
func TestStagedFileArrivesInOrder(t *testing.T) {
	a := newAdapter(&output{})
	a.client = &messagix.Client{}
	if _, code := a.mediaStage(stageRequest(t, 0, []byte("hello "))); code != "" {
		t.Fatalf("first chunk: %s", code)
	}
	if _, code := a.mediaStage(stageRequest(t, 6, []byte("world"))); code != "" {
		t.Fatalf("second chunk: %s", code)
	}
	if string(a.upload) != "hello world" {
		t.Fatalf("staged %q", string(a.upload))
	}
	// A chunk that does not continue where the last one ended belongs to something else.
	if _, code := a.mediaStage(stageRequest(t, 99, []byte("!"))); code == "" {
		t.Error("a gap in the file should be refused")
	}
	// Beginning again replaces what was held, so an abandoned attempt is not sent as part of the next one.
	if _, code := a.mediaStage(stageRequest(t, 0, []byte("new"))); code != "" {
		t.Fatalf("restart: %s", code)
	}
	if string(a.upload) != "new" {
		t.Fatalf("a restart should replace what was held, got %q", string(a.upload))
	}
}

// Nothing staged is nothing to send, and saying so is better than uploading an empty file.
func TestSendingNothingIsRefused(t *testing.T) {
	a := newAdapter(&output{})
	a.client = &messagix.Client{}
	raw, _ := json.Marshal(map[string]any{"chatId": "7", "name": "x.jpg", "mime": "image/jpeg"})
	if _, code := a.sendMedia(t.Context(), request{Method: "ui.sendMedia", Params: raw}); code != "invalid_input" {
		t.Errorf("expected invalid_input, got %q", code)
	}
}
