package main

import (
	"context"
	"encoding/json"
	"strconv"

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

// Current Google Messages Web adds three things to a block that archive does not carry (the bundle
// served on 2026-11-03; docs/research/google-messages-archive-wire.md): field 5 says where the block
// was asked for, 1 being the conversation's details, the one place that needs no message named;
// fields 8 and 9 are the choices about shared location and extensions, 3 meaning none was made.
const blockChoiceNone = 3

func blockAndReportUpdate(c *gmproto.Conversation) *gmproto.UpdateConversationRequest {
	req := archiveConversationUpdate(c, gmproto.ConversationActionStatus_BLOCK_AND_REPORT)
	if req == nil {
		return nil
	}
	req.Action5 = &gmproto.ConversationAction5{Field2: true}
	unknown := req.ProtoReflect().GetUnknown()
	for _, field := range []protowire.Number{8, 9} {
		unknown = protowire.AppendTag(unknown, field, protowire.VarintType)
		unknown = protowire.AppendVarint(unknown, blockChoiceNone)
	}
	req.ProtoReflect().SetUnknown(unknown)
	return req
}

// Archive, unarchive and unblock match the current Google Messages Web action request. Delete matches
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
		return blockAndReportUpdate(c)
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
		return map[string]string{"outcome": "rejected", "reason": "not_sent"}, ""
	}
	// A lost response may still have changed the conversation. Never retry automatically.
	resp, err := a.client.UpdateConversation(ctx, req)
	if err != nil {
		return map[string]string{"outcome": "unknown"}, ""
	}
	if !resp.GetSuccess() {
		return map[string]string{"outcome": "rejected", "reason": refusalReason(resp.ProtoReflect().GetUnknown())}, ""
	}
	return map[string]string{"outcome": "accepted"}, ""
}

// Why Google refused, as numbers alone: the pinned protocol declares only success, and the website reads
// an error code from field 1 of response fields 4 and 5.
func refusalReason(raw []byte) string {
	reason := "refused"
	for len(raw) > 0 {
		number, kind, n := protowire.ConsumeTag(raw)
		if n < 0 {
			break
		}
		raw = raw[n:]
		size := protowire.ConsumeFieldValue(number, kind, raw)
		if size < 0 {
			break
		}
		if (number == 4 || number == 5) && kind == protowire.BytesType {
			if body, m := protowire.ConsumeBytes(raw[:size]); m >= 0 {
				reason += "_f" + strconv.Itoa(int(number)) + "e" + strconv.FormatUint(firstVarint(body), 10)
			}
		}
		raw = raw[size:]
	}
	return reason
}

func firstVarint(raw []byte) uint64 {
	for len(raw) > 0 {
		number, kind, n := protowire.ConsumeTag(raw)
		if n < 0 {
			return 0
		}
		raw = raw[n:]
		if number == 1 && kind == protowire.VarintType {
			value, m := protowire.ConsumeVarint(raw)
			if m < 0 {
				return 0
			}
			return value
		}
		size := protowire.ConsumeFieldValue(number, kind, raw)
		if size < 0 {
			return 0
		}
		raw = raw[size:]
	}
	return 0
}

// The phone field is optional since upstream 612eadb: an empty one is sent as none, as upstream's own
// DeleteConversation does, rather than as an empty number.
func nonEmpty(value string) *string {
	if value == "" {
		return nil
	}
	return &value
}
