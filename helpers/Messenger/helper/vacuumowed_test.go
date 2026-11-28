package main

import (
	"bytes"
	"context"
	"database/sql"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func vacuumOwed(t *testing.T, store *messageStore) int {
	t.Helper()
	var marks int
	backupMust(t, store.db.QueryRowContext(context.Background(), `SELECT COUNT(*) FROM vacuum_owed`).Scan(&marks))
	return marks
}

// A start that stops between the migration's commit and the rebuild leaves its cells sealed and nothing to migrate, so
// the rebuild has to be owed with the rewrite: each later start tries it until it succeeds. A rebuild that fails does
// not hold the start up.
func TestProtectExistingRebuildsAfterAnInterruptedStart(t *testing.T) {
	ctx := context.Background()
	logFile := filepath.Join(t.TempDir(), "helper.log")
	t.Setenv("MESSAGELINK_MESSENGER_LOG", logFile)
	openLog()
	defer closeLog()
	path := filepath.Join(t.TempDir(), "messages.db")
	store, err := openMessageStore(ctx, path)
	backupMust(t, err)
	_, err = store.db.ExecContext(ctx, `INSERT INTO encrypted_message (id, thread, sender, body, at) VALUES ('legacy', 7, 42, ?, 1600)`, strings.Repeat("synthetic legacy plaintext ", 400))
	backupMust(t, err)
	backupMust(t, store.db.Close())

	rebuild := keptVacuum
	defer func() { keptVacuum = rebuild }()
	tries, rebuilt := 0, 0
	keptVacuum = func(context.Context, *sql.DB) error {
		tries++
		return errors.New("stopped before the rebuild")
	}
	// The start that migrates and stops, and one after it that still cannot rebuild: both open, and the debt stays.
	for range 2 {
		store, err = openMessageStore(ctx, path)
		if err != nil {
			t.Fatalf("a failed rebuild held the start up: %v", err)
		}
		if vacuumOwed(t, store) != 1 {
			t.Fatal("the rebuild is not owed after the start that could not do it")
		}
		backupMust(t, store.db.Close())
	}
	// A body longer than a page spills onto overflow pages, which the rewrite frees without clearing: what the rebuild
	// is for. Without it here the check after the rebuild would prove nothing.
	raw, err := os.ReadFile(path)
	backupMust(t, err)
	if !bytes.Contains(raw, []byte("synthetic legacy plaintext")) {
		t.Fatal("the interrupted start left no plaintext in free pages, so this test no longer shows the rebuild removing it")
	}
	stored := storedBodies(t, path)
	if !bytes.HasPrefix(stored["legacy"], protectedPrefix) {
		t.Fatal("the plaintext body was not sealed by the interrupted start")
	}

	keptVacuum = func(ctx context.Context, db *sql.DB) error {
		rebuilt++
		return rebuild(ctx, db)
	}
	// The next start has nothing to migrate and rebuilds anyway, then the one after owes nothing.
	for range 2 {
		store, err = openMessageStore(ctx, path)
		backupMust(t, err)
		if vacuumOwed(t, store) != 0 {
			t.Fatal("the rebuild is still owed after it succeeded")
		}
		backupMust(t, store.db.Close())
	}
	if tries != 2 || rebuilt != 1 {
		t.Fatalf("the rebuild was tried %d times and done %d, not 2 and 1", tries, rebuilt)
	}
	raw, err = os.ReadFile(path)
	backupMust(t, err)
	if bytes.Contains(raw, []byte("synthetic legacy plaintext")) {
		t.Fatal("legacy plaintext survived the owed rebuild")
	}

	closeLog()
	log, err := os.ReadFile(logFile)
	backupMust(t, err)
	var starts []string
	for _, line := range strings.Split(string(log), "\n") {
		if strings.Contains(line, "kept store protect ") {
			starts = append(starts, line)
		}
	}
	want := []struct{ sealed, vacuum string }{
		// The new store's first open owes the rebuild every store made before the mark is given.
		{"sealed=0 ", "vacuum=true "}, {"sealed=1 ", "vacuum=failed "}, {"sealed=0 ", "vacuum=failed "},
		{"sealed=0 ", "vacuum=true "}, {"sealed=0 ", "vacuum=false "},
	}
	if len(starts) != len(want) {
		t.Fatalf("the log shows %d starts, not %d", len(starts), len(want))
	}
	for i, w := range want {
		if !strings.Contains(starts[i], w.sealed) || !strings.Contains(starts[i], w.vacuum) {
			t.Fatalf("start %d logged %q, not %s%s", i+1, starts[i], w.sealed, w.vacuum)
		}
	}
}

// A body long enough to spill onto overflow pages, which a rewrite or a delete frees without clearing.
var spilledPlaintext = strings.Repeat("synthetic spilled plaintext ", 400)

func plaintextOnDisk(t *testing.T, path string) bool {
	t.Helper()
	raw, err := os.ReadFile(path)
	backupMust(t, err)
	return bytes.Contains(raw, []byte("synthetic spilled plaintext"))
}

// A store an earlier build migrated and stopped before rebuilding has no mark and nothing left to migrate. Every store
// made before the mark existed is rebuilt once when the mark's table is made.
func TestProtectExistingRebuildsAStoreFromBeforeTheMark(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "messages.db")
	store, err := openMessageStore(ctx, path)
	backupMust(t, err)
	_, err = store.db.ExecContext(ctx, `INSERT INTO encrypted_message (id, thread, sender, body, at) VALUES ('legacy', 7, 42, ?, 1600)`, spilledPlaintext)
	backupMust(t, err)
	// What the earlier build committed before it stopped: the cell sealed, and no rebuild.
	sealed, err := protectString(spilledPlaintext)
	backupMust(t, err)
	_, err = store.db.ExecContext(ctx, `UPDATE encrypted_message SET body = ? WHERE id = 'legacy'`, sealed)
	backupMust(t, err)
	_, err = store.db.ExecContext(ctx, `DROP TABLE vacuum_owed`)
	backupMust(t, err)
	backupMust(t, store.db.Close())
	if !plaintextOnDisk(t, path) {
		t.Fatal("the earlier build's rewrite left no plaintext in free pages, so this test no longer shows the rebuild removing it")
	}
	store, err = openMessageStore(ctx, path)
	backupMust(t, err)
	if vacuumOwed(t, store) != 0 {
		t.Fatal("the rebuild is still owed after the first open")
	}
	backupMust(t, store.db.Close())
	if plaintextOnDisk(t, path) {
		t.Fatal("plaintext from the earlier build's migration survived the first open")
	}
}

