package main

import (
	"context"
	"encoding/json"
	"strconv"
	"time"

	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
	"go.mau.fi/util/jsontime"
)

// The first methods here that change the account. Everything else this helper does is a read.
//
// Two ids that are not the same thing. A thread carries a short id (`thread_fbid`, which is also `ThreadInfo.ID`) and
// a long one (`thread_id`). Sending addresses the short one and marking read addresses the long one — upstream calls
// both fields `IGThreadIGID`, which is the trap stage 3 warned about when it recorded that these must never be
// collapsed into one guessed id. Both are taken from the thread Instagram itself returned, never derived from each
// other, and a conversation missing either cannot be addressed rather than being addressed by the other.
// Sending, reacting and editing address the short id. Marking read and unsending address the long one.
type writeClient interface {
	SendMessage(context.Context, *slidetypes.SendTextRequest) (*slidetypes.SendTextResponse, error)
	MarkRead(context.Context, *slidetypes.MarkReadRequest) (*slidetypes.MarkReadResponse, error)
	MarkReadValidation(context.Context, *slidetypes.MarkReadRequest) (*slidetypes.MarkReadValidationResponse, error)
	SendReaction(context.Context, *slidetypes.CreateReactionRequest) (*slidetypes.SendReactionResponse, error)
	EditMessage(context.Context, *slidetypes.EditMessageRequest) (*slidetypes.EditMessageResponse, error)
	UnsendMessage(context.Context, *slidetypes.UnsendMessageRequest) (*slidetypes.UnsendMessageResponse, error)
	SendMedia(context.Context, *slidetypes.SendMediaRequest) (*slidetypes.SendMediaResponse, error)
}

// Instagram's own web client tags where a message came from.
const sendAttribution = "igd_web_chat_tab:in_thread"

// Long enough for anything a person types, short enough that a runaway caller cannot post a novel.
const maxSendLength = 8192

func (a *readOnlyAdapter) writeClientFor() (writeClient, string) {
	a.mu.Lock()
	client := a.client
	verified := a.snapshot.IdentityVerified
	allowed := a.canMutate
	a.mu.Unlock()
	// The diagnostics run in a mode that cannot mutate at all, and that is enforced here rather than by not calling
	// these methods: a mode that merely never asks is not the same as one that would refuse.
	if !allowed {
		return nil, "read_only"
	}
	if client == nil || !verified {
		return nil, "login_required"
	}
	write, ok := client.(writeClient)
	if !ok {
		return nil, "unsupported"
	}
	return write, ""
}

func (a *readOnlyAdapter) uiSendText(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.writeClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		ChatID  string `json:"chatId"`
		Text    string `json:"text"`
		ReplyTo string `json:"replyTo"`
	}
	if json.Unmarshal(raw, &params) != nil || params.ChatID == "" || params.Text == "" || len(params.Text) > maxSendLength {
		return nil, "invalid_params"
	}
	chat, code := a.writable(params.ChatID)
	if code != "" {
		return nil, code
	}
	short := chat.shortID
	// One attempt: upstream would otherwise post the same message again after a lost answer (httpclient.SendOnce).
	response, err := client.SendMessage(httpclient.SendOnce(ctx), &slidetypes.SendTextRequest{
		IGThreadIGID:       &short,
		OfflineThreadingID: strconv.FormatInt(methods.GenerateEpochID(), 10),
		Text:               slidetypes.SensitiveString{Value: params.Text},
		SendAttribution:    ptr(sendAttribution),
		// A reply carries the message it answers; an ordinary message carries nothing rather than an empty string.
		ReplyToMessageID: optional(params.ReplyTo),
	})
	a.requests.note(ctx, &a.requests.mutations, err != nil)
	if err != nil {
		return nil, failureCode(err)
	}
	if response == nil {
		// Instagram answered with nothing recognizable. The message may or may not have been sent, so this is
		// reported as uncertain and never retried here.
		return nil, "send_uncertain"
	}
	sent := response.GetMessage()
	// The conversation has moved, so the list is no longer current.
	a.chats.stale()
	return map[string]any{"id": sent.MessageID, "at": sent.TimestampMS.UnixMilli()}, ""
}

