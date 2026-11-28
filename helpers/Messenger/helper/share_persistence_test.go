package main

import (
	"context"
	"database/sql"
	"path/filepath"
	"testing"
)

// Exercise the real database boundary, including the old schema: a share restored after restarting must still
// resolve its thumbnail through mediaSource and carry the browser destination through history's JSON view.
func TestOrdinaryShareLinkSurvivesDatabaseReopen(t *testing.T) {
	for _, legacy := range []bool{false, true} {
		name := "new database"
		if legacy {
			name = "existing database"
		}
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			path := filepath.Join(t.TempDir(), "messages.db")
			if legacy {
				db, err := sql.Open("sqlite", "file:"+path)
				if err != nil {
					t.Fatal(err)
				}
				_, err = db.ExecContext(ctx, `CREATE TABLE message_attachment (
					id TEXT PRIMARY KEY, message TEXT NOT NULL, thread INTEGER NOT NULL,
					name TEXT NOT NULL, mime TEXT NOT NULL, size INTEGER NOT NULL,
					width INTEGER NOT NULL, height INTEGER NOT NULL, video INTEGER NOT NULL,
					animated INTEGER NOT NULL, source TEXT NOT NULL, preview TEXT NOT NULL,
					playable TEXT NOT NULL, previewSource TEXT NOT NULL
				)`)
				if err != nil {
					_ = db.Close()
					t.Fatal(err)
				}
				_, err = db.ExecContext(ctx, `INSERT INTO message_attachment VALUES
					('photo', 'old-message', 7, 'Photo', 'image/jpeg', 0, 4, 3, 0, 0,
					'https://example.test/photo.jpg', '', '', '')`)
				closeErr := db.Close()
				if err != nil {
					t.Fatal(err)
				}
				if closeErr != nil {
					t.Fatal(closeErr)
				}
			}

			kept, err := openMessageStore(ctx, path)
			if err != nil {
				t.Fatal(err)
			}
			defer kept.db.Close()
			if err := kept.rememberChecked(ctx, 7, "share-message", 42, "", "", 1700); err != nil {
				t.Fatal(err)
			}
			item := &attachment{id: "share", name: "Shared reel", mime: "image/jpeg", width: 9, height: 16,
				source: "https://example.test/reel.jpg", link: "https://example.test/reel", actionTitle: "View listing"}
			kept.rememberAttachments(ctx, 7, "share-message", []*attachment{item}, nil)
			if err := kept.db.Close(); err != nil {
				t.Fatal(err)
			}

			kept, err = openMessageStore(ctx, path)
			if err != nil {
				t.Fatal(err)
			}
			defer kept.db.Close()
			back := newStore()
			kept.restore(ctx, back)
			page := back.history(7, 0)
			if len(page) != 1 || len(page[0].Media) != 1 {
				t.Fatal("the share-only message and its thumbnail did not survive reopening")
			}
			if got := page[0].Media[0]; got.Link != item.link || got.ActionTitle != item.actionTitle || got.Mime != item.mime || got.Video || got.Animated {
				t.Fatal("the restored share lost its browser destination or thumbnail type")
			}
			if source, mime, ok := back.mediaSource(item.id); !ok || source != item.source || mime != item.mime {
				t.Fatal("the restored thumbnail cannot be fetched through its media id")
			}
			if legacy {
				if source, _, ok := back.mediaSource("photo"); !ok || source != "https://example.test/photo.jpg" {
					t.Fatal("the link migration lost an existing photo")
				}
				var protectedLink []byte
				if err := kept.db.QueryRowContext(ctx, `SELECT link FROM message_attachment WHERE id = 'photo'`).Scan(&protectedLink); err != nil {
					t.Fatal("existing photos did not receive a protected empty destination")
				}
				if link, err := unprotectString(protectedLink); err != nil || link != "" {
					t.Fatal("existing photos did not receive an empty destination")
				}
			}

			// A refreshed share reuses its stable id; the updated destination must replace the stored one too.
			item.link = "https://example.test/reel-updated"
			kept.rememberAttachments(ctx, 7, "share-message", []*attachment{item}, nil)
			if err := kept.db.Close(); err != nil {
				t.Fatal(err)
			}
			kept, err = openMessageStore(ctx, path)
			if err != nil {
				t.Fatal(err)
			}
			defer kept.db.Close()
			back = newStore()
			kept.restore(ctx, back)
			page = back.history(7, 0)
			if len(page) != 1 || len(page[0].Media) != 1 || page[0].Media[0].Link != item.link {
				t.Fatal("the refreshed destination did not survive reopening")
			}
		})
	}
}
