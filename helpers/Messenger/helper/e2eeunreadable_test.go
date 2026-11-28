package main

import (
	"context"
	"sync"
	"testing"
	"time"

	wadevice "go.mau.fi/whatsmeow/store"
	waTypes "go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
)

func unreadableEvent(id string, at int64, mode events.DecryptFailMode) *events.UndecryptableMessage {
	return &events.UndecryptableMessage{Info: encryptedEnvelope(id, at, false, false).Info, DecryptFailMode: mode}
}

func newestText(a *adapter) string {
	page := a.chats.history(7, 0)
	if len(page) == 0 {
		return ""
	}
	return page[0].Text
}

func waitUnreadable(t *testing.T, want func() bool, what string) {
	t.Helper()
	for deadline := time.Now().Add(2 * time.Second); time.Now().Before(deadline); time.Sleep(5 * time.Millisecond) {
		if want() {
			return
		}
	}
	t.Fatal(what)
}

func keepUnreadableWait(t *testing.T, wait time.Duration) {
	t.Helper()
	before := unreadableWait
	unreadableWait = wait
	t.Cleanup(func() { unreadableWait = before })
}

func unreadableAdapter(t *testing.T) *adapter {
	t.Helper()
	a, _ := identityAdapter(t)
	rows := identityRows(false)
	rows.LSInsertMessage = nil
	a.applyTable(context.Background(), rows)
	return a
}

func TestUnreadableMessageStandsInAfterTheLastFailureAndSurvivesRestart(t *testing.T) {
	keepUnreadableWait(t, time.Hour)
	a := unreadableAdapter(t)
	for attempt := 1; attempt < unreadableAttempts; attempt++ {
		a.encryptedUnreadable(unreadableEvent("lost", 2000, events.DecryptFailShow))
	}
	if a.chats.messageCount(7) != 0 {
		t.Fatal("the line appeared while resends were still expected")
	}
	a.encryptedUnreadable(unreadableEvent("lost", 2000, events.DecryptFailShow))
	waitUnreadable(t, func() bool { return newestText(a) == unreadableText }, "the last failure left no line in the conversation")
	if a.chats.threads[7].snippet != unreadableText {
		t.Fatal("the conversation list does not show the line")
	}
	// The line is drawn just before it is saved, both under this lock.
	a.state.Lock()
	a.state.Unlock()
	restartIdentityAdapter(t, a)
	if newestText(a) != unreadableText {
		t.Fatal("the line did not survive a restart")
	}
}

func TestUnreadableLineGivesWayToAResendThatDecrypts(t *testing.T) {
	keepUnreadableWait(t, 20*time.Millisecond)
	a := unreadableAdapter(t)
	a.encryptedUnreadable(unreadableEvent("late", 2000, events.DecryptFailShow))
	waitUnreadable(t, func() bool { return newestText(a) == unreadableText }, "the wait left no line in the conversation")
	a.applyEncrypted(encryptedEnvelope("late", 2000, false, false))
	if text := newestText(a); text != "new message" || a.chats.messageCount(7) != 1 {
		t.Fatalf("the resend did not replace the line: %q count=%d", text, a.chats.messageCount(7))
	}
	if a.chats.threads[7].snippet != "new message" {
		t.Fatal("the conversation list kept the line")
	}
}

func TestUnreadableLineIsNotShownWhenTheResendDecryptsFirst(t *testing.T) {
	keepUnreadableWait(t, 20*time.Millisecond)
	a := unreadableAdapter(t)
	a.encryptedUnreadable(unreadableEvent("saved", 2000, events.DecryptFailShow))
	a.applyEncrypted(encryptedEnvelope("saved", 2000, false, false))
	time.Sleep(60 * time.Millisecond)
	if text := newestText(a); text != "new message" {
		t.Fatalf("the wait overwrote the decrypted message: %q", text)
	}
}

func TestUnreadableFailureAfterTheMessageIsHereCoversNothing(t *testing.T) {
	keepUnreadableWait(t, 20*time.Millisecond)
	a := unreadableAdapter(t)
	a.applyEncrypted(encryptedEnvelope("here", 2000, false, false))
	a.encryptedUnreadable(unreadableEvent("here", 2000, events.DecryptFailShow))
	time.Sleep(60 * time.Millisecond)
	if text := newestText(a); text != "new message" {
		t.Fatalf("a late failure overwrote the message: %q", text)
	}
}

