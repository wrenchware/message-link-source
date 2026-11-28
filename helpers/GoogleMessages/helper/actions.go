package main

import (
	"context"
	"encoding/json"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"strings"
	"unicode"
	"unicode/utf8"
)

// The phone's own row of six. Its smile+ picker sends any other emoji too, so any one emoji is taken (oneEmoji).
var commonReactions = []string{"👍", "❤️", "😂", "😮", "😢", "👎"}

// oneEmoji reports whether key is an emoji and nothing that reads as words: no letter, space or control, a digit, # or * only as a keycap's
// base, and at least one emoji symbol. A reaction that were words would reach the other person as a text. The app checks it is a single
// grapheme as well; this is the helper's own bound.
func oneEmoji(key string) bool {
	if key == "" || len(key) > 64 || !utf8.ValidString(key) {
		return false
	}
	keycap := strings.ContainsRune(key, '\u20E3')
	symbol := keycap
	for _, r := range key {
		if unicode.IsSpace(r) || unicode.IsControl(r) || unicode.IsLetter(r) || (unicode.IsDigit(r) && !keycap) {
			return false
		}
		if unicode.Is(unicode.So, r) {
			symbol = true
		}
	}
	return symbol
}

type actionInput struct {
	ChatID    string `json:"chatId"`
	MessageID string `json:"messageId"`
	Key       string `json:"key"`
	Remove    bool   `json:"remove"`
}
type actionOptions struct {
	CanReply bool     `json:"canReply"`
	Add      []string `json:"addReactions"`
	Remove   []string `json:"removeReactions"`
}

func reactionEmoji(data *gmproto.ReactionData) string {
	if data.GetUnicode() != "" {
		return data.GetUnicode()
	}
	return data.GetType().Unicode()
}
func ownReactions(m *gmproto.Message, c *gmproto.Conversation) []string {
	self := map[string]bool{c.GetDefaultOutgoingID(): true}
	for _, p := range c.GetParticipants() {
		if p.GetIsMe() {
			self[p.GetID().GetParticipantID()] = true
		}
	}
	result := []string{}
	for _, r := range m.GetReactions() {
		for _, id := range r.GetParticipantIDs() {
			if id != "" && self[id] {
				if key := reactionEmoji(r.GetData()); key != "" {
					result = append(result, key)
				}
				break
			}
		}
	}
	return result
}
func reactionAllowed(c *gmproto.Conversation, m *gmproto.Message, sim *gmproto.SIMCard) bool {
	return c != nil && m != nil && c.GetConversationID() == m.GetConversationID() && !c.GetReadOnly() &&
		!strings.Contains(m.GetMessageStatus().GetStatus().String(), "DELETED") && !strings.Contains(m.GetMessageStatus().GetStatus().String(), "DRAFT") &&
		(c.GetType() == gmproto.ConversationType_RCS || c.GetType() == gmproto.ConversationType_SMS) &&
		c.GetDefaultOutgoingID() != "" && sim.GetSIMParticipant().GetID() == c.GetDefaultOutgoingID() && sim.GetSIMData().GetSIMPayload() != nil
}

// The message an action or a reply names: the one already drawn, or else looked for in the conversation's newest page.
// messageRefs is cleared whole at 5000 entries, so a message still on screen can be missing from it. Nil with no error
// when the page does not hold it either.
func (a *adapter) messageRef(ctx context.Context, chatID, messageID string) (*gmproto.Message, error) {
	a.mediaMu.Lock()
	m := a.messageRefs[messageID]
	a.mediaMu.Unlock()
	if m != nil {
		return m, nil
	}
	page, err := a.client.FetchMessages(ctx, chatID, 20, nil)
	if err != nil {
		return nil, err
	}
	for _, msg := range page.GetMessages() {
		a.viewMessage(msg)
		if msg.GetMessageID() == messageID {
			m = msg
		}
	}
	return m, nil
}

