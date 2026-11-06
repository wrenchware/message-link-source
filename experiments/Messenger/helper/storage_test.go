package main

import (
	"bytes"
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestMessengerStorageUsesInstanceRootAndVerifiedAccount(t *testing.T) {
	root := t.TempDir()
	t.Setenv("NATIVEBEEPER_DATA_ROOT", root)
	a := messengerAccountPath(101, "messages.db")
	b := messengerAccountPath(202, "messages.db")
	if a == b || filepath.Dir(filepath.Dir(filepath.Dir(a))) != filepath.Join(root, "Messenger") {
		t.Fatal("account databases must be distinct beneath the selected app instance root")
	}
	ctx := context.Background()
	store, err := openAccountMessageStore(ctx, a, 101)
	if err != nil {
		t.Fatal(err)
	}
	if err = store.rememberChecked(ctx, 7, "message", 42, "private retained body", "private reply", 1700); err != nil {
		t.Fatal(err)
	}
	if err = store.db.Close(); err != nil {
		t.Fatal(err)
	}
	raw, err := os.ReadFile(a)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(raw, []byte("private retained body")) || bytes.Contains(raw, []byte("private reply")) {
		t.Fatal("retained Messenger content was written to SQLite in plaintext")
	}
	if wrong, err := openAccountMessageStore(ctx, a, 202); err == nil {
		wrong.db.Close()
		t.Fatal("a store claimed by one verified account opened for another")
	}
	other, err := openAccountMessageStore(ctx, b, 202)
	if err != nil {
		t.Fatal(err)
	}
	back := newStore()
	if restored := other.restore(ctx, back); restored != 0 {
		t.Fatal("one account restored another account's history")
	}
	other.db.Close()
}

func TestLegacyDatabaseRequiresProtectedSessionAdoption(t *testing.T) {
	root := t.TempDir()
	t.Setenv("NATIVEBEEPER_DATA_ROOT", root)
	legacy := messengerLegacyPath("messages.db")
	if err := os.WriteFile(legacy, []byte("legacy"), 0o600); err != nil {
		t.Fatal(err)
	}
	target := adoptLegacyDatabase(101, "messages.db", false)
	if _, err := os.Stat(legacy); err != nil {
		t.Fatal("fresh sign-in must leave unowned legacy state quarantined")
	}
	if _, err := os.Stat(target); !os.IsNotExist(err) {
		t.Fatal("fresh sign-in unexpectedly adopted legacy state")
	}
	target = adoptLegacyDatabase(101, "messages.db", true)
	if _, err := os.Stat(target); err != nil {
		t.Fatal("saved-session reconnect did not adopt legacy state")
	}
	if _, err := os.Stat(legacy); !os.IsNotExist(err) {
		t.Fatal("legacy state remained in the shared location")
	}
}

// On the Linux server everything the helper writes lives under the private root (the keys volume), never beside the
// data; an account store still left in the data folder is not opened, and no second one is made beside it. Without the
// variable the paths are the data root's, as the Windows app has them.
func TestHelperStorageFollowsThePrivateRoot(t *testing.T) {
	root := t.TempDir()
	private := t.TempDir()
	t.Setenv("NATIVEBEEPER_DATA_ROOT", root)
	t.Setenv("NATIVEBEEPER_MESSENGER_LOG", "")
	t.Setenv(privateRootVariable, "")
	if got := messengerAccountPath(101, "messages.db"); got != filepath.Join(root, "Messenger", "accounts", messengerAccountKey(101), "messages.db") {
		t.Fatal("without a private root the account must stay in the data root")
	}
	if logPath() != filepath.Join(root, "Messenger", "helper.log") {
		t.Fatal("without a private root the log must stay in the data root")
	}
	t.Setenv(privateRootVariable, private)
	for _, name := range []string{"messages.db", "e2ee.db", "backup-export.tmp"} {
		got := messengerAccountPath(303, name)
		if got != filepath.Join(private, "accounts", messengerAccountKey(303), name) {
			t.Fatal("with a private root every account file must be under it: " + name)
		}
	}
	if adoptLegacyDatabase(303, "e2ee.db", true) != filepath.Join(private, "accounts", messengerAccountKey(303), "e2ee.db") {
		t.Fatal("the device store must be under the private root")
	}
	if logPath() != filepath.Join(private, "helper.log") {
		t.Fatal("with a private root the log must be under it")
	}
	if info, err := os.Stat(filepath.Join(private, "accounts", messengerAccountKey(303))); err != nil || os.PathSeparator == '/' && info.Mode().Perm()&0o077 != 0 {
		t.Fatal("the account folder must be private")
	}
	if messengerAccountPath(0, "messages.db") != "" {
		t.Fatal("an unknown account must get no store")
	}
	// Left in the data folder by an earlier build: neither opened nor replaced.
	stranded := filepath.Join(root, "Messenger", "accounts", messengerAccountKey(202))
	if err := os.MkdirAll(stranded, 0o700); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"e2ee.db", "messages.db"} {
		if err := os.WriteFile(filepath.Join(stranded, name), []byte("kept"), 0o600); err != nil {
			t.Fatal(err)
		}
		if messengerAccountPath(202, name) != "" || existingAccountPath(202, name) != "" || adoptLegacyDatabase(202, name, true) != "" {
			t.Fatal("a store left in the data folder must stop its use, not be replaced: " + name)
		}
		if _, err := os.Stat(filepath.Join(private, "accounts", messengerAccountKey(202), name)); !os.IsNotExist(err) {
			t.Fatal("a second store was made beside the one left in the data folder: " + name)
		}
	}
}

