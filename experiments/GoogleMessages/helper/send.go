package main

import (
	"context"
	"encoding/json"
	"errors"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/events"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/util"
	"strings"
	"unicode/utf8"
)

type sendInput struct {
	ReplyTo string `json:"replyTo"`
	Name    string `json:"name"`
	Mime    string `json:"mime"`
	ChatID  string `json:"chatId"`
	Text    string `json:"text"`
}
type sendPermission struct {
	Allowed       bool   `json:"allowed"`
	Reason        string `json:"reason"`
	MaxTextLength int    `json:"maxTextLength"`
}

func textRequest(c *gmproto.Conversation, sim *gmproto.SIMCard, text, txn string) (*gmproto.SendMessageRequest, string) {
	if c == nil || c.GetConversationID() == "" || c.GetReadOnly() {
		return nil, "This conversation is read-only."
	}
	if c.GetDefaultOutgoingID() == "" || sim.GetSIMParticipant().GetID() != c.GetDefaultOutgoingID() || sim.GetSIMData().GetSIMPayload() == nil {
		return nil, "Waiting for the conversation's SIM information. Reconnect or select this chat again."
	}
	if strings.TrimSpace(text) == "" || !utf8.ValidString(text) || len([]rune(text)) > 65536 || txn == "" {
		return nil, "Enter a message within the text limit."
	}
	// Preserve the phone's automatic SMS/RCS selection; never force a network switch.
	return &gmproto.SendMessageRequest{ConversationID: c.GetConversationID(), TmpID: txn, SIMPayload: sim.GetSIMData().GetSIMPayload(), MessagePayload: &gmproto.MessagePayload{
		TmpID: txn, TmpID2: txn, ConversationID: c.GetConversationID(), ParticipantID: c.GetDefaultOutgoingID(),
		MessageInfo: []*gmproto.MessageInfo{{Data: &gmproto.MessageInfo_MessageContent{MessageContent: &gmproto.MessageContent{Content: text}}}},
	}}, ""
}
func (a *adapter) sendCall(ctx context.Context, r request) (any, string) {
	if a.client == nil || a.client.AuthData.Browser == nil {
		return nil, "pairing_required"
	}
	var p sendInput
	if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" {
		return nil, "invalid_input"
	}
	// The phone's description from the last minute when there is one, or a lookup (conversation_cache.go). Whichever it is
	// gets the same checks below, and the answer names which it was for the app's send log.
	c, source, err := a.trustedConversation(ctx, p.ChatID)
	if err != nil {
		return nil, readErrorCode(err)
	}
	if c == nil || c.GetConversationID() != p.ChatID {
		return nil, "history_failed"
	}
	answer := func(result map[string]any) (any, string) {
		result["conversation"] = source
		return result, ""
	}
	a.simMu.RLock()
	sim := a.sims[c.GetDefaultOutgoingID()]
	a.simMu.RUnlock()
	if sim == nil {
		sim = c.GetSimCard()
	}
	body := p.Text
	if r.Method == "ui.sendPermission" || r.Method == "ui.sendMedia" {
		body = "permission check"
	}
	txn := util.GenerateTmpID()
	req, reason := textRequest(c, sim, body, txn)
	if r.Method == "ui.sendPermission" {
		return sendPermission{req != nil, reason, 65536}, ""
	}
	if req == nil {
		return answer(map[string]any{"outcome": "rejected", "reason": reason})
	}
	if p.ReplyTo != "" {
		a.mediaMu.Lock()
		target := a.messageRefs[p.ReplyTo]
		a.mediaMu.Unlock()
		if !replyAllowed(c, target, sim) {
			return answer(map[string]any{"outcome": "rejected"})
		}
		req.Reply = &gmproto.ReplyPayload{MessageID: p.ReplyTo}
	}
	if r.Method == "ui.sendMedia" {
		data := a.upload
		a.upload = nil
		if len(data) == 0 || len(data) > mediaLimit || !permittedUpload(p.Mime) || len(p.Text) > 65536 || p.Name == "" {
			return answer(map[string]any{"outcome": "rejected"})
		}
		media, err := a.client.UploadMedia(context.Background(), data, p.Name, p.Mime)
		if err != nil {
			return answer(map[string]any{"outcome": "rejected"})
		}
		req.MessagePayload.MessageInfo = []*gmproto.MessageInfo{{Data: &gmproto.MessageInfo_MediaContent{MediaContent: media}}}
		if p.Text != "" {
			req.MessagePayload.MessageInfo = append(req.MessagePayload.MessageInfo, &gmproto.MessageInfo{Data: &gmproto.MessageInfo_MessageContent{MessageContent: &gmproto.MessageContent{Content: p.Text}}})
		}
	}
	// A send waits behind the mutation before it, an upload perhaps for a minute, so what the app checked may no longer hold.
	if _, held := a.heldSends.Load(p.ChatID); held {
		return answer(map[string]any{"outcome": "rejected", "reason": "This conversation isn't in your list any more. Open the one you want from the list."})
	}
	result, code := submitText(ctx, req, a.client.SendMessage)
	if code != "" {
		return nil, code
	}
	return answer(result)
}

