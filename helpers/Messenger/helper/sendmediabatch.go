package main

import (
	"context"
	"encoding/json"
	"net/http"
	"strconv"
	"strings"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

const mediaBatchLimit = 10
const mediaBatchBytesLimit = 100 * 1024 * 1024

type mediaBatchFile struct {
	Name string `json:"name"`
	Mime string `json:"mime"`
}

func (a *adapter) mediaBatchDiscard() (any, string) {
	a.batchUploads = nil
	return map[string]bool{"discarded": true}, ""
}

// A batch is staged in strict file/chunk order. Reset on the first chunk prevents an abandoned attempt from leaking
// bytes into a later send, and the whole set remains invisible to Facebook until ui.sendMediaBatch.
func (a *adapter) mediaBatchStage(r request) (any, string) {
	var p struct {
		Index  int    `json:"index"`
		Offset int    `json:"offset"`
		Reset  bool   `json:"reset"`
		Data   []byte `json:"data"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.Index < 0 || p.Index >= mediaBatchLimit || p.Offset < 0 || len(p.Data) == 0 {
		return nil, "invalid_input"
	}
	if p.Reset {
		if p.Index != 0 || p.Offset != 0 {
			return nil, "invalid_input"
		}
		a.batchUploads = nil
	}
	if p.Index == len(a.batchUploads) {
		if p.Offset != 0 {
			return nil, "invalid_input"
		}
		a.batchUploads = append(a.batchUploads, nil)
	}
	if p.Index != len(a.batchUploads)-1 || p.Offset != len(a.batchUploads[p.Index]) || len(a.batchUploads[p.Index])+len(p.Data) > mediaLimit {
		return nil, "invalid_input"
	}
	total := len(p.Data)
	for _, data := range a.batchUploads {
		total += len(data)
	}
	if total > mediaBatchBytesLimit {
		return nil, "invalid_input"
	}
	a.batchUploads[p.Index] = append(a.batchUploads[p.Index], p.Data...)
	return map[string]int{"next": len(a.batchUploads[p.Index])}, ""
}

func (a *adapter) sendMediaBatch(ctx context.Context, r request) (any, string) {
	if a.client == nil {
		return nil, "login_required"
	}
	var p struct {
		ChatID string           `json:"chatId"`
		Files  []mediaBatchFile `json:"files"`
	}
	if json.Unmarshal(r.Params, &p) != nil || len(p.Files) < 2 || len(p.Files) > mediaBatchLimit || len(p.Files) != len(a.batchUploads) {
		return nil, "invalid_input"
	}
	key, err := strconv.ParseInt(p.ChatID, 10, 64)
	if err != nil {
		return nil, "invalid_input"
	}
	staged := a.batchUploads
	a.batchUploads = nil
	for index := range staged {
		if len(staged[index]) == 0 || !strings.HasPrefix(strings.ToLower(p.Files[index].Mime), "image/") || strings.EqualFold(p.Files[index].Mime, "image/gif") {
			return nil, "invalid_input"
		}
	}
	if encrypted, _ := a.chats.encryptedThread(key); encrypted {
		return a.sendEncryptedMediaBatch(ctx, key, staged, p.Files)
	}
	if !a.readyToSend(ctx) {
		return nil, "not_connected"
	}
	attachments := make([]int64, 0, len(staged))
	for index, data := range staged {
		name := p.Files[index].Name
		if name == "" {
			name = "photo"
		}
		mime := p.Files[index].Mime
		if mime == "" {
			mime = http.DetectContentType(data)
		}
		uploaded, failure := a.uploadMedia(ctx, key, &httpclient.MercuryUploadMedia{Filename: name, MimeType: mime, MediaData: data})
		if failure != nil || uploaded.Payload.RealMetadata.GetFbId() == 0 {
			logf("sendMediaBatch chat=%s upload failed index=%d", tag(key), index)
			return nil, "upload_failed"
		}
		attachments = append(attachments, uploaded.Payload.RealMetadata.GetFbId())
	}
	otid := methods.GenerateEpochID()
	pending := strconv.FormatInt(otid, 10)
	tbl, err := a.request(ctx, func() []socket.Task {
		return mediaBatchTasks(key, otid, attachments, time.Now().UnixMilli())
	})
	if err != nil {
		logf("sendMediaBatch chat=%s request failed", tag(key))
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
		return nil, "send_rejected"
	case "unknown":
		return map[string]any{"outcome": "unknown", "otid": pending}, ""
	}
	if !known {
		return map[string]any{"outcome": "unknown", "otid": pending}, ""
	}
	chat := strconv.FormatInt(key, 10)
	a.chats.markSent(key, "", time.Now().UnixMilli())
	a.out.write(response{Event: "changed", Result: &changes{Chats: []string{chat}, Messages: map[string][]string{chat: {id}}}})
	logf("sendMediaBatch chat=%s accepted message=%s count=%d", tag(key), tagOf(id), len(attachments))
	return map[string]any{"outcome": "accepted", "id": id, "otid": pending}, ""
}

func mediaBatchTasks(key, otid int64, attachments []int64, readAt int64) []socket.Task {
	return []socket.Task{
		&socket.SendMessageTask{ThreadId: key, Otid: otid, Source: table.MESSENGER_INBOX_IN_THREAD,
			InitiatingSource: table.FACEBOOK_INBOX, SendType: table.MEDIA, SyncGroup: 1, AttachmentFBIds: attachments},
		&socket.ThreadMarkReadTask{ThreadId: key, SyncGroup: 1, LastReadWatermarkTs: readAt},
	}
}
