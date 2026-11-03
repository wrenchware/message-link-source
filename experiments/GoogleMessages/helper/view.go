package main

import (
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"strings"
	"time"
)

type viewParticipant struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	Self     bool   `json:"self"`
	NativeID string `json:"nativeId"`
	Phone    string `json:"phone,omitempty"`
	// The first name on its own, which is how Google names a group's readers. The app matches a reader by it only
	// where nobody else in the conversation shares it.
	First string `json:"first,omitempty"`
}
type viewChat struct {
	ExpectedTransport string            `json:"expectedTransport"`
	Transport         string            `json:"transport"`
	OutgoingID        string            `json:"outgoingId"`
	PreviewOutgoing   bool              `json:"previewOutgoing"`
	ReadOnly          bool              `json:"readOnly"`
	ID                string            `json:"id"`
	Title             string            `json:"title"`
	Participants      []viewParticipant `json:"participants"`
	Preview           string            `json:"preview"`
	At                time.Time         `json:"at"`
	Unread            bool              `json:"unread"`
	Group             bool              `json:"group"`
	Archived          bool              `json:"archived"`
	Hidden            bool              `json:"hidden"`
	AvatarColor       string            `json:"avatarColor,omitempty"`
	// Who wrote a group's newest message, as Google names them beside its words: the list's line then says who it was
	// from the start, as the message itself does once read. Empty for the account's own and outside groups.
	PreviewSender string `json:"previewSender,omitempty"`
	Spam          bool   `json:"spam,omitempty"`
}
type viewMessage struct {
	ReplyTo   string         `json:"replyTo,omitempty"`
	Reactions []viewReaction `json:"reactions"`
	Media     []viewMedia    `json:"media"`
	TmpID     string         `json:"tmpId"`
	ID        string         `json:"id"`
	ChatID    string         `json:"chatId"`
	Sender    string         `json:"sender"`
	Text      string         `json:"text"`
	At        time.Time      `json:"at"`
	Status    string         `json:"status"`
	Transport string         `json:"transport,omitempty"`
	// Who has read a group message, in the names Google gives them. Never logged; the app matches each name to one
	// participant or drops it.
	ReadBy []string `json:"readBy,omitempty"`
	// Google reports the message went end-to-end encrypted (encryption.go). Appended: the literal is positional.
	Encrypted bool `json:"encrypted,omitempty"`
}

func mapChat(c *gmproto.Conversation) viewChat {
	knownContacts.note(c)
	counterpart := counterparts(c)
	participants := make([]viewParticipant, 0, len(c.Participants))
	names := []string{}
	for _, p := range c.Participants {
		name := p.GetFullName()
		if name == "" {
			name = p.GetFirstName()
		}
		if name == "" {
			name = p.GetFormattedNumber()
		}
		if name == "" {
			name = "Participant"
		}
		// Only a counterpart carries the header number. A conversation also lists rows the phone never
		// shows, including a second row for the same person, and two numbers leave the header nothing
		// it can safely print.
		phone := ""
		if counterpart(p) {
			phone = p.GetFormattedNumber()
			if phone == "" {
				phone = p.GetID().GetNumber()
			}
		}
		participants = append(participants, viewParticipant{
			ID: p.GetID().GetNumber(), Name: name, Self: p.GetIsMe(),
			NativeID: p.GetID().GetParticipantID(), Phone: phone, First: p.GetFirstName(),
		})
		if !p.GetIsMe() {
			names = append(names, name)
		}
	}
	title := c.GetName()
	if title == "" {
		title = strings.Join(names, ", ")
	}
	if title == "" {
		title = "Conversation"
	}
	return viewChat{expectedTransport(c), c.GetType().String(), c.GetDefaultOutgoingID(), strings.HasPrefix(c.GetLatestMessage().GetLatestMessageStatus().GetStatus().String(), "OUTGOING_"), c.GetReadOnly(), c.GetConversationID(), title, participants, c.GetLatestMessage().GetDisplayContent(), time.UnixMicro(c.GetLastMessageTimestamp()).UTC(), c.GetUnread(), c.GetIsGroupChat(), chatArchived(c), chatHidden(c), avatarColor(c), previewSender(c), chatSpam(c)}
}

func previewSender(c *gmproto.Conversation) string {
	latest := c.GetLatestMessage()
	if !c.GetIsGroupChat() || latest.GetFromMe() != 0 || strings.HasPrefix(latest.GetLatestMessageStatus().GetStatus().String(), "OUTGOING_") {
		return ""
	}
	return strings.TrimSpace(latest.GetDisplayName())
}

