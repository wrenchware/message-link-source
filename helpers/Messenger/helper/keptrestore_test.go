package main

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func keepSyntheticMessage(t *testing.T, viewer int64) {
	t.Helper()
	ctx := context.Background()
	store, err := openAccountMessageStore(ctx, messengerAccountPath(viewer, "messages.db"), viewer)
	if err != nil {
		t.Fatal(err)
	}
	if err = store.rememberChecked(ctx, 7, "kept", 42, "synthetic kept body", "", 1700); err != nil {
		t.Fatal(err)
	}
	if err = store.db.Close(); err != nil {
		t.Fatal(err)
	}
}

func readHelperLog(t *testing.T, path string) string {
	t.Helper()
	closeLog()
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return string(raw)
}

// An account with nothing kept on this computer is not read early, and asking does not make it a folder: before the
// login succeeds the id is only what the cookie claims.
func TestEarlyKeptRestoreNeedsAnExistingStore(t *testing.T) {
	root := t.TempDir()
	t.Setenv("MESSAGELINK_DATA_ROOT", root)
	a := newAdapter(nil)
	if early := a.startKeptRestore(context.Background(), 101); early != nil {
		early.abandon(a)
		t.Fatal("an account with no kept store was read before its login")
	}
	if _, err := os.Stat(filepath.Join(root, "Messenger", "accounts")); !os.IsNotExist(err) {
		t.Fatal("asking about an unverified account created a folder for it")
	}
	if a.startKeptRestore(context.Background(), 0) != nil {
		t.Fatal("a missing account id was read")
	}
}

// The restore runs while the login is out, and the first apply still finds every kept message in place: finish waits
// for it. The line that proves the overlap carries times only.
func TestEarlyKeptRestoreIsInPlaceWhenTheLoginReturns(t *testing.T) {
	root := t.TempDir()
	t.Setenv("MESSAGELINK_DATA_ROOT", root)
	logFile := filepath.Join(t.TempDir(), "helper.log")
	t.Setenv("MESSAGELINK_MESSENGER_LOG", logFile)
	keepSyntheticMessage(t, 101)
	openLog()
	defer closeLog()
	a := newAdapter(nil)
	early := a.startKeptRestore(context.Background(), 101)
	if early == nil {
		t.Fatal("an existing kept store was not read beside the login")
	}
	// Stands in for the login page's round trip.
	time.Sleep(20 * time.Millisecond)
	early.finish(a, 20*time.Millisecond)
	if a.kept == nil {
		t.Fatal("the store the restore opened was not kept for the session")
	}
	defer a.kept.db.Close()
	if _, _, _, _, ok := a.chats.rawMessage(7, "kept"); !ok {
		t.Fatal("a kept message was missing when the first page would have been applied")
	}
	if a.chats.viewer() != 101 {
		t.Fatal("the restore ran without knowing whose messages are whose")
	}
	log := readHelperLog(t, logFile)
	for _, signal := range []string{"restored 1 kept messages in ", "kept restore overlapped login ms=20 restore_ms=", " waited_ms="} {
		if !strings.Contains(log, signal) {
			t.Fatalf("the log does not show %q", signal)
		}
	}
	if strings.Contains(log, "synthetic kept body") {
		t.Fatal("a kept message's text reached the log")
	}
}

// A login Facebook refuses leaves nothing behind: the restore is stopped, its store closed and the conversations
// emptied, so a retry starts exactly where the first attempt did.
func TestEarlyKeptRestoreIsUndoneByAFailedLogin(t *testing.T) {
	root := t.TempDir()
	t.Setenv("MESSAGELINK_DATA_ROOT", root)
	keepSyntheticMessage(t, 101)
	a := newAdapter(nil)
	early := a.startKeptRestore(context.Background(), 101)
	if early == nil {
		t.Fatal("an existing kept store was not read beside the login")
	}
	early.abandon(a)
	if a.kept != nil {
		t.Fatal("a failed login kept the store open for the session")
	}
	if _, _, _, _, ok := a.chats.rawMessage(7, "kept"); ok || a.chats.viewer() != 0 {
		t.Fatal("a failed login left restored state in memory")
	}
	if early.kept != nil && early.kept.db.Ping() == nil {
		t.Fatal("a failed login left the store open")
	}
	// The retry reads it all again from the start.
	again := a.startKeptRestore(context.Background(), 101)
	again.finish(a, 0)
	defer a.kept.db.Close()
	if _, _, _, _, ok := a.chats.rawMessage(7, "kept"); !ok {
		t.Fatal("the retry after a failed login did not restore")
	}
}
