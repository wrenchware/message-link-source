package main

import (
	"bytes"
	"context"
	"database/sql"
	"path/filepath"
	"strings"
	"testing"
)

// The device file as earlier builds left it: messages kept beside the device's keys, in plain text.
func writeLegacyMessages(t *testing.T, path string) {
	t.Helper()
	legacy, err := sql.Open("sqlite", sqliteFile(path, ""))
	backupMust(t, err)
	defer legacy.Close()
	_, err = legacy.ExecContext(context.Background(), `CREATE TABLE encrypted_message (thread INTEGER, id TEXT, sender INTEGER, body TEXT, at INTEGER);
		INSERT INTO encrypted_message VALUES (7, 'legacy-a', 555, 'Private first words', 1000);
		INSERT INTO encrypted_message VALUES (7, 'legacy-b', 1001, 'Private second words', 2000);
		INSERT INTO encrypted_message VALUES (8, 'legacy-c', 556, 'Private third words', 3000)`)
	backupMust(t, err)
}

func legacyTableLeft(t *testing.T, path string) bool {
	t.Helper()
	legacy, err := sql.Open("sqlite", sqliteFile(path, ""))
	backupMust(t, err)
	defer legacy.Close()
	var count int
	backupMust(t, legacy.QueryRow(`SELECT count(*) FROM sqlite_master WHERE name = 'encrypted_message'`).Scan(&count))
	return count > 0
}

// The kept row's words, unsealed, and how many rows the store holds under that id.
func keptBody(t *testing.T, kept *messageStore, id string) (string, int) {
	t.Helper()
	var count int
	backupMust(t, kept.db.QueryRow(`SELECT count(*) FROM encrypted_message WHERE id = ?`, id).Scan(&count))
	if count == 0 {
		return "", 0
	}
	var sealed []byte
	var form string
	backupMust(t, kept.db.QueryRow(`SELECT body, typeof(body) FROM encrypted_message WHERE id = ?`, id).Scan(&sealed, &form))
	if form != "blob" {
		t.Fatalf("%s is kept as %s, not sealed", id, form)
	}
	body, err := unprotectString(sealed)
	backupMust(t, err)
	return body, count
}

func memoryText(a *adapter, thread int64, id string) (string, bool) {
	a.chats.mu.Lock()
	defer a.chats.mu.Unlock()
	held := a.chats.messages[thread][id]
	if held == nil {
		return "", false
	}
	return held.text, true
}

// A first move copies every message, sealed, into the kept store and its conversation, then gives the old table up.
func TestLegacyMigrationCopiesEveryMessage(t *testing.T) {
	ctx := context.Background()
	a, _ := backupAdapter(t, t.TempDir(), backupViewer)
	e2ee := filepath.Join(filepath.Dir(a.kept.path), "e2ee.db")
	writeLegacyMessages(t, e2ee)

	if moved := a.kept.absorb(ctx, e2ee, a.chats); moved != 3 {
		t.Fatalf("moved %d of 3", moved)
	}
	if legacyTableLeft(t, e2ee) {
		t.Fatal("the old table was kept after a complete copy")
	}
	want := map[string]struct {
		thread int64
		body   string
	}{"legacy-a": {7, "Private first words"}, "legacy-b": {7, "Private second words"}, "legacy-c": {8, "Private third words"}}
	for id, w := range want {
		if body, count := keptBody(t, a.kept, id); body != w.body || count != 1 {
			t.Fatalf("%s kept as %q x%d", id, body, count)
		}
		if text, ok := memoryText(a, w.thread, id); !ok || text != w.body {
			t.Fatalf("%s not in its conversation: %q %v", id, text, ok)
		}
	}
	backupRelogin(a)
	for id, w := range want {
		if text, ok := memoryText(a, w.thread, id); !ok || text != w.body {
			t.Fatalf("%s not restored: %q %v", id, text, ok)
		}
	}
}

// A drop that failed after the copy leaves the old table, and the next start copies again. An edit and a deletion that
// reached the kept store in between both survive that second copy, and no message is written twice.
func TestLegacyMigrationRetryKeepsNewerEdits(t *testing.T) {
	ctx := context.Background()
	a, _ := backupAdapter(t, t.TempDir(), backupViewer)
	e2ee := filepath.Join(filepath.Dir(a.kept.path), "e2ee.db")
	writeLegacyMessages(t, e2ee)
	if moved := a.kept.absorb(ctx, e2ee, a.chats); moved != 3 {
		t.Fatalf("moved %d of 3", moved)
	}
	// The drop failing: the same table, the same rows, still in the device file.
	writeLegacyMessages(t, e2ee)

	a.kept.rememberEdit(ctx, "legacy-a", "Private corrected words", 1500)
	backupMust(t, a.kept.forget(ctx, 7, "legacy-b"))

	// The next start: memory from the kept store, then the move again.
	backupRelogin(a)
	a.kept.absorb(ctx, e2ee, a.chats)

	if body, count := keptBody(t, a.kept, "legacy-a"); body != "Private corrected words" || count != 1 {
		t.Fatalf("the edit did not survive the second copy: %q x%d", body, count)
	}
	if text, _ := memoryText(a, 7, "legacy-a"); text != "Private corrected words" {
		t.Fatalf("the conversation shows the first words again: %q", text)
	}
	if _, count := keptBody(t, a.kept, "legacy-b"); count != 0 {
		t.Fatal("a deleted message came back from the old table")
	}
	if _, ok := memoryText(a, 7, "legacy-b"); ok {
		t.Fatal("a deleted message came back into its conversation")
	}
	if body, count := keptBody(t, a.kept, "legacy-c"); body != "Private third words" || count != 1 {
		t.Fatalf("an untouched message changed: %q x%d", body, count)
	}
	var total int
	backupMust(t, a.kept.db.QueryRow(`SELECT count(*) FROM encrypted_message`).Scan(&total))
	if total != 2 {
		t.Fatalf("the store holds %d rows, not 2", total)
	}
	if legacyTableLeft(t, e2ee) {
		t.Fatal("the second copy did not give the old table up")
	}

	// And at the start after that, still the corrected words.
	backupRelogin(a)
	if text, _ := memoryText(a, 7, "legacy-a"); text != "Private corrected words" {
		t.Fatalf("the edit was lost by the following start: %q", text)
	}
}

