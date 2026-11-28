package main

import (
	"context"
	"encoding/json"
	"net/http"
	"time"
)

// The helper downloads media itself so the request carries the same cookies and headers as the session, then serves it
// to the app in chunks, as the Google Messages helper does. A few downloads are kept: the app asks for one chunk at a
// time and several pictures load at once, so holding only the newest made each one download again for every chunk.
const mediaLimit = 25 << 20
const mediaChunk = 192 << 10
const mediaKeep = 6

// Resolve only a one-way diagnostic tag. No message, URL, name or media bytes cross the diagnostic pipe.
func (a *adapter) checkMedia(ctx context.Context, r request) (any, string) {
	var p struct {
		Tag string `json:"tag"`
	}
	if json.Unmarshal(r.Params, &p) != nil || len(p.Tag) != 6 {
		return nil, "invalid_input"
	}
	a.chats.mu.Lock()
	id := ""
	for candidate := range a.chats.media {
		if tagOf(candidate) == p.Tag {
			if id != "" { // Truncated diagnostic tags can collide: never choose arbitrarily.
				a.chats.mu.Unlock()
				return nil, "ambiguous_media"
			}
			id = candidate
		}
	}
	a.chats.mu.Unlock()
	if id == "" {
		return nil, "media_unavailable"
	}
	if _, encrypted := a.chats.secureFor(id); encrypted {
		return nil, "observation_only"
	}
	params, _ := json.Marshal(map[string]any{"id": id})
	result, code := a.mediaRead(ctx, request{Params: params})
	if code != "" {
		return nil, code
	}
	chunk := result.(map[string]any)
	return map[string]any{"total": chunk["total"], "chunk": len(chunk["data"].([]byte))}, ""
}

type mediaCache struct {
	id   string
	mime string
	data []byte
	// When it was last kept or read. Changed only under mediaMu.
	used time.Time
}

// How long a finished download stays once nothing asks for it. The app reads one in chunks back to back, so a minute
// of quiet means it has what it wanted, and on the always-on server six of these could otherwise hold 150 MiB until
// later downloads happened to push them out.
const mediaIdle = 60 * time.Second

func (a *adapter) mediaClock() time.Time {
	if a.mediaNow != nil {
		return a.mediaNow()
	}
	return time.Now()
}

// Call with mediaMu held.
func (a *adapter) dropIdleMedia(now time.Time) {
	kept := a.media[:0]
	for _, item := range a.media {
		if now.Sub(item.used) < mediaIdle {
			kept = append(kept, item)
		}
	}
	clear(a.media[len(kept):]) // So the dropped buffers can be collected.
	a.media = kept
}

// A cache nobody touches is never trimmed by a touch, so one sweep is pending while anything is held. Call with
// mediaMu held.
func (a *adapter) sweepMediaLater() {
	if a.mediaSweep || len(a.media) == 0 {
		return
	}
	a.mediaSweep = true
	time.AfterFunc(mediaIdle, a.sweepMedia)
}

func (a *adapter) sweepMedia() {
	a.mediaMu.Lock()
	defer a.mediaMu.Unlock()
	a.mediaSweep = false
	a.dropIdleMedia(a.mediaClock())
	a.sweepMediaLater()
}

// Newest first. An entry's id, mime and data never change after publication.
func (a *adapter) cached(id string) *mediaCache {
	a.mediaMu.Lock()
	defer a.mediaMu.Unlock()
	now := a.mediaClock()
	a.dropIdleMedia(now)
	for index, item := range a.media {
		if item.id == id {
			if index > 0 {
				a.media = append(append([]*mediaCache{item}, a.media[:index]...), a.media[index+1:]...)
			}
			item.used = now
			return item
		}
	}
	return nil
}

func (a *adapter) keep(item *mediaCache) {
	a.mediaMu.Lock()
	defer a.mediaMu.Unlock()
	now := a.mediaClock()
	a.dropIdleMedia(now)
	item.used = now
	for index, old := range a.media {
		if old.id == item.id {
			a.media = append(a.media[:index], a.media[index+1:]...)
			break
		}
	}
	a.media = append([]*mediaCache{item}, a.media...)
	if len(a.media) > mediaKeep {
		clear(a.media[mediaKeep:])
		a.media = a.media[:mediaKeep]
	}
	a.sweepMediaLater()
}

