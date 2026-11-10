package main

import (
	"context"
	"fmt"
	"os"
	"strconv"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMsgApplication"
	wadevice "go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/store/sqlstore"
	waTypes "go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	waLog "go.mau.fi/whatsmeow/util/log"

	"github.com/google/uuid"
	"google.golang.org/protobuf/proto"

	// Pure Go, so the helper still builds without a C compiler.
	_ "modernc.org/sqlite"
)

// Most of a Messenger account's conversations have been moved to end-to-end encryption, and their messages do not
// travel on the Lightspeed protocol at all: that side carries only their existence and their timestamps. The messages
// themselves go over Meta's WhatsApp-derived channel, which needs a registered device of its own and keeps its keys
// between runs. This is that second connection.
//
// What it can do is carry conversations from the moment it is connected. What it cannot do is produce their past:
// that lives in an encrypted backup which only the account's PIN opens, and asking for the PIN is not this app's
// business. Upstream's bridge has the same boundary.
type encrypted struct {
	devices  *sqlstore.Container
	client   *whatsmeow.Client
	device   *wadevice.Device
	messages *messageStore
}

// Registers this computer as one of the account's devices if it is not already, then connects. Returns the reason it
// could not, as one of the helper's own codes.
//
// restore is the app asking for the device it already has, as it does at every start once encrypted conversations are
// on. It must never register: a lost or never-copied e2ee.db used to be recreated empty here and the computer
// registered again, silently adding a device to the account. With restore set, no e2ee.db (or one without this
// account's device) is "device_missing" and nothing is created or registered. Registration stays what an explicit
// enable in Settings, a first sign-in and the one recovery after Facebook removes the device ask for (restore false).
func (a *adapter) startEncrypted(ctx context.Context, restore bool) string {
	a.encryptedStart.Lock()
	defer a.encryptedStart.Unlock()
	a.mu.Lock()
	exists := a.crypto != nil
	a.mu.Unlock()
	if exists {
		return ""
	}
	self := a.chats.viewer()
	// Before the account folder is touched: a device that now lives on another machine is never started here.
	if movedAway(self) {
		logf("e2ee refused reason=moved_away")
		return "moved_away"
	}
	path := adoptLegacyDatabase(self, "e2ee.db", a.adoptLegacy)
	if path == "" {
		return "no_storage"
	}
	if restore {
		// sqlstore.New would create an empty e2ee.db (and its -wal/-shm) on the way to registering a new device.
		if info, err := os.Stat(path); err != nil || !info.Mode().IsRegular() {
			logf("e2ee restore refused reason=device_missing")
			return "device_missing"
		}
	}
	logger := waLog.Logger(&secureLog{})
	devices, err := sqlstore.New(ctx, "sqlite", sqliteFile(path, "_pragma=foreign_keys(1)&_pragma=busy_timeout(5000)"), logger)
	if err != nil {
		logf("e2ee device store failed")
		return "e2ee_store_failed"
	}
	// The pool on e2ee.db is closed on every way out except a socket that opened. A later attempt opens its own, and
	// each abandoned one used to stay open on the same file for the rest of the process.
	connected := false
	defer func() {
		if !connected {
			_ = devices.Close()
		}
	}()
	if self == 0 {
		return "unknown_account"
	}
	// The device registered on an earlier run is reused. It has to be found by looking through what is stored rather
	// than asked for by number: its number is assigned by Facebook at registration and is not known until then, so
	// asking for it by number finds nothing and every start would register another device on the account.
	stored, err := devices.GetAllDevices(ctx)
	if err != nil {
		logf("e2ee device lookup failed")
		return "e2ee_store_failed"
	}
	var device *wadevice.Device
	for _, candidate := range stored {
		if candidate.ID != nil && candidate.ID.User == strconv.FormatInt(self, 10) {
			device = candidate
			break
		}
	}
	fresh := device == nil
	if fresh && a.observeReceipts {
		return "existing_device_required"
	}
	if fresh && restore {
		logf("e2ee restore refused reason=device_missing")
		return "device_missing"
	}
	if fresh {
		device = devices.NewDevice()
	}
	if suggested := a.client.MessengerLite.GetSuggestedDeviceID(); !a.observeReceipts && suggested != uuid.Nil {
		device.FacebookUUID = suggested
	}
	a.client.SetDevice(device)
	if fresh {
		logf("e2ee registering this computer as a device")
		if err := a.client.RegisterE2EE(ctx, self); err != nil {
			// The call's two minutes ran out before Facebook answered. That is not a refusal, and Facebook may have
			// registered this computer after all, so the app is told which it was.
			if ctx.Err() != nil {
				logf("e2ee registration timed out")
				return "e2ee_register_timeout"
			}
			logf("e2ee registration refused")
			return "e2ee_register_failed"
		}
		if err := device.Save(ctx); err != nil {
			logf("e2ee device could not be saved")
			return "e2ee_store_failed"
		}
	}
	client, err := a.client.PrepareE2EEClient()
	if err != nil {
		logf("e2ee client could not be prepared")
		return "e2ee_failed"
	}
	client = observeReceiptAcks(client)
	// Its downloads bounded on the wire, since whatsmeow buffers a whole file before checking it (mediafetch.go).
	client.SetMediaHTTPClient(secureMediaHTTP())
	// Somewhere to write messages down has to be in hand before the socket opens, not after: Facebook delivers
	// everything waiting for the device the moment it connects and never again, so a message arriving in the gap
	// between connecting and being ready for it is lost for good. Everything kept from earlier runs is already back in
	// its conversations — the helper restores the whole store at login.
	a.mu.Lock()
	kept := a.kept
	a.crypto = &encrypted{devices: devices, client: client, device: device, messages: kept}
	a.deviceID = device.ID.Device
	a.mu.Unlock()
	if !a.observeReceipts {
		if moved := kept.absorb(ctx, path, a.chats); moved > 0 {
			logf("e2ee moved %d kept messages out of the device file", moved)
		}
	}
	a.chats.setSecure(true)
	client.AddEventHandler(a.encryptedEvent)
	if err := client.Connect(); err != nil {
		// Connect retries network failures on its own and returns only what retrying cannot fix: a refused upgrade, a
		// failed handshake, a deleted device. Back to how things stood before the call, conversations included: left
		// secure, they offered a composer whose every send, reaction and read then failed with e2ee_off. The client's
		// device now belongs to a closed pool; the next attempt sets a fresh one before it does anything else.
		a.mu.Lock()
		a.crypto = nil
		a.mu.Unlock()
		a.chats.setSecure(false)
		a.announceEncrypted()
		logf("e2ee socket would not open")
		return "e2ee_failed"
	}
	connected = true
	a.encryptedStateChanged()
	logf("e2ee connected, device reused=%v", !fresh)
	// Connect only starts the socket. Whether it came up is what decides if this conversation can be carried at all,
	// and saying so here turns a later silent failure into something with a time against it.
	go func() {
		defer func() { _ = recover() }()
		for round := 0; round < 20; round++ {
			if !wait(a.lifetime, time.Second) {
				return
			}
			if a.encryptedReady() {
				logf("e2ee ready after %ds", round+1)
				a.announceEncrypted()
				if a.observeReceipts {
					return
				}
				// Typing on this connection is only delivered to a client that says it is available.
				if err := client.SendPresence(a.lifetime, waTypes.PresenceAvailable); err != nil {
					logf("e2ee presence could not be sent")
				}
				return
			}
		}
		logf("e2ee socket did not come up; encrypted conversations cannot send or receive")
	}()
	return ""
}

