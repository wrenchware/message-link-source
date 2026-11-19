package main

import (
	"context"
	"encoding/json"
	"strconv"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	waTypes "go.mau.fi/whatsmeow/types"
	"google.golang.org/protobuf/proto"
)

// Changing a message after it has gone: correcting its words, and taking it back. Both connections can do both, and
// they do them in entirely different ways — the ordinary one as a task, the encrypted one as another message that
// names the first. Which is used is decided by the conversation, not by the caller.

const editLimit = 20000

// Where an existing message is named on the encrypted connection. A group has to be told whose message it is; a
// conversation between two people does not.
func encryptedMessageKey(to waTypes.JID, target viewMessage) *waCommon.MessageKey {
	key := &waCommon.MessageKey{RemoteJID: proto.String(to.String()), ID: proto.String(target.ID)}
	if target.Outgoing {
		key.FromMe = proto.Bool(true)
	}
	if to.Server == waTypes.GroupServer {
		key.Participant = proto.String(waTypes.JID{User: target.Sender, Server: waTypes.MessengerServer}.String())
	}
	return key
}

// What both calls need first: the conversation, the message, and the right to change it.
func (a *adapter) mutationTarget(r request) (int64, viewMessage, bool, string) {
	var p struct {
		ChatID    string `json:"chatId"`
		MessageID string `json:"messageId"`
		Text      string `json:"text"`
		Everyone  bool   `json:"everyone"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return 0, viewMessage{}, false, "invalid_input"
	}
	key, err := strconv.ParseInt(p.ChatID, 10, 64)
	if err != nil || p.MessageID == "" {
		return 0, viewMessage{}, false, "invalid_input"
	}
	target, known := a.chats.messageByID(key, p.MessageID)
	if !known || target.ID == "" {
		return 0, viewMessage{}, false, "message_unavailable"
	}
	if target.Deleted {
		return 0, viewMessage{}, false, "message_unavailable"
	}
	encrypted, _ := a.chats.encryptedThread(key)
	return key, target, encrypted, ""
}

// Corrects the words of a message this account sent. Facebook refuses an edit of anyone else's, and refuses one of a
// message that carries a file rather than words; the app does not offer either, and this refuses them again.
func (a *adapter) editMessage(ctx context.Context, r request) (any, string) {
	var p struct {
		Text string `json:"text"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	key, target, encrypted, refusal := a.mutationTarget(r)
	if refusal != "" {
		return nil, refusal
	}
	if !target.Outgoing {
		return nil, "not_yours"
	}
	if p.Text == "" || len(p.Text) > editLimit {
		return nil, "invalid_input"
	}
	if encrypted {
		return a.editEncrypted(ctx, key, target, p.Text)
	}
	if a.client == nil {
		return nil, "login_required"
	}
	if !a.readyToSend(ctx) {
		return nil, "not_connected"
	}
	tbl, err := a.request(ctx, func() []socket.Task {
		return []socket.Task{&socket.EditMessageTask{MessageID: target.ID, Text: p.Text}}
	})
	if err != nil {
		logf("edit chat=%s failed", tag(key))
		return nil, "edit_failed"
	}
	a.applyTable(ctx, tbl)
	// Facebook answers an accepted edit with the edited row. An answer with no edit row in it is a refusal, and
	// upstream's bridge notes that it sometimes reverses itself afterwards — so an edit is never assumed to have
	// taken, and nothing here retries one.
	if len(tbl.LSEditMessage) == 0 {
		logf("edit chat=%s refused", tag(key))
		return nil, "edit_rejected"
	}
	logf("edit chat=%s message=%s accepted", tag(key), tagOf(target.ID))
	return map[string]any{"outcome": "accepted"}, ""
}

func (a *adapter) editEncrypted(ctx context.Context, key int64, target viewMessage, text string) (any, string) {
	a.mu.Lock()
	crypto := a.crypto
	a.mu.Unlock()
	if crypto == nil {
		return nil, "e2ee_off"
	}
	if !a.encryptedReady() {
		logf("edit chat=%s encrypted socket is not up", tag(key))
		return nil, "e2ee_offline"
	}
	to, ok := a.encryptedAddress(key)
	if !ok {
		return nil, "edit_failed"
	}
	at := time.Now().UnixMilli()
	message := &waConsumerApplication.ConsumerApplication{
		Payload: &waConsumerApplication.ConsumerApplication_Payload{
			Payload: &waConsumerApplication.ConsumerApplication_Payload_Content{
				Content: &waConsumerApplication.ConsumerApplication_Content{
					Content: &waConsumerApplication.ConsumerApplication_Content_EditMessage{
						EditMessage: &waConsumerApplication.ConsumerApplication_EditMessage{
							Key:         encryptedMessageKey(to, target),
							Message:     &waCommon.MessageText{Text: proto.String(text)},
							TimestampMS: proto.Int64(at),
						},
					},
				},
			},
		},
	}
	id := strconv.FormatInt(methods.GenerateEpochID(), 10)
	if _, err := crypto.client.SendFBMessage(ctx, to, message, nil, whatsmeow.SendRequestExtra{ID: waTypes.MessageID(id)}); err != nil {
		logf("edit chat=%s encrypted send failed: %s", tag(key), scrub(err.Error()))
		return nil, "edit_failed"
	}
	// Nothing comes back to confirm an encrypted edit, so the conversation is changed here and the stored copy with
	// it — otherwise the old words would return at the next start.
	touched := a.chats.applyEdit(key, target.ID, text, at)
	crypto.messages.rememberEdit(ctx, target.ID, text, at)
	if !touched.empty() {
		a.out.write(response{Event: "changed", Result: touched})
	}
	logf("edit chat=%s message=%s encrypted accepted", tag(key), tagOf(target.ID))
	return map[string]any{"outcome": "accepted"}, ""
}

// Takes a message back. On the ordinary connection Facebook offers two kinds — gone for everyone, or hidden only from
// this account — and they are different tasks. The encrypted connection offers only the first.
func (a *adapter) deleteMessage(ctx context.Context, r request) (any, string) {
	var p struct {
		Everyone bool `json:"everyone"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	key, target, encrypted, refusal := a.mutationTarget(r)
	if refusal != "" {
		return nil, refusal
	}
	if p.Everyone && !target.Outgoing {
		return nil, "not_yours"
	}
	if encrypted {
		if !p.Everyone {
			// Hiding a message from yourself alone has no equivalent here, and doing it locally would put it back at
			// the next start. Saying so is better than appearing to work.
			return nil, "delete_me_only_unsupported"
		}
		return a.deleteEncrypted(ctx, key, target)
	}
	if a.client == nil {
		return nil, "login_required"
	}
	if !a.readyToSend(ctx) {
		return nil, "not_connected"
	}
	tbl, err := a.request(ctx, func() []socket.Task {
		if p.Everyone {
			return []socket.Task{&socket.DeleteMessageTask{MessageId: target.ID}}
		}
		return []socket.Task{&socket.DeleteMessageMeOnlyTask{ThreadKey: key, MessageId: target.ID}}
	})
	if err != nil {
		logf("delete chat=%s everyone=%v failed", tag(key), p.Everyone)
		return nil, "delete_failed"
	}
	a.applyTable(ctx, tbl)
	logf("delete chat=%s message=%s everyone=%v accepted", tag(key), tagOf(target.ID), p.Everyone)
	return map[string]any{"outcome": "accepted"}, ""
}

func (a *adapter) deleteEncrypted(ctx context.Context, key int64, target viewMessage) (any, string) {
	a.mu.Lock()
	crypto := a.crypto
	a.mu.Unlock()
	if crypto == nil {
		return nil, "e2ee_off"
	}
	// A test standing in for the send has no socket to be up.
	if a.revokeSend == nil && !a.encryptedReady() {
		logf("delete chat=%s encrypted socket is not up", tag(key))
		return nil, "e2ee_offline"
	}
	to, ok := a.encryptedAddress(key)
	if !ok {
		return nil, "delete_failed"
	}
	// A revoke is application data rather than content: it is not a message in the conversation, it is an instruction
	// about one.
	message := &waConsumerApplication.ConsumerApplication{
		Payload: &waConsumerApplication.ConsumerApplication_Payload{
			Payload: &waConsumerApplication.ConsumerApplication_Payload_ApplicationData{
				ApplicationData: &waConsumerApplication.ConsumerApplication_ApplicationData{
					ApplicationContent: &waConsumerApplication.ConsumerApplication_ApplicationData_Revoke{
						Revoke: &waConsumerApplication.ConsumerApplication_RevokeMessage{
							Key: encryptedMessageKey(to, target),
						},
					},
				},
			},
		},
	}
	id := strconv.FormatInt(methods.GenerateEpochID(), 10)
	if err := a.sendRevoke(ctx, crypto, to, message, id); err != nil {
		logf("delete chat=%s encrypted send failed: %s", tag(key), scrub(err.Error()))
		return nil, "delete_failed"
	}
	// Taken back here too, and recorded as taken back, so the stored copy cannot put it back at the next start. Facebook
	// has taken it back whether or not the record is written, so the answer stays accepted either way.
	touched := a.chats.removeEncrypted(key, target.ID)
	a.forgetKept(crypto.messages, key, target.ID)
	if !touched.empty() {
		a.out.write(response{Event: "changed", Result: touched})
	}
	logf("delete chat=%s message=%s encrypted accepted", tag(key), tagOf(target.ID))
	return map[string]any{"outcome": "accepted"}, ""
}

func (a *adapter) sendRevoke(ctx context.Context, crypto *encrypted, to waTypes.JID, message *waConsumerApplication.ConsumerApplication, id string) error {
	if a.revokeSend != nil {
		return a.revokeSend(ctx, to, message, id)
	}
	_, err := crypto.client.SendFBMessage(ctx, to, message, nil, whatsmeow.SendRequestExtra{ID: waTypes.MessageID(id)})
	return err
}

// How long a deletion record that could not be written waits before the next try; it doubles, up to a minute.
var forgetRetryDelay = 500 * time.Millisecond

// Writes the record of a message taken back, and keeps trying while the helper runs when the write fails. The take-back
// itself is never sent again: Facebook has it. Without the record, the stored copy put the message back at the next
// start.
func (a *adapter) forgetKept(kept *messageStore, key int64, id string) {
	if kept.forget(context.Background(), key, id) == nil {
		return
	}
	go func() {
		defer func() { _ = recover() }()
		delay := forgetRetryDelay
		for attempt := 1; ; attempt++ {
			if !wait(a.lifetime, delay) {
				logf("a deleted message's record was still not written at exit message=%s", tagOf(id))
				return
			}
			if kept.forget(context.Background(), key, id) == nil {
				logf("a deleted message's record was written on try %d message=%s", attempt+1, tagOf(id))
				return
			}
			delay = min(delay*2, time.Minute)
		}
	}()
}
