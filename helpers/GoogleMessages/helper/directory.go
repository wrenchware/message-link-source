package main

import (
	"context"
	"encoding/json"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"strings"
)

type viewContact struct {
	ID    string `json:"id"`
	Name  string `json:"name"`
	Phone string `json:"phone"`
}

// Preserve national numbers; never guess a country code or reinterpret extensions.
func cleanPhone(input string) string {
	var b strings.Builder
	for _, r := range strings.TrimSpace(input) {
		switch {
		case r >= '0' && r <= '9':
			b.WriteRune(r)
		case r == '+' && b.Len() == 0:
			b.WriteRune(r)
		case r == ' ' || r == '-' || r == '(' || r == ')' || r == '.':
		default:
			return ""
		}
	}
	phone := b.String()
	digits := strings.TrimPrefix(phone, "+")
	if len(digits) < 3 || len(digits) > 15 {
		return ""
	}
	return phone
}

func directRequest(phone string) *gmproto.GetOrCreateConversationRequest {
	phone = cleanPhone(phone)
	if phone == "" {
		return nil
	}
	// Same direct-chat payload as upstream connector/startchat.go ResolveIdentifier.
	return &gmproto.GetOrCreateConversationRequest{Numbers: []*gmproto.ContactNumber{{MysteriousInt: 2, Number: phone, Number2: phone}}}
}

// Contacts as the app takes them: one row per usable number, in the phone's order, keeping the first name given for
// a number. The order matters for top contacts, where it is the phone's ranking.
func mapContacts(list []*gmproto.Contact) []viewContact {
	result := []viewContact{}
	seen := map[string]bool{}
	for _, c := range list {
		phone := cleanPhone(c.GetNumber().GetNumber())
		if phone == "" {
			phone = cleanPhone(c.GetNumber().GetNumber2())
		}
		if phone == "" || seen[phone] {
			continue
		}
		seen[phone] = true
		name := c.GetName()
		if name == "" {
			name = phone
		}
		result = append(result, viewContact{phone, name, phone})
	}
	return result
}

func (a *adapter) directoryCall(ctx context.Context, r request) (any, string) {
	if a.client == nil || a.client.AuthData.Browser == nil {
		return nil, "pairing_required"
	}
	switch r.Method {
	case "ui.chatList":
		var p struct {
			Count  int    `json:"count"`
			Folder string `json:"folder"`
		}
		if json.Unmarshal(r.Params, &p) != nil || p.Count < 1 || p.Count > 2000 {
			return nil, "invalid_input"
		}
		folder, ok := listFolder(p.Folder)
		if !ok {
			return nil, "invalid_input"
		}
		resp, err := a.listConversations(ctx, p.Count, folder)
		if err != nil {
			return nil, readErrorCode(err)
		}
		result := []viewChat{}
		for _, c := range resp.GetConversations() {
			if c.GetConversationID() != "" {
				result = append(result, mapChat(c))
			}
		}
		return result, ""
	case "ui.contacts":
		list, tries, err := readAllContacts(ctx, a.client.ListContactsPage)
		if err != nil {
			return nil, "contacts_failed"
		}
		return map[string]any{"contacts": mapContacts(list), "tries": tries}, ""
	case "ui.topContacts":
		// The phone's own ranking of who this account writes to most, as Messages for web shows it when starting a
		// chat. A read: it creates, sends and marks nothing. The app asks at most once per New chat and keeps the
		// answer for the connection, so this is never polled.
		resp, err := a.client.ListTopContacts(ctx)
		if err != nil {
			return nil, "contacts_failed"
		}
		return mapContacts(resp.GetContacts()), ""
	case "ui.startDirect":
		var p struct {
			Phone string `json:"phone"`
		}
		if json.Unmarshal(r.Params, &p) != nil {
			return nil, "invalid_input"
		}
		payload := directRequest(p.Phone)
		if payload == nil {
			return nil, "invalid_input"
		}
		resp, err := a.client.GetOrCreateConversation(ctx, payload)
		// A lost response may still have created a chat. Never retry automatically.
		if err != nil || resp.GetConversation().GetConversationID() == "" {
			return map[string]any{"outcome": "unknown"}, ""
		}
		return map[string]any{"outcome": "accepted", "chat": mapChat(resp.GetConversation())}, ""
	}
	return nil, "unsupported_method"
}
