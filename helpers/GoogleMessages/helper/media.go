package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"io"
	"strings"
	"time"
)

const mediaLimit = 50 * 1024 * 1024

// What a picture or video received may be, read or downloaded: the largest Google Messages itself sends. Sending stays at mediaLimit. A video of
// over 50 MB could not be opened once the server, with no copy of its own, had to fetch it (2026-10-12).
const downloadLimit = 100 * 1024 * 1024
const mediaChunk = 256 * 1024

// Independent bounded background downloads; polling never holds a network lock.
type mediaDownload struct {
	id      string
	done    chan struct{}
	data    []byte
	err     error
	preview bool
	// Room reserved while downloading, when last read or polled, and whether its last chunk has been read; see
	// mediaJobBudget.
	declared int
	lastUse  time.Time
	served   bool
}

func startMediaDownload(id string, fetch func() ([]byte, error)) *mediaDownload {
	job := &mediaDownload{id: id, done: make(chan struct{})}
	go func() { job.data, job.err = fetch(); close(job.done) }()
	return job
}

type viewMedia struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	Mime      string `json:"mime"`
	Size      int64  `json:"size"`
	Width     int64  `json:"width"`
	Height    int64  `json:"height"`
	Thumbnail bool   `json:"thumbnail"`
	FullSize  bool   `json:"fullSize"`
	// Where a link preview leads, and whether what it leads to plays. Set only for previews this helper made for a
	// link in the message text; a photo or video sent through Google Messages has neither.
	Link string `json:"link,omitempty"`
	Play bool   `json:"play,omitempty"`
	// What the linked page says about itself, for a generated preview.
	Description string `json:"description,omitempty"`
}