// One decrypted message from an encrypted conversation. Only what the app draws is kept: who sent it, when, and its
// text. Anything richer than text is recorded as an attachment-less message for now.
func (a *adapter) encryptedEvent(raw any) {
	defer func() { _ = recover() }()
	// The events that can change the connection's word tell the app after their own handling has run (encryptedstate.go).
	switch raw.(type) {
	case *events.Connected, *events.LoggedOut, *events.ConnectFailure, *events.TemporaryBan, *events.ClientOutdated:
		defer a.announceEncrypted()
	}
	switch evt := raw.(type) {
	case *events.Receipt:
		a.encryptedRead(evt)
	case *events.ChatPresence:
		a.encryptedTyping(evt)
	case *events.FBMessage:
		a.applyEncrypted(evt)
	case *events.UndecryptableMessage:
		a.encryptedUnreadable(evt)
	case *events.Connected:
		logf("e2ee socket connected")
		// Signed-in may follow the event by a moment; the word is looked at again once it has.
		time.AfterFunc(2*time.Second, a.announceEncrypted)
	case *events.Disconnected:
		a.encryptedStateChanged()
		logf("e2ee socket disconnected")
	case *events.LoggedOut:
		logf("e2ee device was removed from the account")
		a.chats.setSecure(false)
	case *events.ConnectFailure:
		// Why the socket would not come up, by name. Facebook answers a refused connection with a three-digit code and
		// nothing else, and without this it arrived as an unexplained failure to connect.
		logf("e2ee refused: %s", connectRefusal(evt.Reason))
		a.chats.setSecure(false)
		a.forgetDevice(evt.Reason)
	case *events.CATRefreshError:
		// The token the encrypted connection authenticates with could not be renewed, which is a refusal of a
		// different kind: the session is the problem, not the device.
		logf("e2ee authentication token could not be renewed")
	case *events.TemporaryBan:
		logf("e2ee temporarily blocked by Facebook")
		a.chats.setSecure(false)
	case *events.ClientOutdated:
		logf("e2ee refused: Facebook considers this client too old")
		a.chats.setSecure(false)
	case *events.HistorySync:
		// If Meta ever hands a newly registered device a history blob, as WhatsApp does for a linked phone, this is
		// where it arrives — and it would be real history with no PIN involved. Recorded so it is known either way.
		logf("e2ee history sync arrived: conversations=%d progress=%d", len(evt.Data.GetConversations()), evt.Data.GetProgress())
	case *events.OfflineSyncPreview:
		logf("e2ee waiting messages announced: %d", evt.Messages)
	case *events.OfflineSyncCompleted:
		logf("e2ee waiting messages delivered: %d", evt.Count)
	}
}