// Tells Instagram this account has read the conversation as far as a message. Upstream sends the mutation and then a
// validation call, and both are made here for the same reason: one without the other leaves the account's own read
// state disagreeing with itself.
func (a *readOnlyAdapter) uiMarkRead(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.writeClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		ChatID    string `json:"chatId"`
		MessageID string `json:"messageId"`
		At        int64  `json:"at"`
	}
	if json.Unmarshal(raw, &params) != nil || params.ChatID == "" || params.MessageID == "" || params.At <= 0 {
		return nil, "invalid_params"
	}
	chat, known := a.chats.get(params.ChatID)
	if !known || chat.longID == "" {
		return nil, "chat_unknown"
	}
	// Only the inbox. Instagram's spam folder is its hidden message requests, and its own apps do not tell somebody
	// their request was seen until it is accepted — so opening one here must not tell them either. Sends, reactions
	// and edits were already held to this through writable(); marking read was the one change that was not.
	if chat.Folder != "inbox" {
		return nil, "chat_not_writable"
	}
	request := &slidetypes.MarkReadRequest{
		Metadata: slidetypes.MarkReadMetadata{IGThreadIGID: chat.longID},
		Data:     slidetypes.MarkReadData{MessageID: params.MessageID, ItemID: ptr("")},
	}
	marked, err := client.MarkRead(httpclient.SendOnce(ctx), request)
	a.requests.note(ctx, &a.requests.mutations, err != nil)
	if err != nil {
		return nil, failureCode(err)
	}
	// The validation call carries the message's time instead of the item id — the message's own, as upstream sends
	// it (igconnector/handlematrix.go HandleMatrixReadReceipt), which is the app's to supply.
	validation, err := client.MarkReadValidation(httpclient.SendOnce(ctx), &slidetypes.MarkReadRequest{
		Metadata: request.Metadata,
		Data:     slidetypes.MarkReadData{MessageID: params.MessageID, MessageTimestampMS: unixMilli(params.At)},
	})
	a.requests.note(ctx, &a.requests.mutations, err != nil)
	a.chats.stale()
	// The conversation was marked read either way; only the confirmation is in question. Saying it failed would invite
	// a retry of something that already happened. Answered says Instagram replied at all, and validated is what that
	// reply said about the id and time it was given — a call that succeeds can still say no, and reporting the call's
	// success as the answer hid exactly the mismatch this is here to show.
	result := map[string]any{"validated": err == nil && validation != nil && validation.Validated, "answered": err == nil && validation != nil}
	// How far short of the thread's own activity time the watermark Instagram recorded falls. Instagram counts a thread
	// unread against that activity time (upstream igconnector/backfill.go), and Messenger left one unread for being
	// 0.5 s short of it (2026-10-22); this measures whether Instagram does the same before anything is changed for it.
	// Read from the answer already received: nothing more is asked.
	if short, ok := watermarkShort(marked, chat.At); ok {
		result["watermarkShortMs"] = short
	}
	return result, ""
}

func ptr[T any](value T) *T { return &value }

func unixMilli(ms int64) jsontime.UnixMilliString {
	return jsontime.UnixMilliString(jsontime.UM(time.UnixMilli(ms)))
}

// An empty string is not a value Instagram should be given: a field that means "none" carries nothing.
func optional(value string) *string {
	if value == "" {
		return nil
	}
	return &value
}

