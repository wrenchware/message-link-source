package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const (
	migrationThread    = int64(7)
	migrationPlainText = "Synthetic plaintext body"
	migrationOwnText   = "Synthetic own body"
)

// A kept store holding one body in each form a copied or older store can carry: plaintext from before cells were sealed,
// this build's own seal, the retired Windows seal and a Linux server's. Synthetic text only. Answers each id's body
// bytes as written.
func seedForeignSealStore(t *testing.T, path string, viewer int64) map[string][]byte {
	t.Helper()
	ctx := context.Background()
	backupMust(t, os.MkdirAll(filepath.Dir(path), 0o700))
	kept, err := openAccountMessageStore(ctx, path, viewer)
	backupMust(t, err)
	own, err := protectString(migrationOwnText)
	backupMust(t, err)
	server := append([]byte{'M', 'S', 'P', 2}, make([]byte, 12+40)...)
	_, err = rand.Read(server[4:])
	backupMust(t, err)
	bodies := map[string][]byte{
		"plain":   []byte(migrationPlainText),
		"own":     own,
		"retired": sealWith(t, []byte("Synthetic retired body"), []byte{'N', 'B', 'P', 1}, []byte("retired Messenger kept-history entropy v1")),
		"server":  server,
	}
	at := int64(1000)
	for id, body := range bodies {
		backupMust(t, kept.rememberChecked(ctx, migrationThread, id, 555, "placeholder", "", at))
		_, err = kept.db.ExecContext(ctx, `UPDATE encrypted_message SET body = ? WHERE id = ?`, body, id)
		backupMust(t, err)
		at++
	}
	backupMust(t, kept.db.Close())
	return bodies
}

// Only the plaintext body is sealed by the startup migration; the three sealed ones are left byte for byte.
func checkMigrated(t *testing.T, written, stored map[string][]byte) {
	t.Helper()
	if len(stored) != len(written) {
		t.Fatalf("the store holds %d bodies, not %d", len(stored), len(written))
	}
	for _, id := range []string{"own", "retired", "server"} {
		if !bytes.Equal(stored[id], written[id]) {
			t.Fatalf("the migration rewrote the %s body", id)
		}
	}
	if !bytes.HasPrefix(stored["plain"], protectedPrefix) {
		t.Fatal("the plaintext body was not sealed")
	}
	if text, err := unprotectString(stored["plain"]); err != nil || text != migrationPlainText {
		t.Fatal("the sealed plaintext body does not open to its text")
	}
}

func TestStartupMigrationLeavesForeignSealsUntouched(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "accounts", messengerAccountKey(backupViewer), "messages.db")
	written := seedForeignSealStore(t, path, backupViewer)
	// The seed's own open sealed the placeholders; the bodies were swapped in after it, so the next open is the
	// migration over a store in all four forms.
	for pass := range 2 {
		before := refusalsNow()
		kept, err := openAccountMessageStore(ctx, path, backupViewer)
		if err != nil {
			t.Fatalf("open %d failed", pass)
		}
		// Each foreign body is counted once per open (the reply and card columns are empty), and nothing is plaintext.
		if got := refusalsNow().since(before); got != (refusalCounts{retired: 1, unknown: 1}) {
			t.Fatalf("open %d counted %+v", pass, got)
		}
		before = refusalsNow()
		into := newStore()
		restored := kept.restore(ctx, into)
		backupMust(t, kept.db.Close())
		if got := refusalsNow().since(before); got != (refusalCounts{retired: 1, unknown: 1}) {
			t.Fatalf("restore %d counted %+v", pass, got)
		}
		if restored != 2 {
			t.Fatalf("restore %d brought back %d messages, not 2", pass, restored)
		}
		messages := into.messages[migrationThread]
		if messages["plain"] == nil || messages["plain"].text != migrationPlainText || messages["own"] == nil || messages["own"].text != migrationOwnText {
			t.Fatal("the plaintext and own bodies did not restore as their text")
		}
		if messages["retired"] != nil || messages["server"] != nil {
			t.Fatal("a foreign-sealed body was restored")
		}
		stored := storedBodies(t, path)
		checkMigrated(t, written, stored)
		if pass == 0 {
			// The second open finds the plaintext already sealed and must leave every body as the first left it.
			written["plain"] = stored["plain"]
		}
	}
}

// The offline helper a move uses opens the store with the same migration, and its export carries only what opened.
func TestOfflineBackupOpenLeavesForeignSealsUntouched(t *testing.T) {
	t.Setenv("MESSAGELINK_MESSENGER_OFFLINE", "1")
	root := moveRoot(t)
	path := filepath.Join(moveFolder(root, backupViewer), "messages.db")
	written := seedForeignSealStore(t, path, backupViewer)
	a := moveAdapter(t)
	before := refusalsNow()
	if _, code := moveCall(t, a, "backup.open", `{"account":"1001","create":false}`); code != "" {
		t.Fatalf("backup open answered %q", code)
	}
	// The migration counts each foreign body once and the open's restore once more.
	if got := refusalsNow().since(before); got != (refusalCounts{retired: 2, unknown: 2}) {
		t.Fatalf("backup open counted %+v", got)
	}
	checkMigrated(t, written, storedBodies(t, path))
	pages, _, _ := offlineExport(t, a)
	all := string(bytes.Join(pages, []byte("\n")))
	if !strings.Contains(all, migrationPlainText) || !strings.Contains(all, migrationOwnText) {
		t.Fatal("the export lost a body that opened")
	}
	if strings.Contains(all, `"retired"`) || strings.Contains(all, `"server"`) || strings.Contains(all, "Synthetic retired body") {
		t.Fatal("the export carried a foreign-sealed body")
	}
}
