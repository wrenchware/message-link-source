package main

import (
	"context"
	"encoding/json"
	"errors"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"time"
)

func canType(c *gmproto.Conversation, sim *gmproto.SIMCard) bool {
	return c != nil && !c.GetReadOnly() && c.GetType() == gmproto.ConversationType_RCS && c.GetSendMode() == gmproto.ConversationSendMode_SEND_MODE_AUTO && c.GetDefaultOutgoingID() != "" && sim.GetSIMParticipant().GetID() == c.GetDefaultOutgoingID() && sim.GetSIMData().GetSIMPayload() != nil
}
func (a *adapter) typingCall(ctx context.Context, r request) (any, string) {
	if a.client == nil || a.client.AuthData.Browser == nil {
		return nil, "pairing_required"
	}
	var p struct {
		ChatID    string    `json:"chatId"`
		ExpiresAt time.Time `json:"expiresAt"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" {
		return nil, "invalid_input"
	}
	skipped := map[string]bool{"skipped": true}
	if time.Now().After(p.ExpiresAt) {
		return skipped, ""
	}
	// What the phone last said about the conversation, when it has said anything: a ping every few seconds of typing
	// waited for a phone round trip before each one.
	c := a.conversations.get(p.ChatID)
	if c == nil {
		var err error
		c, err = a.client.GetConversation(ctx, p.ChatID)
		if err != nil || c.GetConversationID() != p.ChatID {
			return skipped, ""
		}
		a.conversations.note(c)
	}
	a.simMu.RLock()
	sim := a.sims[c.GetDefaultOutgoingID()]
	a.simMu.RUnlock()
	if sim == nil {
		sim = c.GetSimCard()
	}
	if !canType(c, sim) || time.Now().After(p.ExpiresAt) {
		return skipped, ""
	}
	if a.client.SetTyping(ctx, p.ChatID, sim.GetSIMData().GetSIMPayload()) != nil {
		// A send let go of it (dispatch.go): not a failure, only no longer waited for.
		if errors.Is(context.Cause(ctx), errTypingSuperseded) {
			return skipped, ""
		}
		return nil, "typing_failed"
	}
	return map[string]bool{"submitted": true}, ""
}
