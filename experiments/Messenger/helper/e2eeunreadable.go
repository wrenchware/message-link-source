package main

import (
	"context"
	"strconv"
	"sync"
	"time"

	waTypes "go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
)

// What stands in a conversation for an encrypted message this device could not decrypt. It is delivered once and
// Facebook will not hand it over again, so without this line it is simply missing here while the phone shows it.
const unreadableText = "Couldn't read this message. Open Messenger to see it."

// The library asks the sender to resend, and gives up after the fifth failure. Each failure is reported, and most resends
// succeed, so the line waits for that fifth failure, or for this long after the last one if the sender stops answering.
const unreadableAttempts = 5

var unreadableWait = 15 * time.Second

type unreadableWaiting struct {
	attempts int
	timer    *time.Timer
}

type unreadableMessages struct {
	mu      sync.Mutex
	waiting map[string]*unreadableWaiting
}

// A resend decrypted: nothing need stand in for it.
func (u *unreadableMessages) readable(id string) {
	u.mu.Lock()
	defer u.mu.Unlock()
	if held := u.waiting[id]; held != nil {
		held.timer.Stop()
		delete(u.waiting, id)
	}
}

// One more failure to decrypt id. Calls settle at once on the last failure (exhausted), or after the wait from the latest one.
func (u *unreadableMessages) failed(id string, settle func(exhausted bool)) {
	u.mu.Lock()
	defer u.mu.Unlock()
	if u.waiting == nil {
		u.waiting = map[string]*unreadableWaiting{}
	}
	held := u.waiting[id]
	if held == nil {
		held = &unreadableWaiting{}
		u.waiting[id] = held
	} else {
		held.timer.Stop()
	}
	held.attempts++
	if held.attempts >= unreadableAttempts {
		delete(u.waiting, id)
		go settle(true)
		return
	}
	var timer *time.Timer
	timer = time.AfterFunc(unreadableWait, func() {
		u.mu.Lock()
		current := u.waiting[id]
		if current == nil || current.timer != timer {
			u.mu.Unlock()
			return
		}
		delete(u.waiting, id)
		u.mu.Unlock()
		settle(false)
	})
	held.timer = timer
}

func (a *adapter) encryptedUnreadable(evt *events.UndecryptableMessage) {
	info := evt.Info
	jid, err := strconv.ParseInt(info.Chat.User, 10, 64)
	if err != nil || info.ID == "" {
		return
	}
	key, known := a.chats.threadForJID(jid)
	if !known {
		key = jid
	}
	hidden := evt.DecryptFailMode == events.DecryptFailHide
	logf("e2ee unreadable chat=%s id=%s own=%v unavailable=%v hidden=%v", tag(key), tagOf(info.ID), info.IsFromMe, evt.IsUnavailable, hidden)
	// Facebook marks what a person would not see as a message (a reaction or an edit, say) so that it stays out of the thread.
	if hidden {
		return
	}
	unavailable := evt.IsUnavailable
	a.unreadable.failed(info.ID, func(exhausted bool) {
		if exhausted && !unavailable {
			a.forgetSession(key, info)
		}
		a.placeUnreadable(jid, info)
	})
}

// The sender's device and this one no longer agree on their session: every resend failed, and each later message from
// that device will fail the same way, because nothing here ever starts over. Signal's own clients drop the session at
// this point, and so does this: with none held, the next message sent to that person fetches the device's keys and opens a new session
// from this side, which its replies then come back on. Nothing is sent here. Only the one device's session goes; its
// identity key and every other session stay. Groups are left alone: their messages fail for other reasons too.
func (a *adapter) forgetSession(key int64, info waTypes.MessageInfo) {
	defer func() { _ = recover() }()
	if info.IsGroup || info.Chat.Server != waTypes.MessengerServer || info.Sender.User == "" {
		return
	}
	a.mu.Lock()
	kept := a.crypto
	a.mu.Unlock()
	if kept == nil || kept.device == nil || kept.device.Sessions == nil {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	address := info.Sender.SignalAddress().String()
	held, _ := kept.device.Sessions.HasSession(ctx, address)
	outcome := "ok"
	if err := kept.device.Sessions.DeleteSession(ctx, address); err != nil {
		outcome = "failed"
	}
	logf("e2ee session forgotten chat=%s id=%s own=%v held=%v outcome=%s", tag(key), tagOf(info.ID), info.IsFromMe, held, outcome)
}

func (a *adapter) placeUnreadable(jid int64, info waTypes.MessageInfo) {
	defer func() { _ = recover() }()
	if a.lifetime.Err() != nil {
		return
	}
	a.state.Lock()
	defer a.state.Unlock()
	key, known := a.chats.threadForJID(jid)
	if !known {
		key = jid
	}
	// A failure reported after the message was read, or again after a restart, must not cover what is already here.
	if a.chats.holdsMessage(key, info.ID) {
		return
	}
	a.chats.encryptedIdentity(key, jid, info.Chat.Server)
	sender, _ := strconv.ParseInt(info.Sender.User, 10, 64)
	at := info.Timestamp
	if at.IsZero() {
		at = time.Now()
	}
	logf("e2ee unreadable shown chat=%s id=%s", tag(key), tagOf(info.ID))
	touched := a.chats.addEncrypted(key, info.ID, sender, unreadableText, "", at.UnixMilli())
	if touched.empty() {
		return
	}
	a.mu.Lock()
	kept := a.crypto
	a.mu.Unlock()
	if kept != nil {
		ctx := context.Background()
		kept.messages.saveIdentity(ctx, a.chats)
		kept.messages.remember(ctx, key, info.ID, sender, unreadableText, "", at.UnixMilli())
	}
	a.out.write(response{Event: "changed", Result: touched})
}