// Who the conversation is actually with. Conversation.otherParticipants is Google's own list of
// participant ids excluding this account, and isVisible marks the rows a client displays; the delete
// request already trusts isVisible for the same reason. An RCS conversation can repeat a person under a
// second identity, so "not me" alone does not identify one counterpart. Each signal is used only when it
// selects someone in this conversation, so a shape that populates neither still behaves as before.
func counterparts(c *gmproto.Conversation) func(*gmproto.Participant) bool {
	listed := map[string]bool{}
	for _, id := range c.GetOtherParticipants() {
		if id != "" {
			listed[id] = true
		}
	}
	matched, visible := false, false
	for _, p := range c.GetParticipants() {
		if p.GetIsMe() {
			continue
		}
		if listed[p.GetID().GetParticipantID()] {
			matched = true
		}
		if p.GetIsVisible() {
			visible = true
		}
	}
	return func(p *gmproto.Participant) bool {
		if p.GetIsMe() {
			return false
		}
		if matched {
			return listed[p.GetID().GetParticipantID()]
		}
		return !visible || p.GetIsVisible()
	}
}

// A compose hint, not a guarantee: sending retains the phone's automatic routing.
func expectedTransport(c *gmproto.Conversation) string {
	if c == nil || c.GetReadOnly() || c.GetDefaultOutgoingID() == "" {
		return ""
	}
	switch c.GetSendMode() {
	case gmproto.ConversationSendMode_SEND_MODE_AUTO:
		if c.GetType() == gmproto.ConversationType_RCS {
			return "RCS"
		}
		if c.GetType() != gmproto.ConversationType_SMS {
			return ""
		}
	case gmproto.ConversationSendMode_SEND_MODE_XMS, gmproto.ConversationSendMode_SEND_MODE_XMS_LATCH:
	default:
		return ""
	}
	// The phone's group SMS versus MMS preference is not exposed here.
	if c.GetIsGroupChat() {
		return "SMS/MMS"
	}
	return "SMS"
}
func mapMessage(m *gmproto.Message) viewMessage {
	reactions := []viewReaction{}
	for _, r := range m.GetReactions() {
		if key := reactionEmoji(r.GetData()); key != "" {
			for _, id := range r.GetParticipantIDs() {
				reactions = append(reactions, viewReaction{key, id})
			}
		}
	}
	parts := []string{}
	media := []viewMedia{}
	for i, p := range m.GetMessageInfo() {
		if content := p.GetMessageContent(); content != nil {
			parts = append(parts, content.GetContent())
		}
		if p.GetMediaContent() != nil {
			media = append(media, mediaDescriptor(m, p, i))
		}
	}
	return viewMessage{m.GetReplyMessage().GetMessageID(), reactions, media, m.GetTmpID(), m.GetMessageID(), m.GetConversationID(), m.GetParticipantID(), strings.Join(parts, "\n"), time.UnixMicro(m.GetTimestamp()).UTC(), m.GetMessageStatus().GetStatus().String(), messageTransport(m), groupReaders(m.GetMessageStatus()), messageEncrypted(m)}
}

// Who has read a group message, read the way upstream reads it (pkg/connector/handlegmessages.go): only for a message
// the phone reports as displayed, only from status text beginning "Read by ", split on ", ". Nothing here knows whether
// the conversation is a group; Google words a direct message's reading differently, and the app uses these names in
// groups alone.
func groupReaders(status *gmproto.MessageStatus) []string {
	if status.GetStatus() != gmproto.MessageStatusType_OUTGOING_DISPLAYED {
		return nil
	}
	text, ok := strings.CutPrefix(status.GetStatusText(), "Read by ")
	if !ok {
		return nil
	}
	var names []string
	for name := range strings.SplitSeq(text, ", ") {
		if name = strings.TrimSpace(name); name != "" {
			names = append(names, name)
		}
	}
	return names
}

// Per-message route from gmproto Message.type. Upstream documents 1 = SMS and
// 2/3 = MMS, and marks 4 = RCS as uncertain; any other value stays unknown.
func messageTransport(m *gmproto.Message) string {
	switch m.GetType() {
	case 1:
		return "SMS"
	case 2, 3:
		return "MMS"
	case 4:
		return "RCS"
	}
	return ""
}

type viewReaction struct {
	Key           string `json:"key"`
	ParticipantID string `json:"participantId"`
}