func TestUnreadableHiddenFailureLeavesNoLine(t *testing.T) {
	keepUnreadableWait(t, 20*time.Millisecond)
	a := unreadableAdapter(t)
	for attempt := 0; attempt < unreadableAttempts; attempt++ {
		a.encryptedUnreadable(unreadableEvent("reaction", 2000, events.DecryptFailHide))
	}
	time.Sleep(60 * time.Millisecond)
	if a.chats.messageCount(7) != 0 {
		t.Fatal("a failure Facebook marked hidden put a line in the conversation")
	}
}

// The device's sessions, as far as forgetting one goes.
type forgottenSessions struct {
	wadevice.SessionStore
	mu      sync.Mutex
	held    map[string]bool
	deleted []string
}

func (f *forgottenSessions) HasSession(ctx context.Context, address string) (bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.held[address], nil
}

func (f *forgottenSessions) DeleteSession(ctx context.Context, address string) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	delete(f.held, address)
	f.deleted = append(f.deleted, address)
	return nil
}

func (f *forgottenSessions) forgotten() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]string(nil), f.deleted...)
}

func forgettingAdapter(t *testing.T) (*adapter, *forgottenSessions) {
	t.Helper()
	a := unreadableAdapter(t)
	sessions := &forgottenSessions{held: map[string]bool{"555:26": true, "555:3": true}}
	a.crypto.device = &wadevice.Device{Sessions: sessions}
	return a, sessions
}

func unreadableFrom(id string, device uint16) *events.UndecryptableMessage {
	evt := unreadableEvent(id, 2000, events.DecryptFailShow)
	evt.Info.Sender.Device = device
	return evt
}

// A sender's device whose every resend failed loses its session here and nothing else does, so the next message sent
// to that person starts a new one. Until the last failure the session stays: most resends decrypt on it.
func TestSessionIsForgottenOnlyAfterTheLastFailureAndOnlyForThatDevice(t *testing.T) {
	keepUnreadableWait(t, time.Hour)
	a, sessions := forgettingAdapter(t)
	for attempt := 1; attempt < unreadableAttempts; attempt++ {
		a.encryptedUnreadable(unreadableFrom("stuck", 26))
	}
	time.Sleep(30 * time.Millisecond)
	if got := sessions.forgotten(); len(got) != 0 {
		t.Fatalf("a session was forgotten while resends were still expected: %v", got)
	}
	a.encryptedUnreadable(unreadableFrom("stuck", 26))
	waitUnreadable(t, func() bool { return newestText(a) == unreadableText }, "the last failure left no line in the conversation")
	if got := sessions.forgotten(); len(got) != 1 || got[0] != "555:26" {
		t.Fatalf("forgot %v, want only the failing device's session", got)
	}
	if held, _ := sessions.HasSession(context.Background(), "555:3"); !held {
		t.Fatal("the person's other device lost its session")
	}
}

// A sender who stopped answering, a message Facebook says is unavailable, and a group are not a session gone wrong.
func TestSessionIsKeptWhenTheFailureIsNotAStuckSession(t *testing.T) {
	keepUnreadableWait(t, 20*time.Millisecond)
	a, sessions := forgettingAdapter(t)
	a.encryptedUnreadable(unreadableFrom("silent", 26))
	waitUnreadable(t, func() bool { return newestText(a) == unreadableText }, "the wait left no line in the conversation")
	for attempt := 0; attempt < unreadableAttempts; attempt++ {
		evt := unreadableFrom("unavailable", 26)
		evt.IsUnavailable = true
		a.encryptedUnreadable(evt)
	}
	for attempt := 0; attempt < unreadableAttempts; attempt++ {
		evt := unreadableFrom("group", 26)
		evt.Info.Chat.Server = waTypes.GroupServer
		evt.Info.IsGroup = true
		a.encryptedUnreadable(evt)
	}
	time.Sleep(80 * time.Millisecond)
	if got := sessions.forgotten(); len(got) != 0 {
		t.Fatalf("forgot %v, want nothing", got)
	}
}