func mediaDescriptor(m *gmproto.Message, p *gmproto.MessageInfo, index int) viewMedia {
	c := p.GetMediaContent()
	raw, _ := json.Marshal([]any{m.GetConversationID(), m.GetMessageID(), index, c.GetMediaID(), c.GetThumbnailMediaID()})
	hash := sha256.Sum256(raw)
	mime := c.GetMimeType()
	if mime == "" {
		mime = libgm.FormatToMediaType[c.GetFormat()].Format
	}
	if mime == "" {
		mime = "application/octet-stream"
	}
	return viewMedia{ID: hex.EncodeToString(hash[:]), Name: c.GetMediaName(), Mime: mime, Size: c.GetSize(),
		Width: c.GetDimensions().GetWidth(), Height: c.GetDimensions().GetHeight(),
		Thumbnail: c.GetThumbnailMediaID() != "", FullSize: c.GetMediaID() != ""}
}
func (a *adapter) viewMessage(m *gmproto.Message) viewMessage {
	v := mapMessage(m)
	a.mediaMu.Lock()
	defer a.mediaMu.Unlock()
	if a.messageRefs == nil || len(a.messageRefs) >= 5000 {
		a.messageRefs = make(map[string]*gmproto.Message)
	}
	a.messageRefs[m.GetMessageID()] = m
	if a.mediaRefs == nil {
		a.mediaRefs = make(map[string]*gmproto.MediaContent)
	}
	for i, p := range m.GetMessageInfo() {
		if p.GetMediaContent() != nil {
			ref := mediaDescriptor(m, p, i)
			if len(a.mediaRefs) >= 2000 || a.fullSizeTargets == nil {
				a.mediaRefs = make(map[string]*gmproto.MediaContent)
				a.fullSizeTargets = make(map[string]fullSizeTarget)
			}
			a.mediaRefs[ref.ID] = p.GetMediaContent()
			a.fullSizeTargets[ref.ID] = fullSizeTarget{m.GetMessageID(), p.GetActionMessageID()}
		}
	}
	// A card made for a link in the text, if one was made and the text has not changed since. Google Messages sends
	// no preview of its own, so this is the only picture a link-only message can have.
	if len(v.Media) == 0 {
		if card, ok := a.previewMedia(m.GetMessageID(), v.Text); ok {
			v.Media = append(v.Media, card)
		}
	}
	return v
}
func (a *adapter) mediaRead(ctx context.Context, r request) (any, string) {
	if a.client == nil {
		return nil, "pairing_required"
	}
	var p struct {
		ID        string `json:"id"`
		Offset    int    `json:"offset"`
		Thumbnail bool   `json:"thumbnail"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.Offset < 0 {
		return nil, "invalid_input"
	}
	// A card this helper made for a link is fetched from the website that published it, not from Google. It is the
	// one picture here that never travelled through the phone, so it takes the anonymous fetcher rather than libgm.
	if source, ok := a.previewSource(p.ID); ok {
		return a.previewRead(ctx, p.ID, source, p.Offset)
	}
	a.mediaMu.Lock()
	c := a.mediaRefs[p.ID]
	target := a.fullSizeTargets[p.ID]
	a.mediaMu.Unlock()
	if c == nil || c.GetSize() > downloadLimit {
		return nil, "media_unavailable"
	}
	cacheID := p.ID
	if p.Thumbnail {
		cacheID += "-thumbnail"
	}
	client := a.client
	return a.downloadReadWait(ctx, mediaReadWait, cacheID, p.Offset, false, mediaDeclared(c, p.Thumbnail), func() ([]byte, error) {
		if p.Thumbnail && c.GetThumbnailMediaID() != "" {
			return readMedia(client.DownloadMedia(context.Background(), c.GetThumbnailMediaID(), c.GetThumbnailDecryptionKey()))
		}
		if p.Thumbnail {
			return nil, errors.New("thumbnail unavailable")
		}
		if c.GetMediaID() != "" {
			return readMedia(client.DownloadMedia(context.Background(), c.GetMediaID(), c.GetDecryptionKey()))
		}
		// Only a preview so far: ask the phone for the full image (upstream requestFullMedia). Its message
		// update carries the media ID, which gives the attachment a new identity and reloads it in the app.
		if needsFullSize(c) && a.claimFullSizeRequest(target) {
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			_, _ = client.GetFullSizeImage(ctx, target.messageID, target.actionID)
			cancel()
		}
		if c.GetThumbnailMediaID() != "" {
			return readMedia(client.DownloadMedia(context.Background(), c.GetThumbnailMediaID(), c.GetThumbnailDecryptionKey()))
		}
		if len(c.GetMediaData()) > 0 {
			return c.GetMediaData(), nil
		}
		return nil, errors.New("original unavailable")
	})
}

// What a download reserves before it starts (downloadReadJob): the size the message gives the original, 0 when it gives
// none. It gives none for a thumbnail, which is small, and reserving downloadLimit for each let two video thumbnails
// hold the whole budget and keep every other download waiting. A thumbnail larger than thumbnailReserve is handled as
// any download larger than it declared.
func mediaDeclared(c *gmproto.MediaContent, thumbnail bool) int64 {
	if thumbnail {
		return thumbnailReserve
	}
	return c.GetSize()
}

const thumbnailReserve = 4 * 1024 * 1024

// How long a read waits for a download that is still running before it answers pending. The app waits 500 ms after a
// pending answer (GoogleMediaBackend.cs), so a picture fetched in a few milliseconds still took 500 ms, or 1.5 s, to
// appear (server logs, 2026-10-14). A download that finishes within this is answered at once; a slower one is polled
// as before.
const mediaReadWait = 400 * time.Millisecond

// External images always use the anonymous public-address fetcher.
func (a *adapter) previewRead(ctx context.Context, id, source string, offset int) (any, string) {
	// The fetcher refuses a card larger than previewImageLimit, so that is what it may hold, not the unknown-size reserve.
	return a.downloadReadWait(ctx, mediaReadWait, id, offset, true, previewImageLimit, func() ([]byte, error) {
		ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
		defer cancel()
		data, _, err := fetchPreviewImage(ctx, source)
		return data, err
	})
}

// Entries publish immutable bytes through done. Four originals and two website images can run, as far as what they
// reserve fits mediaJobBudget; completed entries are retained within it, so interleaved chunks do not continually refetch.
func (a *adapter) downloadRead(id string, offset int, fetch func() ([]byte, error)) (any, string) {
	return a.downloadReadJob(id, offset, false, 0, fetch)
}

func (a *adapter) downloadReadKind(id string, offset int, preview bool, fetch func() ([]byte, error)) (any, string) {
	return a.downloadReadJob(id, offset, preview, 0, fetch)
}

// What finished downloads may hold together: two of the largest items received. A download whose chunks are still
// being read is never evicted to make room; a new one answers pending instead. Evicting one mid-transfer made its next
// chunk fetch the whole file again, which evicted the other, until the app's 135 s deadline ran out (code review,
// round 2, 2026-10-12).
const mediaJobBudget = 2 * downloadLimit

// How long a finished download stays held for a reader that has not taken its last chunk. The app polls every 500 ms
// and reads chunks back to back, so a reader this quiet has given up.
const mediaJobIdle = 30 * time.Second

// Held for its reader: finished, not yet read to the end, and read (or polled) recently. Called with downloads held.
func (job *mediaDownload) inUse(now time.Time) bool {
	return job.err == nil && !job.served && now.Sub(job.lastUse) < mediaJobIdle
}

// How long a finished download is held after it was last read, however much room the bounds leave. Without it, up to
// seven finished downloads, 200 MiB between them, stayed held for as long as no new download came along.
const mediaJobExpiry = 60 * time.Second

// Drops finished downloads nobody has read for mediaJobExpiry, then those nobody is reading, least recently used first,
// until the count and byte bounds hold (with room to spare) or only downloads in use remain. Called with downloads held.
func (a *adapter) trimMediaJobs(now time.Time, keep *mediaDownload, room int) {
	for key, entry := range a.mediaJobs {
		select {
		case <-entry.done:
			if entry != keep && now.Sub(entry.lastUse) >= mediaJobExpiry {
				delete(a.mediaJobs, key)
			}
		default:
		}
	}
	for {
		held := 0
		var oldest *mediaDownload
		oldestKey := ""
		for key, entry := range a.mediaJobs {
			select {
			case <-entry.done:
				held += len(entry.data)
				if entry != keep && !entry.inUse(now) && (oldest == nil || entry.lastUse.Before(oldest.lastUse)) {
					oldest, oldestKey = entry, key
				}
			default:
			}
		}
		if oldest == nil || (len(a.mediaJobs) < 8 && held+room <= mediaJobBudget) {
			return
		}
		delete(a.mediaJobs, oldestKey)
	}
}

// What downloads other than skip hold or have reserved: the real size of each finished one still in use, the
// reservation of each still running. Called with downloads held.
func (a *adapter) committedMediaJobs(now time.Time, skip *mediaDownload) int {
	committed := 0
	for _, entry := range a.mediaJobs {
		if entry == skip {
			continue
		}
		select {
		case <-entry.done:
			if entry.inUse(now) {
				committed += len(entry.data)
			}
		default:
			committed += entry.declared
		}
	}
	return committed
}

// declared is the size the message gives for the item, 0 when it gives none. It is reserved before the download
// starts, so that downloads being read and downloads still running stay within mediaJobBudget together. A size that
// is not given reserves downloadLimit, the most a download may be: reserving nothing let four originals of unknown
// size each reach 100 MiB and all be held for their readers, 400 MiB against the 200 MiB budget (code review,
// round 3, 2026-10-13). So at most two of those run at once; more answer pending.
func (a *adapter) downloadReadJob(id string, offset int, preview bool, declared int64, fetch func() ([]byte, error)) (any, string) {
	return a.downloadReadWait(context.Background(), 0, id, offset, preview, declared, fetch)
}

// downloadReadJob for a reader that may wait: a download found or started still running is given up to wait, or until
// ctx ends, to finish before the answer is pending. Downloads are not held while it waits, so other reads go on.
func (a *adapter) downloadReadWait(ctx context.Context, wait time.Duration, id string, offset int, preview bool, declared int64, fetch func() ([]byte, error)) (any, string) {
	a.downloads.Lock()
	job := a.mediaJob(id, preview, declared, fetch)
	if job == nil {
		a.downloads.Unlock()
		return map[string]any{"pending": true}, ""
	}
	if wait > 0 && !job.finished() {
		a.downloads.Unlock()
		timer := time.NewTimer(wait)
		select {
		case <-job.done:
		case <-timer.C:
		case <-ctx.Done():
		}
		timer.Stop()
		a.downloads.Lock()
		// Let go while this read waited (it came back larger than it declared): the next read starts it again.
		if a.mediaJobs[id] != job {
			a.downloads.Unlock()
			return map[string]any{"pending": true}, ""
		}
	}
	defer a.downloads.Unlock()
	return a.serveMediaJob(job, offset, time.Now())
}

func (job *mediaDownload) finished() bool {
	select {
	case <-job.done:
		return true
	default:
		return false
	}
}

// The download for id: the one already held, or a new one started if what it reserves fits; nil when it must wait for
// room. Called with downloads held.
func (a *adapter) mediaJob(id string, preview bool, declared int64, fetch func() ([]byte, error)) *mediaDownload {
	if a.mediaJobs == nil {
		a.mediaJobs = make(map[string]*mediaDownload)
	}
	if job := a.mediaJobs[id]; job != nil {
		return job
	}
	now := time.Now()
	reserve := int(min(max(declared, 0), downloadLimit))
	if reserve == 0 {
		reserve = downloadLimit
	}
	// It came back larger than it had declared and was let go (below); this time its real size is reserved.
	if known := a.mediaSizes[id]; known > reserve {
		reserve = known
	}
	active, committed := 0, a.committedMediaJobs(now, nil)
	for _, entry := range a.mediaJobs {
		if !entry.finished() && entry.preview == preview {
			active++
		}
	}
	limit := 4
	if preview {
		limit = 2
	}
	if active >= limit || (committed > 0 && committed+reserve > mediaJobBudget) {
		return nil
	}
	a.trimMediaJobs(now, nil, reserve)
	delete(a.mediaSizes, id)
	job := &mediaDownload{id: id, done: make(chan struct{}), preview: preview, declared: reserve}
	a.mediaJobs[id] = job
	go func() {
		data, err := fetch()
		if len(data) > downloadLimit {
			data = nil
			err = errMediaTooLarge
		}
		a.downloads.Lock()
		defer a.downloads.Unlock()
		// Larger than the message declared, and holding it would pass the budget: it is not kept. Its reader is
		// answered pending and its next read reserves the real size, waiting for room as any other download does.
		if err == nil && len(data) > job.declared && a.mediaJobs[id] == job {
			if others := a.committedMediaJobs(time.Now(), job); others > 0 && others+len(data) > mediaJobBudget {
				delete(a.mediaJobs, id)
				if a.mediaSizes == nil || len(a.mediaSizes) >= 2000 {
					a.mediaSizes = make(map[string]int)
				}
				a.mediaSizes[id] = len(data)
				job.err = errors.New("media larger than declared")
				close(job.done)
				return
			}
		}
		job.data, job.err = data, err
		// Its reader has been polling; the finished bytes are held for it from now.
		job.lastUse = time.Now()
		close(job.done)
		a.trimMediaJobs(job.lastUse, job, 0)
		a.armMediaSweep()
	}()
	return job
}

// A chunk of a finished download, or pending while it runs. Called with downloads held.
func (a *adapter) serveMediaJob(job *mediaDownload, offset int, now time.Time) (any, string) {
	if !job.finished() {
		return map[string]any{"pending": true}, ""
	}
	job.lastUse = now
	// Empty is not a picture: Google gave a photo sent a minute earlier as zero bytes (2026-10-19). Dropped like a failure,
	// so the next read fetches it again instead of being handed nothing for as long as the job is held.
	if job.err != nil || len(job.data) == 0 || len(job.data) > downloadLimit {
		delete(a.mediaJobs, job.id)
		return nil, "media_unavailable"
	}
	if offset > len(job.data) {
		return nil, "invalid_input"
	}
	end := min(len(job.data), offset+mediaChunk)
	job.served = end == len(job.data)
	return map[string]any{"data": job.data[offset:end], "total": len(job.data), "next": end}, ""
}

// Trims finished downloads once nobody has read them for mediaJobExpiry, even when no other download comes along to
// trim them: one timer at a time, armed while any download is held. Called with downloads held.
func (a *adapter) armMediaSweep() {
	if a.mediaSweep == nil {
		a.mediaSweep = time.AfterFunc(mediaJobExpiry, a.sweepMediaJobs)
	}
}

func (a *adapter) sweepMediaJobs() {
	a.downloads.Lock()
	defer a.downloads.Unlock()
	a.mediaSweep = nil
	a.trimMediaJobs(time.Now(), nil, 0)
	if len(a.mediaJobs) > 0 {
		a.armMediaSweep()
	}
}

var errMediaTooLarge = errors.New("media exceeds limit")

func permittedUpload(mime string) bool {
	return strings.HasPrefix(mime, "image/") || strings.HasPrefix(mime, "video/") || strings.HasPrefix(mime, "audio/") || strings.HasPrefix(mime, "application/") || strings.HasPrefix(mime, "text/")
}
func (a *adapter) mediaStage(r request) (any, string) {
	if a.client == nil {
		return nil, "pairing_required"
	}
	var p struct {
		Offset int    `json:"offset"`
		Data   []byte `json:"data"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.Offset < 0 || len(p.Data) > mediaChunk {
		return nil, "invalid_input"
	}
	if p.Offset == 0 {
		a.upload = nil
	}
	if p.Offset != len(a.upload) || len(a.upload)+len(p.Data) > mediaLimit {
		return nil, "invalid_input"
	}
	a.upload = append(a.upload, p.Data...)
	return map[string]int{"next": len(a.upload)}, ""
}

type fullSizeTarget struct {
	messageID string
	actionID  string
}

// Media with only a thumbnail or inline preview. In-progress media with neither waits for the phone's own download.
func needsFullSize(c *gmproto.MediaContent) bool {
	return c.GetMediaID() == "" && (c.GetThumbnailMediaID() != "" || len(c.GetMediaData()) > 0)
}

// Once per message part per helper session, as upstream does.
func (a *adapter) claimFullSizeRequest(t fullSizeTarget) bool {
	if t.messageID == "" || t.actionID == "" {
		return false
	}
	a.mediaMu.Lock()
	defer a.mediaMu.Unlock()
	if a.fullSizeRequested == nil || len(a.fullSizeRequested) >= 2000 {
		a.fullSizeRequested = make(map[fullSizeTarget]bool)
	}
	if a.fullSizeRequested[t] {
		return false
	}
	a.fullSizeRequested[t] = true
	return true
}

// The library streams a download since upstream 6ce6b49; the helper still hands the app whole files, in chunks it
// reads back from its own cache, so it reads the stream to the end here. It reads no further than one byte past
// downloadLimit: a longer stream is refused before it is held whole, not after.
func readMedia(stream io.ReadCloser, err error) ([]byte, error) {
	if err != nil {
		return nil, err
	}
	defer stream.Close()
	data, err := io.ReadAll(io.LimitReader(stream, downloadLimit+1))
	if err != nil {
		return nil, err
	}
	if len(data) > downloadLimit {
		return nil, errMediaTooLarge
	}
	return data, nil
}