// A conversation left the list (or came back) while sends may be queued for it. Not a mutation, so it passes those queued: mutationMethod.
func (a *adapter) holdSends(r request) (any, string) {
	var p struct {
		ChatID string `json:"chatId"`
		Held   bool   `json:"held"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" {
		return nil, "invalid_input"
	}
	if p.Held {
		a.heldSends.Store(p.ChatID, true)
	} else {
		a.heldSends.Delete(p.ChatID)
	}
	return true, ""
}

// Every SendMessage error used to be reported as pending, so a send Google's relay answered in 58 ms with an error, never
// passed to the phone, waited on a clock in the app for good (2026-10-20: no echo, absent from Messages for Web). Upstream
// (connector/handlematrix.go) keeps a send pending only when the phone did not answer, and fails it on any other error.
// Here a refusal by the relay (4xx) never reached the phone, so it is a code like the checks before the call; any other
// error (5xx, network, a closed connection, an odd answer) may have been passed on, so it is reported as unknown, never
// as sent or not sent. The code names the class for the app's send log.
func submitText(ctx context.Context, req *gmproto.SendMessageRequest, send func(context.Context, *gmproto.SendMessageRequest) (*gmproto.SendMessageResponse, error)) (map[string]any, string) {
	resp, err := send(ctx, req)
	outcome := "pending"
	var reason string
	if err != nil {
		reason = readErrorCode(err)
		// A RequestError unwraps to its HTTPError.
		var httpErr events.HTTPError
		status := 0
		if errors.As(err, &httpErr) && httpErr.Resp != nil {
			status = httpErr.Resp.StatusCode
		}
		switch {
		case status >= 400 && status < 500:
			return nil, sendRefusalCode(status)
		case errors.Is(err, libgm.ErrPhoneNotResponding) || errors.Is(err, context.DeadlineExceeded):
			// The relay took it and the phone has not answered: it may still send when it comes back, and its echo
			// resolves the transaction.
		default:
			outcome = "unknown"
		}
	} else if resp != nil && (resp.GetStatus() == gmproto.SendMessageResponse_FAILURE_2 || resp.GetStatus() == gmproto.SendMessageResponse_FAILURE_3 || resp.GetStatus() == gmproto.SendMessageResponse_FAILURE_4) {
		outcome = "rejected"
	}
	// A timeout can happen after the server accepts the request. Retain the transaction for echo reconciliation.
	result := map[string]any{"outcome": outcome, "pendingId": req.TmpID}
	if reason != "" {
		result["error"] = reason
	}
	// FAILURE_4 is the phone saying Google Messages is not its default SMS app (upstream connector/errors.go). Named,
	// so the app says that rather than a generic refusal, and knows the phone's setting from it (settings.go).
	if outcome == "rejected" && resp.GetStatus() == gmproto.SendMessageResponse_FAILURE_4 {
		result["code"] = notDefaultSMSCode
		result["reason"] = notDefaultSMSReason
	}
	return result, ""
}

// The relay's refusal as the codes the app already words for a send that never went.
func sendRefusalCode(status int) string {
	switch status {
	case 401:
		return "authentication_required"
	case 403:
		return "access_denied"
	case 429:
		return "rate_limited"
	default:
		return "send_rejected"
	}
}

const (
	notDefaultSMSCode = "not_default_sms_app"
	// Upstream's own wording (connector/bridgestate.go) says SMS and RCS both stop: Android lets only the default SMS
	// app send, and Google Messages turns its RCS chats off when it is not the default.
	notDefaultSMSReason = "Not sent. Google Messages isn't the default SMS app on your phone, so it can't send SMS, MMS or RCS. Make it the default SMS app on your phone, then send again."
)
