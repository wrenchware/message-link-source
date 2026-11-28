package main

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
)

// A photo body of zeros that counts what was read from it.
type countingBody struct{ left, read int }

func (b *countingBody) Read(p []byte) (int, error) {
	if b.left == 0 {
		return 0, io.EOF
	}
	n := min(len(p), b.left)
	b.left -= n
	b.read += n
	return n, nil
}

func (b *countingBody) Close() error { return nil }

// voice.google.com as far as a photo download sees it: every address answers a picture of size bytes.
type photoServer struct {
	mu     sync.Mutex
	size   int
	paths  []string
	bodies []*countingBody
}

func (s *photoServer) RoundTrip(req *http.Request) (*http.Response, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	body := &countingBody{left: s.size}
	s.paths = append(s.paths, req.URL.Path)
	s.bodies = append(s.bodies, body)
	return &http.Response{StatusCode: http.StatusOK, Header: http.Header{"Content-Type": {"image/png"}}, Body: body, Request: req}, nil
}

func mediaRead(a *adapter, id string) (any, string) {
	params, _ := json.Marshal(map[string]any{"id": id, "offset": 0})
	return a.callContext(context.Background(), request{Method: "ui.mediaRead", Params: params})
}

// libgv's DownloadAttachment read a photo's whole answer before its size could be refused. A photo is read no further
// than one byte past the 50 MiB limit.
func TestPhotoDownloadStopsAtTheLimit(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	client := newSessionClient(map[string]string{"SID": "synthetic"}, "")
	server := &photoServer{size: mediaLimit + 4*1024*1024}
	client.HTTP.Transport = &sendGuard{base: server}
	a.installClient(client)
	if _, code := mediaRead(a, "photo-1"); code != "media_unavailable" {
		t.Fatalf("a photo past the limit was answered %q", code)
	}
	if len(server.bodies) != 1 || server.bodies[0].read > mediaLimit+1 || server.paths[0] != "/u/0/a/i/photo-1" {
		t.Fatalf("read %d bytes of a photo past the %d byte limit from %v", server.bodies[0].read, mediaLimit, server.paths)
	}
	server.size = 1234
	result, code := mediaRead(a, "photo-2")
	if code != "" || result.(map[string]any)["total"] != 1234 || result.(map[string]any)["mime"] != "image/png" {
		t.Fatalf("a small photo was answered %v %q", result, code)
	}
}

// A photo's id goes into a voice.google.com path, as a voicemail's does, and is held to one path segment the same way.
func TestPhotoIDsStayOnePathSegment(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	a.installClient(newSessionClient(map[string]string{"SID": "synthetic"}, ""))
	asked := []string{}
	a.downloadAttachment = func(_ context.Context, _ *libgv.Client, id string) ([]byte, string, error) {
		asked = append(asked, id)
		return []byte("synthetic"), "image/png", nil
	}
	for _, id := range []string{"../v/x", "a/b", "a?s=1", "a#b", "a%2Fb", "a\\b", "a b", "a\n", "a\x00b", ".", "..", strings.Repeat("a", 1025)} {
		if _, code := mediaRead(a, id); code != "media_unavailable" {
			t.Fatalf("photo id %q was answered %q", id, code)
		}
	}
	if len(asked) != 0 {
		t.Fatalf("photo ids that could leave their path segment were asked for: %q", asked)
	}
	// Anything else goes in as upstream puts it, raw: base64 of either kind, padding and the unreserved marks.
	for _, id := range []string{"AbC123-_", "c.Ab1", "AbC==", "-_~", "a+b=", "a,b;c@d:e!f*g(h)j$k&l"} {
		if _, code := mediaRead(a, id); code != "" {
			t.Fatalf("photo id %q was refused: %q", id, code)
		}
	}
	if len(asked) != 6 {
		t.Fatalf("photo ids asked for: %q", asked)
	}
	for _, id := range asked {
		address, err := url.Parse(photoAddress("0", id))
		if err != nil || address.RawQuery != "" || address.Fragment != "" || !strings.HasSuffix(address.Path, "/"+id) ||
			strings.Count(address.Path, "/") != strings.Count(photoAddress("0", "x"), "/")-2 {
			t.Fatalf("photo id %q did not stay one raw segment: %v %v", id, address, err)
		}
	}
	for _, line := range sink.lines {
		if strings.Contains(line, "../") || strings.Contains(line, "a/b") {
			t.Fatalf("a refused photo id reached the pipe: %s", line)
		}
	}
}

// Sixteen media of up to 50 MiB each could be held, 800 MiB, for as long as the helper ran. What is held stays within
// 64 MiB, the most recently read kept, and nothing stays once nobody has read it for a minute.
func TestHeldMediaStaysWithinBytesAndExpires(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	defer func() {
		a.mediaMu.Lock()
		if a.mediaSweep != nil {
			a.mediaSweep.Stop()
		}
		a.mediaMu.Unlock()
	}()
	for i := range 3 {
		a.keep(&heldMedia{id: strconv.Itoa(i), data: make([]byte, 20*1024*1024)})
	}
	if a.cached("0") == nil {
		t.Fatal("three 20 MiB items fit within 64 MiB")
	}
	// "0" was read most recently, so the fourth lets "1" go.
	a.keep(&heldMedia{id: "3", data: make([]byte, 20*1024*1024)})
	a.mediaMu.Lock()
	held, ids := 0, map[string]bool{}
	for _, item := range a.media {
		held += len(item.data)
		ids[item.id] = true
	}
	a.mediaMu.Unlock()
	if held > mediaHeldBytes || !ids["0"] || ids["1"] || !ids["2"] || !ids["3"] {
		t.Fatalf("held %d bytes: %v", held, ids)
	}
	a.mediaMu.Lock()
	a.trimMedia(time.Now().Add(mediaIdle + time.Second))
	left := len(a.media)
	a.mediaMu.Unlock()
	if left != 0 {
		t.Fatalf("%d items stayed held after a minute unread", left)
	}
}

// Nothing else needs to happen for idle media to go: keeping one arms a sweep, which stays armed only while anything is held.
func TestMediaSweepLetsIdleMediaGo(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	a.keep(&heldMedia{id: "photo", data: []byte("synthetic")})
	a.mediaMu.Lock()
	armed := a.mediaSweep != nil
	a.mediaSweep.Stop()
	a.media[0].lastUse = time.Now().Add(-mediaIdle - time.Second)
	a.mediaMu.Unlock()
	if !armed {
		t.Fatal("keeping media armed no sweep")
	}
	a.sweepMedia()
	a.mediaMu.Lock()
	left, rearmed := len(a.media), a.mediaSweep != nil
	a.mediaMu.Unlock()
	if left != 0 || rearmed {
		t.Fatalf("after the sweep: %d held, sweep armed %v", left, rearmed)
	}
}

func TestPhotoTooLargeIsNotAnOutage(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	a.installClient(newSessionClient(map[string]string{"SID": "synthetic"}, ""))
	a.downloadAttachment = func(context.Context, *libgv.Client, string) ([]byte, string, error) {
		return nil, "", errPhotoTooLarge
	}
	if _, code := mediaRead(a, "photo"); code != "media_unavailable" {
		t.Fatalf("a photo past the limit was answered %q", code)
	}
	a.downloadAttachment = func(context.Context, *libgv.Client, string) ([]byte, string, error) {
		return nil, "", errors.New("offline")
	}
	if _, code := mediaRead(a, "photo"); code != "voice_unavailable" {
		t.Fatalf("an outage was answered %q", code)
	}
}