// Adding or removing this account's reaction to a message. Instagram treats both as the same call with a different
// status, so removing is not a separate operation that could disagree with adding.
func (a *readOnlyAdapter) uiReact(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.writeClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		ChatID    string `json:"chatId"`
		MessageID string `json:"messageId"`
		Emoji     string `json:"emoji"`
		Remove    bool   `json:"remove"`
	}
	if json.Unmarshal(raw, &params) != nil || params.ChatID == "" || params.MessageID == "" {
		return nil, "invalid_params"
	}
	// Removing does not need an emoji; adding one without it would be a reaction to nothing.
	if !params.Remove && (params.Emoji == "" || len(params.Emoji) > 64) {
		return nil, "invalid_params"
	}
	chat, code := a.writable(params.ChatID)
	if code != "" {
		return nil, code
	}
	status := slidetypes.ReactionStatusCreated
	if params.Remove {
		status = slidetypes.ReactionStatusDeleted
	}
	_, err := client.SendReaction(httpclient.SendOnce(ctx), &slidetypes.CreateReactionRequest{Input: slidetypes.ReactionInput{
		Emoji: params.Emoji, ItemID: "", MessageID: params.MessageID, ReactionStatus: status, ThreadID: chat.shortID,
	}})
	a.requests.note(ctx, &a.requests.mutations, err != nil)
	if err != nil {
		return nil, failureCode(err)
	}
	a.chats.stale()
	return map[string]any{"ok": true}, ""
}

// Changing the words of a message this account already sent.
func (a *readOnlyAdapter) uiEditMessage(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.writeClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		ChatID    string `json:"chatId"`
		MessageID string `json:"messageId"`
		Text      string `json:"text"`
	}
	if json.Unmarshal(raw, &params) != nil || params.ChatID == "" || params.MessageID == "" ||
		params.Text == "" || len(params.Text) > maxSendLength {
		return nil, "invalid_params"
	}
	chat, code := a.writable(params.ChatID)
	if code != "" {
		return nil, code
	}
	_, err := client.EditMessage(httpclient.SendOnce(ctx), &slidetypes.EditMessageRequest{
		ThreadID: chat.shortID, TargetItemID: "", Body: slidetypes.SensitiveString{Value: params.Text},
		OfflineThreadingID: strconv.FormatInt(methods.GenerateEpochID(), 10), TargetMessageID: params.MessageID,
	})
	a.requests.note(ctx, &a.requests.mutations, err != nil)
	if err != nil {
		return nil, failureCode(err)
	}
	a.chats.stale()
	return map[string]any{"ok": true}, ""
}

// Taking back a message this account sent, which removes it for everyone. Unsending addresses the long thread id,
// not the short one the other three use.
func (a *readOnlyAdapter) uiDeleteMessage(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.writeClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		ChatID    string `json:"chatId"`
		MessageID string `json:"messageId"`
	}
	if json.Unmarshal(raw, &params) != nil || params.ChatID == "" || params.MessageID == "" {
		return nil, "invalid_params"
	}
	chat, known := a.chats.get(params.ChatID)
	if !known || chat.longID == "" {
		return nil, "chat_unknown"
	}
	_, err := client.UnsendMessage(httpclient.SendOnce(ctx), &slidetypes.UnsendMessageRequest{
		MessageID: params.MessageID, SendData: slidetypes.SendData{ThreadID: chat.longID},
	})
	a.requests.note(ctx, &a.requests.mutations, err != nil)
	if err != nil {
		return nil, failureCode(err)
	}
	a.chats.stale()
	return map[string]any{"ok": true}, ""
}

// A conversation that can be written to, addressed by its short id. A message request or a spam thread is not one:
// answering a request accepts it, which is a separate decision nobody has made.
func (a *readOnlyAdapter) writable(chatID string) (*uiChat, string) {
	chat, known := a.chats.get(chatID)
	if !known || chat.shortID == "" {
		return nil, "chat_unknown"
	}
	if chat.Folder != "inbox" {
		return nil, "chat_not_writable"
	}
	return chat, ""
}

// How many milliseconds the watermark in Instagram's answer to a read falls short of the thread's activity time
// (negative when past it), and whether the answer carried one.
func watermarkShort(marked *slidetypes.MarkReadResponse, activity int64) (int64, bool) {
	if marked == nil || activity <= 0 || marked.ReadReceipt.WatermarkTimestampMS.IsZero() {
		return 0, false
	}
	return activity - marked.ReadReceipt.WatermarkTimestampMS.UnixMilli(), true
}
