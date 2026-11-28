package main

import (
	"context"
	"encoding/json"
	"strings"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

const groupLimit = 20

// groupRequest mirrors upstream connector/startchat.go CreateGroup: every member number, optional RCS group name.
func groupRequest(phones []string, name string) *gmproto.GetOrCreateConversationRequest {
	name = strings.TrimSpace(name)
	if len(phones) < 2 || len(phones) > groupLimit || len([]rune(name)) > 100 {
		return nil
	}
	seen := make(map[string]bool, len(phones))
	req := &gmproto.GetOrCreateConversationRequest{}
	for _, raw := range phones {
		phone := cleanPhone(raw)
		if phone == "" || seen[phone] {
			return nil
		}
		seen[phone] = true
		req.Numbers = append(req.Numbers, &gmproto.ContactNumber{MysteriousInt: 2, Number: phone, Number2: phone})
	}
	if name != "" {
		req.RCSGroupName = &name
	}
	return req
}

func (a *adapter) startGroupCall(ctx context.Context, r request) (any, string) {
	if a.client == nil || a.client.AuthData.Browser == nil {
		return nil, "pairing_required"
	}
	var p struct {
		Phones []string `json:"phones"`
		Name   string   `json:"name"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	payload := groupRequest(p.Phones, p.Name)
	if payload == nil {
		return nil, "invalid_input"
	}
	resp, err := a.client.GetOrCreateConversation(ctx, payload)
	// CREATE_RCS without a conversation means nothing was created yet: the phone asks for explicit RCS group
	// creation, which upstream sends as the same request with createRCSGroup. Any other uncertain result is
	// reported as unknown and never retried, because a lost response may still have created the group.
	if err == nil && resp.GetStatus() == gmproto.GetOrCreateConversationResponse_CREATE_RCS && resp.GetConversation().GetConversationID() == "" {
		if payload.RCSGroupName == nil {
			empty := ""
			payload.RCSGroupName = &empty
		}
		create := true
		payload.CreateRCSGroup = &create
		resp, err = a.client.GetOrCreateConversation(ctx, payload)
	}
	if err != nil || resp.GetConversation().GetConversationID() == "" {
		return map[string]any{"outcome": "unknown"}, ""
	}
	return map[string]any{"outcome": "accepted", "chat": mapChat(resp.GetConversation())}, ""
}
