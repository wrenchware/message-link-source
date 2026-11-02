package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"

	"golang.org/x/sync/singleflight"
)

const linkPreviewPrefix = "link-preview:"

var previewLinks = regexp.MustCompile(`https?://[^\s<>"'` + "`" + `]+`)

// One preview per message, using an actual link already present in its text. Only that URL is requested; accompanying
// message text stays local. Requests use a separate HTTP client without the Messenger session.
func firstPreviewURL(text string) string {
	for _, match := range previewLinks.FindAllString(text, -1) {
		match = strings.TrimRight(match, ".,;:!?")
		for _, pair := range []string{"()", "[]", "{}"} {
			for strings.HasSuffix(match, pair[1:]) && strings.Count(match, pair[1:]) > strings.Count(match, pair[:1]) {
				match = strings.TrimSuffix(match, pair[1:])
			}
		}
		parsed, err := url.Parse(match)
		if err == nil && parsed.User == nil && shareWebLink(match) != "" {
			return match
		}
	}
	return ""
}

func generatedLinkPreview(id string) bool { return strings.HasPrefix(id, linkPreviewPrefix) }

// A card the service made for this message's link. Its address need not match the words: for a pasted Instagram profile
// Facebook's card and the typed address (with its ?xtok= share code) differ, and comparing them drew a second card under
// Facebook's own (2026-11-01). The service makes one card a message, so any card of its own carrying a link is that one.
func serviceLinkCard(id, link string) bool { return link != "" && !generatedLinkPreview(id) }

// Whether a message already holding this card needs none made for link.
func cardCovers(id, cardLink, link string) bool {
	return sameLink(cardLink, link) || serviceLinkCard(id, cardLink)
}

type linkPreviewTarget struct {
	thread         int64
	id, text, link string
}

func (target linkPreviewTarget) key() string {
	sum := sha256.Sum256([]byte(strconv.FormatInt(target.thread, 10) + "\x00" + target.id + "\x00" + target.text))
	return hex.EncodeToString(sum[:])
}

type linkPreviewCached struct {
	result  *linkPreviewResult
	expires time.Time
}

type linkPreviewJobs struct {
	mu           sync.Mutex
	wg           sync.WaitGroup
	ctx          context.Context
	a            *adapter
	fetch        func(context.Context, string) (*linkPreviewResult, error)
	queue        chan linkPreviewTarget
	pending      map[string]bool
	attempted    map[string]time.Time
	cache        map[string]linkPreviewCached
	imageRetries map[string]time.Time
	group        singleflight.Group
}

func newLinkPreviewJobs(a *adapter, ctx context.Context, fetch func(context.Context, string) (*linkPreviewResult, error)) *linkPreviewJobs {
	jobs := &linkPreviewJobs{a: a, ctx: ctx, fetch: fetch, queue: make(chan linkPreviewTarget, 200),
		pending: map[string]bool{}, attempted: map[string]time.Time{}, cache: map[string]linkPreviewCached{}, imageRetries: map[string]time.Time{}}
	for range 2 {
		jobs.wg.Add(1)
		go jobs.work()
	}
	return jobs
}

// Called only by an explicit ui.linkPreviews request: the selected conversation's, and the app's for a newly arrived
// message whose words are a Facebook or Instagram share (IncomingPhotos), so its card is ready before the conversation
// opens. History/archive/list reads never schedule website requests. The bounded queue and two workers leave the
// serialized Messenger request pipe free.
func (a *adapter) loadLinkPreviews(key int64, ids []string) {
	a.mu.Lock()
	if a.previewJobs == nil {
		ctx := a.lifetime
		if ctx == nil {
			ctx = context.Background()
		}
		a.previewJobs = newLinkPreviewJobs(a, ctx, fetchLinkPreview)
	}
	jobs := a.previewJobs
	a.mu.Unlock()
	for index, id := range ids {
		if index >= 200 {
			break
		}
		current, ok := a.chats.messageByID(key, id)
		if !ok || current.Deleted {
			continue
		}
		link := firstPreviewURL(current.Text)
		if link == "" {
			continue
		}
		pictured := false
		for _, item := range current.Media {
			if cardCovers(item.ID, item.Link, link) {
				pictured = true
				break
			}
		}
		if pictured {
			continue
		}
		jobs.offer(linkPreviewTarget{thread: key, id: current.ID, text: current.Text, link: link})
	}
}

