package main

import (
	"bytes"
	"context"
	"database/sql"
	"errors"
	"strconv"
	"strings"
	"time"
)

// What the encrypted connection delivers is delivered once. Facebook sends a conversation's waiting messages when the
// device connects and never again, and the ordinary connection will not repeat them, so anything not written down is
// gone at the next start — which is why those conversations looked empty again after a restart.
//
// This is a plain local database beside the device's own keys, which whatsmeow already keeps there unencrypted. It
// holds only what those conversations contain; nothing leaves this computer.
type messageStore struct {
	db *sql.DB
	// Where the store is, so a backup's snapshot can sit beside it in the same account folder.
	path string
	// What each identity row held last time it was written, as a digest of its plaintext. The protected form cannot
	// answer that question: protectLocal is CryptProtectData, which seals the same bytes differently every time, so
	// the "write only when it differs" guard in each of saveIdentity's statements is always true and every
	// conversation, contact and picture was re-sealed and rewritten on every apply. Guarded by the store lock
	// saveIdentity already holds, and only updated once the transaction has committed.
	savedThreads  map[int64][32]byte
	savedContacts map[int64][32]byte
}

func openAccountMessageStore(ctx context.Context, path string, viewer int64) (*messageStore, error) {
	store, err := openMessageStore(ctx, path)
	if err != nil {
		return nil, err
	}
	if _, err = store.db.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS store_owner (slot INTEGER PRIMARY KEY CHECK(slot=1), owner TEXT NOT NULL)`); err == nil {
		var owner string
		expected := messengerAccountKey(viewer)
		scan := store.db.QueryRowContext(ctx, `SELECT owner FROM store_owner WHERE slot=1`).Scan(&owner)
		switch {
		case scan == sql.ErrNoRows:
			_, err = store.db.ExecContext(ctx, `INSERT INTO store_owner(slot,owner) VALUES(1,?)`, expected)
		case scan != nil:
			err = scan
		case owner != expected:
			err = errors.New("message store belongs to another account")
		}
	}
	if err != nil {
		_ = store.db.Close()
		return nil, err
	}
	return store, nil
}

// Where one write goes: the store's own pool, or a transaction opened around a batch of them. Both satisfy it.
// Keeping a message is a single statement, and SQLite in its default journal mode fsyncs twice and creates and
// deletes a journal file for every statement that stands alone — measured at 5.3 ms each on this machine, which is
// where fifteen seconds of a cold start went for the couple of thousand rows the first page brings. The same writes
// inside one transaction cost 0.46 ms each, with no change to durability and no pragma touched.
type execer interface {
	ExecContext(ctx context.Context, query string, args ...any) (sql.Result, error)
}

// The wrappers each write on their own, for every caller that is not writing a batch. The encrypted connection writes
// to this same store without holding the lock applyTable holds, so a batch must be a value passed down and never a
// field here: a message arriving mid-batch would otherwise join a transaction that might roll back.
func (m *messageStore) rememberChecked(ctx context.Context, thread int64, id string, sender int64, body, replyTo string, at int64, channel ...bool) error {
	if m == nil {
		return errors.New("no store")
	}
	return m.rememberCheckedOn(ctx, m.db, thread, id, sender, body, replyTo, at, channel...)
}

func (m *messageStore) rememberAI(ctx context.Context, thread int64, id string, from *store) {
	if m == nil {
		return
	}
	m.rememberAIOn(ctx, m.db, thread, id, from)
}

func (m *messageStore) rememberOrdinaryAI(ctx context.Context, thread int64, id string, from *store) {
	if m == nil {
		return
	}
	m.rememberOrdinaryAIOn(ctx, m.db, thread, id, from)
}

func (m *messageStore) rememberAttachments(ctx context.Context, thread int64, messageID string, items []*attachment, previews map[string]string) {
	if m == nil {
		return
	}
	m.rememberAttachmentsOn(ctx, m.db, thread, messageID, items, previews)
}

// A database file as the SQLite URI every store here is opened by. SQLite ends a URI's path at '?' or '#' and decodes
// '%', so a folder name holding one of them (a Windows user name may) would quietly open or create another file.
func sqliteFile(path, params string) string {
	dsn := "file:" + sqlitePathEscaper.Replace(path)
	if params != "" {
		dsn += "?" + params
	}
	return dsn
}

var sqlitePathEscaper = strings.NewReplacer("%", "%25", "?", "%3F", "#", "%23")

func openMessageStore(ctx context.Context, path string) (*messageStore, error) {
	if path == "" {
		return nil, errors.New("message store path is unavailable")
	}
	db, err := sql.Open("sqlite", sqliteFile(path, "_pragma=busy_timeout(5000)"))
	if err != nil {
		return nil, err
	}
	if _, err := db.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS encrypted_message (
		id TEXT PRIMARY KEY,
		thread INTEGER NOT NULL,
		sender INTEGER NOT NULL,
		body TEXT NOT NULL,
		at INTEGER NOT NULL
	)`); err != nil {
		_ = db.Close()
		return nil, err
	}
	// Which message this one quoted, for builds whose table predates replies. Nothing to do when it is already there.
	_, _ = db.ExecContext(ctx, `ALTER TABLE encrypted_message ADD COLUMN reply TEXT NOT NULL DEFAULT ''`)
	var hasAIUpdatedAt int
	if err := db.QueryRowContext(ctx, `SELECT COUNT(*) FROM pragma_table_info('encrypted_message') WHERE name = 'ai_updated_at'`).Scan(&hasAIUpdatedAt); err != nil {
		_ = db.Close()
		return nil, err
	}
	if hasAIUpdatedAt == 0 {
		if _, err := db.ExecContext(ctx, `ALTER TABLE encrypted_message ADD COLUMN ai_updated_at INTEGER NOT NULL DEFAULT 0`); err != nil {
			_ = db.Close()
			return nil, err
		}
	}
	var hasEditedAt int
	if err := db.QueryRowContext(ctx, `SELECT COUNT(*) FROM pragma_table_info('encrypted_message') WHERE name = 'edited_at'`).Scan(&hasEditedAt); err != nil {
		_ = db.Close()
		return nil, err
	}
	if hasEditedAt == 0 {
		if _, err := db.ExecContext(ctx, `ALTER TABLE encrypted_message ADD COLUMN edited_at INTEGER NOT NULL DEFAULT 0`); err != nil {
			_ = db.Close()
			return nil, err
		}
	}
	var hasCardText int
	if err := db.QueryRowContext(ctx, `SELECT COUNT(*) FROM pragma_table_info('encrypted_message') WHERE name = 'card_text'`).Scan(&hasCardText); err != nil {
		_ = db.Close()
		return nil, err
	}
	if hasCardText == 0 {
		if _, err := db.ExecContext(ctx, `ALTER TABLE encrypted_message ADD COLUMN card_text TEXT NOT NULL DEFAULT ''`); err != nil {
			_ = db.Close()
			return nil, err
		}
	}
	// The same archive also holds ordinary history. Its IDs cannot be sent as encrypted stanza IDs.
	var hasChannel int
	if err := db.QueryRowContext(ctx, `SELECT COUNT(*) FROM pragma_table_info('encrypted_message') WHERE name = 'encrypted'`).Scan(&hasChannel); err != nil {
		_ = db.Close()
		return nil, err
	}
	if hasChannel == 0 {
		// Old rows lost provenance. Our encrypted IDs are decimal epoch IDs; unknown legacy formats remain
		// archived but are not acknowledged on the encrypted connection until received with known provenance.
		tx, err := db.BeginTx(ctx, nil)
		if err == nil {
			_, err = tx.ExecContext(ctx, `ALTER TABLE encrypted_message ADD COLUMN encrypted INTEGER NOT NULL DEFAULT 0;
				UPDATE encrypted_message SET encrypted = 1 WHERE id != '' AND id NOT GLOB '*[^0-9]*'`)
			if err == nil {
				err = tx.Commit()
			}
			_ = tx.Rollback()
		}
		if err != nil {
			_ = db.Close()
			return nil, err
		}
	}
	// The files those messages carry. Not the file itself — the directions to it, which is all that arrives and all
	// that is needed to fetch it again later.
	if _, err := db.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS encrypted_media (
		id TEXT PRIMARY KEY,
		message TEXT NOT NULL,
		thread INTEGER NOT NULL,
		name TEXT NOT NULL,
		mime TEXT NOT NULL,
		size INTEGER NOT NULL,
		width INTEGER NOT NULL,
		height INTEGER NOT NULL,
		video INTEGER NOT NULL,
		animated INTEGER NOT NULL,
		kind TEXT NOT NULL,
		transport BLOB NOT NULL,
		at INTEGER NOT NULL,
		link TEXT NOT NULL DEFAULT ''
	)`); err != nil {
		_ = db.Close()
		return nil, err
	}
	// Where a shared reel or post leads, for stores made before they were carried. Nothing to do when it is there.
	_, _ = db.ExecContext(ctx, `ALTER TABLE encrypted_media ADD COLUMN link TEXT NOT NULL DEFAULT ''`)
	// Keep the share's received schema too. An encrypted message is delivered only once, and retaining only the
	// fields this build understands would make a later decoder fix unable to recover its thumbnail or destination.
	if _, err := db.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS encrypted_share (
		message TEXT PRIMARY KEY,
		payload BLOB NOT NULL
	)`); err != nil {
		_ = db.Close()
		return nil, err
	}
	// The attachments on ordinary messages. Keeping a message's text but not its pictures was the fault behind a day
	// of missing photos: a photo with no caption has neither, so it was filtered out of its conversation as an empty
	// row and disappeared. The url is kept with it, but urls expire — so it is a starting point, not a guarantee, and
	// a fetch that fails on one sends the conversation back to Facebook for a fresh one.
	if _, err := db.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS message_attachment (
		id TEXT PRIMARY KEY,
		message TEXT NOT NULL,
		thread INTEGER NOT NULL,
		name TEXT NOT NULL,
		mime TEXT NOT NULL,
		size INTEGER NOT NULL,
		width INTEGER NOT NULL,
		height INTEGER NOT NULL,
		video INTEGER NOT NULL,
		animated INTEGER NOT NULL,
		source TEXT NOT NULL,
		preview TEXT NOT NULL,
		playable TEXT NOT NULL,
		previewSource TEXT NOT NULL,
		link TEXT NOT NULL DEFAULT '',
		description TEXT NOT NULL DEFAULT '',
		actionTitle TEXT NOT NULL DEFAULT ''
	)`); err != nil {
		_ = db.Close()
		return nil, err
	}
	// Existing ordinary attachments predate shared links. Check before adding the column so a real migration
	// failure is returned, rather than silently turning every attachment read/write into a failed query.
	var hasAttachmentLink int
	if err := db.QueryRowContext(ctx, `SELECT COUNT(*) FROM pragma_table_info('message_attachment') WHERE name = 'link'`).Scan(&hasAttachmentLink); err != nil {
		_ = db.Close()
		return nil, err
	}
	if hasAttachmentLink == 0 {
		if _, err := db.ExecContext(ctx, `ALTER TABLE message_attachment ADD COLUMN link TEXT NOT NULL DEFAULT ''`); err != nil {
			_ = db.Close()
			return nil, err
		}
	}
	// And the linked page's description, for stores made before previews carried one.
	var hasDescription int
	if err := db.QueryRowContext(ctx, `SELECT COUNT(*) FROM pragma_table_info('message_attachment') WHERE name = 'description'`).Scan(&hasDescription); err != nil {
		_ = db.Close()
		return nil, err
	}
	if hasDescription == 0 {
		if _, err := db.ExecContext(ctx, `ALTER TABLE message_attachment ADD COLUMN description TEXT NOT NULL DEFAULT ''`); err != nil {
			_ = db.Close()
			return nil, err
		}
	}
	// And the words on a shared card's button, for stores made before cards carried them.
	var hasActionTitle int
	if err := db.QueryRowContext(ctx, `SELECT COUNT(*) FROM pragma_table_info('message_attachment') WHERE name = 'actionTitle'`).Scan(&hasActionTitle); err != nil {
		_ = db.Close()
		return nil, err
	}
	if hasActionTitle == 0 {
		if _, err := db.ExecContext(ctx, `ALTER TABLE message_attachment ADD COLUMN actionTitle TEXT NOT NULL DEFAULT ''`); err != nil {
			_ = db.Close()
			return nil, err
		}
	}
	// Reactions, which travel on the encrypted connection as messages of their own. Facebook delivers those once, so
	// a reaction not written down is gone at the next start even though its message is still there.
	if _, err := db.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS encrypted_reaction (
		message TEXT NOT NULL,
		actor INTEGER NOT NULL,
		thread INTEGER NOT NULL,
		emoji TEXT NOT NULL,
		PRIMARY KEY (message, actor)
	)`); err != nil {
		_ = db.Close()
		return nil, err
	}
	// Messages taken back, on this account's devices or by whoever sent them. Without this the stored copy puts them
	// back at the next start and a message deleted days ago reappears.
	if _, err := db.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS deleted_message (
		id TEXT PRIMARY KEY,
		thread INTEGER NOT NULL,
		at INTEGER NOT NULL
	)`); err != nil {
		_ = db.Close()
		return nil, err
	}
	for _, statement := range []string{
		`CREATE TABLE IF NOT EXISTS thread_identity (thread INTEGER PRIMARY KEY, identity BLOB NOT NULL)`,
		`CREATE TABLE IF NOT EXISTS thread_mapping (jid INTEGER PRIMARY KEY, thread INTEGER NOT NULL)`,
		`CREATE TABLE IF NOT EXISTS thread_contact (id INTEGER PRIMARY KEY, name TEXT NOT NULL, picture TEXT NOT NULL)`,
		receiptWatermarkTable, receiptWaitingTable,
	} {
		if _, err := db.ExecContext(ctx, statement); err != nil {
			_ = db.Close()
			return nil, err
		}
	}
	store := &messageStore{db: db, path: path}
	if err := store.protectExisting(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	return store, nil
}

// Rewrite every legacy sensitive cell in one transaction. IDs and numeric relationship keys remain queryable, while
// message text, contact data, URLs, attachment directions and received schemas are opaque outside this Windows user.
func (m *messageStore) protectExisting(ctx context.Context) (err error) {
	specs := map[string][]string{
		"encrypted_message":  {"body", "reply", "card_text"},
		"encrypted_media":    {"name", "mime", "kind", "transport", "link"},
		"encrypted_share":    {"payload"},
		"message_attachment": {"name", "mime", "source", "preview", "playable", "previewSource", "link", "description", "actionTitle"},
		"encrypted_reaction": {"emoji"},
		"thread_identity":    {"identity"},
		"thread_contact":     {"name", "picture"},
	}
	// Read back with unprotectLocal, which refuses an empty cell, so an empty one there is sealed as before. Every other
	// column is read with unprotectString, which answers "" for an empty cell without DPAPI, so an empty cell holds
	// nothing to protect. Sealing those is what made nearly every start VACUUM the whole store: each message kept last
	// session had an empty card_text, which carries no prefix and so looked like legacy plaintext.
	sealsEmpty := map[string]bool{"encrypted_media.transport": true, "encrypted_share.payload": true, "thread_identity.identity": true}
	started := time.Now()
	sealedCells, emptyCells := 0, 0
	tx, err := m.db.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	migrated := false
	defer func() {
		if err == nil {
			logf("kept store protect sealed=%d empty=%d vacuum=%v ms=%d", sealedCells, emptyCells, migrated, time.Since(started).Milliseconds())
		}
	}()
	for table, columns := range specs {
		for _, column := range columns {
			rows, err := tx.QueryContext(ctx, `SELECT rowid, `+column+` FROM `+table)
			if err != nil {
				return err
			}
			type value struct {
				row  int64
				data []byte
			}
			var pending []value
			for rows.Next() {
				var item value
				if err = rows.Scan(&item.row, &item.data); err != nil {
					rows.Close()
					return err
				}
				switch {
				case bytes.HasPrefix(item.data, protectedPrefix):
				case len(item.data) == 0 && !sealsEmpty[table+"."+column]:
					emptyCells++
				default:
					pending = append(pending, item)
				}
			}
			if err = rows.Err(); err != nil {
				rows.Close()
				return err
			}
			rows.Close()
			for _, item := range pending {
				sealed, e := protectLocal(item.data)
				if e != nil {
					return e
				}
				if _, e = tx.ExecContext(ctx, `UPDATE `+table+` SET `+column+`=? WHERE rowid=?`, sealed, item.row); e != nil {
					return e
				}
				sealedCells++
				migrated = true
			}
		}
	}
	if err = tx.Commit(); err != nil {
		return err
	}
	if !migrated {
		return nil
	}
	// UPDATE alone can leave old plaintext in SQLite free pages or a journal. Rebuild and truncate before the store is
	// accepted so a copied database cannot recover the pre-migration values.
	if _, err = m.db.ExecContext(ctx, `PRAGMA wal_checkpoint(TRUNCATE)`); err != nil {
		return err
	}
	_, err = m.db.ExecContext(ctx, `VACUUM`)
	return err
}

// The attachments a message carries, written down beside it. Nothing is downloaded here: what is kept is what the row
// said, so the message still draws as a picture after a restart instead of vanishing.
func (m *messageStore) rememberAttachmentsOn(ctx context.Context, on execer, thread int64, messageID string, items []*attachment, previews map[string]string) {
	if m == nil || len(items) == 0 {
		return
	}
	for _, item := range items {
		sealed, err := protectStrings(item.name, item.mime, item.source, item.preview, item.playable, previews[item.preview], item.link, item.description, item.actionTitle)
		if err != nil {
			logf("an attachment could not be protected")
			return
		}
		_, err = on.ExecContext(ctx,
			`INSERT INTO message_attachment (id, message, thread, name, mime, size, width, height, video, animated, source, preview, playable, previewSource, link, description, actionTitle)
			 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
			 ON CONFLICT(id) DO UPDATE SET source = excluded.source, preview = excluded.preview,
			   playable = excluded.playable, previewSource = excluded.previewSource, link = excluded.link, description = excluded.description, actionTitle = excluded.actionTitle`,
			item.id, messageID, thread, sealed[0], sealed[1], item.size, item.width, item.height,
			item.video, item.animated, sealed[2], sealed[3], sealed[4], sealed[5], sealed[6], sealed[7], sealed[8])
		if err != nil {
			logf("an attachment could not be kept")
			return
		}
	}
}

func (m *messageStore) restoreAttachments(ctx context.Context, into *store) {
	rows, err := m.db.QueryContext(ctx,
		`SELECT id, message, name, mime, size, width, height, video, animated, source, preview, playable, previewSource, link, description, actionTitle FROM message_attachment`)
	if err != nil {
		return
	}
	defer rows.Close()
	for rows.Next() {
		var item attachment
		var messageID string
		var values [9][]byte
		if rows.Scan(&item.id, &messageID, &values[0], &values[1], &item.size, &item.width, &item.height,
			&item.video, &item.animated, &values[2], &values[3], &values[4], &values[5], &values[6], &values[7], &values[8]) != nil {
			continue
		}
		plain := make([]string, len(values))
		valid := true
		for index := range values {
			plain[index], err = unprotectString(values[index])
			if err != nil {
				valid = false
				break
			}
		}
		if !valid {
			continue
		}
		item.name, item.mime, item.source, item.preview, item.playable, item.link, item.description, item.actionTitle = plain[0], plain[1], plain[2], plain[3], plain[4], plain[6], plain[7], plain[8]
		copied := item
		into.restoreAttachment(messageID, &copied, plain[5])
	}
}

// A message taken back stays taken back. The stored copy is removed and the deletion itself recorded, so restoring
// cannot resurrect it.
func (m *messageStore) forget(ctx context.Context, thread int64, id string) {
	if m == nil || id == "" {
		return
	}
	if _, err := m.db.ExecContext(ctx, `DELETE FROM encrypted_message WHERE id = ?`, id); err != nil {
		logf("a deleted message could not be removed from the store")
		return
	}
	_, _ = m.db.ExecContext(ctx, `DELETE FROM encrypted_media WHERE message = ?`, id)
	_, _ = m.db.ExecContext(ctx, `DELETE FROM encrypted_share WHERE message = ?`, id)
	_, _ = m.db.ExecContext(ctx, `DELETE FROM encrypted_reaction WHERE message = ?`, id)
	_, _ = m.db.ExecContext(ctx,
		`INSERT INTO deleted_message (id, thread, at) VALUES (?, ?, ?) ON CONFLICT(id) DO NOTHING`,
		id, thread, time.Now().UnixMilli())
}

// One person's reaction to one message, kept. An empty emoji is the reaction being taken away.
func (m *messageStore) rememberReaction(ctx context.Context, thread int64, messageID string, actor int64, emoji string) {
	if m == nil || messageID == "" || actor == 0 {
		return
	}
	var err error
	if emoji == "" {
		_, err = m.db.ExecContext(ctx, `DELETE FROM encrypted_reaction WHERE message = ? AND actor = ?`, messageID, actor)
	} else {
		sealed, protectErr := protectString(emoji)
		if protectErr != nil {
			logf("a reaction could not be protected")
			return
		}
		_, err = m.db.ExecContext(ctx,
			`INSERT INTO encrypted_reaction (message, actor, thread, emoji) VALUES (?, ?, ?, ?)
			 ON CONFLICT(message, actor) DO UPDATE SET emoji = excluded.emoji`,
			messageID, actor, thread, sealed)
	}
	if err != nil {
		logf("a reaction could not be kept")
	}
}

func (m *messageStore) rememberMedia(ctx context.Context, thread int64, messageID string, at int64, item *attachment, held *secureMedia) {
	if m == nil || item == nil || held == nil {
		return
	}
	sealed, protectErr := protectStrings(item.name, item.mime, held.kind, item.link)
	transport, transportErr := protectLocal(held.transport)
	if protectErr != nil || transportErr != nil {
		logf("e2ee attachment could not be protected")
		return
	}
	_, err := m.db.ExecContext(ctx,
		`INSERT INTO encrypted_media (id, message, thread, name, mime, size, width, height, video, animated, kind, transport, at, link)
		 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
		 ON CONFLICT(id) DO UPDATE SET transport = excluded.transport, kind = excluded.kind, link = excluded.link`,
		item.id, messageID, thread, sealed[0], sealed[1], item.size, item.width, item.height,
		item.video, item.animated, sealed[2], transport, at, sealed[3])
	if err != nil {
		logf("e2ee attachment could not be kept")
	}
}

func (m *messageStore) remember(ctx context.Context, thread int64, id string, sender int64, body, replyTo string, at int64, channel ...bool) {
	if err := m.rememberChecked(ctx, thread, id, sender, body, replyTo, at, channel...); err != nil {
		logf("e2ee message could not be kept")
	}
}

// Persist the accepted row, including its update watermark, rather than the incoming partial envelope.
func (m *messageStore) rememberAIOn(ctx context.Context, on execer, thread int64, id string, from *store) {
	from.mu.Lock()
	current := from.messages[thread][id]
	if current == nil {
		from.mu.Unlock()
		return
	}
	row := *current
	from.mu.Unlock()
	sealed, protectErr := protectStrings(row.text, row.replyTo)
	if protectErr != nil {
		logf("meta ai accepted message could not be protected")
		return
	}
	_, err := on.ExecContext(ctx, `INSERT INTO encrypted_message (id, thread, sender, body, reply, at, encrypted, ai_updated_at, edited_at)
		SELECT ?, ?, ?, ?, ?, ?, ?, ?, ? WHERE NOT EXISTS (SELECT 1 FROM deleted_message WHERE id = ?)
		ON CONFLICT(id) DO UPDATE SET thread = excluded.thread, body = excluded.body,
		 reply = excluded.reply, encrypted = excluded.encrypted, ai_updated_at = excluded.ai_updated_at, edited_at = excluded.edited_at
		WHERE excluded.ai_updated_at >= encrypted_message.ai_updated_at AND excluded.edited_at >= encrypted_message.edited_at`,
		id, thread, row.sender, sealed[0], sealed[1], row.timestamp, row.encrypted, row.aiUpdatedAt, row.editedAt, id)
	if err != nil {
		logf("meta ai accepted message could not be kept")
	}
}

// Save provider body and its separate card projection in one statement.
func (m *messageStore) rememberOrdinaryAIOn(ctx context.Context, on execer, thread int64, id string, from *store) {
	from.mu.Lock()
	current := from.messages[thread][id]
	if current == nil || current.sender != metaAIMessengerID || current.sender == from.self || current.encrypted || current.unsent {
		from.mu.Unlock()
		return
	}
	row := *current
	from.mu.Unlock()
	sealed, protectErr := protectStrings(row.text, row.replyTo, row.cardText)
	if protectErr != nil {
		logf("meta ai card text could not be protected")
		return
	}
	if _, err := on.ExecContext(ctx, `INSERT INTO encrypted_message (id, thread, sender, body, reply, at, encrypted, card_text)
		SELECT ?, ?, ?, ?, ?, ?, 0, ? WHERE NOT EXISTS (SELECT 1 FROM deleted_message WHERE id = ?)
		ON CONFLICT(id) DO UPDATE SET thread = excluded.thread, body = excluded.body,
		 reply = excluded.reply, at = excluded.at, card_text = excluded.card_text
		WHERE encrypted_message.encrypted = 0`, id, thread, row.sender, sealed[0], sealed[1], row.timestamp, sealed[2], id); err != nil {
		logf("meta ai card text could not be kept")
	}
}

// Corrected words, against the copy already written down. Without this the message would come back saying what it
// said before the edit at the next start, which is the same fault the deletion record exists to prevent.
func (m *messageStore) rememberEdit(ctx context.Context, id, text string, at int64) {
	if m == nil || id == "" {
		return
	}
	sealed, protectErr := protectString(text)
	if protectErr != nil {
		logf("an edit could not be protected")
		return
	}
	if _, err := m.db.ExecContext(ctx, `UPDATE encrypted_message SET body = ?, edited_at = ? WHERE id = ?
		AND (ai_updated_at = 0 OR (? >= ai_updated_at AND ? >= edited_at))`, sealed, at, id, at, at); err != nil {
		logf("an edit could not be kept")
	}
}

// The same, for callers that cannot treat a failed write as nothing happening — a migration that deletes its source
// has to know whether the copy arrived.
func (m *messageStore) rememberCheckedOn(ctx context.Context, on execer, thread int64, id string, sender int64, body, replyTo string, at int64, channel ...bool) error {
	if m == nil {
		return errors.New("no store")
	}
	encrypted := len(channel) == 0 || channel[0]
	sealed, err := protectStrings(body, replyTo)
	if err != nil {
		return err
	}
	_, err = on.ExecContext(ctx,
		`INSERT INTO encrypted_message (id, thread, sender, body, reply, at, encrypted) VALUES (?, ?, ?, ?, ?, ?, ?)
	 ON CONFLICT(id) DO UPDATE SET thread = excluded.thread, body = excluded.body, reply = excluded.reply, at = excluded.at, encrypted = excluded.encrypted`,
		id, thread, sender, sealed[0], sealed[1], at, encrypted)
	return err
}

// Everything kept, back into the conversations it belongs to.
func (m *messageStore) restore(ctx context.Context, into *store) int {
	if m == nil {
		return 0
	}
	// How many cells this restore unsealed, and how many held nothing. Sealing empty optional fields is a choice
	// about what a copied store gives away, so it is measured before anyone changes it. Anything else unsealing at
	// the same moment is counted too; at login nothing else is.
	cells, empty, started := unsealedCells.Load(), unsealedEmpty.Load(), time.Now()
	defer func() {
		logf("kept restore sealedEmpty=%d of %d cells in %dms", unsealedEmpty.Load()-empty, unsealedCells.Load()-cells,
			time.Since(started).Milliseconds())
	}()
	m.restoreIdentity(ctx, into)
	rows, err := m.db.QueryContext(ctx, `SELECT thread, id, sender, body, reply, at, encrypted, ai_updated_at, edited_at, card_text FROM encrypted_message ORDER BY at`)
	if err != nil {
		logf("e2ee kept messages could not be read")
		return 0
	}
	defer rows.Close()
	restored := 0
	for rows.Next() {
		var thread, sender, at, aiUpdatedAt, editedAt int64
		var id string
		var protectedBody, protectedReply, protectedCard []byte
		var encrypted bool
		if rows.Scan(&thread, &id, &sender, &protectedBody, &protectedReply, &at, &encrypted, &aiUpdatedAt, &editedAt, &protectedCard) != nil {
			continue
		}
		body, bodyErr := unprotectString(protectedBody)
		replyTo, replyErr := unprotectString(protectedReply)
		cardText, cardErr := unprotectString(protectedCard)
		if bodyErr != nil || replyErr != nil || cardErr != nil {
			continue
		}
		into.addEncrypted(thread, id, sender, body, replyTo, at, encrypted)
		if aiUpdatedAt > 0 || editedAt > 0 || cardText != "" {
			into.mu.Lock()
			if current := into.messages[thread][id]; current != nil {
				current.aiUpdatedAt = aiUpdatedAt
				current.editedAt = editedAt
				current.cardText = cardText
			}
			into.mu.Unlock()
		}
		into.markFromStore(thread)
		restored++
	}
	rows.Close()
	m.restoreMedia(ctx, into)
	m.restoreAttachments(ctx, into)
	m.restoreShares(ctx, into)
	m.restoreReactions(ctx, into)
	// Anything taken back since is taken back again: a stored copy from before the deletion must not put it back.
	m.applyDeletions(ctx, into)
	return restored
}

func (m *messageStore) restoreReactions(ctx context.Context, into *store) {
	rows, err := m.db.QueryContext(ctx, `SELECT message, actor, emoji FROM encrypted_reaction`)
	if err != nil {
		return
	}
	defer rows.Close()
	for rows.Next() {
		var messageID string
		var protectedEmoji []byte
		var actor int64
		if rows.Scan(&messageID, &actor, &protectedEmoji) != nil {
			continue
		}
		emoji, err := unprotectString(protectedEmoji)
		if err != nil {
			continue
		}
		into.restoreReaction(messageID, actor, emoji)
	}
}

func (m *messageStore) applyDeletions(ctx context.Context, into *store) {
	rows, err := m.db.QueryContext(ctx, `SELECT id, thread FROM deleted_message`)
	if err != nil {
		return
	}
	defer rows.Close()
	for rows.Next() {
		var id string
		var thread int64
		if rows.Scan(&id, &thread) != nil {
			continue
		}
		into.forget(thread, id)
	}
}

// The files those messages carried, hung back on them. The messages go back first: an attachment belongs to one.
func (m *messageStore) restoreMedia(ctx context.Context, into *store) {
	rows, err := m.db.QueryContext(ctx,
		`SELECT id, message, thread, name, mime, size, width, height, video, animated, kind, transport, at, link FROM encrypted_media`)
	if err != nil {
		return
	}
	defer rows.Close()
	for rows.Next() {
		var item attachment
		var held secureMedia
		var messageID string
		var protectedName, protectedMime, protectedKind, protectedTransport, protectedLink []byte
		var thread, at int64
		if rows.Scan(&item.id, &messageID, &thread, &protectedName, &protectedMime, &item.size, &item.width, &item.height,
			&item.video, &item.animated, &protectedKind, &protectedTransport, &at, &protectedLink) != nil {
			continue
		}
		var err error
		if item.name, err = unprotectString(protectedName); err != nil {
			continue
		}
		if item.mime, err = unprotectString(protectedMime); err != nil {
			continue
		}
		if held.kind, err = unprotectString(protectedKind); err != nil {
			continue
		}
		if held.transport, err = unprotectLocal(protectedTransport); err != nil {
			continue
		}
		if item.link, err = unprotectString(protectedLink); err != nil {
			continue
		}
		copied := item
		into.attachSecure(thread, messageID, at, &copied, &held)
	}
}

// What keeping a batch of rows cost, split far enough that no part of it has to be guessed at.
type keepCost struct {
	identity, deletions, messages time.Duration
	identityWrites, ids           int
}

// Everything a batch of rows brought, written down. Facebook stops serving a conversation's history the moment it
// moves that conversation to end-to-end encryption, so what has already been fetched is worth keeping: it is the only
// copy that survives the change.
func (a *adapter) keepMessages(ctx context.Context, touched *changes) (cost keepCost) {
	a.mu.Lock()
	kept := a.kept
	a.mu.Unlock()
	if kept == nil {
		return
	}
	identity := time.Now()
	cost.identityWrites = kept.saveIdentity(ctx, a.chats)
	cost.identity = time.Since(identity)
	deleting := time.Now()
	// A deletion is as much a fact about the conversation as a message is, and the one that has to outlast a restart:
	// the stored copy is older than the deletion and would put the message back.
	for chat, ids := range touched.Deleted {
		key, err := parseKey(chat)
		if err != nil {
			continue
		}
		for _, id := range ids {
			kept.forget(ctx, key, id)
		}
	}
	cost.deletions = time.Since(deleting)
	if len(touched.Messages) == 0 {
		return
	}
	writing := time.Now()
	defer func() { cost.messages = time.Since(writing) }()
	// One transaction for the whole batch. Written one at a time these cost 5.3 ms each, nearly all of it SQLite
	// creating, syncing and deleting a journal per statement; together they cost 0.46 ms each, with the same
	// durability and no pragma changed. The first page of a cold start brings a couple of thousand of them.
	tx, err := kept.db.BeginTx(ctx, nil)
	if err != nil {
		logf("kept messages could not be written")
		return
	}
	committed := false
	defer func() {
		if !committed {
			_ = tx.Rollback()
		}
	}()
	for chat, ids := range touched.Messages {
		cost.ids += len(ids)
		key, err := strconv.ParseInt(chat, 10, 64)
		if err != nil {
			continue
		}
		for _, id := range ids {
			if sender, body, replyTo, at, ok := a.chats.rawMessage(key, id); ok {
				if sender == metaAIMessengerID && a.chats.messageEncrypted(key, id) {
					kept.rememberAIOn(ctx, tx, key, id, a.chats)
				} else if sender == metaAIMessengerID && sender != a.chats.viewer() {
					kept.rememberOrdinaryAIOn(ctx, tx, key, id, a.chats)
				} else if e := kept.rememberCheckedOn(ctx, tx, key, id, sender, body, replyTo, at, a.chats.messageEncrypted(key, id)); e != nil {
					logf("e2ee message could not be kept")
				}
			}
			// The pictures too, not only the words. A message whose attachment is not kept comes back as a row with
			// nothing in it and is dropped from its conversation altogether.
			if items, previews := a.chats.attachmentsOf(id); len(items) > 0 {
				kept.rememberAttachmentsOn(ctx, tx, key, id, items, previews)
			}
		}
	}
	if tx.Commit() != nil {
		logf("kept messages could not be written")
		return
	}
	committed = true
	return
}

// A conversation id as Facebook's own number.
func parseKey(id string) (int64, error) {
	return strconv.ParseInt(id, 10, 64)
}

// Conversations already walked to the end, so a later run does not walk them again. Only the fact is kept: a number
// and a time.
func (m *messageStore) markComplete(ctx context.Context, thread int64) {
	if m == nil {
		return
	}
	_, _ = m.db.ExecContext(ctx,
		`CREATE TABLE IF NOT EXISTS archived_v3 (thread INTEGER PRIMARY KEY, at INTEGER NOT NULL)`)
	_, _ = m.db.ExecContext(ctx,
		`INSERT INTO archived_v3 (thread, at) VALUES (?, ?) ON CONFLICT(thread) DO UPDATE SET at = excluded.at`,
		thread, time.Now().Unix())
}

func (m *messageStore) complete(ctx context.Context, thread int64) bool {
	if m == nil {
		return false
	}
	var at int64
	row := m.db.QueryRowContext(ctx, `SELECT at FROM archived_v3 WHERE thread = ?`, thread)
	return row.Scan(&at) == nil && at > 0
}

// Messages kept beside the device's own keys by earlier builds, moved into the main store and back into their
// conversations. Two places to write them down was one too many: e2ee.db is the file to delete when a device has to be
// registered again, and what these conversations deliver is delivered once — losing it that way would be permanent.
func (m *messageStore) absorb(ctx context.Context, path string, into *store) int {
	if m == nil || path == "" {
		return 0
	}
	old, err := sql.Open("sqlite", sqliteFile(path, "_pragma=busy_timeout(5000)"))
	if err != nil {
		return 0
	}
	defer old.Close()
	rows, err := old.QueryContext(ctx, `SELECT thread, id, sender, body, at FROM encrypted_message ORDER BY at`)
	if err != nil {
		// No such table: no build ever kept anything there.
		return 0
	}
	type held struct {
		thread, sender, at int64
		id, body           string
	}
	var all []held
	for rows.Next() {
		var h held
		if rows.Scan(&h.thread, &h.id, &h.sender, &h.body, &h.at) != nil {
			// A row that cannot be read is a row that cannot be copied, and the source is the only place it exists.
			rows.Close()
			logf("e2ee migration stopped: a kept message could not be read")
			return 0
		}
		all = append(all, h)
	}
	failed := rows.Err() != nil
	rows.Close()
	if failed {
		logf("e2ee migration stopped: the kept messages could not be read through")
		return 0
	}
	// Every message has to arrive in the new store before the old one is given up. These are delivered once by
	// Facebook and never again, so a copy that failed and a source that was dropped anyway would lose them for good.
	for _, h := range all {
		if err := m.rememberChecked(ctx, h.thread, h.id, h.sender, h.body, "", h.at); err != nil {
			logf("e2ee migration stopped: a kept message could not be written, the old copy is left alone")
			return 0
		}
		into.addEncrypted(h.thread, h.id, h.sender, h.body, "", h.at)
	}
	if len(all) > 0 {
		if _, err := old.ExecContext(ctx, `DROP TABLE encrypted_message`); err != nil {
			// Harmless: the copy is safely in the new store, and the next start finds nothing left to move.
			logf("e2ee migration kept the old table, which is now a duplicate")
		} else {
			// DROP leaves the one-delivery plaintext in free pages. Rebuild the device database after the protected copy
			// is durable so the legacy body cannot be recovered from its former SQLite allocation.
			_, _ = old.ExecContext(ctx, `PRAGMA wal_checkpoint(TRUNCATE)`)
			if _, err := old.ExecContext(ctx, `VACUUM`); err != nil {
				logf("e2ee migration could not purge the old free pages")
			}
		}
	}
	return len(all)
}

// One reaction, written down alongside the message it belongs to. Reactions on the encrypted connection arrive once
// and are never replayed as history, so the only copy is the one kept here.
func (a *adapter) remember(ctx context.Context, key int64, messageID string, actor int64, emoji string) {
	a.mu.Lock()
	kept := a.kept
	a.mu.Unlock()
	kept.rememberReaction(ctx, key, messageID, actor, emoji)
}