func replyAllowed(c *gmproto.Conversation, m *gmproto.Message, sim *gmproto.SIMCard) bool {
	return reactionAllowed(c, m, sim) && c.GetType() == gmproto.ConversationType_RCS && m.GetMessageID() != "" &&
		(m.GetMessageStatus().GetStatus() == gmproto.MessageStatusType_INCOMING_COMPLETE ||
			m.GetMessageStatus().GetStatus() == gmproto.MessageStatusType_OUTGOING_COMPLETE ||
			m.GetMessageStatus().GetStatus() == gmproto.MessageStatusType_OUTGOING_DELIVERED ||
			m.GetMessageStatus().GetStatus() == gmproto.MessageStatusType_OUTGOING_DISPLAYED)
}
func reactionRequest(c *gmproto.Conversation, m *gmproto.Message, sim *gmproto.SIMCard, key string, remove bool) *gmproto.SendReactionRequest {
	if !reactionAllowed(c, m, sim) {
		return nil
	}
	own := ownReactions(m, c)
	emoji := oneEmoji(key)
	mine := false
	for _, k := range own {
		if k == key {
			mine = true
		}
	}
	if (!remove && !emoji) || (remove && !mine) {
		return nil
	}
	action := gmproto.SendReactionRequest_ADD
	if remove {
		action = gmproto.SendReactionRequest_REMOVE
	} else if len(own) > 0 {
		action = gmproto.SendReactionRequest_SWITCH
	}
	return &gmproto.SendReactionRequest{MessageID: m.GetMessageID(), ReactionData: gmproto.MakeReactionData(key), Action: action, SIMPayload: sim.GetSIMData().GetSIMPayload()}
}
func (a *adapter) actionCall(ctx context.Context, r request) (any, string) {
	if a.client == nil || a.client.AuthData.Browser == nil {
		return nil, "pairing_required"
	}
	var p actionInput
	if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" {
		return nil, "invalid_input"
	}
	// Marked read without looking the conversation up first, which only checked that it exists and cost a phone round
	// trip. A conversation the phone does not have fails at MarkRead itself, as read_failed; the app logs that and keeps
	// the conversation unread.
	if r.Method == "ui.markRead" {
		if p.MessageID == "" {
			return nil, "invalid_input"
		}
		if a.client.MarkRead(ctx, p.ChatID, p.MessageID) != nil {
			return nil, "read_failed"
		}
		return map[string]bool{"accepted": true}, ""
	}
	// A reaction takes the phone's description from the last minute when there is one, as a send does (send.go). Opening
	// the conversation and a message's options ask the phone each time, which renews what a reaction then takes.
	look := a.fetchedConversation
	if r.Method == "ui.react" {
		look = a.trustedConversation
	}
	c, source, err := look(ctx, p.ChatID)
	if err != nil || c == nil || c.GetConversationID() != p.ChatID {
		return nil, "history_failed"
	}
	if r.Method == "ui.chat" {
		return mapChat(c), ""
	}
	if p.MessageID == "" {
		return nil, "invalid_input"
	}
	a.simMu.RLock()
	sim := a.sims[c.GetDefaultOutgoingID()]
	a.simMu.RUnlock()
	if sim == nil {
		sim = c.GetSimCard()
	}
	m, err := a.messageRef(ctx, p.ChatID, p.MessageID)
	if err != nil {
		return nil, "history_failed"
	}
	if r.Method == "ui.actions" {
		opts := actionOptions{Add: []string{}, Remove: []string{}}
		opts.CanReply = replyAllowed(c, m, sim)
		if reactionAllowed(c, m, sim) {
			opts.Add = commonReactions
			opts.Remove = ownReactions(m, c)
		}
		return opts, ""
	}
	// Which description the reaction was checked against, for the app's log.
	answer := func(outcome string) (any, string) {
		return map[string]string{"outcome": outcome, "conversation": source}, ""
	}
	req := reactionRequest(c, m, sim, p.Key, p.Remove)
	if req == nil {
		return answer("rejected")
	}
	result, err := a.client.SendReaction(ctx, req)
	if err != nil {
		return answer("unknown")
	}
	if !result.GetSuccess() {
		return answer("rejected")
	}
	return answer("accepted")
}
