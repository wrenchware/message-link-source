package main

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/whatsmeow/store/sqlstore"
	waLog "go.mau.fi/whatsmeow/util/log"
)

// These cover what a move of the Messenger device to or from a Messages Server relies on: the helper never registers a
// device while restoring one, never wakes an account that now lives elsewhere, and can move the kept history without
// ever signing in.

const moveViewer = int64(4242)

// A private storage root for this test; the data-folder variable is cleared so no stranded-store rule applies.
func moveRoot(t *testing.T) string {
	t.Helper()
	root := t.TempDir()
	t.Setenv(privateRootVariable, root)
	t.Setenv("MESSAGELINK_DATA_ROOT", "")
	return root
}

// A helper whose open store is released when the test ends, so Windows can remove its folder.
func moveAdapter(t *testing.T) *adapter {
	t.Helper()
	a := newAdapter(nil)
	t.Cleanup(func() {
		a.stop()
		if a.kept != nil {
			_ = a.kept.db.Close()
		}
	})
	return a
}

func moveFolder(root string, viewer int64) string {
	return filepath.Join(root, "accounts", messengerAccountKey(viewer))
}

func folderNames(t *testing.T, folder string) []string {
	t.Helper()
	entries, err := os.ReadDir(folder)
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		t.Fatal(err)
	}
	var names []string
	for _, entry := range entries {
		names = append(names, entry.Name())
	}
	return names
}

func writeMovedAway(t *testing.T, root string, viewer int64) {
	t.Helper()
	folder := moveFolder(root, viewer)
	if err := os.MkdirAll(folder, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(folder, movedAwayName), []byte(`{}`), 0o600); err != nil {
		t.Fatal(err)
	}
}

// A signed-in stand-in: ui.encrypted needs a client to exist, and none of these paths may touch it.
func signedInStandIn(a *adapter) { a.client = &messagix.Client{} }

func moveCall(t *testing.T, a *adapter, method string, params string) (any, string) {
	t.Helper()
	var raw json.RawMessage
	if params != "" {
		raw = json.RawMessage(params)
	}
	return a.callContext(context.Background(), request{ID: 1, Method: method, Params: raw})
}

func TestEncryptedRestoreNeverCreatesOrRegistersADevice(t *testing.T) {
	root := moveRoot(t)
	folder := moveFolder(root, moveViewer)
	a := moveAdapter(t)
	a.chats.setSelf(moveViewer)

	// No e2ee.db at all. Nothing may be created: not the database, its journal files, nor a device.
	if code := a.startEncrypted(context.Background(), true); code != "device_missing" {
		t.Fatalf("a restore with no device file answered %q", code)
	}
	for _, name := range []string{"e2ee.db", "e2ee.db-wal", "e2ee.db-shm"} {
		if _, err := os.Stat(filepath.Join(folder, name)); !os.IsNotExist(err) {
			t.Fatalf("the refused restore left %s behind", name)
		}
	}
	if a.crypto != nil {
		t.Fatal("a refused restore left an encrypted connection")
	}

	// Through the method, with the flag read from its params.
	signedInStandIn(a)
	if _, code := moveCall(t, a, "ui.encrypted", `{"restore":true}`); code != "device_missing" {
		t.Fatalf("ui.encrypted restore answered %q", code)
	}
	if _, code := moveCall(t, a, "ui.encrypted", `{"restore":`); code != "invalid_input" {
		t.Fatalf("unparseable params answered %q", code)
	}
	if _, err := os.Stat(filepath.Join(folder, "e2ee.db")); !os.IsNotExist(err) {
		t.Fatal("ui.encrypted restore created e2ee.db")
	}

	// A device file that holds no device for this account is just as missing. Built the way the store builds it.
	devices, err := sqlstore.New(context.Background(), "sqlite", "file:"+filepath.Join(folder, "e2ee.db")+"?_pragma=foreign_keys(1)", waLog.Noop)
	if err != nil {
		t.Fatal(err)
	}
	if err := devices.Close(); err != nil {
		t.Fatal(err)
	}
	// The client is nil here: reaching the registration step would dereference it and panic.
	a.client = nil
	if code := a.startEncrypted(context.Background(), true); code != "device_missing" {
		t.Fatalf("a restore against a file with no device answered %q", code)
	}
	if a.crypto != nil {
		t.Fatal("a restore without a device started an encrypted connection")
	}
}

func TestEncryptedWithoutRestoreStillGoesOnToRegister(t *testing.T) {
	root := moveRoot(t)
	a := moveAdapter(t)
	a.chats.setSelf(moveViewer)
	// restore false is the explicit turn-on: it creates the device file and goes on to register. Registration needs the
	// network, which this test must not use, so it stops where the nil client is first used; that it got that far is
	// the point.
	reached := func() (reached bool) {
		defer func() { reached = recover() != nil }()
		a.startEncrypted(context.Background(), false)
		return false
	}()
	if !reached {
		t.Fatal("restore false no longer proceeds to registration")
	}
	if _, err := os.Stat(filepath.Join(moveFolder(root, moveViewer), "e2ee.db")); err != nil {
		t.Fatal("restore false no longer creates the device file")
	}
}

