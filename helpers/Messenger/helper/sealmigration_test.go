package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"database/sql"
	"path/filepath"
	"testing"
)

// A store's message bodies as SQLite holds them, read past the helper.
func storedBodies(t *testing.T, path string) map[string][]byte {
	t.Helper()
	db, err := sql.Open("sqlite", sqliteFile(path, "_pragma=busy_timeout(5000)"))
	backupMust(t, err)
	defer db.Close()
	rows, err := db.Query(`SELECT id, body FROM encrypted_message`)
	backupMust(t, err)
	defer rows.Close()
	found := map[string][]byte{}
	for rows.Next() {
		var id string
		var body []byte
		backupMust(t, rows.Scan(&id, &body))
		found[id] = body
	}
	backupMust(t, rows.Err())
	return found
}

type refusalCounts struct{ retired, unknown, plain int64 }

func refusalsNow() refusalCounts {
	return refusalCounts{refusedRetired.Load(), refusedUnknown.Load(), refusedPlain.Load()}
}

func (c refusalCounts) since(before refusalCounts) refusalCounts {
	return refusalCounts{c.retired - before.retired, c.unknown - before.unknown, c.plain - before.plain}
}

// A cell in the retired Windows seal: refused on every platform, so it stands for a contact field that does not open.
func retiredCell(t *testing.T) []byte {
	t.Helper()
	cell := append([]byte{'N', 'B', 'P', 1}, make([]byte, 40)...)
	_, err := rand.Read(cell[4:])
	backupMust(t, err)
	return cell
}

// Legacy text was written as TEXT and every seal as a BLOB, so text that begins like a seal is still legacy text.
func TestStartupMigrationSealsTextThatBeginsLikeASeal(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "messages.db")
	kept, err := openMessageStore(ctx, path)
	backupMust(t, err)
	texts := map[string]string{
		"windows": "MLP\x01 synthetic legacy text",
		"server":  "MSP\x02 synthetic legacy text",
		"retired": "NBP\x01 synthetic legacy text",
	}
	at := int64(1000)
	for id, text := range texts {
		_, err = kept.db.ExecContext(ctx, `INSERT INTO encrypted_message (id, thread, sender, body, at) VALUES (?, 7, 555, ?, ?)`, id, text, at)
		backupMust(t, err)
		at++
	}
	blob := retiredCell(t)
	backupMust(t, kept.rememberChecked(ctx, 7, "sealed", 555, "placeholder", "", at))
	_, err = kept.db.ExecContext(ctx, `UPDATE encrypted_message SET body = ? WHERE id = 'sealed'`, blob)
	backupMust(t, err)
	var storage string
	backupMust(t, kept.db.QueryRowContext(ctx, `SELECT typeof(body) FROM encrypted_message WHERE id = 'windows'`).Scan(&storage))
	if storage != "text" {
		t.Fatalf("the legacy body was stored as %s", storage)
	}
	backupMust(t, kept.db.QueryRowContext(ctx, `SELECT typeof(body) FROM encrypted_message WHERE id = 'sealed'`).Scan(&storage))
	if storage != "blob" {
		t.Fatalf("a seal was stored as %s", storage)
	}
	backupMust(t, kept.db.Close())

	kept, err = openMessageStore(ctx, path)
	backupMust(t, err)
	into := newStore()
	restored := kept.restore(ctx, into)
	backupMust(t, kept.db.Close())
	stored := storedBodies(t, path)
	for id, text := range texts {
		if !bytes.HasPrefix(stored[id], protectedPrefix) {
			t.Fatalf("the %s legacy text was not sealed", id)
		}
		if opened, err := unprotectString(stored[id]); err != nil || opened != text {
			t.Fatalf("the %s legacy text does not open to itself", id)
		}
		if into.messages[7][id] == nil || into.messages[7][id].text != text {
			t.Fatalf("the %s legacy text did not restore", id)
		}
	}
	if !bytes.Equal(stored["sealed"], blob) || into.messages[7]["sealed"] != nil || restored != len(texts) {
		t.Fatal("the sealed blob was rewritten or restored")
	}
}

