package main

import (
	"bytes"
	"context"
	"path/filepath"
	"testing"
)

// A Windows store copied onto the server holds cells sealed with DPAPI. The startup migration must leave them byte for
// byte rather than seal their ciphertext as if it were text, and a read refuses them.
func TestStartupMigrationLeavesWindowsSealsUntouched(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "messages.db")
	kept, err := openMessageStore(ctx, path)
	backupMust(t, err)
	written := map[string][]byte{
		"plain":   []byte("Synthetic plaintext body"),
		"windows": append([]byte{'M', 'L', 'P', 1}, bytes.Repeat([]byte{7}, 40)...),
		"retired": append([]byte{'N', 'B', 'P', 1}, bytes.Repeat([]byte{8}, 40)...),
	}
	at := int64(1000)
	for id, body := range written {
		backupMust(t, kept.rememberChecked(ctx, 7, id, 555, "placeholder", "", at))
		_, err = kept.db.ExecContext(ctx, `UPDATE encrypted_message SET body = ? WHERE id = ?`, body, id)
		backupMust(t, err)
		at++
	}
	backupMust(t, kept.db.Close())
	before := refusalsNow()
	kept, err = openMessageStore(ctx, path)
	backupMust(t, err)
	if got := refusalsNow().since(before); got != (refusalCounts{retired: 1, unknown: 1}) {
		t.Fatalf("the open counted %+v", got)
	}
	into := newStore()
	restored := kept.restore(ctx, into)
	backupMust(t, kept.db.Close())
	stored := storedBodies(t, path)
	if !bytes.Equal(stored["windows"], written["windows"]) || !bytes.Equal(stored["retired"], written["retired"]) {
		t.Fatal("the migration rewrote a Windows-sealed body")
	}
	if !bytes.HasPrefix(stored["plain"], protectedPrefix) {
		t.Fatal("the plaintext body was not sealed")
	}
	if restored != 1 || into.messages[7]["plain"] == nil || into.messages[7]["windows"] != nil || into.messages[7]["retired"] != nil {
		t.Fatalf("the restore brought back %d messages, not only the plaintext one", restored)
	}
}
