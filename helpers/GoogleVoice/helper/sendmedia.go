package main

import (
	"context"
	"encoding/json"
	"strings"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

// Google Voice sends a photo inside the text message itself, up to 2 MB, in these formats (upstream's limits). The app
// shrinks a larger photo before staging it.
const photoLimit = 2 * 1024 * 1024

var photoTypes = map[string]gvproto.ReqSendSMS_Media_Type{
	"image/jpeg": gvproto.ReqSendSMS_Media_JPEG,
	"image/png":  gvproto.ReqSendSMS_Media_PNG,
	"image/gif":  gvproto.ReqSendSMS_Media_GIF,
	"image/bmp":  gvproto.ReqSendSMS_Media_BMP,
	"image/tiff": gvproto.ReqSendSMS_Media_TIFF,
}

// The pipe to the app carries one line at a time, so a photo arrives in ordered chunks first. Nothing leaves the helper
// until ui.sendMedia.
func (a *adapter) mediaStage(r request) (any, string) {
	if a.current() == nil {
		return nil, "login_required"
	}
	var p struct {
		Offset int    `json:"offset"`
		Data   []byte `json:"data"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.Offset < 0 || len(p.Data) == 0 || len(p.Data) > mediaChunk {
		return nil, "invalid_input"
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if p.Offset == 0 {
		a.upload = nil
	}
	// Chunks out of order, or more than a photo may be, are two files muddled together.
	if p.Offset != len(a.upload) || len(a.upload)+len(p.Data) > photoLimit {
		a.upload = nil
		return nil, "invalid_input"
	}
	a.upload = append(a.upload, p.Data...)
	return map[string]int{"next": len(a.upload)}, ""
}

// Sends the staged photo, with the caption as the message's text, exactly once (see sendGuard). The staged bytes are
// let go whatever happens, so a second send cannot repeat them.
func (a *adapter) sendMedia(ctx context.Context, client *libgv.Client, r request) (any, string) {
	var p struct {
		ChatID string `json:"chatId"`
		Mime   string `json:"mime"`
		Text   string `json:"text"`
	}
	a.mu.Lock()
	data := a.upload
	a.upload = nil
	a.mu.Unlock()
	if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" || len(p.Text) > maxTextBytes {
		return nil, "invalid_input"
	}
	kind, supported := photoTypes[strings.ToLower(p.Mime)]
	if !supported || len(data) == 0 || len(data) > photoLimit {
		return nil, "invalid_input"
	}
	return a.sendRequest(ctx, client, &gvproto.ReqSendSMS{
		ThreadID: p.ChatID,
		Text:     p.Text,
		Media:    &gvproto.ReqSendSMS_Media{Type: kind, Data: data},
	})
}