func TestMovedAwayAccountDoesNotLoginOrStartEncrypted(t *testing.T) {
	root := moveRoot(t)
	folder := moveFolder(root, moveViewer)
	writeMovedAway(t, root, moveViewer)
	a := moveAdapter(t)

	// A context that is already over: should the guard ever be missing, the login fails at its first request instead of
	// reaching Facebook, and the answer is login_failed rather than moved_away.
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	params := `{"platform":"messenger","cookies":{"c_user":"4242","xs":"x","datr":"d"}}`
	if _, code := a.callContext(ctx, request{ID: 1, Method: "login", Params: json.RawMessage(params)}); code != "moved_away" {
		t.Fatalf("login of a moved-away account answered %q", code)
	}
	if a.client != nil || a.kept != nil {
		t.Fatal("a refused login left a client or a store")
	}

	a.chats.setSelf(moveViewer)
	for _, restore := range []bool{true, false} {
		if code := a.startEncrypted(context.Background(), restore); code != "moved_away" {
			t.Fatalf("startEncrypted(restore=%v) answered %q", restore, code)
		}
	}
	signedInStandIn(a)
	if _, code := moveCall(t, a, "ui.encrypted", ""); code != "moved_away" {
		t.Fatalf("ui.encrypted answered %q", code)
	}
	if names := folderNames(t, folder); !reflect.DeepEqual(names, []string{movedAwayName}) {
		t.Fatalf("a refused call wrote into the account folder: %v", names)
	}

	// Only this account: another one on the same root is not held back by it.
	other := moveAdapter(t)
	other.chats.setSelf(moveViewer + 1)
	if code := other.startEncrypted(context.Background(), true); code != "device_missing" {
		t.Fatalf("another account answered %q", code)
	}
}

func TestOfflineProcessRefusesEverythingButTheHistoryMethods(t *testing.T) {
	root := moveRoot(t)
	t.Setenv("MESSAGELINK_MESSENGER_OFFLINE", "1")
	a := moveAdapter(t)

	cookies := `{"platform":"messenger","cookies":{"c_user":"4242","xs":"x","datr":"d"}}`
	for _, method := range []string{"login", "connect", "ui.encrypted", "ui.sendText", "counts", "session", "ui.chats", "ui.markRead",
		"ui.mediaRead", "reauthenticate", "probe.media", "probe.receipt", "no.such.method"} {
		if _, code := moveCall(t, a, method, cookies); code != "offline_only" {
			t.Fatalf("an offline process answered %s with %q", method, code)
		}
	}
	if a.client != nil || a.crypto != nil || a.kept != nil {
		t.Fatal("an offline process created a client, a device connection or a store")
	}
	if names := folderNames(t, filepath.Join(root, "accounts")); len(names) != 0 {
		t.Fatalf("a refused login created account folders: %v", names)
	}

	result, code := moveCall(t, a, "hello", "")
	if code != "" || result.(map[string]any)["offline"] != true || result.(map[string]any)["connected"] != false {
		t.Fatalf("hello: %v %q", result, code)
	}
	if _, code := moveCall(t, a, "disconnect", ""); code != "" {
		t.Fatalf("disconnect with nothing connected answered %q", code)
	}
	if _, code := moveCall(t, a, "backup.exportEnd", ""); code != "" {
		t.Fatalf("backup.exportEnd answered %q", code)
	}
	// Nothing to export or import into until backup.open has opened a store.
	for _, method := range []string{"backup.export", "backup.import"} {
		if _, code := moveCall(t, a, method, `{}`); code != "login_required" {
			t.Fatalf("%s before backup.open answered %q", method, code)
		}
	}
	// A process that is not offline cannot open a store this way.
	t.Setenv("MESSAGELINK_MESSENGER_OFFLINE", "")
	online := moveAdapter(t)
	if _, code := moveCall(t, online, "backup.open", `{"account":"4242","create":true}`); code != "offline_only" {
		t.Fatalf("backup.open outside the offline process answered %q", code)
	}
	if names := folderNames(t, filepath.Join(root, "accounts")); len(names) != 0 {
		t.Fatalf("a refused backup.open created account folders: %v", names)
	}
}

