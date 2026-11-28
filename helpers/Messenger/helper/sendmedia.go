package main

import (
	"context"
	"encoding/json"
	"math"
	"net/http"
	"strconv"
	"strings"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
)

// A file is sent in two parts: its bytes arrive a chunk at a time and are held here, then one call sends them. The
// pipe carries a line of JSON at a time, so a photograph handed over in one piece would be a line of megabytes.
//
// Only one file is ever in hand: the app sends one at a time and waits for the answer, and a second beginning at
// offset zero replaces whatever was there.
func (a *adapter) mediaStage(r request) (any, string) {
	if a.client == nil {
		return nil, "login_required"
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
	// The chunks have to arrive in order and add up to something sendable; anything else is a muddle of two files.
	if p.Offset != len(a.upload) || len(a.upload)+len(p.Data) > mediaLimit {
		return nil, "invalid_input"
	}
	a.upload = append(a.upload, p.Data...)
	return map[string]int{"next": len(a.upload)}, ""
}

// Sends what was staged. Facebook takes the file first and gives it a number, and the message then names that number
// rather than carrying the bytes.
func (a *adapter) sendMedia(ctx context.Context, r request) (any, string) {
	if a.client == nil {
		return nil, "login_required"
	}
	var p struct {
		ChatID string `json:"chatId"`
		Name   string `json:"name"`
		Mime   string `json:"mime"`
		Text   string `json:"text"`
		Voice  bool   `json:"voice"`
		// The recording's length, sent with a voice message (an addition: older callers leave it out, which reads as zero).
		Seconds float64 `json:"seconds"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	key, err := strconv.ParseInt(p.ChatID, 10, 64)
	if err != nil || len(a.upload) == 0 {
		return nil, "invalid_input"
	}
	staged := a.upload
	// Whether it goes or not, these bytes are spent: a failure must not leave them to be sent by the next attempt.
	a.upload = nil
	name := p.Name
	if name == "" {
		name = "file"
	}
	mime := p.Mime
	if mime == "" {
		mime = http.DetectContentType(staged)
	}
	if encrypted, _ := a.chats.encryptedThread(key); encrypted {
		// The encrypted connection carries its files a different way entirely: it encrypts them here and uploads
		// them itself, rather than handing them to Facebook and naming the number Facebook gives back.
		return a.sendEncryptedMedia(ctx, key, staged, name, mime, p.Text, voiceClip(p.Voice, mime), wholeSeconds(p.Seconds))
	}
	if !a.readyToSend(ctx) {
		return nil, "not_connected"
	}
	voice := voiceClip(p.Voice, mime)
	logf("sendMedia chat=%s bytes=%d mime=%s voice=%v", tag(key), len(staged), mime, voice)
	uploaded, err := a.uploadMedia(ctx, key, &httpclient.MercuryUploadMedia{Filename: name, MimeType: mime, MediaData: staged, IsVoiceClip: voice})
	if err != nil {
		logf("sendMedia chat=%s upload failed", tag(key))
		return nil, "send_failed"
	}
	attachment := uploaded.Payload.RealMetadata.GetFbId()
	if attachment == 0 {
		logf("sendMedia chat=%s upload returned no attachment", tag(key))
		return nil, "send_rejected"
	}
	otid := methods.GenerateEpochID()
	pending := strconv.FormatInt(otid, 10)
	tbl, err := a.request(ctx, func() []socket.Task {
		return []socket.Task{
			&socket.SendMessageTask{
				ThreadId:         key,
				Otid:             otid,
				Source:           table.MESSENGER_INBOX_IN_THREAD,
				InitiatingSource: table.FACEBOOK_INBOX,
				SendType:         table.MEDIA,
				SyncGroup:        1,
				AttachmentFBIds:  []int64{attachment},
				Text:             p.Text,
			},
			&socket.ThreadMarkReadTask{ThreadId: key, SyncGroup: 1, LastReadWatermarkTs: time.Now().UnixMilli()},
		}
	})
	if err != nil {
		// The file is uploaded and the message may or may not have gone. Nothing is sent again on its own.
		logf("sendMedia chat=%s request failed", tag(key))
		return nil, "send_failed"
	}
	a.applyTable(ctx, tbl)
	id, known := a.chats.sentAs(pending)
	if !known {
		if sent, ok := a.chats.messageByID(key, pending); ok && sent.ID != "" {
			id, known = sent.ID, true
		}
	}
	switch mediaSendRefusal(tbl, pending, known) {
	case "rejected":
		logf("sendMedia chat=%s refused", tag(key))
		return nil, "send_rejected"
	case "unknown":
		logf("sendMedia chat=%s confirmed and named failed", tag(key))
		return map[string]any{"outcome": "unknown", "otid": pending}, ""
	}
	if !known {
		logf("sendMedia chat=%s accepted with no id", tag(key))
		return map[string]any{"outcome": "unknown", "otid": pending}, ""
	}
	chat := strconv.FormatInt(key, 10)
	a.chats.markSent(key, p.Text, time.Now().UnixMilli())
	a.out.write(response{Event: "changed", Result: &changes{Chats: []string{chat}, Messages: map[string][]string{chat: {id}}}})
	logf("sendMedia chat=%s accepted message=%s", tag(key), tagOf(id))
	return map[string]any{"outcome": "accepted", "id": id, "otid": pending}, ""
}

// What Facebook's failure rows say about one media send. A row naming this send refuses it; a row naming none may be the
// mark-read task sent with it, so it refuses only a send Facebook did not also confirm, as a text send reads it. A send both
// confirmed and named as failed is unknown, never refused: the app offers Retry on a refusal, which would send it twice.
func mediaSendRefusal(tbl *table.LSTable, pending string, known bool) string {
	named, unnamed := false, false
	for _, row := range tbl.LSMarkOptimisticMessageFailed {
		named = named || row.OTID == pending
		unnamed = unnamed || row.OTID == ""
	}
	for _, row := range tbl.LSHandleFailedTask {
		named = named || row.OTID == pending
		unnamed = unnamed || row.OTID == ""
	}
	switch {
	case named && known:
		return "unknown"
	case named || unnamed && !known:
		return "rejected"
	}
	return ""
}

// A recording made in the app's composer goes as a voice message, as upstream sends one: Facebook's upload is told it is
// a voice clip, and an encrypted conversation gets an audio message marked push-to-talk. Anything that is not audio
// stays an ordinary file, and so does an audio file the person chose.
func voiceClip(voice bool, mime string) bool {
	return voice && strings.HasPrefix(strings.ToLower(mime), "audio/")
}

// The length the app gave, in the whole seconds Messenger carries. Nothing recorded here is negative or runs past a day,
// so anything outside that is taken as not given rather than sent.
func wholeSeconds(seconds float64) uint32 {
	if !(seconds > 0) || seconds > 86400 {
		return 0
	}
	return uint32(math.Round(seconds))
}

// Uploads retain caller-owned buffers and do not hold a cross-request state lock.
func (a *adapter) uploadMedia(ctx context.Context, key int64, media *httpclient.MercuryUploadMedia) (*types.MercuryUploadResponse, error) {
	return a.client.GetHTTP().SendMercuryUploadRequest(ctx, key, media)
}
