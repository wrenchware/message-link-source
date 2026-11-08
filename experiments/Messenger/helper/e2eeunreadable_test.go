package main

import (
	"context"
	"testing"
	"time"

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