func (jobs *linkPreviewJobs) offer(target linkPreviewTarget) {
	key, now := target.key(), time.Now()
	jobs.mu.Lock()
	defer jobs.mu.Unlock()
	if jobs.ctx.Err() != nil || jobs.pending[key] || now.Before(jobs.attempted[key]) {
		return
	}
	// Bound negative/recent attempts even when many old pages are visited.
	if len(jobs.attempted) >= 256 {
		var oldestKey string
		var oldest time.Time
		for candidate, expiry := range jobs.attempted {
			if !jobs.pending[candidate] && (oldestKey == "" || expiry.Before(oldest)) {
				oldestKey, oldest = candidate, expiry
			}
		}
		if oldestKey != "" {
			delete(jobs.attempted, oldestKey)
		}
	}
	select {
	case jobs.queue <- target:
		jobs.pending[key] = true
		jobs.attempted[key] = now.Add(10 * time.Minute)
	default:
		// A later selected-page render can offer it again; no goroutine is left waiting for space.
	}
}

func (jobs *linkPreviewJobs) result(link string) *linkPreviewResult {
	value, _, _ := jobs.group.Do(link, func() (any, error) {
		jobs.mu.Lock()
		cached, ok := jobs.cache[link]
		jobs.mu.Unlock()
		if ok && time.Now().Before(cached.expires) {
			return cached.result, nil
		}
		ctx, cancel := context.WithTimeout(jobs.ctx, 10*time.Second)
		defer cancel()
		result, err := jobs.fetch(ctx, link)
		ttl := 24 * time.Hour
		if err != nil || result == nil || result.imageURL == "" {
			result = nil
			ttl = 10 * time.Minute
		}
		jobs.mu.Lock()
		if len(jobs.cache) >= 256 {
			var oldestKey string
			var oldest time.Time
			for candidate, entry := range jobs.cache {
				if oldestKey == "" || entry.expires.Before(oldest) {
					oldestKey, oldest = candidate, entry.expires
				}
			}
			delete(jobs.cache, oldestKey)
		}
		jobs.cache[link] = linkPreviewCached{result: result, expires: time.Now().Add(ttl)}
		jobs.mu.Unlock()
		return result, nil
	})
	result, _ := value.(*linkPreviewResult)
	return result
}

func (jobs *linkPreviewJobs) work() {
	defer jobs.wg.Done()
	for {
		select {
		case <-jobs.ctx.Done():
			return
		case target := <-jobs.queue:
			if jobs.ctx.Err() != nil {
				return
			}
			current, exists := jobs.a.chats.messageByID(target.thread, target.id)
			needed := exists && !current.Deleted && current.Text == target.text
			for _, media := range current.Media {
				if cardCovers(media.ID, media.Link, target.link) {
					needed = false
					break
				}
			}
			if !needed {
				jobs.mu.Lock()
				delete(jobs.pending, target.key())
				jobs.mu.Unlock()
				continue
			}
			result := jobs.result(target.link)
			var item *attachment
			if result != nil && jobs.ctx.Err() == nil {
				item = jobs.a.chats.attachLinkPreview(target.thread, target.id, target.text, result)
				if item != nil {
					jobs.a.mu.Lock()
					kept := jobs.a.kept
					if kept == nil && jobs.a.crypto != nil {
						kept = jobs.a.crypto.messages
					}
					jobs.a.mu.Unlock()
					if kept != nil {
						kept.rememberLinkPreview(jobs.ctx, target.thread, target.id, target.text, item)
					}
				}
			}
			logf("link preview message=%s image=%v attached=%v", tagOf(target.id), result != nil, item != nil)
			jobs.mu.Lock()
			delete(jobs.pending, target.key())
			jobs.mu.Unlock()
			if item != nil && jobs.a.out != nil {
				chat := strconv.FormatInt(target.thread, 10)
				jobs.a.out.write(response{Event: "changed", Result: &changes{Messages: map[string][]string{chat: {target.id}}}})
			}
		}
	}
}

// Recheck the exact message after HTTP finishes. A deleted/edited message or a native preview arriving in the
// meantime must not receive an obsolete or duplicate generated card.
func (s *store) attachLinkPreview(key int64, id, originalText string, result *linkPreviewResult) *attachment {
	s.mu.Lock()
	defer s.mu.Unlock()
	current := s.messages[key][id]
	if current == nil || current.unsent || current.text != originalText || result == nil || result.imageURL == "" {
		return nil
	}
	link := firstPreviewURL(originalText)
	if link == "" {
		return nil
	}
	for _, existing := range s.attached[id] {
		if cardCovers(existing.id, existing.link, link) {
			return nil
		}
	}
	sum := sha256.Sum256([]byte(strconv.FormatInt(key, 10) + "\x00" + id + "\x00" + link))
	name := result.title
	if name == "" {
		name = "Shared link"
	}
	item := &attachment{id: linkPreviewPrefix + hex.EncodeToString(sum[:]), name: name, mime: "image/jpeg",
		source: result.imageURL, link: link, width: result.width, height: result.height, description: result.description,
		// A preview of something that plays gets the badge, wherever the preview came from.
		play: playableLink(link)}
	s.attach(id, item)
	return item
}

