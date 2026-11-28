package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/url"
	"regexp"
	"strings"
	"sync"
	"time"
)

// Google Messages carries no preview of a link. RCS has a field for one and the phone does not fill it, so a link
// someone sends arrives as text and nothing else — which is why a shared article or video shows as a bare address
// here while the phone draws a card for it.
//
// The card is therefore made here, by asking the linked page for the picture it publishes for exactly this purpose.
// That means this computer requests the page. Which pages, and when, is deliberately narrow: only links already
// visible in the conversation the user has open, never the chat list, never history being walked in the background,
// and never anything the user has not been shown. The request carries no Google session, no cookies and no referrer.
//
// This is the same design already running for Messenger; the fetcher itself is the same file.

const linkPreviewPrefix = "link-preview:"

var previewLinks = regexp.MustCompile(`https?://[^\s<>"'` + "`" + `]+`)

// The one link a message gets a card for: the first real web address in it. Trailing punctuation and a closing
// bracket that belongs to the sentence rather than the address are trimmed off.
func firstPreviewURL(text string) string {
	for _, match := range previewLinks.FindAllString(text, -1) {
		match = strings.TrimRight(match, ".,;:!?")
		for _, pair := range []string{"()", "[]", "{}"} {
			for strings.HasSuffix(match, pair[1:]) && strings.Count(match, pair[1:]) > strings.Count(match, pair[:1]) {
				match = strings.TrimSuffix(match, pair[1:])
			}
		}
		parsed, err := url.Parse(match)
		if err != nil || parsed.User != nil || parsed.Host == "" {
			continue
		}
		if parsed.Scheme == "http" || parsed.Scheme == "https" {
			return match
		}
	}
	return ""
}

func generatedLinkPreview(id string) bool { return strings.HasPrefix(id, linkPreviewPrefix) }

// A card that was made for one message, kept against the text it was made from. An edited message is a different
// message as far as this is concerned, and its old card is not shown.
type linkPreview struct {
	link   string
	text   string
	result *linkPreviewResult
}

func previewID(messageID, link string) string {
	sum := sha256.Sum256([]byte(messageID + "\x00" + link))
	return linkPreviewPrefix + hex.EncodeToString(sum[:])
}

type previewTarget struct {
	id, text, link string
}

type previewCached struct {
	result  *linkPreviewResult
	expires time.Time
}

type previewJobs struct {
	mu        sync.Mutex
	a         *adapter
	ctx       context.Context
	fetch     func(context.Context, string) (*linkPreviewResult, error)
	queue     chan previewTarget
	pending   map[string]bool
	attempted map[string]time.Time
	cache     map[string]previewCached
}

func newPreviewJobs(a *adapter, ctx context.Context, fetch func(context.Context, string) (*linkPreviewResult, error)) *previewJobs {
	jobs := &previewJobs{a: a, ctx: ctx, fetch: fetch, queue: make(chan previewTarget, 200),
		pending: map[string]bool{}, attempted: map[string]time.Time{}, cache: map[string]previewCached{}}
	for range 2 {
		go jobs.work()
	}
	return jobs
}

