package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

// What downloaded media this helper holds for chunked reads: at most mediaKeep items and mediaHeldBytes together, each
// let go once nobody has read it for mediaIdle. Sixteen items of up to 50 MiB each could hold 800 MiB.
const mediaHeldBytes = 64 * 1024 * 1024
const mediaIdle = 60 * time.Second

// A received photo's id goes into the path of voice.google.com/u/<n>/a/i/<id>, so it must stay one path segment. What
// Google's photo ids may contain has not been seen, so the id goes in as upstream's DownloadAttachment puts it, raw,
// and only what could take it out of its segment is refused: a slash or backslash, "?", "#" and "%" (which would
// start a query, a fragment or an escape), "." and "..", control characters and spaces, and an absurd length. A list
// of allowed characters that turned out too short would leave every photo unavailable, and escaping would change any
// id holding one of those characters from what upstream asks for. A refused id is reported by its shape only.
func validPhotoID(id string) bool {
	if id == "" || id == "." || id == ".." || len(id) > 1024 {
		return false
	}
	return !strings.ContainsFunc(id, func(r rune) bool { return r <= 0x20 || r == 0x7f || strings.ContainsRune(`/\?#%`, r) })
}

// The address libgv's DownloadAttachment reads, the id as it is (validPhotoID keeps it one segment).
func photoAddress(authUser, id string) string {
	return fmt.Sprintf(libgv.EndpointDownloadTemplate, url.PathEscape(authUser), id)
}

var errPhotoTooLarge = errors.New("photo exceeds limit")

// A received photo, from the address libgv's DownloadAttachment reads, but read no further than one byte past
// mediaLimit: DownloadAttachment reads the whole answer, however long, before anything here could refuse it.
func downloadPhoto(ctx context.Context, client *libgv.Client, id string) ([]byte, string, error) {
	resp, err := client.MakeRequest(ctx, http.MethodGet, photoAddress(client.AuthUser, id), url.Values{
		"s": {strconv.Itoa(int(gvproto.Attachment_Metadata_ORIGINAL))},
	}, nil, nil)
	if err != nil {
		return nil, "", err
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(io.LimitReader(resp.Body, mediaLimit+1))
	if err != nil {
		return nil, "", err
	}
	if len(data) > mediaLimit {
		return nil, "", errPhotoTooLarge
	}
	return data, resp.Header.Get("Content-Type"), nil
}

// A received photo for ui.mediaRead. A refusal of the photo is not a refusal of the account, as for a voicemail's
// recording and a contact's picture: it is answered media_unavailable, and the session is neither recovered nor
// replaced on its account. Through runtimeRead, one photo address Google would not serve replaced the client with one
// without the short-lived cookies, which the app then saved, and a second refusal answered auth_required.
func (a *adapter) photo(ctx context.Context, client *libgv.Client, id string) ([]byte, string, string) {
	if !validPhotoID(id) {
		a.diagnose(map[string]any{"photo": "refused_id", "shape": idShape(id)})
		return nil, "", "media_unavailable"
	}
	download := a.downloadAttachment
	if download == nil {
		download = downloadPhoto
	}
	data, mime, err := download(ctx, client, id)
	if err != nil {
		if libgv.IsAuthError(err) {
			a.diagnose(map[string]any{"photo": "refused", "status": statusOf(err)})
			return nil, "", "media_unavailable"
		}
		if errors.Is(err, errPhotoTooLarge) {
			return nil, "", "media_unavailable"
		}
		return nil, "", authCode(err)
	}
	return data, mime, ""
}

func (a *adapter) cached(id string) *heldMedia {
	a.mediaMu.Lock()
	defer a.mediaMu.Unlock()
	now := time.Now()
	a.trimMedia(now)
	for i, held := range a.media {
		if held.id == id {
			// Most recently read last, so the bounds let go of what was read longest ago.
			held.lastUse = now
			a.media = append(append(a.media[:i:i], a.media[i+1:]...), held)
			return held
		}
	}
	return nil
}

func (a *adapter) keep(held *heldMedia) {
	a.mediaMu.Lock()
	defer a.mediaMu.Unlock()
	held.lastUse = time.Now()
	a.media = append(a.media, held)
	a.trimMedia(held.lastUse)
	if a.mediaSweep == nil {
		a.mediaSweep = time.AfterFunc(mediaIdle, a.sweepMedia)
	}
}

// Keeps the most recently read media within mediaKeep items and mediaHeldBytes, and none idle for mediaIdle. Called
// with mediaMu held.
func (a *adapter) trimMedia(now time.Time) {
	kept := make([]*heldMedia, 0, len(a.media))
	total := 0
	for i := len(a.media) - 1; i >= 0; i-- {
		held := a.media[i]
		if now.Sub(held.lastUse) >= mediaIdle || len(kept) >= mediaKeep || total+len(held.data) > mediaHeldBytes {
			continue
		}
		total += len(held.data)
		kept = append(kept, held)
	}
	for i, j := 0, len(kept)-1; i < j; i, j = i+1, j-1 {
		kept[i], kept[j] = kept[j], kept[i]
	}
	a.media = kept
}

// Lets idle media go even when no other read comes along to trim it; armed while anything is held.
func (a *adapter) sweepMedia() {
	a.mediaMu.Lock()
	defer a.mediaMu.Unlock()
	a.mediaSweep = nil
	a.trimMedia(time.Now())
	if len(a.media) > 0 {
		a.mediaSweep = time.AfterFunc(mediaIdle, a.sweepMedia)
	}
}
