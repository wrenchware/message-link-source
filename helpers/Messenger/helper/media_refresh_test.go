package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
)

type mediaTestTransport func(*http.Request) (*http.Response, error)

func (f mediaTestTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

func videoMediaRows(source string) *table.LSTable {
	return &table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "old-video", SenderId: 1, TimestampMs: 1700}},
		LSInsertBlobAttachment: []*table.LSInsertBlobAttachment{{ThreadKey: 7, MessageId: "old-video", AttachmentFbid: "video", AttachmentType: table.AttachmentTypeVideo,
			AttachmentMimeType: "video/mp4", PlayableUrl: source, PreviewUrl: source + "/preview"}},
	}
}

func expiredVideoFixture(t *testing.T, status int) (*adapter, *atomic.Int32) {
	t.Helper()
	a := newAdapter(&output{})
	t.Cleanup(a.stop)
	a.client = messagix.NewClient(&cookies.Cookies{Platform: types.Facebook}, zerolog.Nop(), &messagix.Config{})
	requests := &atomic.Int32{}
	a.client.GetHTTP().HTTP = &http.Client{Transport: mediaTestTransport(func(r *http.Request) (*http.Response, error) {
		requests.Add(1)
		code, body := status, []byte("expired")
		if r.URL.Path == "/fresh" || r.URL.Path == "/fresh/preview" {
			code, body = http.StatusOK, bytes.Repeat([]byte{42}, mediaChunk+17)
		}
		return &http.Response{StatusCode: code, Header: http.Header{"Content-Type": {"video/mp4"}}, Body: io.NopCloser(bytes.NewReader(body)), Request: r}, nil
	})}
	a.chats.apply(videoMediaRows("https://scontent.xx.fbcdn.net/expired"))
	// A newest-page refresh must not stand in for the old video that playback actually needs.
	a.chats.addEncrypted(7, "newer", 1, "synthetic", "", 9900)
	return a, requests
}

func TestExpiredVideoRenewsDuringPlaybackAndKeepsChunks(t *testing.T) {
	for _, status := range []int{http.StatusForbidden, http.StatusNotFound, http.StatusGone} {
		t.Run(http.StatusText(status), func(t *testing.T) {
			a, requests := expiredVideoFixture(t, status)
			fetches := 0
			a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
				fetches++
				if key != 7 || anchor.id != "newer" || anchor.at != 9900 {
					t.Fatal("renewal did not include the video before its newer neighbour")
				}
				return videoMediaRows("https://scontent.xx.fbcdn.net/fresh"), nil
			}
			first, code := a.mediaRead(context.Background(), request{Params: json.RawMessage(`{"id":"video"}`)})
			if code != "" || first == nil {
				t.Fatalf("playback failed after renewal: %s", code)
			}
			chunk := first.(map[string]any)
			if len(chunk["data"].([]byte)) != mediaChunk || chunk["total"] != mediaChunk+17 || chunk["mime"] != "video/mp4" {
				t.Fatal("renewed video was not returned as a complete chunked download")
			}
			last, code := a.mediaRead(context.Background(), request{Params: json.RawMessage(`{"id":"video","offset":196608}`)})
			if code != "" || len(last.(map[string]any)["data"].([]byte)) != 17 || requests.Load() != 2 || fetches != 1 {
				t.Fatal("later chunks refetched the video or its history")
			}
		})
	}
}

func TestExpiredVideoPreviewAlsoRenewsItsOwnMessage(t *testing.T) {
	a, _ := expiredVideoFixture(t, http.StatusForbidden)
	a.fetch = func(context.Context, int64, pageAnchor) (*table.LSTable, error) {
		return videoMediaRows("https://scontent.xx.fbcdn.net/fresh"), nil
	}
	if result, code := a.mediaRead(context.Background(), request{Params: json.RawMessage(`{"id":"video:preview"}`)}); code != "" || result == nil {
		t.Fatalf("preview did not recover: %s", code)
	}
}

func TestFailedVideoRenewalIsPacedAndDoesNotRetryExpiredDownload(t *testing.T) {
	a, requests := expiredVideoFixture(t, http.StatusForbidden)
	fetches := 0
	a.fetch = func(context.Context, int64, pageAnchor) (*table.LSTable, error) {
		fetches++
		return nil, errors.New("offline synthetic refusal")
	}
	for range 2 {
		if _, code := a.mediaRead(context.Background(), request{Params: json.RawMessage(`{"id":"video"}`)}); code != "media_unavailable" {
			t.Fatal("a refused renewal must remain unavailable")
		}
	}
	if fetches != 1 || requests.Load() != 2 {
		t.Fatal("a failed renewal looped or retried the same expired address")
	}
}