func (a *adapter) applyEncrypted(evt *events.FBMessage) {
	a.unreadable.readable(evt.Info.ID)
	a.state.Lock()
	defer a.state.Unlock()
	jid, err := strconv.ParseInt(evt.Info.Chat.User, 10, 64)
	if err != nil || evt.Info.ID == "" {
		return
	}
	// Which conversation this belongs to on the ordinary side. Facebook numbers an encrypted conversation
	// differently there and tells us the pairing; without it the message starts a conversation of its own beside
	// the real one, with no name and nobody in it.
	// Facebook hands a newly registered device everything waiting for it, and that arrives before the ordinary
	// connection has said which conversation is which. A message whose pairing is not known yet is kept under the
	// number it came with and moved when the pairing arrives — dropping it would throw away the one delivery of it
	// there will ever be.
	key, known := a.chats.threadForJID(jid)
	if !known {
		key = jid
		logf("e2ee message held under its own number, pairing not known yet jid=%s", tag(jid))
	}
	a.chats.encryptedIdentity(key, jid, evt.Info.Chat.Server)
	sender, _ := strconv.ParseInt(evt.Info.Sender.User, 10, 64)
	// A reaction travels as a message of its own that names the one it is about. It is not a message in the
	// conversation and must not be added as one.
	if reaction := evt.GetConsumerApplication().GetPayload().GetContent().GetReactionMessage(); reaction != nil {
		about := reaction.GetKey().GetID()
		if about == "" {
			return
		}
		logf("e2ee reaction chat=%s about=%s remove=%v", tag(key), tagOf(about), reaction.GetText() == "")
		a.remember(context.Background(), key, about, sender, reaction.GetText())
		a.out.write(response{Event: "changed", Result: a.chats.react(key, about, sender, reaction.GetText())})
		return
	}
	// A correction to a message already here, whether made on this computer or on the phone. Like a reaction, it is an
	// instruction about a message rather than a message of its own, and adding it as one would double the conversation.
	if edit := evt.GetConsumerApplication().GetPayload().GetContent().GetEditMessage(); edit != nil {
		about := edit.GetKey().GetID()
		if about == "" {
			return
		}
		at := edit.GetTimestampMS()
		if at == 0 {
			at = time.Now().UnixMilli()
		}
		logf("e2ee edit chat=%s about=%s", tag(key), tagOf(about))
		touched := a.chats.applyEdit(key, about, edit.GetMessage().GetText(), at)
		a.mu.Lock()
		kept := a.crypto
		a.mu.Unlock()
		if kept != nil {
			kept.messages.rememberEdit(context.Background(), about, edit.GetMessage().GetText(), at)
		}
		if !touched.empty() {
			a.out.write(response{Event: "changed", Result: touched})
		}
		return
	}
	// A message taken back. It arrives as application data rather than as content, which is why it is read from a
	// different part of the payload than everything else here.
	if revoke := evt.GetConsumerApplication().GetPayload().GetApplicationData().GetRevoke(); revoke != nil {
		about := revoke.GetKey().GetID()
		if about == "" {
			return
		}
		logf("e2ee revoke chat=%s about=%s", tag(key), tagOf(about))
		touched := a.chats.removeEncrypted(key, about)
		a.mu.Lock()
		kept := a.crypto
		a.mu.Unlock()
		if kept != nil {
			kept.messages.forget(context.Background(), key, about)
		}
		if !touched.empty() {
			a.out.write(response{Event: "changed", Result: touched})
		}
		return
	}
	items, text := encryptedContents(evt)
	// The message this one quoted, when it quoted one.
	replyTo := evt.FBApplication.GetMetadata().GetQuotedMessage().GetStanzaID()
	at := evt.Info.Timestamp
	if at.IsZero() {
		at = time.Now()
	}
	logf("e2ee message chat=%s id=%s text=%v media=%v", tag(key), tagOf(evt.Info.ID), text != "", len(items) != 0)
	if a.testReceipt && !evt.Info.IsFromMe {
		a.receiptOrigins.Store(evt.Info.ID, evt.Info.Sender)
	}
	ctx := context.Background()
	touched := a.chats.addEncrypted(key, evt.Info.ID, sender, text, replyTo, at.UnixMilli())
	if touched.empty() {
		return
	}
	for _, media := range items {
		a.chats.attachSecure(key, evt.Info.ID, at.UnixMilli(), media.item, media.held)
	}
	a.mu.Lock()
	kept := a.crypto
	a.mu.Unlock()
	if kept != nil {
		kept.messages.saveIdentity(ctx, a.chats)
		if sender == metaAIMessengerID && sender != a.chats.viewer() {
			kept.messages.rememberAI(ctx, key, evt.Info.ID, a.chats)
		} else {
			kept.messages.remember(ctx, key, evt.Info.ID, sender, text, replyTo, at.UnixMilli())
		}
		if share := evt.GetArmadillo().GetPayload().GetContent().GetExtendedContentMessage(); share != nil {
			kept.messages.rememberShare(ctx, evt.Info.ID, share)
		}
		for _, media := range items {
			kept.messages.rememberMedia(ctx, key, evt.Info.ID, at.UnixMilli(), media.item, media.held)
		}
	}
	if !touched.empty() {
		a.out.write(response{Event: "changed", Result: touched})
	}
	a.recoverGroupIdentity(jid, evt.Info.Chat)
}

