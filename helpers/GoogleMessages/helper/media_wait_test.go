package main

import (
	"context"
	"encoding/json"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"testing"
	"time"
)

// A picture that downloads quickly is answered by the read that started it. Every first read answered pending, and the
// app waits 500 ms after pending, so each picture's first bytes arrived 500 ms after it was asked for (2026-10-14).
func TestQuickDownloadAnswersItsFirstRead(t *testing.T) {
	a := &adapter{client: &libgm.Client{AuthData: libgm.NewAuthData()}, mediaRefs: map[string]*gmproto.MediaContent{"id": {MediaData: []byte("inline-picture")}}}
	params, _ := json.Marshal(map[string]any{"id": "id", "offset": 0})
	result, code := a.mediaRead(context.Background(), request{Params: params})
	if code != "" || result.(map[string]any)["pending"] == true || string(result.(map[string]any)["data"].([]byte)) != "inline-picture" {
		t.Fatalf("a picture already at hand was answered %v %q", result, code)
	}
	began := time.Now()
	result, code = a.downloadReadWait(context.Background(), mediaReadWait, "fifty", 0, false, 16, func() ([]byte, error) {
		time.Sleep(50 * time.Millisecond)
		return []byte("fifty-payload"), nil
	})
	if code != "" || result.(map[string]any)["pending"] == true || string(result.(map[string]any)["data"].([]byte)) != "fifty-payload" {
		t.Fatalf("a 50 ms download was answered %v %q", result, code)
	}
	if took := time.Since(began); took >= mediaReadWait {
		t.Fatalf("the read waited %v, past the download finishing", took)
	}
}

// A slow download still answers pending once the wait is over, other reads go on while it waits, and a request that
// ends stops its wait.
func TestWaitingReadLetsOthersOnAndEndsWithItsRequest(t *testing.T) {
	a := &adapter{}
	release := make(chan struct{})
	defer close(release)
	slow := func() ([]byte, error) { <-release; return []byte("slow"), nil }
	waited := make(chan time.Duration, 1)
	go func() {
		began := time.Now()
		result, code := a.downloadReadWait(context.Background(), mediaReadWait, "slow", 0, false, 16, slow)
		if code != "" || result.(map[string]any)["pending"] != true {
			t.Errorf("a slow download was answered %v %q", result, code)
		}
		waited <- time.Since(began)
	}()
	for deadline := time.Now().Add(time.Second); ; time.Sleep(time.Millisecond) {
		a.downloads.Lock()
		started := a.mediaJobs["slow"] != nil
		a.downloads.Unlock()
		if started {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("the slow download never started")
		}
	}
	result, code := a.downloadReadWait(context.Background(), mediaReadWait, "quick", 0, false, 16, func() ([]byte, error) { return []byte("quick"), nil })
	if code != "" || result.(map[string]any)["pending"] == true {
		t.Fatalf("a quick download behind a waiting read was answered %v %q", result, code)
	}
	select {
	case <-waited:
		t.Fatal("the quick read was held until the slow read's wait was over")
	default:
	}
	if took := <-waited; took < mediaReadWait {
		t.Fatalf("the slow read answered after %v, before its wait was over", took)
	}
	ended, cancel := context.WithCancel(context.Background())
	cancel()
	began := time.Now()
	result, code = a.downloadReadWait(ended, mediaReadWait, "slow", 0, false, 16, slow)
	if code != "" || result.(map[string]any)["pending"] != true || time.Since(began) >= mediaReadWait {
		t.Fatalf("a read whose request had ended waited %v: %v %q", time.Since(began), result, code)
	}
}