func TestMediaRenewalWaitsForExactAttachmentNotOtherHistory(t *testing.T) {
	a, _ := expiredVideoFixture(t, http.StatusForbidden)
	a.fetch = func(context.Context, int64, pageAnchor) (*table.LSTable, error) {
		go func() {
			time.Sleep(75 * time.Millisecond)
			a.chats.apply(videoMediaRows("https://scontent.xx.fbcdn.net/fresh"))
		}()
		return answerFor(7, pageAnchor{at: 9900, id: "newer"}), nil
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	if result, code := a.mediaRead(ctx, request{Params: json.RawMessage(`{"id":"video"}`)}); code != "" || result == nil {
		t.Fatalf("asynchronous attachment rows were not awaited: %s", code)
	}
}

func TestMediaRenewalCancellationAndUnchangedAddress(t *testing.T) {
	a, requests := expiredVideoFixture(t, http.StatusForbidden)
	a.fetch = func(context.Context, int64, pageAnchor) (*table.LSTable, error) {
		return videoMediaRows("https://scontent.xx.fbcdn.net/expired"), nil
	}
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	if _, code := a.mediaRead(ctx, request{Params: json.RawMessage(`{"id":"video"}`)}); code != "media_unavailable" || requests.Load() != 1 {
		t.Fatal("unchanged media address was retried or cancellation ignored")
	}
	if _, ok := a.refreshMediaSource(ctx, "video", "https://scontent.xx.fbcdn.net/expired"); ok {
		t.Fatal("cancelled renewal succeeded")
	}
}

func TestMediaAuthenticationRefusalDoesNotRefreshHistory(t *testing.T) {
	a, requests := expiredVideoFixture(t, http.StatusUnauthorized)
	a.fetch = func(context.Context, int64, pageAnchor) (*table.LSTable, error) {
		t.Fatal("authentication refusal triggered an attachment renewal")
		return nil, nil
	}
	if _, code := a.mediaRead(context.Background(), request{Params: json.RawMessage(`{"id":"video"}`)}); code != "media_unavailable" || requests.Load() != 1 {
		t.Fatal("authentication refusal did not stay unavailable")
	}
}

func TestConcurrentExpiredVideoReadsShareRenewal(t *testing.T) {
	a, _ := expiredVideoFixture(t, http.StatusForbidden)
	var fetches atomic.Int32
	a.fetch = func(context.Context, int64, pageAnchor) (*table.LSTable, error) {
		fetches.Add(1)
		return videoMediaRows("https://scontent.xx.fbcdn.net/fresh"), nil
	}
	var reads sync.WaitGroup
	for range 4 {
		reads.Go(func() {
			if _, code := a.mediaRead(context.Background(), request{Params: json.RawMessage(`{"id":"video"}`)}); code != "" {
				t.Errorf("concurrent playback failed: %s", code)
			}
		})
	}
	reads.Wait()
	if fetches.Load() != 1 {
		t.Fatal("concurrent downloads duplicated the anchored renewal")
	}
}

func TestMediaRenewalNeverAsksOrdinaryConnectionForEncryptedHistory(t *testing.T) {
	a, _ := expiredVideoFixture(t, http.StatusForbidden)
	a.chats.apply(&table.LSTable{LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 7, ThreadType: table.ENCRYPTED_OVER_WA_GROUP}}})
	a.fetch = func(context.Context, int64, pageAnchor) (*table.LSTable, error) {
		t.Fatal("encrypted media asked the ordinary connection for history")
		return nil, nil
	}
	if _, renewed := a.refreshMediaSource(context.Background(), "video", "https://scontent.xx.fbcdn.net/expired"); renewed {
		t.Fatal("encrypted media renewed on the ordinary connection")
	}
}

func TestMediaRefreshAnchorChoosesClosestNewerMessage(t *testing.T) {
	for _, test := range []struct {
		name string
		rows []*table.LSUpsertMessage
		want pageAnchor
	}{
		{"closest", []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "near", TimestampMs: 1701}, {ThreadKey: 7, MessageId: "far", TimestampMs: 9000}}, pageAnchor{1701, "near"}},
		{"same timestamp", []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "old-video-z", TimestampMs: 1700}, {ThreadKey: 7, MessageId: "old-video-a", TimestampMs: 1700}}, pageAnchor{1700, "old-video-a"}},
		{"older and other chat", []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "older", TimestampMs: 1699}, {ThreadKey: 8, MessageId: "different-chat", TimestampMs: 1701}}, pageAnchor{1700, "old-video"}},
	} {
		t.Run(test.name, func(t *testing.T) {
			s := newStore()
			s.apply(videoMediaRows("https://scontent.xx.fbcdn.net/expired"))
			s.apply(&table.LSTable{LSUpsertMessage: test.rows})
			key, target, known := s.anchorForMedia("video:preview")
			if !known || key != 7 || s.mediaRefreshAnchor(key, target) != test.want {
				t.Fatal("media renewal chose the wrong page boundary")
			}
		})
	}
}

func TestMediaCheckAllowsOnlyOrdinaryReadDiagnostics(t *testing.T) {
	a, _ := expiredVideoFixture(t, http.StatusForbidden)
	if _, code := a.callContext(context.Background(), request{Method: "probe.media"}); code != "observation_only" {
		t.Fatal("media diagnostic was exposed in an ordinary app process")
	}
	a.mediaCheck = true
	for _, method := range []string{"ui.sendText", "ui.sendMedia", "ui.read", "ui.typing", "ui.encrypted", "session", "probe.receipt", "ui.deleteChat"} {
		if _, code := a.callContext(context.Background(), request{Method: method}); code != "observation_only" {
			t.Fatalf("media diagnostic allowed %s", method)
		}
	}
	a.fetch = func(context.Context, int64, pageAnchor) (*table.LSTable, error) {
		return videoMediaRows("https://scontent.xx.fbcdn.net/fresh"), nil
	}
	params, _ := json.Marshal(map[string]string{"tag": tagOf("video")})
	result, code := a.callContext(context.Background(), request{Method: "probe.media", Params: params})
	if code != "" || result == nil {
		t.Fatalf("ordinary media diagnostic failed: %s", code)
	}
	counts := result.(map[string]any)
	if len(counts) != 2 || counts["total"] != mediaChunk+17 || counts["chunk"] != mediaChunk {
		t.Fatal("media diagnostic returned anything beyond byte counts")
	}
}