// The text of a decrypted message, for the message shapes Messenger uses for ordinary conversation.
func encryptedText(evt *events.FBMessage) string {
	if consumer := evt.GetConsumerApplication(); consumer != nil {
		if content := consumer.GetPayload().GetContent(); content != nil {
			if message := content.GetMessageText(); message != nil {
				return message.GetText()
			}
			if extended := content.GetExtendedTextMessage(); extended != nil {
				return extended.GetText().GetText()
			}
		}
	}
	return ""
}

// Where an encrypted conversation is addressed on the other connection.
func (a *adapter) encryptedAddress(key int64) (waTypes.JID, bool) {
	jid, ok := a.chats.jidForThread(key)
	if !ok {
		return waTypes.JID{}, false
	}
	_, group := a.chats.encryptedThread(key)
	server := waTypes.MessengerServer
	if group {
		server = waTypes.GroupServer
	}
	return waTypes.JID{User: strconv.FormatInt(jid, 10), Server: server}, true
}

// Sends one text message into an encrypted conversation, quoting another one when replyTo names it. Facebook will not
// take it on the ordinary connection at all, which is why a reply into one of these used to go nowhere.
func (a *adapter) sendEncrypted(ctx context.Context, key int64, text, replyTo string) (any, string) {
	a.mu.Lock()
	crypto := a.crypto
	a.mu.Unlock()
	if crypto == nil {
		return nil, "e2ee_off"
	}
	// A socket that was started but never came up refuses a message in a way that reads like Facebook rejecting it.
	// Saying so plainly means the app can report it as not sent, rather than leaving it uncertain forever.
	if !a.encryptedReady() {
		logf("send chat=%s encrypted socket is not up", tag(key))
		return nil, "e2ee_offline"
	}
	to, ok := a.encryptedAddress(key)
	if !ok {
		logf("send chat=%s has no encrypted address", tag(key))
		return nil, "send_failed"
	}
	metadata := &waMsgApplication.MessageApplication_Metadata{}
	if replyTo != "" {
		target, known := a.chats.messageByID(key, replyTo)
		if !known {
			return nil, "reply_unavailable"
		}
		quoted := &waMsgApplication.MessageApplication_Metadata_QuotedMessage{StanzaID: proto.String(target.ID)}
		// A group needs to be told who is being quoted; a conversation between two people does not.
		if to.Server == waTypes.GroupServer {
			quoted.Participant = proto.String(waTypes.JID{User: target.Sender, Server: waTypes.MessengerServer}.String())
		}
		metadata.QuotedMessage = quoted
	}
	started := time.Now()
	id := strconv.FormatInt(methods.GenerateEpochID(), 10)
	message := &waConsumerApplication.ConsumerApplication{
		Payload: &waConsumerApplication.ConsumerApplication_Payload{
			Payload: &waConsumerApplication.ConsumerApplication_Payload_Content{
				Content: &waConsumerApplication.ConsumerApplication_Content{
					Content: &waConsumerApplication.ConsumerApplication_Content_MessageText{
						MessageText: &waCommon.MessageText{Text: proto.String(text)},
					},
				},
			},
		},
	}
	receipt, err := crypto.client.SendFBMessage(ctx, to, message, metadata,
		whatsmeow.SendRequestExtra{ID: waTypes.MessageID(id)})
	if err != nil {
		logf("send chat=%s encrypted send failed: %s", tag(key), scrub(err.Error()))
		return nil, "send_failed"
	}
	// The conversation shows it straight away; the copy that comes back over the connection replaces it by id.
	at := encryptedSendTimestamp(receipt.Timestamp, started)
	touched := a.chats.addEncrypted(key, id, a.chats.viewer(), text, replyTo, at)
	crypto.messages.remember(ctx, key, id, a.chats.viewer(), text, replyTo, at)
	a.out.write(response{Event: "changed", Result: touched})
	a.readAfterSend(key, at)
	logf("send chat=%s encrypted accepted message=%s", tag(key), tagOf(id))
	return map[string]any{"id": id, "otid": id}, ""
}