// The guarded insert keeps a completion racing a deletion/edit from writing an obsolete preview back to disk.
func (m *messageStore) rememberLinkPreview(ctx context.Context, key int64, id, originalText string, item *attachment) {
	if m == nil || item == nil {
		return
	}
	tx, err := m.db.BeginTx(ctx, nil)
	if err != nil {
		logf("link preview could not be kept")
		return
	}
	defer tx.Rollback()
	var protectedBody []byte
	if err = tx.QueryRowContext(ctx, `SELECT body FROM encrypted_message WHERE id=? AND thread=?
		AND NOT EXISTS(SELECT 1 FROM deleted_message WHERE id=?)`, id, key, id).Scan(&protectedBody); err != nil {
		return
	}
	body, err := unprotectString(protectedBody)
	if err != nil || body != originalText {
		return
	}
	sealed, err := protectStrings(item.name, item.mime, item.source, "", "", "", item.link, item.description)
	if err != nil {
		logf("link preview could not be protected")
		return
	}
	_, err = tx.ExecContext(ctx, `INSERT INTO message_attachment
		(id,message,thread,name,mime,size,width,height,video,animated,source,preview,playable,previewSource,link,description)
		SELECT ?,id,thread,?,?,0,?,?,0,0,?,?,?,?,?,? FROM encrypted_message
		WHERE id=? AND thread=? AND NOT EXISTS(SELECT 1 FROM deleted_message WHERE id=?)
		ON CONFLICT(id) DO UPDATE SET name=excluded.name,mime=excluded.mime,width=excluded.width,height=excluded.height,
		 source=excluded.source,link=excluded.link,description=excluded.description`, item.id, sealed[0], sealed[1], item.width, item.height,
		sealed[2], sealed[3], sealed[4], sealed[5], sealed[6], sealed[7], id, key, id)
	if err == nil {
		err = tx.Commit()
	}
	if err != nil {
		logf("link preview could not be kept")
	}
}

// An external CDN URL may expire independently of Messenger history. Drop only the generated preview and refresh
// its page once, then cool down on repeat failures so an unavailable website cannot create a request/event loop.
func (a *adapter) retryLinkPreview(id string) {
	a.chats.mu.Lock()
	var target *linkPreviewTarget
	for key, messages := range a.chats.messages {
		for messageID, current := range messages {
			list := a.chats.attached[messageID]
			for index, item := range list {
				if item.id != id || !generatedLinkPreview(id) {
					continue
				}
				a.chats.attached[messageID] = append(list[:index], list[index+1:]...)
				delete(a.chats.media, id)
				if !current.unsent && firstPreviewURL(current.text) == item.link {
					target = &linkPreviewTarget{thread: key, id: messageID, text: current.text, link: item.link}
				}
				break
			}
			if target != nil {
				break
			}
		}
		if target != nil {
			break
		}
	}
	a.chats.mu.Unlock()
	if target == nil {
		return
	}
	a.mu.Lock()
	jobs, kept := a.previewJobs, a.kept
	if kept == nil && a.crypto != nil {
		kept = a.crypto.messages
	}
	a.mu.Unlock()
	if kept != nil {
		_, _ = kept.db.ExecContext(context.Background(), `DELETE FROM message_attachment WHERE id=?`, id)
	}
	if jobs != nil {
		jobs.mu.Lock()
		now := time.Now()
		retry := !now.Before(jobs.imageRetries[target.link])
		if retry {
			if len(jobs.imageRetries) >= 256 {
				clear(jobs.imageRetries)
			}
			jobs.imageRetries[target.link] = now.Add(10 * time.Minute)
			delete(jobs.cache, target.link)
			delete(jobs.attempted, target.key())
		}
		jobs.mu.Unlock()
		if retry {
			jobs.offer(*target)
		}
	}
	if a.out != nil {
		chat := strconv.FormatInt(target.thread, 10)
		a.out.write(response{Event: "changed", Result: &changes{Messages: map[string][]string{chat: {target.id}}}})
	}
}
