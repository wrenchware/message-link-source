package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/http"
	"strings"
	"sync"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
)

// The helper downloads media itself, so the request carries the session's own cookies and headers, then hands it to
// the app in chunks the way the other two helpers do. The app never receives an Instagram url and never fetches one:
// it asks for an id, which is all it is given.
const mediaLimit = 25 << 20
const mediaChunk = 192 << 10

// The app asks for one chunk at a time and several pictures load at once, so holding only the newest download would
// make every picture fetch again for every chunk.
const mediaKeep = 6

// How long a finished download stays once nothing asks for it. The app reads one in chunks back to back, so a minute
// of quiet means it has what it wanted, and on the always-on server six of these could otherwise hold 150 MiB until
// later downloads happened to push them out.
const mediaIdle = 60 * time.Second

type mediaItem struct {
	id   string
	mime string
	data []byte
	// When it was last kept or read. Changed only under the store's lock.
	used time.Time
}

// What an id stands for. Ids are handed out by the helper and resolved only here, so a url cannot reach the app and
// the app cannot ask the helper to fetch an arbitrary address.
type mediaStore struct {
	mu    sync.Mutex
	urls  map[string]string
	items []*mediaItem
	// Whether a sweep of idle downloads is pending; and the clock they are aged by, nil but in tests.
	sweeping bool
	now      func() time.Time
}

func newMediaStore() *mediaStore { return &mediaStore{urls: map[string]string{}} }

// A stable, opaque name for a url. An attachment has an id of its own and keeps it, because it names the same picture
// across sessions and lets the app cache it; everything else is named by a digest of its address rather than by the
// address, which must not travel.
func mediaID(known, url string) string {
	if url == "" {
		return ""
	}
	if known != "" {
		return known
	}
	sum := sha256.Sum256([]byte(url))
	return hex.EncodeToString(sum[:16])
}

func (s *mediaStore) remember(known, url string) string {
	id := mediaID(known, url)
	if id == "" {
		return ""
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	// The newest address wins: Instagram's urls expire, and a conversation read again supplies fresh ones.
	s.urls[id] = url
	return id
}

func (s *mediaStore) address(id string) (string, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	url, ok := s.urls[id]
	return url, ok
}

func (s *mediaStore) clock() time.Time {
	if s.now != nil {
		return s.now()
	}
	return time.Now()
}

// Call with the lock held.
func (s *mediaStore) dropIdle(now time.Time) {
	kept := s.items[:0]
	for _, item := range s.items {
		if now.Sub(item.used) < mediaIdle {
			kept = append(kept, item)
		}
	}
	clear(s.items[len(kept):]) // So the dropped buffers can be collected.
	s.items = kept
}

// A store nobody touches is never trimmed by a touch, so one sweep is pending while anything is held. Call with the
// lock held.
func (s *mediaStore) sweepLater() {
	if s.sweeping || len(s.items) == 0 {
		return
	}
	s.sweeping = true
	time.AfterFunc(mediaIdle, s.sweep)
}

func (s *mediaStore) sweep() {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.sweeping = false
	s.dropIdle(s.clock())
	s.sweepLater()
}

// Newest first. An entry's id, mime and data never change once published.
func (s *mediaStore) cached(id string) *mediaItem {
	s.mu.Lock()
	defer s.mu.Unlock()
	now := s.clock()
	s.dropIdle(now)
	for index, item := range s.items {
		if item.id == id {
			if index > 0 {
				s.items = append(append([]*mediaItem{item}, s.items[:index]...), s.items[index+1:]...)
			}
			item.used = now
			return item
		}
	}
	return nil
}

func (s *mediaStore) keep(item *mediaItem) {
	s.mu.Lock()
	defer s.mu.Unlock()
	now := s.clock()
	s.dropIdle(now)
	item.used = now
	for index, old := range s.items {
		if old.id == item.id {
			s.items = append(s.items[:index], s.items[index+1:]...)
			break
		}
	}
	s.items = append([]*mediaItem{item}, s.items...)
	if len(s.items) > mediaKeep {
		clear(s.items[mediaKeep:])
		s.items = s.items[:mediaKeep]
	}
	s.sweepLater()
}

// Whether reading the conversation list again could give a failed picture a fresh address. Only avatars: the list
// names a person's picture and a group's, and nothing else — a message's pictures and previews are named only when
// that conversation is read, and search results only by a search. Forcing a list re-read for those paged the whole
// inbox again, about a dozen requests, for an answer it could never contain. Nor is a read that was abandoned or ran
// out of time evidence that an address expired.
func listRefreshes(id string, err error) bool {
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return false
	}
	return strings.HasPrefix(id, "avatar:")
}

// Downloads through the session so the request looks like the one the account's own client would make.
type mediaClient interface {
	GetHTTP() *httpclient.HTTPClient
}

func (a *readOnlyAdapter) mediaRead(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.readClientFor()
	if code != "" {
		return nil, code
	}
	var p struct {
		ID     string `json:"id"`
		Offset int    `json:"offset"`
	}
	if json.Unmarshal(raw, &p) != nil || p.ID == "" || p.Offset < 0 {
		return nil, "invalid_params"
	}
	a.mu.Lock()
	if a.media == nil {
		a.media = newMediaStore()
	}
	store := a.media
	a.mu.Unlock()
	// An id this helper never handed out resolves to nothing. The app cannot name an address of its own.
	source, known := store.address(p.ID)
	if !known || source == "" {
		return nil, "media_unavailable"
	}
	held := store.cached(p.ID)
	if held == nil {
		session, ok := client.(mediaClient)
		if !ok || session.GetHTTP() == nil {
			return nil, "unsupported"
		}
		anonymous := a.anonymousMedia
		if anonymous == nil {
			anonymous = anonymousMedia
		}
		resp, body, _, err := fetchMedia(ctx, session.GetHTTP(), anonymous, source)
		failed := err != nil || resp == nil || resp.StatusCode != http.StatusOK || len(body) == 0
		// Only a Meta address is a request of Instagram's to count: anything else was refused or fetched elsewhere.
		if sessionMedia(source) {
			a.requests.note(ctx, &a.requests.media, failed)
		}
		if refusedMedia(err) {
			return nil, "media_unavailable"
		}
		if failed {
			// Instagram's urls expire, so a stored one outlives its usefulness. A fresh address comes only from
			// reading again whatever named the picture, so that is made stale rather than the download retried here.
			if ctx.Err() == nil && listRefreshes(p.ID, err) {
				a.chats.stale()
			}
			return nil, "media_unavailable"
		}
		mime := ""
		if kind := resp.Header.Get("Content-Type"); kind != "" {
			mime = kind
		}
		held = &mediaItem{id: p.ID, mime: mime, data: body}
		store.keep(held)
	}
	if p.Offset > len(held.data) {
		return nil, "invalid_params"
	}
	end := p.Offset + mediaChunk
	if end > len(held.data) {
		end = len(held.data)
	}
	// A byte slice marshals as base64, matching what the app already expects from the other helpers.
	return map[string]any{
		"total": len(held.data), "next": end, "mime": held.mime, "data": held.data[p.Offset:end],
	}, ""
}
