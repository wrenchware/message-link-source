package main

import (
	"context"
	"strconv"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	waTypes "go.mau.fi/whatsmeow/types"
	"google.golang.org/protobuf/proto"
)

// One reaction on one message, added or taken away. Messenger keeps one reaction per person, so adding replaces what
// this account had; an empty one removes it. Which connection carries it depends on the conversation, the same way
// messages do.
//
// A removal names the reaction it takes away, and Facebook's own removal does not: it clears whatever this account
// has. So it is sent only while that reaction is still this account's. A removal asked for from an older view of the
// message, after a different reaction replaced it, used to clear the newer one; now it changes nothing and says so.
func (a *adapter) reactTo(ctx context.Context, key int64, messageID, reaction string, remove bool) (any, string) {
	if key == 0 || messageID == "" || reaction == "" {
		return nil, "invalid_input"
	}
	target, known := a.chats.messageByID(key, messageID)
	if !known || target.ID == "" {
		return nil, "reaction_unavailable"
	}
	self := a.chats.viewer()
	if self == 0 {
		return nil, "login_required"
	}
	if remove {
		if current := a.chats.reactionBy(target.ID, self); current != reaction {
			logf("react chat=%s remove skipped: not this account's reaction now (has one=%v)", tag(key), current != "")
			return map[string]any{"outcome": "accepted", "changed": false}, ""
		}
		reaction = ""
	}
	if encrypted, _ := a.chats.encryptedThread(key); encrypted {
		return a.reactEncrypted(ctx, key, target, reaction, self)
	}
	if !a.readyToSend(ctx) {
		return nil, "not_connected"
	}
	tbl, err := a.request(ctx, func() []socket.Task {
		return []socket.Task{&socket.SendReactionTask{
			ThreadKey:       key,
			MessageID:       target.ID,
			Reaction:        reaction,
			ActorID:         self,
			SyncGroup:       1,
			SendAttribution: table.MESSENGER_INBOX_IN_THREAD,
		}}
	})
	if err != nil {
		logf("react chat=%s request failed", tag(key))
		return nil, "react_failed"
	}
	// Facebook answers with the reaction row itself, which is what the conversation is redrawn from. Showing it here
	// as well would be inventing state the account may not have: an outright refusal comes back as a failed task.
	if len(tbl.LSHandleFailedTask) > 0 {
		logf("react chat=%s refused", tag(key))
		return nil, "react_rejected"
	}
	touched := a.applyTable(ctx, tbl)
	if !touched.empty() {
		a.out.write(response{Event: "changed", Result: touched})
	}
	logf("react chat=%s accepted remove=%v", tag(key), reaction == "")
	return map[string]any{"outcome": "accepted"}, ""
}

// The same thing over the encrypted connection, where a reaction is an ordinary message that names the one it is
// about. Nothing comes back to confirm it, so what this account did is put into the conversation here.
func (a *adapter) reactEncrypted(ctx context.Context, key int64, target viewMessage, reaction string, self int64) (any, string) {
	a.mu.Lock()
	crypto := a.crypto
	a.mu.Unlock()
	if crypto == nil {
		return nil, "e2ee_off"
	}
	if !a.encryptedReady() {
		logf("react chat=%s encrypted socket is not up", tag(key))
		return nil, "e2ee_offline"
	}
	to, ok := a.encryptedAddress(key)
	if !ok {
		logf("react chat=%s has no encrypted address", tag(key))
		return nil, "react_failed"
	}
	reacted := &waCommon.MessageKey{RemoteJID: proto.String(to.String()), ID: proto.String(target.ID)}
	if target.Outgoing {
		reacted.FromMe = proto.Bool(true)
	}
	// A group has to be told whose message is being reacted to; a conversation between two people does not.
	if to.Server == waTypes.GroupServer {
		reacted.Participant = proto.String(waTypes.JID{User: target.Sender, Server: waTypes.MessengerServer}.String())
	}
	message := &waConsumerApplication.ConsumerApplication{
		Payload: &waConsumerApplication.ConsumerApplication_Payload{
			Payload: &waConsumerApplication.ConsumerApplication_Payload_Content{
				Content: &waConsumerApplication.ConsumerApplication_Content{
					Content: &waConsumerApplication.ConsumerApplication_Content_ReactionMessage{
						ReactionMessage: &waConsumerApplication.ConsumerApplication_ReactionMessage{
							Key:               reacted,
							Text:              proto.String(reaction),
							SenderTimestampMS: proto.Int64(time.Now().UnixMilli()),
						},
					},
				},
			},
		},
	}
	id := strconv.FormatInt(methods.GenerateEpochID(), 10)
	if _, err := crypto.client.SendFBMessage(ctx, to, message, nil, whatsmeow.SendRequestExtra{ID: waTypes.MessageID(id)}); err != nil {
		logf("react chat=%s encrypted send failed: %s", tag(key), scrub(err.Error()))
		return nil, "react_failed"
	}
	a.remember(ctx, key, target.ID, self, reaction)
	a.out.write(response{Event: "changed", Result: a.chats.react(key, target.ID, self, reaction)})
	logf("react chat=%s encrypted accepted remove=%v", tag(key), reaction == "")
	return map[string]any{"outcome": "accepted"}, ""
}