// Use the same server clock as incoming messages and read watermarks. The local
// time after waiting for a send acknowledgement puts the message beyond a valid
// receipt for it. A missing server timestamp falls back to dispatch time.
func encryptedSendTimestamp(server, dispatched time.Time) int64 {
	if !server.IsZero() && server.UnixMilli() > 0 {
		return server.UnixMilli()
	}
	return dispatched.UnixMilli()
}

// Whether the encrypted socket is actually up, rather than merely constructed. A send on a socket that never
// connected fails in a way that reads like Facebook refusing the message, which is a different problem entirely.
func (a *adapter) encryptedReady() bool {
	a.mu.Lock()
	crypto := a.crypto
	a.mu.Unlock()
	return crypto != nil && crypto.client != nil && crypto.client.IsConnected() && crypto.client.IsLoggedIn()
}

// What Facebook's three-digit refusal means, in words. Which one it is decides what can be done about it: a device
// that is no longer registered needs turning on again, a token problem clears itself, a block does not.
func connectRefusal(reason events.ConnectFailureReason) string {
	switch reason {
	case events.ConnectFailureLoggedOut:
		return "this computer is no longer one of the account's Messenger devices (401)"
	case events.ConnectFailureMainDeviceGone:
		return "the account's main device is gone (403)"
	case events.ConnectFailureUnknownLogout:
		return "Facebook signed this device out (406)"
	case events.ConnectFailureCATExpired:
		return "the authentication token has expired (413)"
	case events.ConnectFailureCATInvalid:
		return "the authentication token was refused (414)"
	case events.ConnectFailureNotFound:
		return "Facebook has no record of this device (415)"
	case events.ConnectFailureClientOutdated:
		return "this client is too old for Facebook (405)"
	case events.ConnectFailureBadUserAgent:
		return "Facebook refused the client identification (409)"
	case events.ConnectFailureTempBanned:
		return "the account is temporarily blocked (402)"
	case events.ConnectFailureServiceUnavailable:
		return "Facebook's encrypted service is unavailable (503)"
	case events.ConnectFailureInternalServerError:
		return "Facebook's encrypted service failed (500)"
	default:
		return fmt.Sprintf("refused with code %d", int(reason))
	}
}

