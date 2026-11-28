package main

import (
	"context"
	"encoding/json"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"testing"
)

// A received video over 50 MB could not be opened once the server had to fetch it (2026-10-12): what is received is held to the 100 MB Google
// Messages sends, and only sending to the 50 MB the app lets a file be.
func TestReceivedMediaUpToGooglesLimitIsRead(t *testing.T) {
	read := func(size int64) string {
		job := startMediaDownload("id", func() ([]byte, error) { return []byte("synthetic"), nil })
		<-job.done
		a := adapter{client: &libgm.Client{AuthData: libgm.NewAuthData()}, mediaJobs: map[string]*mediaDownload{"id": job},
			mediaRefs: map[string]*gmproto.MediaContent{"id": {Size: size}}}
		params, _ := json.Marshal(map[string]any{"id": "id", "offset": 0})
		_, code := a.mediaRead(context.Background(), request{Params: params})
		return code
	}
	if code := read(60 * 1024 * 1024); code != "" {
		t.Fatalf("a 60 MB video was refused: %s", code)
	}
	if code := read(downloadLimit + 1); code != "media_unavailable" {
		t.Fatalf("a video past 100 MB was read: %q", code)
	}
	if mediaLimit != 50*1024*1024 {
		t.Fatal("the sending limit changed")
	}
}