// The app's request, naming messages it is actually showing. Nothing else in the helper ever calls this: history
// paging, the conversation list and the notification path never ask a website for anything.
func (a *adapter) loadLinkPreviews(r request) (any, string) {
	var p struct {
		ChatID string   `json:"chatId"`
		IDs    []string `json:"messageIds"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" {
		return nil, "invalid_input"
	}
	a.previewMu.Lock()
	if a.previewJobs == nil {
		a.previewJobs = newPreviewJobs(a, context.Background(), fetchLinkPreview)
	}
	jobs := a.previewJobs
	a.previewMu.Unlock()
	asked := 0
	for index, id := range p.IDs {
		if index >= 200 {
			break
		}
		a.mediaMu.Lock()
		known := a.messageRefs[id]
		a.mediaMu.Unlock()
		if known == nil {
			continue
		}
		text := mapMessage(known).Text
		link := firstPreviewURL(text)
		if link == "" {
			continue
		}
		// Already made, and still for this text: nothing to ask again.
		a.previewMu.Lock()
		current, made := a.previews[id]
		a.previewMu.Unlock()
		if made && current.text == text {
			continue
		}
		jobs.offer(previewTarget{id: id, text: text, link: link})
		asked++
	}
	return map[string]any{"queued": asked}, ""
}

func (jobs *previewJobs) offer(target previewTarget) {
	key, now := target.id+"\x00"+target.text, time.Now()
	jobs.mu.Lock()
	defer jobs.mu.Unlock()
	if jobs.ctx.Err() != nil || jobs.pending[key] || now.Before(jobs.attempted[key]) {
		return
	}
	// A page that has nothing to offer must not be asked again every time the conversation is scrolled past it.
	if len(jobs.attempted) >= 256 {
		var oldest string
		var when time.Time
		for candidate, expiry := range jobs.attempted {
			if !jobs.pending[candidate] && (oldest == "" || expiry.Before(when)) {
				oldest, when = candidate, expiry
			}
		}
		if oldest != "" {
			delete(jobs.attempted, oldest)
		}
	}
	select {
	case jobs.queue <- target:
		jobs.pending[key] = true
		jobs.attempted[key] = now.Add(10 * time.Minute)
	default:
		// Full. A later render of the same page offers it again; nothing waits here for room.
	}
}

// One request per address, however many messages carry it, with what it answered kept for a day. A page that gives
// nothing is remembered for ten minutes so a conversation full of the same dead link asks once.
func (jobs *previewJobs) result(link string) *linkPreviewResult {
	jobs.mu.Lock()
	cached, ok := jobs.cache[link]
	jobs.mu.Unlock()
	if ok && time.Now().Before(cached.expires) {
		return cached.result
	}
	ctx, cancel := context.WithTimeout(jobs.ctx, 10*time.Second)
	defer cancel()
	result, err := jobs.fetch(ctx, link)
	ttl := 24 * time.Hour
	if err != nil || result == nil || result.imageURL == "" {
		result, ttl = nil, 10*time.Minute
	}
	jobs.mu.Lock()
	if len(jobs.cache) >= 256 {
		var oldest string
		var when time.Time
		for candidate, entry := range jobs.cache {
			if oldest == "" || entry.expires.Before(when) {
				oldest, when = candidate, entry.expires
			}
		}
		delete(jobs.cache, oldest)
	}
	jobs.cache[link] = previewCached{result: result, expires: time.Now().Add(ttl)}
	jobs.mu.Unlock()
	return result
}

func (jobs *previewJobs) work() {
	for {
		select {
		case <-jobs.ctx.Done():
			return
		case target := <-jobs.queue:
			jobs.run(target)
		}
	}
}

func (jobs *previewJobs) run(target previewTarget) {
	key := target.id + "\x00" + target.text
	defer func() {
		jobs.mu.Lock()
		delete(jobs.pending, key)
		jobs.mu.Unlock()
	}()
	result := jobs.result(target.link)
	if result == nil || jobs.ctx.Err() != nil {
		return
	}
	// The message may have been edited or removed while the page was being asked. What is kept is checked against
	// the text the card was made from, so an obsolete card is never shown.
	a := jobs.a
	a.mediaMu.Lock()
	known := a.messageRefs[target.id]
	a.mediaMu.Unlock()
	if known == nil || mapMessage(known).Text != target.text {
		return
	}
	a.previewMu.Lock()
	if a.previews == nil || len(a.previews) >= 500 {
		a.previews = map[string]*linkPreview{}
	}
	a.previews[target.id] = &linkPreview{link: target.link, text: target.text, result: result}
	a.previewMu.Unlock()
	// The message again, now carrying its card. An update rather than an arrival: this is the same message the
	// conversation is already showing, and announcing it as new would alert about a message the user has read.
	a.out.write(response{Event: "messageUpdate", Result: a.viewMessage(known)})
}

// The card a message has, if one was made for the text it still has. Called while mapping a message for the app.
func (a *adapter) previewMedia(messageID, text string) (viewMedia, bool) {
	a.previewMu.Lock()
	current, ok := a.previews[messageID]
	a.previewMu.Unlock()
	if !ok || current.text != text || current.result == nil {
		return viewMedia{}, false
	}
	name := current.result.title
	if name == "" {
		name = "Shared link"
	}
	return viewMedia{
		ID: previewID(messageID, current.link), Name: name, Mime: "image/jpeg",
		Width: current.result.width, Height: current.result.height,
		FullSize: true, Link: current.link, Play: playableLink(current.link), Description: current.result.description,
	}, true
}

// Where the picture for a generated card is fetched from, by the id the app was given.
func (a *adapter) previewSource(id string) (string, bool) {
	if !generatedLinkPreview(id) {
		return "", false
	}
	a.previewMu.Lock()
	defer a.previewMu.Unlock()
	for messageID, current := range a.previews {
		if current.result != nil && previewID(messageID, current.link) == id {
			return current.result.imageURL, true
		}
	}
	return "", false
}

// Whether a preview stands for something that plays, so the app can draw a play badge on it rather than implying
// there is an article behind it.
func playableLink(link string) bool {
	parsed, err := url.Parse(link)
	if err != nil {
		return false
	}
	host := strings.TrimPrefix(strings.ToLower(parsed.Hostname()), "www.")
	path := strings.ToLower(parsed.Path)
	switch host {
	case "youtube.com", "m.youtube.com", "youtu.be":
		return true
	case "tiktok.com", "vm.tiktok.com":
		return true
	case "instagram.com":
		return strings.HasPrefix(path, "/reel/") || strings.HasPrefix(path, "/reels/")
	case "facebook.com", "fb.watch":
		return strings.HasPrefix(path, "/reel/") || strings.HasPrefix(path, "/watch") || strings.HasPrefix(path, "/share/v/")
	case "vimeo.com":
		return true
	}
	return false
}
