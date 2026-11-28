package main

import (
	"context"
	"encoding/json"
	"net/http"
	"strconv"
	"strings"

	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
)

// A file is sent in two parts: its bytes arrive a chunk at a time and are held here, then one call sends them. The
// pipe carries a line of JSON at a time, so a photograph handed over in one piece would be a line of megabytes.
//
// Only one file is ever in hand: the app sends one and waits for the answer, and a second beginning at offset zero
// replaces whatever was there.
func (a *readOnlyAdapter) uiMediaStage(raw json.RawMessage) (any, string) {
	if _, code := a.writeClientFor(); code != "" {
		return nil, code
	}
	var p struct {
		Offset int    `json:"offset"`
		Data   []byte `json:"data"`
	}
	if json.Unmarshal(raw, &p) != nil || p.Offset < 0 || len(p.Data) > mediaChunk {
		return nil, "invalid_params"
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if p.Offset == 0 {
		a.upload = nil
	}
	// The chunks have to arrive in order and add up to something sendable; anything else is a muddle of two files.
	if p.Offset != len(a.upload) || len(a.upload)+len(p.Data) > mediaLimit {
		return nil, "invalid_params"
	}
	a.upload = append(a.upload, p.Data...)
	return map[string]int{"next": len(a.upload)}, ""
}

// Uploads what was staged and sends a message naming it. Instagram takes the file first and gives it a number; the
// message then carries that number rather than the bytes.
func (a *readOnlyAdapter) uiSendMedia(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.writeClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		ChatID  string `json:"chatId"`
		Name    string `json:"name"`
		Mime    string `json:"mime"`
		ReplyTo string `json:"replyTo"`
		Voice   bool   `json:"voice"`
	}
	if json.Unmarshal(raw, &params) != nil || params.ChatID == "" {
		return nil, "invalid_params"
	}
	a.mu.Lock()
	file := a.upload
	a.upload = nil
	a.mu.Unlock()
	// Sending is what consumes the staged file, so a failure here does not leave it to be sent by accident later.
	if len(file) == 0 {
		return nil, "no_media_staged"
	}
	chat, code := a.writable(params.ChatID)
	if code != "" {
		return nil, code
	}
	session, ok := client.(mediaClient)
	if !ok || session.GetHTTP() == nil {
		return nil, "unsupported"
	}
	mime := params.Mime
	if mime == "" {
		mime = http.DetectContentType(file)
	}
	// The thread number is only the referer on the upload, but it is the one Instagram's own web client would send.
	uploaded, err := session.GetHTTP().SendMercuryUploadRequest(ctx, chat.threadKey, &httpclient.MercuryUploadMedia{
		Filename: params.Name, MimeType: mime, MediaData: file, IsVoiceClip: voiceClip(params.Voice, mime),
	})
	a.requests.note(ctx, &a.requests.mutations, err != nil)
	if err != nil {
		return nil, uploadCode(err)
	}
	if uploaded == nil {
		return nil, "upload_failed"
	}
	attachment := uploaded.Payload.RealMetadata.GetFbId()
	if attachment == 0 {
		// Some accounts cannot upload video through the web api at all and upstream falls back to Instagram's Android
		// one. That fallback is not built here, so this says the upload produced nothing rather than pretending the
		// message was sent.
		return nil, "upload_failed"
	}
	// The upload above may be repeated harmlessly; this send may not (httpclient.SendOnce).
	response, err := client.SendMedia(httpclient.SendOnce(ctx), &slidetypes.SendMediaRequest{
		AttachmentFBID:     strconv.FormatInt(attachment, 10),
		ThreadID:           chat.shortID,
		OfflineThreadingID: strconv.FormatInt(methods.GenerateEpochID(), 10),
		ReplyToMessageID:   optional(params.ReplyTo),
	})
	a.requests.note(ctx, &a.requests.mutations, err != nil)
	if err != nil {
		return nil, failureCode(err)
	}
	if response == nil {
		// The file reached Instagram and the message may or may not have. Never retried here.
		return nil, "send_uncertain"
	}
	sent := response.GetMessage()
	a.chats.stale()
	return map[string]any{"id": sent.MessageID, "at": sent.TimestampMS.UnixMilli()}, ""
}

// A recording made in the app's composer is uploaded as a voice clip, as upstream uploads a voice message, so Instagram
// draws it with its voice player. Anything that is not audio stays an ordinary upload.
func voiceClip(voice bool, mime string) bool {
	return voice && strings.HasPrefix(strings.ToLower(mime), "audio/")
}

// The upload is an ordinary request rather than a graph mutation, so its failures are named separately.
func uploadCode(err error) string {
	if code := failureCode(err); code != "connection_failed" {
		return code
	}
	return "upload_failed"
}

var _ = types.NONE