// The start's purge of a taken-back message's attachments can remove legacy plaintext, which owes the rebuild as a
// rewritten cell does.
func TestProtectExistingOwesTheRebuildForPurgedAttachments(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "messages.db")
	store, err := openMessageStore(ctx, path)
	backupMust(t, err)
	_, err = store.db.ExecContext(ctx, `INSERT INTO deleted_message (id, thread, at) VALUES ('gone', 7, 1600)`)
	backupMust(t, err)
	_, err = store.db.ExecContext(ctx, `INSERT INTO message_attachment (id, message, thread, name, mime, size, width, height,
		video, animated, source, preview, playable, previewSource) VALUES ('a', 'gone', 7, ?, 'image/jpeg', 1, 1, 1, 0, 0, '', '', '', '')`, spilledPlaintext)
	backupMust(t, err)
	backupMust(t, store.db.Close())

	rebuild := keptVacuum
	defer func() { keptVacuum = rebuild }()
	keptVacuum = func(context.Context, *sql.DB) error { return errors.New("stopped before the rebuild") }
	store, err = openMessageStore(ctx, path)
	backupMust(t, err)
	if tableCount(t, store, "message_attachment") != 0 || vacuumOwed(t, store) != 1 {
		t.Fatal("the purge did not remove the attachment and owe the rebuild")
	}
	backupMust(t, store.db.Close())
	keptVacuum = rebuild
	store, err = openMessageStore(ctx, path)
	backupMust(t, err)
	backupMust(t, store.db.Close())
	if plaintextOnDisk(t, path) {
		t.Fatal("the purged attachment's plaintext survived the owed rebuild")
	}
}

// A copy left in write-ahead mode takes its rebuilt pages into the log; the database file holds the old ones until a
// checkpoint after the rebuild, so the mark is cleared only once that has run.
func TestProtectExistingRebuildReachesTheFileInWriteAheadMode(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "messages.db")
	store, err := openMessageStore(ctx, path)
	backupMust(t, err)
	var mode string
	backupMust(t, store.db.QueryRowContext(ctx, `PRAGMA journal_mode=WAL`).Scan(&mode))
	if mode != "wal" {
		t.Fatalf("the store went into %q mode, not wal", mode)
	}
	_, err = store.db.ExecContext(ctx, `INSERT INTO encrypted_message (id, thread, sender, body, at) VALUES ('legacy', 7, 42, ?, 1600)`, spilledPlaintext)
	backupMust(t, err)
	backupMust(t, store.db.Close())
	store, err = openMessageStore(ctx, path)
	backupMust(t, err)
	defer store.db.Close()
	if vacuumOwed(t, store) != 0 {
		t.Fatal("the rebuild is still owed after it succeeded")
	}
	// Read while the store is open: closing it checkpoints on its own and would hide a rebuild left in the log.
	for _, file := range []string{path, path + "-wal"} {
		raw, err := os.ReadFile(file)
		if errors.Is(err, os.ErrNotExist) {
			continue
		}
		backupMust(t, err)
		if bytes.Contains(raw, []byte("synthetic spilled plaintext")) {
			t.Fatalf("plaintext survived the rebuild in %s", filepath.Base(file))
		}
	}
}