func (a *adapter) mediaRead(ctx context.Context, r request) (any, string) {
	if a.client == nil {
		return nil, "login_required"
	}
	var p struct {
		ID     string `json:"id"`
		Offset int    `json:"offset"`
		Avatar bool   `json:"avatar"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.ID == "" || p.Offset < 0 {
		return nil, "invalid_input"
	}
	// An encrypted conversation's files come down the other connection and arrive encrypted; the rest are a plain
	// fetch from a url Facebook supplied.
	secure, encrypted := a.chats.secureFor(p.ID)
	source, mime, ok := a.chats.mediaSource(p.ID)
	if !encrypted && (!ok || source == "") {
		logf("media id=%s not known here known=%v addressed=%v", tagOf(p.ID), ok, source != "")
		return nil, "media_unavailable"
	}
	held := a.cached(p.ID)
	if held == nil {
		release, acquired := a.acquireDownload(ctx, p.Avatar, generatedLinkPreview(p.ID))
		if !acquired {
			return nil, "request_cancelled"
		}
		defer release()
		held = a.cached(p.ID)
	}
	if p.Offset == 0 {
		logf("media id=%s encrypted=%v cached=%v", tagOf(p.ID), encrypted, held != nil)
	}
	// An encrypted attachment that says it is larger than the limit could only fail once its bytes were in, so it is not
	// fetched: its length is the file's own. Lightspeed's Filesize is not held to this, since it may describe the original
	// upload where what is fetched is a smaller playable copy; the body's own bound (fetchBounded) stops those.
	if held == nil && encrypted && a.chats.mediaSize(p.ID) > mediaLimit {
		logf("media id=%s refused reason=declared_size", tagOf(p.ID))
		return nil, "media_unavailable"
	}
	if held == nil && encrypted {
		body, kind, fetched := a.fetchSecure(ctx, secure)
		if !fetched || len(body) > mediaLimit {
			logf("media id=%s encrypted fetch failed bytes=%d", tagOf(p.ID), len(body))
			return nil, "media_unavailable"
		}
		if kind != "" {
			mime = kind
		}
		held = &mediaCache{id: p.ID, mime: mime, data: body}
		a.keep(held)
	}
	if held == nil && generatedLinkPreview(p.ID) {
		// External preview images must never inherit Facebook cookies/headers, including after a failed fetch.
		body, kind, err := fetchPreviewImage(ctx, source)
		if err != nil {
			logf("link preview image id=%s fetched=false", tagOf(p.ID))
			a.retryLinkPreview(p.ID)
			return nil, "media_unavailable"
		}
		held = &mediaCache{id: p.ID, mime: kind, data: body}
		a.keep(held)
	}
	if held == nil {
		anonymous := a.anonymousMedia
		if anonymous == nil {
			anonymous = anonymousMedia
		}
		resp, body, unsigned, err := fetchMedia(ctx, a.client.GetHTTP(), anonymous, source)
		// A saved Facebook address can expire while the message and video still exist. Renew the exact message and
		// retry this read once, rather than making the person leave the viewer and page back to it again.
		if resp != nil && (resp.StatusCode == http.StatusForbidden || resp.StatusCode == http.StatusNotFound || resp.StatusCode == http.StatusGone) && ctx.Err() == nil {
			if fresh, renewed := a.refreshMediaSource(ctx, p.ID, source); renewed {
				resp, body, unsigned, err = fetchMedia(ctx, a.client.GetHTTP(), anonymous, fresh)
			}
		}
		// Whether a download off Meta ever happens, which nothing yet has shown.
		if unsigned {
			logf("media id=%s anonymous=yes", tagOf(p.ID))
		}
		// Asking Facebook for the conversation again would only name the same address, or the same file.
		if reason := mediaRefusal(err); reason != "" {
			logf("media id=%s refused reason=%s", tagOf(p.ID), reason)
			return nil, "media_unavailable"
		}
		if err != nil || resp == nil || resp.StatusCode != http.StatusOK || len(body) == 0 {
			status := 0
			if resp != nil {
				status = resp.StatusCode
			}
			logf("media id=%s download failed status=%d bytes=%d error=%v", tagOf(p.ID), status, len(body), err != nil)
			// Facebook's urls expire, and a stored one outlives its usefulness. The conversation goes back to
			// Facebook the next time it is read, which is the only place a fresh url comes from.
			if key, ok := a.chats.chatForMedia(p.ID); ok {
				a.chats.refreshAgain(key)
				logf("media id=%s chat=%s will be asked for again", tagOf(p.ID), tag(key))
			}
			return nil, "media_unavailable"
		}
		if kind := resp.Header.Get("Content-Type"); kind != "" {
			mime = kind
		}
		held = &mediaCache{id: p.ID, mime: mime, data: body}
		a.keep(held)
	}
	data := held.data
	if p.Offset > len(data) {
		return nil, "invalid_input"
	}
	end := p.Offset + mediaChunk
	if end > len(data) {
		end = len(data)
	}
	// A byte slice marshals as base64, matching what the app expects from the other helper.
	return map[string]any{"total": len(data), "next": end, "mime": held.mime, "data": data[p.Offset:end]}, ""
}

// One read-only anchored request, paced per attachment and serialized with its conversation's history reads.
// Receipt changes or unrelated messages are not completion: only a different address for this media lets it retry.
func (a *adapter) refreshMediaSource(ctx context.Context, id, failedSource string) (string, bool) {
	// A stuck history socket must not spend the media call's whole two-minute budget renewing one address.
	ctx, cancel := context.WithTimeout(ctx, 8*time.Second)
	defer cancel()
	key, target, known := a.chats.anchorForMedia(id)
	if !known || !a.pageRefreshable(key) {
		return "", false
	}
	value, _ := a.historyLocks.LoadOrStore(key, make(chan struct{}, 1))
	turn := value.(chan struct{})
	select {
	case turn <- struct{}{}:
	case <-ctx.Done():
		return "", false
	}
	defer func() { <-turn }()
	fresh := func() (string, bool) {
		source, _, ok := a.chats.mediaSource(id)
		return source, ok && source != "" && source != failedSource && ctx.Err() == nil
	}
	if source, ok := fresh(); ok {
		return source, true
	}
	if ctx.Err() != nil {
		return "", false
	}
	a.mediaMu.Lock()
	if last := a.mediaRefreshes[id]; !last.IsZero() && time.Since(last) < 30*time.Second {
		a.mediaMu.Unlock()
		return "", false
	}
	if a.mediaRefreshes == nil {
		a.mediaRefreshes = map[string]time.Time{}
	}
	a.mediaRefreshes[id] = time.Now()
	a.mediaMu.Unlock()
	anchor := a.chats.mediaRefreshAnchor(key, target)
	logf("media id=%s renew anchor_newer=%v", tagOf(id), anchor != target)
	tbl, err := a.fetchFrom(ctx, key, anchor)
	if err != nil {
		logf("media id=%s renew fetched=false", tagOf(id))
		return "", false
	}
	a.observeHistoryTable(tbl)
	a.applyTable(ctx, tbl)
	deadline := time.NewTimer(2 * time.Second)
	defer deadline.Stop()
	poll := time.NewTicker(50 * time.Millisecond)
	defer poll.Stop()
	for {
		if source, ok := fresh(); ok {
			logf("media id=%s renew fetched=true changed=true", tagOf(id))
			return source, true
		}
		select {
		case <-ctx.Done():
			return "", false
		case <-deadline.C:
			logf("media id=%s renew fetched=true changed=false", tagOf(id))
			return "", false
		case <-poll.C:
		}
	}
}

// Separate budgets prevent slow originals from exhausting the slots needed by faces or website cards.
// Cached chunks bypass these network slots entirely.
func (a *adapter) acquireDownload(ctx context.Context, avatar, preview bool) (func(), bool) {
	a.mediaMu.Lock()
	if a.downloadSlots[0] == nil {
		a.downloadSlots = [3]chan struct{}{make(chan struct{}, 4), make(chan struct{}, 2), make(chan struct{}, 2)}
	}
	kind := 0
	if avatar {
		kind = 1
	}
	if preview {
		kind = 2
	}
	slots := a.downloadSlots[kind]
	a.mediaMu.Unlock()
	select {
	case slots <- struct{}{}:
		if ctx.Err() != nil {
			<-slots
			return nil, false
		}
		return func() { <-slots }, true
	case <-ctx.Done():
		return nil, false
	}
}