func contactCells(t *testing.T, path string) (name, picture []byte) {
	t.Helper()
	db, err := sql.Open("sqlite", sqliteFile(path, "_pragma=busy_timeout(5000)"))
	backupMust(t, err)
	defer db.Close()
	backupMust(t, db.QueryRow(`SELECT name, picture FROM thread_contact WHERE id = 555`).Scan(&name, &picture))
	return name, picture
}

// A contact field that did not open, or was never loaded, is not overwritten by a save that only knows the other one.
func TestContactSaveKeepsAFieldItDidNotLoad(t *testing.T) {
	const name, picture = "Synthetic person", "https://example.test/synthetic-face.jpg"
	ctx := context.Background()
	seed := func(t *testing.T) (string, *messageStore) {
		path := filepath.Join(t.TempDir(), "messages.db")
		kept, err := openMessageStore(ctx, path)
		backupMust(t, err)
		s := newStore()
		s.contacts[555], s.pictures[555] = name, picture
		kept.saveIdentity(ctx, s)
		return path, kept
	}
	reopen := func(t *testing.T, path string) (*messageStore, *store) {
		kept, err := openMessageStore(ctx, path)
		backupMust(t, err)
		into := newStore()
		kept.restore(ctx, into)
		return kept, into
	}
	backupName := func(s *store, value string) {
		s.adoptBackupIdentity(&backupPageIn{Contacts: []backupContact{{ID: 555, Name: value}}}, &backupImportResult{}, &importChanges{})
	}

	t.Run("name did not open", func(t *testing.T) {
		path, kept := seed(t)
		_, err := kept.db.ExecContext(ctx, `UPDATE thread_contact SET name = ? WHERE id = 555`, retiredCell(t))
		backupMust(t, err)
		backupMust(t, kept.db.Close())
		kept, into := reopen(t, path)
		if into.pictures[555] != picture || into.contacts[555] != "" {
			t.Fatal("the picture that opened was not kept, or the name that did not was")
		}
		backupName(into, "Synthetic backup name")
		kept.saveIdentity(ctx, into)
		backupMust(t, kept.db.Close())
		sealedName, sealedPicture := contactCells(t, path)
		if opened, err := unprotectString(sealedPicture); err != nil || opened != picture {
			t.Fatal("the picture was lost")
		}
		if opened, err := unprotectString(sealedName); err != nil || opened != "Synthetic backup name" {
			t.Fatal("the backup's name was not saved")
		}
	})

	t.Run("picture did not open", func(t *testing.T) {
		path, kept := seed(t)
		unreadable := retiredCell(t)
		_, err := kept.db.ExecContext(ctx, `UPDATE thread_contact SET picture = ? WHERE id = 555`, unreadable)
		backupMust(t, err)
		backupMust(t, kept.db.Close())
		kept, into := reopen(t, path)
		if into.contacts[555] != name || into.pictures[555] != "" {
			t.Fatal("the name that opened was not kept, or the picture that did not was")
		}
		backupName(into, "Synthetic backup name")
		kept.saveIdentity(ctx, into)
		backupMust(t, kept.db.Close())
		_, sealedPicture := contactCells(t, path)
		if !bytes.Equal(sealedPicture, unreadable) {
			t.Fatal("a save that never loaded the picture wrote over it")
		}
	})

	t.Run("picture never loaded", func(t *testing.T) {
		path, kept := seed(t)
		fresh := newStore()
		fresh.contacts[555] = "Synthetic renamed person"
		kept.saveIdentity(ctx, fresh)
		backupMust(t, kept.db.Close())
		sealedName, sealedPicture := contactCells(t, path)
		if opened, err := unprotectString(sealedPicture); err != nil || opened != picture {
			t.Fatal("a name-only save wrote over the picture")
		}
		if opened, err := unprotectString(sealedName); err != nil || opened != "Synthetic renamed person" {
			t.Fatal("the new name was not saved")
		}
	})
}