// A copy started with only the data root must keep its log inside that root. It used to rotate the installed app's
// helper.log to .previous, destroying the real run's record, because only the databases honoured the root.
func TestHelperLogFollowsTheDataRoot(t *testing.T) {
	root := t.TempDir()
	t.Setenv("NATIVEBEEPER_MESSENGER_LOG", "")
	t.Setenv("NATIVEBEEPER_DATA_ROOT", root)
	if got, want := logPath(), filepath.Join(root, "Messenger", "helper.log"); got != want {
		t.Fatalf("the helper log went to %q, not inside the data root", got)
	}
	named := filepath.Join(t.TempDir(), "named.log")
	t.Setenv("NATIVEBEEPER_MESSENGER_LOG", named)
	if logPath() != named {
		t.Fatal("an explicitly named log was not honoured")
	}
}

// A helper started by hand, with no data root, falls back to the product's own folder. It used to recreate
// %LOCALAPPDATA%\NativeBeeper\Messenger, a folder the app had renamed away and a name nobody may see.
func TestFallbackFolderIsTheProductsOwnName(t *testing.T) {
	cache := t.TempDir()
	t.Setenv("NATIVEBEEPER_DATA_ROOT", "")
	t.Setenv("NATIVEBEEPER_MESSENGER_LOG", "")
	t.Setenv("LOCALAPPDATA", cache)
	t.Setenv("XDG_CACHE_HOME", cache)
	base, err := os.UserCacheDir()
	if err != nil {
		t.Skip("no user cache folder on this system")
	}
	if got, want := messengerStorageRoot(), filepath.Join(base, "Messages for Windows", "Messenger"); got != want {
		t.Fatalf("the fallback folder was %q, not %q", got, want)
	}
	if strings.Contains(messengerStorageRoot(), "NativeBeeper") || strings.Contains(logPath(), "NativeBeeper") {
		t.Fatal("the fallback folder still carries the historical name")
	}
}

// Every message kept last session has an empty card_text, which carries no protected prefix. It used to be taken for
// legacy plaintext, sealed, and followed by a full VACUUM on nearly every start. An empty cell holds nothing to seal;
// real legacy plaintext is still sealed and still vacuumed away.
func TestProtectExistingLeavesEmptyCellsAndStillSealsPlaintext(t *testing.T) {
	ctx := context.Background()
	logFile := filepath.Join(t.TempDir(), "helper.log")
	t.Setenv("NATIVEBEEPER_MESSENGER_LOG", logFile)
	openLog()
	defer closeLog()
	path := filepath.Join(t.TempDir(), "messages.db")
	store, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	if err = store.rememberChecked(ctx, 7, "kept", 42, "synthetic kept body", "", 1700); err != nil {
		t.Fatal(err)
	}
	store.db.Close()
	if store, err = openMessageStore(ctx, path); err != nil {
		t.Fatal(err)
	}
	var card []byte
	if err = store.db.QueryRowContext(ctx, `SELECT card_text FROM encrypted_message WHERE id = 'kept'`).Scan(&card); err != nil {
		t.Fatal(err)
	}
	if len(card) != 0 {
		t.Fatal("an empty card_text was sealed at start")
	}
	if _, err = store.db.ExecContext(ctx, `INSERT INTO encrypted_message (id, thread, sender, body, at) VALUES ('legacy', 7, 42, 'synthetic legacy plaintext', 1600)`); err != nil {
		t.Fatal(err)
	}
	store.db.Close()
	if store, err = openMessageStore(ctx, path); err != nil {
		t.Fatal(err)
	}
	restored := newStore()
	store.restore(ctx, restored)
	store.db.Close()
	for _, id := range []string{"kept", "legacy"} {
		if _, _, _, _, ok := restored.rawMessage(7, id); !ok {
			t.Fatalf("%s did not restore", id)
		}
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(raw, []byte("synthetic legacy plaintext")) {
		t.Fatal("legacy plaintext survived the start")
	}
	closeLog()
	log, err := os.ReadFile(logFile)
	if err != nil {
		t.Fatal(err)
	}
	for _, signal := range []string{"kept store protect sealed=0 empty=1 vacuum=false", "kept store protect sealed=1 empty=3 vacuum=true"} {
		if !strings.Contains(string(log), signal) {
			t.Fatalf("the log does not show %q", signal)
		}
	}
}

// A restore says how many cells it unsealed and how many of those held a sealed "", so the cost of sealing empty
// optional fields can be measured before anyone decides to stop. A kept message with no reply seals one.
func TestRestoreCountsSealedEmptyCells(t *testing.T) {
	ctx := context.Background()
	logFile := filepath.Join(t.TempDir(), "helper.log")
	t.Setenv("NATIVEBEEPER_MESSENGER_LOG", logFile)
	openLog()
	defer closeLog()
	path := filepath.Join(t.TempDir(), "messages.db")
	store, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	if err = store.rememberChecked(ctx, 7, "kept", 42, "synthetic kept body", "", 1700); err != nil {
		t.Fatal(err)
	}
	store.restore(ctx, newStore())
	store.db.Close()
	closeLog()
	log, err := os.ReadFile(logFile)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(log), "kept restore sealedEmpty=1 of 2 cells in ") {
		t.Fatalf("the restore did not count its sealed empty reply: %q", string(log))
	}
}
