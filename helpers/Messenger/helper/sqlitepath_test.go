package main

import (
	"context"
	"database/sql"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"go.mau.fi/whatsmeow/store/sqlstore"
	waLog "go.mau.fi/whatsmeow/util/log"
)

// A data folder whose name holds '#', '%' or '?' (a Windows user name may hold the first two) still opens the stores
// inside it. SQLite reads every address here as a URI, which ends a path at '?' or '#' and decodes '%', so an unescaped
// one opened or made a file beside the folder instead, or could not open at all.
func TestStoresOpenInsideAFolderNamedWithURICharacters(t *testing.T) {
	all := "a#b%41c"
	if runtime.GOOS != "windows" {
		// Windows allows no '?' in a file name.
		all = "a#b?c%41d"
	}
	// Subtests are named plainly: t.TempDir keeps '#' and '%' from a test's name in its own folder name.
	for _, c := range []struct{ label, folder string }{{"hash", "a#b"}, {"escape", "a%41b"}, {"all", all}} {
		t.Run(c.label, func(t *testing.T) {
			ctx := context.Background()
			parent := t.TempDir()
			// The message store, as login opens it.
			a, _ := backupAdapter(t, filepath.Join(parent, c.folder), backupViewer)
			folder := filepath.Dir(a.kept.path)

			// The device store, by the address e2ee.go opens it with, holding a message the way earlier builds kept them.
			e2ee := filepath.Join(folder, "e2ee.db")
			devices, err := sqlstore.New(ctx, "sqlite", sqliteFile(e2ee, "_pragma=foreign_keys(1)&_pragma=busy_timeout(5000)"), waLog.Noop)
			backupMust(t, err)
			backupMust(t, devices.Close())
			legacy, err := sql.Open("sqlite", sqliteFile(e2ee, ""))
			backupMust(t, err)
			_, err = legacy.ExecContext(ctx, `CREATE TABLE encrypted_message (thread INTEGER, id TEXT, sender INTEGER, body TEXT, at INTEGER);
				INSERT INTO encrypted_message VALUES (7, 'kept-in-e2ee', 555, 'Private kept body', 1000)`)
			backupMust(t, err)
			backupMust(t, legacy.Close())
			if moved := a.kept.absorb(ctx, e2ee, a.chats); moved != 1 {
				t.Fatalf("the message kept in e2ee.db was not moved: %d", moved)
			}

			// The export's snapshot, opened beside the store.
			carried := false
			for _, page := range exportAll(t, a) {
				carried = carried || strings.Contains(string(page), `"kept-in-e2ee"`)
			}
			if !carried {
				t.Fatal("the export did not carry the moved message")
			}

			if entries, err := os.ReadDir(parent); err != nil || len(entries) != 1 || entries[0].Name() != c.folder {
				t.Fatalf("a database was made beside the folder: %d %v", len(entries), err)
			}
		})
	}
	if sqliteFile(`C:\a#b%c`, "x=1") != `file:C:\a%23b%25c?x=1` || sqliteFile("/a?b", "") != "file:/a%3Fb" {
		t.Fatal("the URI is not escaped")
	}
}