// Deleting a whole conversation removes its messages and their deletion records together, so only the conversation's
// cutoff remembers them. A second copy honors it: nothing at or before it is written back, where a backup would carry it.
func TestLegacyMigrationRetryKeepsADeletedConversationOut(t *testing.T) {
	ctx := context.Background()
	a, _ := backupAdapter(t, t.TempDir(), backupViewer)
	e2ee := filepath.Join(filepath.Dir(a.kept.path), "e2ee.db")
	writeLegacyMessages(t, e2ee)
	if moved := a.kept.absorb(ctx, e2ee, a.chats); moved != 3 {
		t.Fatalf("moved %d of 3", moved)
	}
	writeLegacyMessages(t, e2ee)
	a.forgetChat(ctx, 7)

	backupRelogin(a)
	if moved := a.kept.absorb(ctx, e2ee, a.chats); moved != 0 {
		t.Fatalf("the second copy wrote %d messages", moved)
	}
	for _, id := range []string{"legacy-a", "legacy-b"} {
		if _, count := keptBody(t, a.kept, id); count != 0 {
			t.Fatalf("%s came back into a deleted conversation's store", id)
		}
		if _, ok := memoryText(a, 7, id); ok {
			t.Fatalf("%s came back into a deleted conversation", id)
		}
	}
	if body, count := keptBody(t, a.kept, "legacy-c"); body != "Private third words" || count != 1 {
		t.Fatalf("another conversation's message changed: %q x%d", body, count)
	}
	for _, page := range exportAll(t, a) {
		if strings.Contains(string(page), `"legacy-a"`) || strings.Contains(string(page), `"legacy-b"`) {
			t.Fatal("a backup carries a deleted conversation's message")
		}
	}
	if legacyTableLeft(t, e2ee) {
		t.Fatal("the second copy did not give the old table up")
	}
}

// A kept copy in a seal this build refuses (here the retired one) is left exactly as it is, and the old table, which
// then holds the only readable copy, is not dropped.
func TestLegacyMigrationKeepsTheOldTableBehindARefusedSeal(t *testing.T) {
	ctx := context.Background()
	a, _ := backupAdapter(t, t.TempDir(), backupViewer)
	e2ee := filepath.Join(filepath.Dir(a.kept.path), "e2ee.db")
	writeLegacyMessages(t, e2ee)
	if moved := a.kept.absorb(ctx, e2ee, a.chats); moved != 3 {
		t.Fatalf("moved %d of 3", moved)
	}
	writeLegacyMessages(t, e2ee)
	retired := append(append([]byte{}, retiredSealPrefix...), 1, 2, 3, 4, 5, 6, 7, 8)
	_, err := a.kept.db.ExecContext(ctx, `UPDATE encrypted_message SET body = ? WHERE id = 'legacy-a'`, retired)
	backupMust(t, err)

	backupRelogin(a)
	if moved := a.kept.absorb(ctx, e2ee, a.chats); moved != 0 {
		t.Fatalf("the second copy wrote %d messages", moved)
	}
	var kept []byte
	backupMust(t, a.kept.db.QueryRow(`SELECT body FROM encrypted_message WHERE id = 'legacy-a'`).Scan(&kept))
	if !bytes.Equal(kept, retired) {
		t.Fatal("the refused seal was rewritten")
	}
	if !legacyTableLeft(t, e2ee) {
		t.Fatal("the old table was dropped while its copy of a message is the only one that opens")
	}
	var total int
	backupMust(t, a.kept.db.QueryRow(`SELECT count(*) FROM encrypted_message`).Scan(&total))
	if total != 3 {
		t.Fatalf("the store holds %d rows, not 3", total)
	}

	// Once the kept copy opens again, the next start gives the table up.
	sealed, err := protectString("Private first words")
	backupMust(t, err)
	_, err = a.kept.db.ExecContext(ctx, `UPDATE encrypted_message SET body = ? WHERE id = 'legacy-a'`, sealed)
	backupMust(t, err)
	a.kept.absorb(ctx, e2ee, a.chats)
	if legacyTableLeft(t, e2ee) {
		t.Fatal("the old table was kept after every kept copy opened")
	}
}
