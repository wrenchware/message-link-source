package main

import (
	"bytes"
	"context"
	"encoding/json"
	"image"
	"image/png"
	"net/http"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// The writer is an observable completion boundary: preview change events are emitted
// after attachment and persistence, so asynchronous assertions need no fixed delay.
type previewEvents struct{ changed chan struct{} }

func (w *previewEvents) Write(body []byte) (int, error) {
	var event response
	if err := json.Unmarshal(body, &event); err != nil {
		return 0, err
	}
	if event.Event == "changed" {
		w.changed <- struct{}{}
	}
	return len(body), nil
}

func awaitPreviewSignal(t *testing.T, signal <-chan struct{}) {
	t.Helper()
	select {
	case <-signal:
	case <-time.After(5 * time.Second):
		t.Fatal("preview operation did not complete")
	}
}

func previewAdapter(t *testing.T, fetch func(context.Context, string) (*linkPreviewResult, error)) (*adapter, *previewEvents) {
	t.Helper()
	events := &previewEvents{changed: make(chan struct{}, 32)}
	a := newAdapter(&output{encoder: json.NewEncoder(events)})
	a.previewJobs = newLinkPreviewJobs(a, a.lifetime, fetch)
	t.Cleanup(func() {
		a.stop()
		a.previewJobs.wg.Wait()
	})
	return a, events
}

func fixturePreview() *linkPreviewResult {
	return &linkPreviewResult{title: "Synthetic reel", imageURL: "https://example.test/cover.jpg", width: 180, height: 320}
}

func TestLinkPreviewURLExtraction(t *testing.T) {
	for _, tc := range []struct{ text, want string }{
		{"https://example.test/reel?ref=share&item=5", "https://example.test/reel?ref=share&item=5"},
		{"See https://example.test/reel then https://example.test/other", "https://example.test/reel"},
		{"A shared link: (https://example.test/reel).", "https://example.test/reel"},
		{"No address in this synthetic message", ""},
		{"mailto:synthetic@example.test", ""},
		{"file:///synthetic/local.jpg", ""},
	} {
		if got := firstPreviewURL(tc.text); got != tc.want {
			t.Errorf("URL extraction: got %q, want %q", got, tc.want)
		}
	}
}

func TestLinkPreviewExplicitRequestReturnsBeforeFetchAndDeduplicates(t *testing.T) {
	entered, release := make(chan struct{}), make(chan struct{})
	var calls atomic.Int32
	a, events := previewAdapter(t, func(ctx context.Context, _ string) (*linkPreviewResult, error) {
		if calls.Add(1) == 1 {
			close(entered)
		}
		select {
		case <-release:
			return fixturePreview(), nil
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	})
	a.chats.addEncrypted(7, "synthetic-message", 4, "https://example.test/reel", "", 1700)
	a.client = &messagix.Client{}
	returned := make(chan struct{})
	status := make(chan string, 1)
	go func() {
		_, failure := a.call(request{Method: "ui.linkPreviews", Params: json.RawMessage(`{"chatId":"7","messageIds":["synthetic-message","synthetic-message"]}`)})
		status <- failure
		close(returned)
	}()
	awaitPreviewSignal(t, returned)
	if <-status != "" {
		t.Fatal("the explicit preview endpoint rejected a valid request")
	}
	awaitPreviewSignal(t, entered)
	for i := 0; i < 10; i++ {
		a.loadLinkPreviews(7, []string{"synthetic-message"})
	}
	if page := a.chats.history(7, 0); len(page) != 1 || len(page[0].Media) != 0 {
		t.Fatal("a pending preview changed the message before its result arrived")
	}
	close(release)
	awaitPreviewSignal(t, events.changed)
	a.loadLinkPreviews(7, []string{"synthetic-message"})
	page := a.chats.history(7, 0)
	if len(page) != 1 || len(page[0].Media) != 1 || page[0].Media[0].Link != "https://example.test/reel" {
		t.Fatal("the explicit request did not attach one clickable thumbnail")
	}
	if calls.Load() != 1 {
		t.Fatal("repeated requests downloaded the same preview again")
	}
}

func TestLinkPreviewSameURLCoalescesAcrossMessages(t *testing.T) {
	entered, release := make(chan struct{}), make(chan struct{})
	var first sync.Once
	var calls atomic.Int32
	a, events := previewAdapter(t, func(ctx context.Context, _ string) (*linkPreviewResult, error) {
		calls.Add(1)
		first.Do(func() { close(entered) })
		select {
		case <-release:
			return fixturePreview(), nil
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	})
	a.chats.addEncrypted(7, "synthetic-first", 4, "https://example.test/reel", "", 1700)
	a.chats.addEncrypted(8, "synthetic-second", 4, "Shared: https://example.test/reel", "", 1800)
	a.loadLinkPreviews(7, []string{"synthetic-first"})
	awaitPreviewSignal(t, entered)
	a.loadLinkPreviews(8, []string{"synthetic-second"})
	close(release)
	awaitPreviewSignal(t, events.changed)
	awaitPreviewSignal(t, events.changed)
	if calls.Load() != 1 {
		t.Fatal("the same URL was fetched separately for different messages")
	}
	for _, key := range []int64{7, 8} {
		if page := a.chats.history(key, 0); len(page) != 1 || len(page[0].Media) != 1 {
			t.Fatal("a coalesced result did not reach each requesting message")
		}
	}
}

func TestLinkPreviewWithoutPictureKeepsTextAndSuppressesRepeatedFetch(t *testing.T) {
	var calls atomic.Int32
	a, events := previewAdapter(t, func(context.Context, string) (*linkPreviewResult, error) {
		calls.Add(1)
		return &linkPreviewResult{title: "Synthetic page without an image"}, nil
	})
	const original = "https://example.test/reel"
	a.chats.addEncrypted(7, "synthetic-message", 4, original, "", 1700)
	a.loadLinkPreviews(7, []string{"synthetic-message"})
	if !settle(t, func() bool {
		a.previewJobs.mu.Lock()
		defer a.previewJobs.mu.Unlock()
		return len(a.previewJobs.pending) == 0
	}) {
		t.Fatal("the imageless result did not finish")
	}
	for i := 0; i < 10; i++ {
		a.loadLinkPreviews(7, []string{"synthetic-message"})
	}
	a.stop()
	a.previewJobs.wg.Wait()
	if calls.Load() != 1 {
		t.Fatal("a page with no picture was repeatedly requested")
	}
	page := a.chats.history(7, 0)
	if len(page) != 1 || page[0].Text != original || len(page[0].Media) != 0 {
		t.Fatal("missing preview metadata changed or hid the original link")
	}
	select {
	case <-events.changed:
		t.Fatal("an imageless result announced a thumbnail")
	default:
	}
}

func TestLinkPreviewCancellationStopsPendingFetch(t *testing.T) {
	entered := make(chan struct{})
	a, events := previewAdapter(t, func(ctx context.Context, _ string) (*linkPreviewResult, error) {
		close(entered)
		<-ctx.Done()
		return nil, ctx.Err()
	})
	const original = "https://example.test/reel"
	a.chats.addEncrypted(7, "synthetic-message", 4, original, "", 1700)
	a.loadLinkPreviews(7, []string{"synthetic-message"})
	awaitPreviewSignal(t, entered)
	a.stop()
	finished := make(chan struct{})
	go func() {
		a.previewJobs.wg.Wait()
		close(finished)
	}()
	awaitPreviewSignal(t, finished)
	page := a.chats.history(7, 0)
	if len(page) != 1 || page[0].Text != original || len(page[0].Media) != 0 {
		t.Fatal("a canceled request modified the message")
	}
	select {
	case <-events.changed:
		t.Fatal("a canceled request announced a thumbnail")
	default:
	}
}

func TestLinkPreviewLateResultDoesNotRestoreDeletedOrEditedMessage(t *testing.T) {
	for _, mutate := range []string{"delete", "edit", "native card"} {
		t.Run(mutate, func(t *testing.T) {
			s := newStore()
			const original = "https://example.test/reel"
			s.addEncrypted(7, "synthetic-message", 4, original, "", 1700)
			switch mutate {
			case "delete":
				s.forget(7, "synthetic-message")
			case "edit":
				s.addEncrypted(7, "synthetic-message", 4, "https://example.test/different", "", 1700)
			case "native card":
				s.attachSecure(7, "synthetic-message", 1700, &attachment{id: "native-card", name: "Native share", mime: "image/jpeg", source: "https://example.test/native.jpg", link: original}, nil)
			}
			if item := s.attachLinkPreview(7, "synthetic-message", original, fixturePreview()); item != nil {
				t.Fatal("a late preview attached after the message was removed, edited or supplied a native card")
			}
			for _, view := range s.history(7, 0) {
				for _, media := range view.Media {
					if strings.HasPrefix(media.ID, "link-preview:") {
						t.Fatal("a stale generated thumbnail remained visible")
					}
				}
			}
		})
	}
}

func TestLinkPreviewFetchCompletionRechecksMessage(t *testing.T) {
	for _, mutate := range []string{"delete", "edit", "native card"} {
		t.Run(mutate, func(t *testing.T) {
			entered, release := make(chan struct{}), make(chan struct{})
			a, events := previewAdapter(t, func(ctx context.Context, _ string) (*linkPreviewResult, error) {
				close(entered)
				select {
				case <-release:
					return fixturePreview(), nil
				case <-ctx.Done():
					return nil, ctx.Err()
				}
			})
			const original = "https://example.test/reel"
			a.chats.addEncrypted(7, "synthetic-message", 4, original, "", 1700)
			a.loadLinkPreviews(7, []string{"synthetic-message"})
			awaitPreviewSignal(t, entered)
			switch mutate {
			case "delete":
				a.chats.forget(7, "synthetic-message")
			case "edit":
				a.chats.addEncrypted(7, "synthetic-message", 4, "https://example.test/edited", "", 1700)
			case "native card":
				a.chats.attachSecure(7, "synthetic-message", 1700, &attachment{id: "native-card", name: "Native share", mime: "image/jpeg", source: "https://example.test/native.jpg", link: original}, nil)
			}
			close(release)
			if !settle(t, func() bool {
				a.previewJobs.mu.Lock()
				defer a.previewJobs.mu.Unlock()
				return len(a.previewJobs.pending) == 0
			}) {
				t.Fatal("the gated fetch did not finish processing its result")
			}
			select {
			case <-events.changed:
				t.Fatal("a stale fetch result announced an attachment")
			default:
			}
			for _, view := range a.chats.history(7, 0) {
				for _, media := range view.Media {
					if strings.HasPrefix(media.ID, "link-preview:") {
						t.Fatal("the completed fetch attached to an obsolete message")
					}
				}
			}
		})
	}
}

func TestLinkPreviewQueuedMessageIsRecheckedBeforeFetch(t *testing.T) {
	for _, mutate := range []string{"delete", "edit", "native card"} {
		t.Run(mutate, func(t *testing.T) {
			entered, release := make(chan struct{}, 2), make(chan struct{})
			var calls atomic.Int32
			a, _ := previewAdapter(t, func(ctx context.Context, address string) (*linkPreviewResult, error) {
				calls.Add(1)
				if address != "https://example.test/queued" {
					entered <- struct{}{}
					select {
					case <-release:
					case <-ctx.Done():
						return nil, ctx.Err()
					}
				}
				return fixturePreview(), nil
			})
			a.chats.addEncrypted(7, "synthetic-block-one", 4, "https://example.test/block-one", "", 1700)
			a.chats.addEncrypted(7, "synthetic-block-two", 4, "https://example.test/block-two", "", 1800)
			const original = "https://example.test/queued"
			a.chats.addEncrypted(7, "synthetic-queued", 4, original, "", 1900)
			a.loadLinkPreviews(7, []string{"synthetic-block-one", "synthetic-block-two"})
			awaitPreviewSignal(t, entered)
			awaitPreviewSignal(t, entered)
			// Both workers are occupied, so this accepted request cannot be dequeued
			// until after its message has changed.
			a.loadLinkPreviews(7, []string{"synthetic-queued"})
			switch mutate {
			case "delete":
				a.chats.forget(7, "synthetic-queued")
			case "edit":
				a.chats.addEncrypted(7, "synthetic-queued", 4, "https://example.test/replacement", "", 1900)
			case "native card":
				a.chats.attachSecure(7, "synthetic-queued", 1900, &attachment{id: "native-queued", name: "Native share", mime: "image/jpeg", source: "https://example.test/native.jpg", link: original}, nil)
			}
			close(release)
			if !settle(t, func() bool {
				a.previewJobs.mu.Lock()
				defer a.previewJobs.mu.Unlock()
				return len(a.previewJobs.pending) == 0
			}) {
				t.Fatal("queued preview work did not drain")
			}
			if calls.Load() != 2 {
				t.Fatal("a queued obsolete message contacted its linked website")
			}
		})
	}
}

func TestLinkPreviewMediaReadUsesIsolatedImageFetcher(t *testing.T) {
	var encoded bytes.Buffer
	if err := png.Encode(&encoded, image.NewRGBA(image.Rect(0, 0, 3, 2))); err != nil {
		t.Fatal(err)
	}
	for _, fails := range []bool{false, true} {
		name := "successful public image"
		if fails {
			name = "failed public image cannot fall back to session"
		}
		t.Run(name, func(t *testing.T) {
			previous := publicPreviewFetcher
			t.Cleanup(func() { publicPreviewFetcher = previous })
			var requests int
			publicPreviewFetcher = newPreviewFetcher(previewTestTransport(func(req *http.Request) (*http.Response, error) {
				requests++
				for _, header := range []string{"Cookie", "Authorization", "Proxy-Authorization", "Referer"} {
					if req.Header.Get(header) != "" {
						t.Fatalf("generated image request contained a credential/referrer header: %s", header)
					}
				}
				if req.URL.User != nil || req.URL.String() != "https://example.test/cover.jpg" || req.Method != http.MethodGet {
					t.Fatal("generated image request changed its isolated destination")
				}
				if fails {
					return previewTestResponse(req, http.StatusForbidden, "text/plain", "synthetic failure"), nil
				}
				return previewTestResponse(req, http.StatusOK, "image/png", encoded.String()), nil
			}))
			// This dummy has no initialized authenticated HTTP client. Any accidental
			// fallback to Messenger's GetHTTP/BuildHeaders path would fail this test.
			a := &adapter{client: &messagix.Client{}, chats: newStore()}
			const original = "https://example.test/reel?ref=share#original"
			a.chats.addEncrypted(7, "synthetic-message", 4, original, "", 1700)
			item := a.chats.attachLinkPreview(7, "synthetic-message", original, fixturePreview())
			if item == nil {
				t.Fatal("fixture did not attach a generated image")
			}
			params, err := json.Marshal(map[string]any{"id": item.id, "offset": 0})
			if err != nil {
				t.Fatal(err)
			}
			result, status := a.mediaRead(context.Background(), request{Method: "ui.mediaRead", Params: params})
			if fails {
				if status != "media_unavailable" || result != nil {
					t.Fatal("failed public image did not return the ordinary unavailable result")
				}
			} else {
				if status != "" {
					t.Fatal("generated image could not be read through the media endpoint")
				}
				body := result.(map[string]any)
				if body["mime"] != "image/png" || !bytes.Equal(body["data"].([]byte), encoded.Bytes()) {
					t.Fatal("media endpoint lost the fetched thumbnail bytes or detected image type")
				}
				if _, status := a.mediaRead(context.Background(), request{Params: params}); status != "" {
					t.Fatal("cached generated image became unavailable")
				}
			}
			if requests != 1 {
				t.Fatal("generated image was fetched more than once")
			}
			page := a.chats.history(7, 0)
			if len(page) != 1 || page[0].Text != original {
				t.Fatal("image loading changed the original shared URL")
			}
			if !fails && (len(page[0].Media) != 1 || page[0].Media[0].Link != original) {
				t.Fatal("the image destination replaced the original browser link")
			}
		})
	}
}

func TestLinkPreviewNativeAttachmentWinsEitherArrivalOrder(t *testing.T) {
	for _, nativeFirst := range []bool{false, true} {
		s := newStore()
		const original = "https://example.test/reel"
		s.addEncrypted(7, "synthetic-message", 4, original, "", 1700)
		attachNative := func() {
			s.attachSecure(7, "synthetic-message", 1700, &attachment{id: "native-card", name: "Native share", mime: "image/jpeg", source: "https://example.test/native.jpg", link: original}, nil)
		}
		if nativeFirst {
			attachNative()
		}
		generated := s.attachLinkPreview(7, "synthetic-message", original, fixturePreview())
		if nativeFirst && generated != nil || !nativeFirst && generated == nil {
			t.Fatal("generated preview eligibility ignored an existing native thumbnail")
		}
		if !nativeFirst {
			attachNative()
		}
		page := s.history(7, 0)
		if len(page) != 1 || len(page[0].Media) != 1 || page[0].Media[0].ID != "native-card" {
			t.Fatal("native and generated thumbnails were both displayed")
		}
	}
}

func TestLinkPreviewOldCardIsHiddenAfterEditAndCanBeReplaced(t *testing.T) {
	s := newStore()
	const original = "https://example.test/reel"
	s.addEncrypted(7, "synthetic-message", 4, original, "", 1700)
	if s.attachLinkPreview(7, "synthetic-message", original, fixturePreview()) == nil {
		t.Fatal("fixture did not attach")
	}
	s.addEncrypted(7, "synthetic-message", 4, "edited synthetic text without a link", "", 1700)
	if page := s.history(7, 0); len(page) != 1 || len(page[0].Media) != 0 {
		t.Fatal("the preview of a removed URL remained visible")
	}
	const replacement = "https://example.test/other"
	s.addEncrypted(7, "synthetic-message", 4, replacement, "", 1700)
	if s.attachLinkPreview(7, "synthetic-message", replacement, fixturePreview()) == nil {
		t.Fatal("an obsolete preview prevented the edited link's replacement")
	}
	if page := s.history(7, 0); len(page) != 1 || len(page[0].Media) != 1 || page[0].Media[0].Link != replacement {
		t.Fatal("the replacement link did not become the single visible preview")
	}
}

func TestLinkPreviewPersistsSourceAndLinkAcrossSQLiteReopen(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "messages.db")
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	const original = "https://example.test/reel?ref=share"
	if err := kept.rememberChecked(ctx, 7, "synthetic-message", 4, original, "", 1700); err != nil {
		t.Fatal(err)
	}
	a, events := previewAdapter(t, func(context.Context, string) (*linkPreviewResult, error) {
		return fixturePreview(), nil
	})
	a.kept = kept
	a.chats.addEncrypted(7, "synthetic-message", 4, original, "", 1700)
	a.loadLinkPreviews(7, []string{"synthetic-message"})
	awaitPreviewSignal(t, events.changed)
	a.stop()
	a.previewJobs.wg.Wait()
	if err := kept.db.Close(); err != nil {
		t.Fatal(err)
	}
	kept, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	back := newStore()
	kept.restore(ctx, back)
	page := back.history(7, 0)
	if len(page) != 1 || len(page[0].Media) != 1 {
		t.Fatal("preview disappeared across database reopen")
	}
	media := page[0].Media[0]
	if media.Link != original || media.Width != 180 || media.Height != 320 || media.Name != "Synthetic reel" || !strings.HasPrefix(media.ID, "link-preview:") {
		t.Fatal("preview metadata or explicit unauthenticated fetch identity was lost")
	}
	if source, _, ok := back.mediaSource(media.ID); !ok || source != fixturePreview().imageURL {
		t.Fatal("restored thumbnail no longer resolves to its public image source")
	}
}

func TestLinkPreviewPersistenceRejectsStaleAndDeletedResults(t *testing.T) {
	for _, mutate := range []string{"delete", "edit"} {
		t.Run(mutate, func(t *testing.T) {
			ctx := context.Background()
			kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
			if err != nil {
				t.Fatal(err)
			}
			defer kept.db.Close()
			const original = "https://example.test/reel"
			if err := kept.rememberChecked(ctx, 7, "synthetic-message", 4, original, "", 1700); err != nil {
				t.Fatal(err)
			}
			s := newStore()
			s.addEncrypted(7, "synthetic-message", 4, original, "", 1700)
			item := s.attachLinkPreview(7, "synthetic-message", original, fixturePreview())
			if mutate == "delete" {
				kept.forget(ctx, 7, "synthetic-message")
			} else if err := kept.rememberChecked(ctx, 7, "synthetic-message", 4, "edited synthetic message", "", 1700); err != nil {
				t.Fatal(err)
			}
			kept.rememberLinkPreview(ctx, 7, "synthetic-message", original, item)
			var count int
			if err := kept.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM message_attachment WHERE id = ?`, item.id).Scan(&count); err != nil || count != 0 {
				t.Fatal("stale preview was persisted after its message changed")
			}
		})
	}
}

func TestLinkPreviewsAreNotQueuedByRestoreOrHistory(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	if err := kept.rememberChecked(ctx, 7, "synthetic-message", 4, "https://example.test/reel", "", 1700); err != nil {
		t.Fatal(err)
	}
	var calls atomic.Int32
	a, _ := previewAdapter(t, func(context.Context, string) (*linkPreviewResult, error) {
		calls.Add(1)
		return fixturePreview(), nil
	})
	a.client = &messagix.Client{}
	// A cold conversation with no anchor returns without an authenticated request.
	if _, status := a.call(request{Method: "ui.messages", Params: json.RawMessage(`{"chatId":"8"}`)}); status != "" {
		t.Fatal("offline empty history did not return")
	}
	kept.restore(ctx, a.chats)
	a.chats.markPageRefreshed(7, 0)
	a.asked[7] = time.Now()
	if _, status := a.call(request{Method: "ui.messages", Params: json.RawMessage(`{"chatId":"7"}`)}); status != "" {
		t.Fatal("offline restored history did not return")
	}
	// A queued operation would leave an attempt record even if its worker had not
	// yet run. Check that before cancellation so cancellation cannot hide a request.
	a.previewJobs.mu.Lock()
	queued := len(a.previewJobs.pending) + len(a.previewJobs.attempted)
	a.previewJobs.mu.Unlock()
	a.stop()
	a.previewJobs.wg.Wait()
	if queued != 0 || calls.Load() != 0 {
		t.Fatal("reading or restoring messages contacted a linked website without a preview request")
	}
}

// Facebook's card for a pasted Instagram profile does not carry the words' address (2026-11-01: the words had a ?xtok=
// share code), so the two compare as different links. Its card is still the message's one card, in either order.
func TestLinkPreviewServiceCardWithAnotherAddressIsTheCard(t *testing.T) {
	const words = "https://www.instagram.com/synthetic?xtok=c3ludGhldGlj"
	const card = "https://www.instagram.com/synthetic/"
	native := func() *attachment {
		return &attachment{id: "native-card", name: "synthetic", mime: "image/jpeg", source: "https://example.test/native.jpg", link: card}
	}
	shown := func(s *store) []viewMedia {
		t.Helper()
		history := s.history(7, 0)
		if len(history) != 1 {
			t.Fatalf("messages=%d", len(history))
		}
		return history[0].Media
	}

	s := newStore()
	s.addEncrypted(7, "native-first", 4, words, "", 1700)
	s.restoreAttachment("native-first", native(), "")
	if s.attachLinkPreview(7, "native-first", words, fixturePreview()) != nil {
		t.Fatal("a second card was drawn under Facebook's own")
	}
	if media := shown(s); len(media) != 1 || media[0].ID != "native-card" {
		t.Fatalf("cards after a refused preview: %+v", media)
	}

	// Facebook's card landing after ours replaces it, as does one restored over a kept generated row.
	s = newStore()
	s.addEncrypted(7, "generated-first", 4, words, "", 1700)
	generated := s.attachLinkPreview(7, "generated-first", words, fixturePreview())
	if generated == nil {
		t.Fatal("no preview for a message without a card")
	}
	s.restoreAttachment("generated-first", native(), "")
	if media := shown(s); len(media) != 1 || media[0].ID != "native-card" {
		t.Fatalf("cards after Facebook's arrived: %+v", media)
	}
	if _, kept := s.media[generated.id]; kept {
		t.Fatal("the replaced preview's picture is still served")
	}

	// A photo carries no link and is no card: the words' link still gets its preview.
	s = newStore()
	s.addEncrypted(7, "photo", 4, words, "", 1700)
	s.restoreAttachment("photo", &attachment{id: "a-photo", name: "Photo", mime: "image/jpeg", source: "https://example.test/photo.jpg"}, "")
	if s.attachLinkPreview(7, "photo", words, fixturePreview()) == nil {
		t.Fatal("a photo stopped the words' link getting a preview")
	}

	// The worker's own request is refused before anything is fetched.
	var calls atomic.Int32
	a, _ := previewAdapter(t, func(context.Context, string) (*linkPreviewResult, error) {
		calls.Add(1)
		return fixturePreview(), nil
	})
	a.chats.addEncrypted(7, "asked", 4, words, "", 1700)
	a.chats.restoreAttachment("asked", native(), "")
	a.loadLinkPreviews(7, []string{"asked"})
	a.previewJobs.mu.Lock()
	queued := len(a.previewJobs.pending) + len(a.previewJobs.attempted)
	a.previewJobs.mu.Unlock()
	if queued != 0 || calls.Load() != 0 {
		t.Fatal("the page was asked for a message Facebook had already carded")
	}
}

// Facebook's card for a shared page links through its l.facebook.com redirect while the words hold the page itself;
// the two once read as different links, so the helper drew its own card under Facebook's (2026-10-18).
func TestLinkPreviewFacebookRedirectCardIsTheSameLink(t *testing.T) {
	const page = "https://share.example.test/AbC123"
	const redirect = "https://l.facebook.com/l.php?u=https%3A%2F%2Fshare.example.test%2FAbC123&h=AT0synthetic"
	if linkDestination(redirect) != page || linkDestination(page) != page || !sameLink(redirect, page) {
		t.Fatal("Facebook's redirect was not read as the page it leads to")
	}
	if linkDestination("https://l.facebook.com/l.php?u=javascript%3Aalert(1)") != "https://l.facebook.com/l.php?u=javascript%3Aalert(1)" {
		t.Fatal("a redirect to something other than a web page was unwrapped")
	}

	s := newStore()
	s.apply(&table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 4, MessageId: "redirected", TimestampMs: 10, Text: "Listing " + page}}})
	item := shareTestItem("redirected", "redirected-item")
	item.DefaultActionUrl = redirect
	s.apply(&table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{item}})
	if media := shareTestMedia(t, s, "redirected", 1)[0]; media.Link != page {
		t.Fatalf("Facebook's card still names its redirect: %q", media.Link)
	}
	if s.attachLinkPreview(4, "redirected", "Listing "+page, fixturePreview()) != nil {
		t.Fatal("a second card was drawn under Facebook's own")
	}
	shareTestMedia(t, s, "redirected", 1)

	// A card kept with the redirect before this fix, restored in either order against a generated one.
	for _, keptFirst := range []bool{false, true} {
		s := newStore()
		s.addEncrypted(7, "kept", 4, "Listing "+page, "", 1700)
		restore := func() {
			s.restoreAttachment("kept", &attachment{id: "kept-card", name: "Native share", mime: "image/jpeg", source: "https://example.test/native.jpg", link: redirect}, "")
		}
		if keptFirst {
			restore()
		}
		generated := s.attachLinkPreview(7, "kept", "Listing "+page, fixturePreview())
		if keptFirst != (generated == nil) {
			t.Fatal("generated preview eligibility ignored a kept redirect card")
		}
		if !keptFirst {
			restore()
		}
		shown := s.history(7, 0)
		if len(shown) != 1 || len(shown[0].Media) != 1 || shown[0].Media[0].ID != "kept-card" || shown[0].Media[0].Link != page {
			t.Fatal("a kept redirect card and a generated card were both displayed")
		}
	}
}
