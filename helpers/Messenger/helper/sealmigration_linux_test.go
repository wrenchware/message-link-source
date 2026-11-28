package main

import (
	"bytes"
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
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

// A cell in the server's seal from before the rename (MSP 2), under the given key, or this test run's key when nil.
func previousSealCell(t *testing.T, key []byte, plain string) []byte {
	t.Helper()
	if key == nil {
		var err error
		key, err = protectionKey()
		backupMust(t, err)
		defer clear(key)
	}
	block, err := aes.NewCipher(key)
	backupMust(t, err)
	gcm, err := cipher.NewGCM(block)
	backupMust(t, err)
	nonce := make([]byte, gcm.NonceSize())
	_, err = rand.Read(nonce)
	backupMust(t, err)
	return gcm.Seal(append(append([]byte{}, previousSealPrefix...), nonce...), nonce, []byte(plain), previousEntropy)
}

// The startup migration opens every cell in the previous server seal and seals it again in this build's own, in the
// transaction that seals plaintext, so a store that was interrupted is migrated whole on the next start and a store
// migrated already is left as it is. A previous-seal cell under another key opens nothing and stays byte for byte.
func TestStartupMigrationResealsThePreviousServerSeal(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "messages.db")
	kept, err := openMessageStore(ctx, path)
	backupMust(t, err)
	other := make([]byte, 32)
	_, err = rand.Read(other)
	backupMust(t, err)
	written := map[string][]byte{
		"previous": previousSealCell(t, nil, "Synthetic body in the previous seal"),
		"another":  previousSealCell(t, other, "Synthetic body under another key"),
		// What a build from before the rename makes of this build's seal over a rolled-back store: its own seal around it.
		"doubled": previousSealCell(t, nil, string(mustSeal(t, "Synthetic body sealed twice"))),
	}
	at := int64(1000)
	for _, id := range []string{"previous", "another", "doubled", "current"} {
		backupMust(t, kept.rememberChecked(ctx, 7, id, 555, "Synthetic current body", "", at))
		if cell := written[id]; cell != nil {
			_, err = kept.db.ExecContext(ctx, `UPDATE encrypted_message SET body = ? WHERE id = ?`, cell, id)
			backupMust(t, err)
		}
		at++
	}
	identity := newStore()
	identity.contacts[555], identity.pictures[555] = "Synthetic person", "https://example.test/synthetic-face.jpg"
	kept.saveIdentity(ctx, identity)
	_, err = kept.db.ExecContext(ctx, `UPDATE thread_contact SET name = ? WHERE id = 555`, previousSealCell(t, nil, "Synthetic person"))
	backupMust(t, err)
	if _, err := unprotectLocal(written["previous"]); err == nil {
		t.Fatal("a read opened the previous seal")
	}
	backupMust(t, kept.db.Close())

	before := refusalsNow()
	kept, err = openMessageStore(ctx, path)
	backupMust(t, err)
	if got := refusalsNow().since(before); got != (refusalCounts{unknown: 1}) {
		t.Fatalf("the open counted %+v", got)
	}
	into := newStore()
	restored := kept.restore(ctx, into)
	backupMust(t, kept.db.Close())
	stored := storedBodies(t, path)
	if !bytes.HasPrefix(stored["previous"], protectedPrefix) || bytes.Contains(stored["previous"], []byte("Synthetic")) {
		t.Fatal("the previous-seal body was not sealed again")
	}
	if opened, err := unprotectString(stored["previous"]); err != nil || opened != "Synthetic body in the previous seal" {
		t.Fatal("the resealed body does not open to its text")
	}
	if !bytes.Equal(stored["another"], written["another"]) {
		t.Fatal("a cell under another key was rewritten")
	}
	if restored != 3 || into.messages[7]["doubled"] == nil || into.messages[7]["doubled"].text != "Synthetic body sealed twice" || into.messages[7]["previous"] == nil || into.messages[7]["previous"].text != "Synthetic body in the previous seal" ||
		into.messages[7]["current"] == nil || into.messages[7]["another"] != nil {
		t.Fatalf("the restore brought back %d messages, not the resealed, the unwrapped and the current one", restored)
	}
	if name, _ := contactCells(t, path); !bytes.HasPrefix(name, protectedPrefix) || into.contacts[555] != "Synthetic person" {
		t.Fatal("the previous-seal contact name was not sealed again")
	}

	// Run again: nothing is left in the previous seal that opens, so nothing changes.
	kept, err = openMessageStore(ctx, path)
	backupMust(t, err)
	backupMust(t, kept.db.Close())
	again := storedBodies(t, path)
	for id, cell := range stored {
		if !bytes.Equal(again[id], cell) {
			t.Fatalf("a second start rewrote the %s body", id)
		}
	}
}

func mustSeal(t *testing.T, plain string) []byte {
	t.Helper()
	sealed, err := protectString(plain)
	backupMust(t, err)
	return sealed
}