func TestBackupOpenValidatesAndNeedsTheStoreUnlessCreating(t *testing.T) {
	root := moveRoot(t)
	t.Setenv("MESSAGELINK_MESSENGER_OFFLINE", "1")
	a := moveAdapter(t)
	for _, params := range []string{"", `{`, `{}`, `{"account":""}`, `{"account":"0"}`, `{"account":"-5"}`, `{"account":"abc"}`, `{"account":4242}`} {
		if _, code := moveCall(t, a, "backup.open", params); code != "invalid_input" {
			t.Fatalf("backup.open %q answered %q", params, code)
		}
	}
	if _, code := moveCall(t, a, "backup.open", `{"account":"4242"}`); code != "backup_unavailable" {
		t.Fatalf("opening a store that is not there answered %q", code)
	}
	if names := folderNames(t, filepath.Join(root, "accounts")); len(names) != 0 {
		t.Fatalf("opening without create made account folders: %v", names)
	}
	result, code := moveCall(t, a, "backup.open", `{"account":"4242","create":true}`)
	if code != "" || !reflect.DeepEqual(result, map[string]any{"opened": true, "account": messengerAccountKey(4242)}) {
		t.Fatalf("create: %v %q", result, code)
	}
	// Again: the same success, and the same open store.
	kept := a.kept
	if result, code = moveCall(t, a, "backup.open", `{"account":"4242","create":false}`); code != "" || a.kept != kept {
		t.Fatalf("second open: %v %q", result, code)
	}
	if _, code = moveCall(t, a, "backup.open", `{"account":"4243","create":true}`); code != "session_already_started" {
		t.Fatalf("a second account answered %q", code)
	}
	if a.client != nil {
		t.Fatal("an offline process created a client")
	}
}

func TestOfflineBackupMovesHistoryBetweenTwoRootsWithoutSigningIn(t *testing.T) {
	budget := backupPageBudget
	backupPageBudget = 512
	t.Cleanup(func() { backupPageBudget = budget })
	t.Setenv("MESSAGELINK_MESSENGER_OFFLINE", "1")
	account := `{"account":"1001","create":true}`

	// The PC: its store is made and filled the way a signed-in helper fills it, then read out by an offline process.
	rootA := moveRoot(t)
	source := moveAdapter(t)
	if _, code := moveCall(t, source, "backup.open", account); code != "" {
		t.Fatalf("source open: %q", code)
	}
	seedBackupSource(t, source)
	pages, expected, exported := offlineExport(t, source)
	if len(pages) < 3 || expected.Messages != 4 {
		t.Fatalf("the source exported %d pages, %+v", len(pages), expected)
	}

	// The server: another root, an account it has never seen, the same Facebook id.
	rootB := moveRoot(t)
	if rootA == rootB {
		t.Fatal("the two stores share a root")
	}
	target := moveAdapter(t)
	if _, code := moveCall(t, target, "backup.open", account); code != "" {
		t.Fatalf("target open: %q", code)
	}
	for index, page := range pages {
		if _, code := moveCall(t, target, "backup.import", string(page)); code != "" {
			t.Fatalf("import page %d answered %q", index, code)
		}
	}
	if source.client != nil || target.client != nil {
		t.Fatal("an offline process created a client")
	}

	_, expectedAgain, exportedAgain := offlineExport(t, target)
	expected.Orphans = 0 // Rows hung on a missing message are not carried across.
	if expectedAgain != expected || exportedAgain != exported {
		t.Fatalf("the moved store exports differently:\n%+v / %+v\n%+v / %+v", expectedAgain, exportedAgain, expected, exported)
	}
	if !reflect.DeepEqual(restoredView(t, target.kept), restoredView(t, source.kept)) {
		t.Fatal("the moved store does not restore the same history")
	}

	// A page of another account is still refused: the server must be opened for the same id.
	other := moveAdapter(t)
	if _, code := moveCall(t, other, "backup.open", `{"account":"1002","create":true}`); code != "" {
		t.Fatalf("other account open: %q", code)
	}
	if _, code := moveCall(t, other, "backup.import", string(pages[0])); code != "backup_other_account" {
		t.Fatalf("a page of another account answered %q", code)
	}
}

// Reads a whole export through the method, as the app would, and answers its pages with the first page's expected tally
// and the last page's exported one.
func offlineExport(t *testing.T, a *adapter) ([][]byte, backupTally, backupTally) {
	t.Helper()
	var pages [][]byte
	cursor := ""
	for range 10000 {
		params, _ := json.Marshal(map[string]string{"cursor": cursor})
		result, code := moveCall(t, a, "backup.export", string(params))
		if code != "" {
			t.Fatalf("export page %d answered %q", len(pages), code)
		}
		raw, err := json.Marshal(result)
		backupMust(t, err)
		pages = append(pages, raw)
		if cursor = result.(*backupPageOut).Next; cursor == "" {
			break
		}
	}
	var first, last struct {
		Expected *backupTally `json:"expected"`
		Exported *backupTally `json:"exported"`
	}
	backupMust(t, json.Unmarshal(pages[0], &first))
	backupMust(t, json.Unmarshal(pages[len(pages)-1], &last))
	if first.Expected == nil || last.Exported == nil {
		t.Fatal("the export has no tallies")
	}
	if strings.Contains(string(pages[0]), "\"cursor\"") {
		t.Fatal("unexpected page shape")
	}
	return pages, *first.Expected, *last.Exported
}
