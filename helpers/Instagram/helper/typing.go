package main

import (
	"context"
	"encoding/json"
	"strconv"
	"time"

	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
)

// Typing, in both directions, in --app mode only. Nothing about it is stored: a report that is missed is simply not
// shown, and the app expires anybody who stops being reported. The diagnostics' client is built with typing
// disabled, so it opens neither typing socket and SetTyping there returns before touching the network.

// The same event Messenger's helper writes, so the app reads both the same way.
type typingEvent struct {
	ChatID string `json:"chatId"`
	Sender string `json:"sender"`
	Typing bool   `json:"typing"`
}

// What the application's client can do beyond reading. Kept apart from readClient on purpose: the diagnostics'
// interface must not be able to type even by mistake.
type typingClient interface {
	SetTyping(context.Context, string, bool) error
}

// After this many sends fail in a row, typing rests for a while rather than opening a doomed socket on every burst.
const (
	typingFailuresBeforeRest = 3
	typingRest               = 5 * time.Minute
)

// One typing report, as the app should see it. Called from handle with a.mu held.
//
// The report names the thread by its long id and the sender by Instagram user id — neither of which the app knows —
// and both are resolved exactly as upstream does, through data this helper already holds from the list: the long id
// to the conversation, the Instagram id to the messaging id through that thread's own people. Anything that does not
// resolve is dropped, never guessed: a message request is not loaded, and a spam thread is not somewhere the app
// shows who is typing.
func (a *readOnlyAdapter) publishTyping(e *slidetypes.TypingNotification) {
	if !a.canMutate || e == nil || e.ThreadID == "" || e.SenderID <= 0 || a.jar == nil {
		return
	}
	sender := strconv.FormatInt(e.SenderID, 10)
	// This account typing on another device. Compared as Instagram ids, which is what the report carries.
	if sender == a.jar.Get(cookies.IGCookieDSUserID) {
		return
	}
	person := ""
	if a.chats != nil {
		if chat, ok := a.chats.byLongID(e.ThreadID); ok && chat.Folder == "inbox" {
			person = chat.igUsers[sender]
			if person != "" {
				a.requests.typing.in.Add(1)
				a.requests.typing.ttl.Store(int64(e.TTL))
				_ = a.out.write(response{Event: "typing", Result: typingEvent{chat.ID, person, e.ActivityStatus != 0}})
				return
			}
		}
	}
	a.requests.typing.unplaced.Add(1)
}

// This account typing, or stopping, in one conversation. Answered beside the serial queue and never taking a.op:
// the first send may wait five seconds for its socket to open, and a message must never wait behind that. Best
// effort throughout: it is never retried, and a failure is reported as not sent rather than as an error.
func (a *readOnlyAdapter) uiTyping(ctx context.Context, r request) (any, string) {
	client, code := a.writeClientFor() // read_only in the diagnostics' mode, login_required before identity
	if code != "" {
		return nil, code
	}
	typer, ok := client.(typingClient)
	if !ok {
		return nil, "unsupported"
	}
	var params struct {
		ChatID string `json:"chatId"`
		Typing *bool  `json:"typing"`
	}
	if json.Unmarshal(r.Params, &params) != nil || params.ChatID == "" || params.Typing == nil {
		return nil, "invalid_params"
	}
	// Inbox only: a message request or a spam thread is never told this account is typing.
	chat, code := a.writable(params.ChatID)
	if code != "" {
		return nil, code
	}
	a.mu.Lock()
	up := a.snapshot.State == "connected"
	resting := time.Now().Before(a.typingRestUntil)
	a.mu.Unlock()
	// Without a live connection SetTyping would open a socket on a context already cancelled and wait five seconds
	// for it; after repeated failures it would do the same on every burst. Neither is worth a request.
	if !up || resting {
		a.requests.typing.skipped.Add(1)
		return map[string]any{"sent": false}, ""
	}
	// The short id, as upstream addresses it (igconnector HandleMatrixTyping, meta.IGID) and as sending does here.
	err := typer.SetTyping(ctx, chat.shortID, *params.Typing)
	a.mu.Lock()
	if err != nil {
		a.typingFailures++
		if a.typingFailures >= typingFailuresBeforeRest {
			a.typingFailures, a.typingRestUntil = 0, time.Now().Add(typingRest)
		}
	} else {
		a.typingFailures = 0
	}
	a.mu.Unlock()
	if err != nil {
		a.requests.typing.failed.Add(1)
		return map[string]any{"sent": false}, ""
	}
	a.requests.typing.out.Add(1)
	return map[string]any{"sent": true}, ""
}