// A device Facebook has no record of is worth nothing, and keeping it means every start fails the same way for good.
// The stored keys are thrown away so that turning encrypted conversations on again registers this computer afresh.
//
// Only the refusals that mean the device itself is gone. A token that expired, a service that is down or a block on
// the account are all reasons to wait, not reasons to give up what is stored — and registering is not done here in
// any case: it puts a device on the account, which is the user's to ask for, from Settings.
func (a *adapter) forgetDevice(reason events.ConnectFailureReason) {
	if a.observeReceipts {
		logf("e2ee observation will not remove or register a device")
		return
	}
	switch reason {
	case events.ConnectFailureNotFound, events.ConnectFailureLoggedOut, events.ConnectFailureUnknownLogout:
	default:
		return
	}
	a.mu.Lock()
	crypto := a.crypto
	a.crypto = nil
	a.mu.Unlock()
	if crypto == nil {
		return
	}
	ctx := context.Background()
	// Every stored device, not only the one that was refused. This account has several from the days when a failed
	// lookup registered a new one on every start, and they are all equally dead: clearing one simply hands the next
	// corpse to the next attempt, which is refused in exactly the same way.
	cleared := 0
	if crypto.devices != nil {
		if stored, err := crypto.devices.GetAllDevices(ctx); err == nil {
			for _, candidate := range stored {
				if candidate.Delete(ctx) == nil {
					cleared++
				}
			}
		}
	}
	if cleared == 0 && crypto.device != nil {
		if err := crypto.device.Delete(ctx); err != nil {
			logf("e2ee the dead device could not be cleared")
			return
		}
		cleared = 1
	}
	logf("e2ee cleared %d stored device(s)", cleared)
	// Registering again is done here, once. The user has already said this computer should be one of the account's
	// devices — that is what turning encrypted conversations on meant — and the device it was given has been taken
	// away by Facebook, not by them. Leaving it to a control in Settings that is already switched on gives them
	// nothing to press.
	//
	// Once per run, and no more. The pile of duplicate devices on this account came from registering on every start
	// when the lookup failed; this happens only when Facebook has said outright that the device is gone, and a second
	// refusal after a fresh registration is a real refusal, not something to keep answering.
	// A device that arrived by a move is not replaced on its own. The dead keys are gone (above) so that the person's explicit
	// enable registers afresh; nothing else does.
	if arrivedByMove(a.chats.viewer()) {
		logf("e2ee the moved device was refused by Facebook; not registering another on its own")
		a.encryptedGone.Store(true)
		a.announceEncrypted()
		return
	}
	if a.reregistered.Swap(true) {
		logf("e2ee registered again already this run and was refused again; leaving it alone")
		a.encryptedGone.Store(true)
		a.announceEncrypted()
		return
	}
	logf("e2ee this computer is no longer a registered device; registering it again, once")
	go func() {
		defer func() { _ = recover() }()
		if !wait(a.lifetime, 3*time.Second) {
			return
		}
		if code := a.startEncrypted(a.lifetime, false); code != "" {
			logf("e2ee could not register again: %s", code)
			a.encryptedGone.Store(true)
			a.announceEncrypted()
			return
		}
		logf("e2ee registered again")
	}()
}
