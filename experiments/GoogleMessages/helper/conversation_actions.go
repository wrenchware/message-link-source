package main

import (
	"context"
	"encoding/json"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"google.golang.org/protobuf/encoding/protowire"
)

const (
	// Current Google Messages Web uses outer action codes 3 and 4 for unarchive and
	// archive. The pinned generated enum does not yet name these values.
	conversationActionUnarchive = gmproto.ConversationActionStatus(3)
	conversationActionArchive   = gmproto.ConversationActionStatus(4)
)

func chatArchived(c *gmproto.Conversation) bool {
	return c.GetStatus() == gmproto.ConversationStatus_ARCHIVED || c.GetStatus() == gmproto.ConversationStatus_KEEP_ARCHIVED
}

// Deleted conversations leave the conversation list.
func chatHidden(c *gmproto.Conversation) bool {
	return c.GetStatus() == gmproto.ConversationStatus_DELETED
}

// What Google's Spam & blocked folder holds, listed only in the Spam view.
func chatSpam(c *gmproto.Conversation) bool {
	switch c.GetStatus() {
	case gmproto.ConversationStatus_SPAM_FOLDER, gmproto.ConversationStatus_BLOCKED_FOLDER:
		return true
	}
	return false
}

func otherParticipantNumber(c *gmproto.Conversation) string {
	otherNumbers := map[string]bool{}
	isCounterpart := counterparts(c)
	for _, p := range c.GetParticipants() {
		if isCounterpart(p) && p.GetID().GetNumber() != "" {
			otherNumbers[p.GetID().GetNumber()] = true
		}
	}
	if !c.GetIsGroupChat() && len(otherNumbers) == 1 {
		for number := range otherNumbers {
			return number
		}
	}
	return ""
}

// The current web client sends a conversation identity in request field 6. The pinned
// generated protocol still labels that field as deleteData, so encode the current wire
// shape explicitly: conversation ID, optional stable RCS group ID, and optional direct
// participant number.
func conversationIdentity(c *gmproto.Conversation) []byte {
	id := c.GetConversationID()
	if id == "" {
		return nil
	}
	identity := protowire.AppendTag(nil, 1, protowire.BytesType)
	identity = protowire.AppendString(identity, id)
	if groupID := c.GetSomeKindOfGroupID().GetNestedID().GetId(); groupID != "" {
		group := protowire.AppendTag(nil, 1, protowire.BytesType)
		group = protowire.AppendString(group, groupID)
		identity = protowire.AppendTag(identity, 2, protowire.BytesType)
		identity = protowire.AppendBytes(identity, group)
	}
	if phone := otherParticipantNumber(c); phone != "" {
		identity = protowire.AppendTag(identity, 3, protowire.BytesType)
		identity = protowire.AppendString(identity, phone)
	}
	return identity
}

func archiveConversationUpdate(c *gmproto.Conversation, action gmproto.ConversationActionStatus) *gmproto.UpdateConversationRequest {
	identity := conversationIdentity(c)
	if len(identity) == 0 {
		return nil
	}
	req := &gmproto.UpdateConversationRequest{Action: action, ConversationID: c.GetConversationID()}
	unknown := protowire.AppendTag(nil, 6, protowire.BytesType)
	unknown = protowire.AppendBytes(unknown, identity)
	req.ProtoReflect().SetUnknown(unknown)
	return req
}

// Archive and unarchive match the current Google Messages Web action request. Delete matches
// libgm DeleteConversation, including the other participant's number for one-to-one conversations;
// groups send no number, as upstream does.
func conversationUpdate(c *gmproto.Conversation, action string) *gmproto.UpdateConversationRequest {
	id := c.GetConversationID()
	if id == "" {
		return nil
	}
	switch action {
	case "blockAndReport":
		// A person's number is what is blocked; a group has none to block.
		if c.GetIsGroupChat() || otherParticipantNumber(c) == "" {
			return nil
		}
		return archiveConversationUpdate(c, gmproto.ConversationActionStatus_BLOCK_AND_REPORT)
	case "unblock":
		return archiveConversationUpdate(c, gmproto.ConversationActionStatus_UNBLOCK)
	case "archive":
		return archiveConversationUpdate(c, conversationActionArchive)
	case "unarchive":
		return archiveConversationUpdate(c, conversationActionUnarchive)
	case "delete":
		phone := otherParticipantNumber(c)
		if !c.GetIsGroupChat() && phone == "" {
			return nil
		}
		return &gmproto.UpdateConversationRequest{Action: gmproto.ConversationActionStatus_DELETE, ConversationID: id,
			Data: &gmproto.UpdateConversationRequest_DeleteData{DeleteData: &gmproto.DeleteConversationData{ConversationID: id, Phone: nonEmpty(phone)}}}
	}
	return nil
}

func (a *adapter) updateChatCall(ctx context.Context, r request) (any, string) {
	if a.client == nil || a.client.AuthData.Browser == nil {
		return nil, "pairing_required"
	}
	var p struct {
		ChatID string `json:"chatId"`
		Action string `json:"action"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" {
		return nil, "invalid_input"
	}
	c, err := a.client.GetConversation(ctx, p.ChatID)
	if err != nil || c == nil || c.GetConversationID() != p.ChatID {
		return nil, "history_failed"
	}
	req := conversationUpdate(c, p.Action)
	if req == nil {
		return map[string]string{"outcome": "rejected"}, ""
	}
	// A lost response may still have changed the conversation. Never retry automatically.
	resp, err := a.client.UpdateConversation(ctx, req)
	if err != nil {
		return map[string]string{"outcome": "unknown"}, ""
	}
	if !resp.GetSuccess() {
		return map[string]string{"outcome": "rejected"}, ""
	}
	return map[string]string{"outcome": "accepted"}, ""
}

// The phone field is optional since upstream 612eadb: an empty one is sent as none, as upstream's own
// DeleteConversation does, rather than as an empty number.
func nonEmpty(value string) *string {
	if value == "" {
		return nil
	}
	return &value
}
