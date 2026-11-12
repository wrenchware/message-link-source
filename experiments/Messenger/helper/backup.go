package main

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"slices"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	"go.mau.fi/whatsmeow/proto/waArmadilloXMA"
	"google.golang.org/protobuf/proto"
)

// A backup of the Messenger history that exists nowhere else: encrypted conversations, and ordinary ones Facebook
// stopped serving once it encrypted them. It lives only in messages.db, sealed to this Windows user, so the helper is
// the one place that can read it out as plain data and seal it back in.
//
// Protocol, format "nativebeeper.messenger.history" version 1. Both directions need a signed-in helper (login done;
// connect is not needed) and neither makes a network request. An offline helper (NATIVEBEEPER_MESSENGER_OFFLINE=1,
// which can do nothing else) takes backup.open in place of a login; see backupOpen.
//
//	backup.export    {"cursor": ""}        start an export and answer its first page
//	backup.export    {"cursor": "<next>"}  answer the page after the one whose "next" this was
//	backup.exportEnd {}                    abandon the export in progress; harmless when there is none
//	backup.import    <page>                write one exported page back, for the same account only
//
// One export at a time: starting one abandons the last. Its cursor must be the latest "next" handed out, or the one
// before it, which answers the same page again for a caller whose answer went missing; after the last page, or five
// minutes without a request, the export is gone. It reads a snapshot taken when it started, so pages are consistent
// with each other and nothing the account receives meanwhile is waiting on it.
//
// Errors: login_required, backup_unavailable (no message store), invalid_input, backup_expired (that export is gone:
// start again), backup_other_account, backup_unsupported (format or version), backup_failed.
//
// A page, with empty values left out:
//
//	{"format", "version", "account": the store owner key (32 hex, a one-way hash of the Facebook id),
//	 "export": this export's id, "page": 0-based sequence,
//	 "expected": {contacts, threads, mappings, deletions, messages, media, shares, attachments, reactions, receipts,
//	             orphans} on the first page only,
//	 "contacts":  [{"id", "name", "picture"}],
//	 "threads":   [{"key", "name", "picture", "folder", "snippet", "kind", "participants": [ids], "activity",
//	               "readAt", "anchorAt", "anchorId", "cannotReply", "deleted", "unresolved", "deletedAt",
//	               "previewOutgoing", "muteUntil"}],
//	 "mappings":  [{"jid", "thread"}],
//	 "deletions": [{"id", "thread", "at"}],
//	 "messages":  [{"id", "thread", "sender", "body", "reply", "at", "encrypted", "aiUpdatedAt", "editedAt", "cardText", "notice",
//	               "media": [{"id", "name", "mime", "size", "width", "height", "video", "animated", "kind",
//	                          "transport", "at", "link"}],
//	               "share": the retained share schema,
//	               "attachments": [{"id", "name", "mime", "size", "width", "height", "video", "animated", "source",
//	                                "preview", "playable", "previewSource", "link", "description", "actionTitle"}],
//	               "reactions": [{"actor", "emoji"}]}],
//	 "receipts":  [{"thread", "contact", "read", "readFrom", "readAt", "delivered"}],
//	 "next": the cursor for the following page, "" on the last,
//	 "exported": the same counts as expected plus unreadable and oversized, on the last page only}
//
// Facebook ids are decimal strings, times Unix milliseconds, transport and share base64. Sections come in the order
// above and a message carries everything hung on it, so each page imports on its own; pages must still be imported in
// order, because a deletion or a conversation's cutoff has to be known before the messages it covers, and the messages
// before the receipts that place faces on them. Receipts are the encrypted conversations' kept read and delivery
// positions (receiptstore.go): the encrypted connection reports each one once, so they exist nowhere else either. A
// build that predates them ignores the section.
//
// Deliberately not in it: e2ee.db (the device identity and Signal sessions — restored anywhere else they clone this
// computer's device), encrypted.on, the session and its cookies, the archive walk markers, rows hung on a message that
// is not there to show them, and receipts still waiting for a message this computer never had (receipt_waiting),
// which are given up after a week anyway.
const backupFormat = "nativebeeper.messenger.history"
const backupVersion = 1

// How much one page may hold, counted as if .NET had written it again (see escapedSize). The page comes back through
// the dispatcher's 1 MiB request line, so what fits coming out has to fit going back in.
var backupPageBudget = 256 << 10

// A message whose page could never go back through that line is left out of the export and counted, not written.
// 704 KiB, so that a page (a 256 KiB budget and at most one unit past it) and its fields stay under the 1 MiB line it comes back through, with margin.
var backupUnitLimit = 704 << 10

// An export nobody asks the next page of for this long is abandoned and its snapshot removed.
var backupIdle = 5 * time.Minute

// Rows read from the snapshot per query.
const backupBatch = 500

// The export's own copy of the store, beside it in the account folder and no less protected than it.
const backupSnapshotName = "backup-export.tmp"

const (
	sectionContacts = iota
	sectionThreads
	sectionMappings
	sectionDeletions
	sectionMessages
	sectionReceipts
	sectionCount
)

type backupContact struct {
	ID      int64  `json:"id,string"`
	Name    string `json:"name,omitempty"`
	Picture string `json:"picture,omitempty"`
}

type backupThread struct {
	Key             int64    `json:"key,string"`
	Name            string   `json:"name,omitempty"`
	Picture         string   `json:"picture,omitempty"`
	Folder          string   `json:"folder,omitempty"`
	Snippet         string   `json:"snippet,omitempty"`
	Kind            int64    `json:"kind,omitempty"`
	Participants    []string `json:"participants,omitempty"`
	Activity        int64    `json:"activity,omitempty"`
	ReadAt          int64    `json:"readAt,omitempty"`
	AnchorAt        int64    `json:"anchorAt,omitempty"`
	AnchorID        string   `json:"anchorId,omitempty"`
	CannotReply     bool     `json:"cannotReply,omitempty"`
	Deleted         bool     `json:"deleted,omitempty"`
	Unresolved      bool     `json:"unresolved,omitempty"`
	DeletedAt       int64    `json:"deletedAt,omitempty"`
	PreviewOutgoing bool     `json:"previewOutgoing,omitempty"`
	MuteUntil       int64    `json:"muteUntil,omitempty"`
}

type backupMapping struct {
	JID    int64 `json:"jid,string"`
	Thread int64 `json:"thread,string"`
}

type backupDeletion struct {
	ID     string `json:"id"`
	Thread int64  `json:"thread,string"`
	At     int64  `json:"at"`
}

type backupMedia struct {
	ID        string `json:"id"`
	Name      string `json:"name,omitempty"`
	Mime      string `json:"mime,omitempty"`
	Size      int64  `json:"size,omitempty"`
	Width     int64  `json:"width,omitempty"`
	Height    int64  `json:"height,omitempty"`
	Video     bool   `json:"video,omitempty"`
	Animated  bool   `json:"animated,omitempty"`
	Kind      string `json:"kind,omitempty"`
	Transport []byte `json:"transport,omitempty"`
	At        int64  `json:"at,omitempty"`
	Link      string `json:"link,omitempty"`
}

type backupAttachment struct {
	ID            string `json:"id"`
	Name          string `json:"name,omitempty"`
	Mime          string `json:"mime,omitempty"`
	Size          int64  `json:"size,omitempty"`
	Width         int64  `json:"width,omitempty"`
	Height        int64  `json:"height,omitempty"`
	Video         bool   `json:"video,omitempty"`
	Animated      bool   `json:"animated,omitempty"`
	Source        string `json:"source,omitempty"`
	Preview       string `json:"preview,omitempty"`
	Playable      string `json:"playable,omitempty"`
	PreviewSource string `json:"previewSource,omitempty"`
	Link          string `json:"link,omitempty"`
	Description   string `json:"description,omitempty"`
	ActionTitle   string `json:"actionTitle,omitempty"`
}

type backupReaction struct {
	Actor int64  `json:"actor,string"`
	Emoji string `json:"emoji"`
}

type backupMessage struct {
	ID          string             `json:"id"`
	Thread      int64              `json:"thread,string"`
	Sender      int64              `json:"sender,string"`
	Body        string             `json:"body,omitempty"`
	Reply       string             `json:"reply,omitempty"`
	At          int64              `json:"at"`
	Encrypted   bool               `json:"encrypted,omitempty"`
	AIUpdatedAt int64              `json:"aiUpdatedAt,omitempty"`
	EditedAt    int64              `json:"editedAt,omitempty"`
	CardText    string             `json:"cardText,omitempty"`
	Notice      bool               `json:"notice,omitempty"`
	Media       []backupMedia      `json:"media,omitempty"`
	Share       []byte             `json:"share,omitempty"`
	Attachments []backupAttachment `json:"attachments,omitempty"`
	Reactions   []backupReaction   `json:"reactions,omitempty"`
}

// One person's kept positions in one encrypted conversation: how far they have read, the position that reading moved
// from (-1 when not known) and when, and how far everything has reached them.
type backupReceipt struct {
	Thread    int64 `json:"thread,string"`
	Contact   int64 `json:"contact,string"`
	Read      int64 `json:"read,omitempty"`
	ReadFrom  int64 `json:"readFrom"`
	ReadAt    int64 `json:"readAt,omitempty"`
	Delivered int64 `json:"delivered,omitempty"`
}

// Rows by kind. Expected counts what the export means to write, exported what it wrote; orphans are rows hung on a
// message that is missing or deleted, unreadable ones would not unseal, oversized ones could never be imported.
type backupTally struct {
	Contacts    int `json:"contacts"`
	Threads     int `json:"threads"`
	Mappings    int `json:"mappings"`
	Deletions   int `json:"deletions"`
	Messages    int `json:"messages"`
	Media       int `json:"media"`
	Shares      int `json:"shares"`
	Attachments int `json:"attachments"`
	Reactions   int `json:"reactions"`
	Receipts    int `json:"receipts"`
	Orphans     int `json:"orphans,omitempty"`
	Unreadable  int `json:"unreadable,omitempty"`
	Oversized   int `json:"oversized,omitempty"`
}

func (t *backupTally) add(other backupTally) {
	t.Contacts += other.Contacts
	t.Threads += other.Threads
	t.Mappings += other.Mappings
	t.Deletions += other.Deletions
	t.Messages += other.Messages
	t.Media += other.Media
	t.Shares += other.Shares
	t.Attachments += other.Attachments
	t.Reactions += other.Reactions
	t.Receipts += other.Receipts
	t.Orphans += other.Orphans
	t.Unreadable += other.Unreadable
	t.Oversized += other.Oversized
}

type backupPageOut struct {
	Format    string            `json:"format"`
	Version   int               `json:"version"`
	Account   string            `json:"account"`
	Export    string            `json:"export"`
	Page      int               `json:"page"`
	Expected  *backupTally      `json:"expected,omitempty"`
	Contacts  []json.RawMessage `json:"contacts,omitempty"`
	Threads   []json.RawMessage `json:"threads,omitempty"`
	Mappings  []json.RawMessage `json:"mappings,omitempty"`
	Deletions []json.RawMessage `json:"deletions,omitempty"`
	Messages  []json.RawMessage `json:"messages,omitempty"`
	Receipts  []json.RawMessage `json:"receipts,omitempty"`
	Next      string            `json:"next"`
	Exported  *backupTally      `json:"exported,omitempty"`
}

type backupPageIn struct {
	Format    string           `json:"format"`
	Version   int              `json:"version"`
	Account   string           `json:"account"`
	Export    string           `json:"export"`
	Page      int              `json:"page"`
	Contacts  []backupContact  `json:"contacts"`
	Threads   []backupThread   `json:"threads"`
	Mappings  []backupMapping  `json:"mappings"`
	Deletions []backupDeletion `json:"deletions"`
	Messages  []backupMessage  `json:"messages"`
	Receipts  []backupReceipt  `json:"receipts"`
}

// One export or import step at a time, and the export in progress if there is one.
type backupState struct {
	mu      sync.Mutex
	current *backupExport
}

type backupExport struct {
	id       string
	path     string
	db       *sql.DB
	account  string
	page     int
	idle     *time.Timer
	started  time.Time
	used     time.Time
	expected backupTally
	exported backupTally
	// The cursor handed out last, and the page last served with the cursor that asked for it. Only those two are
	// answered: the next page, or the same page again for a caller whose answer went missing. Anything else would
	// skip or repeat rows without anyone noticing.
	next   string
	served string
	last   *backupPageOut
}

func (a *adapter) backupCall(ctx context.Context, r request) (any, string) {
	switch r.Method {
	case "backup.exportEnd":
		a.endBackup("ended")
		return map[string]bool{"ended": true}, ""
	case "backup.count":
		// What the store holds now, counted the way an export counts what it means to write, read back from the database: the check that what an import
		// reported is what is there. Local, and needs only the store that backup.open (or a login) opened.
		kept, _ := a.backupStore()
		if kept == nil {
			return nil, "backup_unavailable"
		}
		held, err := countExpected(ctx, kept.db)
		if err != nil {
			return nil, "backup_failed"
		}
		return held, ""
	case "backup.export", "backup.import":
		// Local reads and writes, but only for an account the helper has signed in to and opened the store of.
		if a.client == nil {
			// An offline process never signs in; backup.open is its sign-in, so the store it opened stands in for one.
			if kept, _ := a.backupStore(); !a.offline || kept == nil {
				return nil, "login_required"
			}
		}
		if r.Method == "backup.export" {
			return a.backupExport(ctx, r.Params)
		}
		return a.backupImport(ctx, r.Params)
	}
	return nil, "unsupported_method"
}

// Opens an account's kept history in an offline process (NATIVEBEEPER_MESSENGER_OFFLINE=1), the way login does but
// without any contact with Facebook, so the history can be read out of one machine's messages.db and written into
// another's during a move. The app never uses it on a signed-in helper.
//
//	backup.open {"account": "<decimal Facebook id>", "create": false|true}  ->  {"opened": true, "account": "<32 hex key>"}
//
// create false needs the account's messages.db to be there already (backup_unavailable otherwise); true creates it at
// the account's path, which is how a server that has never seen the account receives its history. Opening the same
// account again answers the same success without reopening; another account is session_already_started.
// Errors: offline_only (not an offline process), invalid_input, backup_unavailable, no_storage (the storage root or the
// account's folder cannot be made), session_already_started.
func (a *adapter) backupOpen(ctx context.Context, r request) (any, string) {
	if !a.offline {
		return nil, "offline_only"
	}
	var p struct {
		Account string `json:"account"`
		Create  bool   `json:"create"`
	}
	if len(r.Params) == 0 || json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	viewer, err := strconv.ParseInt(p.Account, 10, 64)
	if err != nil || viewer <= 0 {
		return nil, "invalid_input"
	}
	key := messengerAccountKey(viewer)
	a.mu.Lock()
	open := a.kept != nil
	a.mu.Unlock()
	if open {
		if a.chats.viewer() == viewer {
			return map[string]any{"opened": true, "account": key}, ""
		}
		return nil, "session_already_started"
	}
	var path string
	if p.Create {
		if path = messengerAccountPath(viewer, "messages.db"); path == "" {
			return nil, "no_storage"
		}
	} else if path = existingAccountPath(viewer, "messages.db"); path == "" {
		return nil, "backup_unavailable"
	}
	a.chats.setSelf(viewer)
	kept, err := openAccountMessageStore(ctx, path, viewer)
	if err != nil {
		logf("backup open: the message store could not be opened")
		return nil, "backup_unavailable"
	}
	discardStaleBackup(kept)
	// The conversations are restored as login restores them: an import decides what to add against what is held, and a
	// receipt's place is only taken in a conversation that is.
	restored := kept.restore(ctx, a.chats)
	kept.restoreReceipts(ctx, a.chats)
	a.mu.Lock()
	a.kept = kept
	a.mu.Unlock()
	logf("backup open created=%v restored=%d", p.Create, restored)
	return map[string]any{"opened": true, "account": key}, ""
}

// The store and whose it is, or nothing when there is no signed-in store to read or write.
func (a *adapter) backupStore() (*messageStore, string) {
	a.mu.Lock()
	kept := a.kept
	a.mu.Unlock()
	viewer := a.chats.viewer()
	if kept == nil || kept.path == "" || viewer <= 0 {
		return nil, ""
	}
	return kept, messengerAccountKey(viewer)
}

func (a *adapter) backupExport(ctx context.Context, params json.RawMessage) (any, string) {
	var p struct {
		Cursor string `json:"cursor"`
	}
	if len(params) > 0 && json.Unmarshal(params, &p) != nil {
		return nil, "invalid_input"
	}
	kept, account := a.backupStore()
	if kept == nil {
		return nil, "backup_unavailable"
	}
	a.backup.mu.Lock()
	defer a.backup.mu.Unlock()
	export := a.backup.current
	section, after := sectionContacts, int64(0)
	if p.Cursor == "" {
		if export != nil {
			a.discardExportLocked("replaced")
		}
		var code string
		if export, code = openExport(ctx, kept, account); code != "" {
			return nil, code
		}
		a.backup.current = export
		// Judged by when a page was last read rather than by the timer alone: a timer that fires while a page is being
		// read waits for the lock, and must not then throw away the export that page just used.
		export.idle = time.AfterFunc(backupIdle, func() {
			a.backup.mu.Lock()
			defer a.backup.mu.Unlock()
			if a.backup.current == export && time.Since(export.used) >= backupIdle {
				a.discardExportLocked("idle")
			}
		})
	} else {
		id, at, rowid, ok := parseBackupCursor(p.Cursor)
		if !ok {
			return nil, "invalid_input"
		}
		if export == nil || export.id != id {
			return nil, "backup_expired"
		}
		if p.Cursor == export.served && export.last != nil {
			export.used = time.Now()
			export.idle.Reset(backupIdle)
			return export.last, ""
		}
		if p.Cursor != export.next {
			return nil, "backup_expired"
		}
		section, after = at, rowid
	}
	page, err := export.nextPage(ctx, section, after)
	if err != nil {
		logf("backup export page=%d could not be read", export.page)
		a.discardExportLocked("failed")
		return nil, "backup_failed"
	}
	if page.Next == "" {
		a.discardExportLocked("finished")
	} else {
		export.served, export.last, export.next = p.Cursor, page, page.Next
		export.used = time.Now()
		export.idle.Reset(backupIdle)
	}
	return page, ""
}

// The snapshot is one statement's read of the live store, so it is consistent without holding a transaction open
// across pages: in the store's rollback journal an open read would hold both writers off for the whole export, and a
// writer that waits past its busy timeout loses a message Facebook delivers once. VACUUM INTO does not sync the copy,
// which keeps that one read short; 30 MB took 70 ms on this machine.
func openExport(ctx context.Context, kept *messageStore, account string) (*backupExport, string) {
	path := filepath.Join(filepath.Dir(kept.path), backupSnapshotName)
	removeBackupSnapshot(path)
	started := time.Now()
	if _, err := kept.db.ExecContext(ctx, `VACUUM INTO ?`, path); err != nil {
		removeBackupSnapshot(path)
		logf("backup export snapshot failed")
		return nil, "backup_failed"
	}
	held := time.Since(started)
	var size int64
	if info, err := os.Stat(path); err == nil {
		size = info.Size()
	}
	db, err := sql.Open("sqlite", sqliteFile(path, "_pragma=busy_timeout(5000)"))
	if err != nil {
		removeBackupSnapshot(path)
		return nil, "backup_failed"
	}
	db.SetMaxOpenConns(1)
	export := &backupExport{id: randomBackupID(), path: path, db: db, account: account, started: started, used: started}
	fail := func() (*backupExport, string) {
		_ = db.Close()
		removeBackupSnapshot(path)
		logf("backup export snapshot could not be prepared")
		return nil, "backup_failed"
	}
	// Whose store this copy is, checked again on the copy itself rather than assumed from the one it came from.
	var owner string
	if db.QueryRowContext(ctx, `SELECT owner FROM store_owner WHERE slot=1`).Scan(&owner) != nil || owner != account {
		return fail()
	}
	// The copy is this export's own, so indexing it costs the live store nothing and each page reads what hangs on
	// its messages without scanning every attachment.
	for _, statement := range []string{
		`CREATE INDEX IF NOT EXISTS backup_media_message ON encrypted_media(message)`,
		`CREATE INDEX IF NOT EXISTS backup_attachment_message ON message_attachment(message)`,
	} {
		if _, err := db.ExecContext(ctx, statement); err != nil {
			return fail()
		}
	}
	if export.expected, err = countExpected(ctx, db); err != nil {
		return fail()
	}
	e := export.expected
	logf("backup export started snapshot=%dms bytes=%d contacts=%d threads=%d mappings=%d deletions=%d messages=%d media=%d shares=%d attachments=%d reactions=%d receipts=%d orphans=%d",
		held.Milliseconds(), size, e.Contacts, e.Threads, e.Mappings, e.Deletions, e.Messages, e.Media, e.Shares, e.Attachments, e.Reactions, e.Receipts, e.Orphans)
	return export, ""
}

// What the export means to write: everything, less what hangs on a message that is missing or deleted.
func countExpected(ctx context.Context, db *sql.DB) (backupTally, error) {
	var t backupTally
	const shown = `JOIN encrypted_message AS p ON p.id = x.message WHERE NOT EXISTS (SELECT 1 FROM deleted_message AS d WHERE d.id = p.id)`
	var all [4]int
	err := db.QueryRowContext(ctx, `SELECT
		(SELECT COUNT(*) FROM thread_contact), (SELECT COUNT(*) FROM thread_identity), (SELECT COUNT(*) FROM thread_mapping),
		(SELECT COUNT(*) FROM deleted_message),
		(SELECT COUNT(*) FROM encrypted_message AS p WHERE NOT EXISTS (SELECT 1 FROM deleted_message AS d WHERE d.id = p.id)),
		(SELECT COUNT(*) FROM encrypted_media AS x `+shown+`), (SELECT COUNT(*) FROM encrypted_share AS x `+shown+`),
		(SELECT COUNT(*) FROM message_attachment AS x `+shown+`), (SELECT COUNT(*) FROM encrypted_reaction AS x `+shown+`),
		(SELECT COUNT(*) FROM encrypted_media), (SELECT COUNT(*) FROM encrypted_share),
		(SELECT COUNT(*) FROM message_attachment), (SELECT COUNT(*) FROM encrypted_reaction), (SELECT COUNT(*) FROM receipt_watermark)`).Scan(
		&t.Contacts, &t.Threads, &t.Mappings, &t.Deletions, &t.Messages, &t.Media, &t.Shares, &t.Attachments, &t.Reactions,
		&all[0], &all[1], &all[2], &all[3], &t.Receipts)
	t.Orphans = all[0] + all[1] + all[2] + all[3] - t.Media - t.Shares - t.Attachments - t.Reactions
	return t, err
}

func randomBackupID() string {
	var raw [8]byte
	_, _ = rand.Read(raw[:])
	return hex.EncodeToString(raw[:])
}

func removeBackupSnapshot(path string) bool {
	removed := os.Remove(path) == nil
	_ = os.Remove(path + "-journal")
	return removed
}

// Call with the backup lock held.
func (a *adapter) discardExportLocked(reason string) {
	export := a.backup.current
	if export == nil {
		return
	}
	a.backup.current = nil
	if export.idle != nil {
		export.idle.Stop()
	}
	_ = export.db.Close()
	removeBackupSnapshot(export.path)
	x := export.exported
	logf("backup export ended reason=%s pages=%d ms=%d contacts=%d threads=%d mappings=%d deletions=%d messages=%d media=%d shares=%d attachments=%d reactions=%d receipts=%d unreadable=%d oversized=%d",
		reason, export.page, time.Since(export.started).Milliseconds(), x.Contacts, x.Threads, x.Mappings, x.Deletions,
		x.Messages, x.Media, x.Shares, x.Attachments, x.Reactions, x.Receipts, x.Unreadable, x.Oversized)
}

// Abandons the export in progress, when the app asks or the helper is going away.
func (a *adapter) endBackup(reason string) {
	a.backup.mu.Lock()
	defer a.backup.mu.Unlock()
	a.discardExportLocked(reason)
}

// A snapshot left by a helper that stopped mid-export holds messages that may have been deleted since, so it goes at
// the next sign-in rather than waiting for the next export.
func discardStaleBackup(kept *messageStore) {
	if kept == nil || kept.path == "" {
		return
	}
	if removeBackupSnapshot(filepath.Join(filepath.Dir(kept.path), backupSnapshotName)) {
		logf("backup export snapshot left by an earlier run removed")
	}
}

func backupCursor(id string, section int, rowid int64) string {
	return id + "." + strconv.Itoa(section) + "." + strconv.FormatInt(rowid, 10)
}

func parseBackupCursor(cursor string) (string, int, int64, bool) {
	parts := strings.Split(cursor, ".")
	if len(parts) != 3 || parts[0] == "" {
		return "", 0, 0, false
	}
	section, err := strconv.Atoi(parts[1])
	if err != nil || section < 0 || section >= sectionCount {
		return "", 0, 0, false
	}
	rowid, err := strconv.ParseInt(parts[2], 10, 64)
	if err != nil || rowid < 0 {
		return "", 0, 0, false
	}
	return parts[0], section, rowid, true
}

// How long a piece of JSON can become once .NET has written it again, which is what backup.import receives: its
// default encoder writes every non-ASCII character, and quotes, backslashes, angle brackets, ampersands, apostrophes,
// plus signs and backticks, as \u escapes. Counting that here keeps a page that fit going out fitting coming back.
func escapedSize(data []byte) int {
	size := 0
	for len(data) > 0 {
		r, width := utf8.DecodeRune(data)
		data = data[width:]
		switch {
		case r > 0xFFFF:
			size += 12
		case r >= utf8.RuneSelf:
			size += 6
		case strings.ContainsRune("\"\\<>&'+`", r):
			size += 6
		default:
			size++
		}
	}
	return size
}

// One row, or one message with everything on it, ready for a page. data is nil for a row left out.
type backupUnit struct {
	rowid int64
	data  json.RawMessage
	size  int
	tally backupTally
}

func (u *backupUnit) encode(value any, counted backupTally) {
	data, err := json.Marshal(value)
	if err != nil {
		u.tally = backupTally{Unreadable: 1}
		return
	}
	u.data, u.size, u.tally = data, escapedSize(data), counted
}

func (e *backupExport) nextPage(ctx context.Context, section int, after int64) (*backupPageOut, error) {
	page := &backupPageOut{Format: backupFormat, Version: backupVersion, Account: e.account, Export: e.id, Page: e.page}
	if e.page == 0 {
		expected := e.expected
		page.Expected = &expected
	}
	used, full := 0, false
	for section < sectionCount && !full {
		units, exhausted, err := e.batch(ctx, section, after)
		if err != nil {
			return nil, err
		}
		for _, unit := range units {
			if unit.data != nil {
				if unit.size > backupUnitLimit {
					e.exported.Oversized++
					after = unit.rowid
					continue
				}
				if used > 0 && used+unit.size > backupPageBudget {
					full = true
					break
				}
				used += unit.size
				switch section {
				case sectionContacts:
					page.Contacts = append(page.Contacts, unit.data)
				case sectionThreads:
					page.Threads = append(page.Threads, unit.data)
				case sectionMappings:
					page.Mappings = append(page.Mappings, unit.data)
				case sectionDeletions:
					page.Deletions = append(page.Deletions, unit.data)
				case sectionMessages:
					page.Messages = append(page.Messages, unit.data)
				default:
					page.Receipts = append(page.Receipts, unit.data)
				}
			}
			e.exported.add(unit.tally)
			after = unit.rowid
		}
		if !full && exhausted {
			section, after = section+1, 0
		}
	}
	if section < sectionCount {
		page.Next = backupCursor(e.id, section, after)
	} else {
		exported := e.exported
		page.Exported = &exported
	}
	e.page++
	return page, nil
}

// The next rows of one section after a rowid, and whether the section has nothing after them.
func (e *backupExport) batch(ctx context.Context, section int, after int64) ([]backupUnit, bool, error) {
	if section == sectionMessages {
		return e.messageBatch(ctx, after)
	}
	queries := map[int]string{
		sectionContacts:  `SELECT rowid, id, name, picture FROM thread_contact WHERE rowid > ? ORDER BY rowid LIMIT ?`,
		sectionThreads:   `SELECT rowid, identity FROM thread_identity WHERE rowid > ? ORDER BY rowid LIMIT ?`,
		sectionMappings:  `SELECT rowid, jid, thread FROM thread_mapping WHERE rowid > ? ORDER BY rowid LIMIT ?`,
		sectionDeletions: `SELECT rowid, id, thread, at FROM deleted_message WHERE rowid > ? ORDER BY rowid LIMIT ?`,
		sectionReceipts: `SELECT rowid, thread, contact, read_mark, read_from, read_at, delivered FROM receipt_watermark
			WHERE rowid > ? ORDER BY rowid LIMIT ?`,
	}
	rows, err := e.db.QueryContext(ctx, queries[section], after, backupBatch)
	if err != nil {
		return nil, false, err
	}
	defer rows.Close()
	var units []backupUnit
	for rows.Next() {
		unit := backupUnit{tally: backupTally{Unreadable: 1}}
		switch section {
		case sectionContacts:
			var id int64
			var name, picture []byte
			if err := rows.Scan(&unit.rowid, &id, &name, &picture); err != nil {
				return nil, false, err
			}
			plainName, nameErr := unprotectString(name)
			plainPicture, pictureErr := unprotectString(picture)
			if nameErr == nil && pictureErr == nil {
				unit.encode(backupContact{ID: id, Name: plainName, Picture: plainPicture}, backupTally{Contacts: 1})
			}
		case sectionThreads:
			var sealed []byte
			if err := rows.Scan(&unit.rowid, &sealed); err != nil {
				return nil, false, err
			}
			var saved savedThread
			if plain, err := unprotectLocal(sealed); err == nil && json.Unmarshal(plain, &saved) == nil && saved.Key != 0 {
				unit.encode(threadForBackup(saved), backupTally{Threads: 1})
			}
		case sectionMappings:
			var mapping backupMapping
			if err := rows.Scan(&unit.rowid, &mapping.JID, &mapping.Thread); err != nil {
				return nil, false, err
			}
			unit.encode(mapping, backupTally{Mappings: 1})
		case sectionDeletions:
			var deletion backupDeletion
			if err := rows.Scan(&unit.rowid, &deletion.ID, &deletion.Thread, &deletion.At); err != nil {
				return nil, false, err
			}
			unit.encode(deletion, backupTally{Deletions: 1})
		case sectionReceipts:
			var receipt backupReceipt
			if err := rows.Scan(&unit.rowid, &receipt.Thread, &receipt.Contact, &receipt.Read, &receipt.ReadFrom, &receipt.ReadAt,
				&receipt.Delivered); err != nil {
				return nil, false, err
			}
			unit.encode(receipt, backupTally{Receipts: 1})
		}
		units = append(units, unit)
	}
	return units, len(units) < backupBatch, rows.Err()
}

func threadForBackup(saved savedThread) backupThread {
	people := make([]string, 0, len(saved.Participants))
	for id := range saved.Participants {
		people = append(people, strconv.FormatInt(id, 10))
	}
	sort.Strings(people)
	return backupThread{Key: saved.Key, Name: saved.Name, Picture: saved.Picture, Folder: saved.Folder, Snippet: saved.Snippet,
		Kind: int64(saved.Kind), Participants: people, Activity: saved.Activity, ReadAt: saved.ReadAt, AnchorAt: saved.AnchorAt,
		AnchorID: saved.AnchorID, CannotReply: saved.CannotReply, Deleted: saved.Deleted, Unresolved: saved.Unresolved,
		DeletedAt: saved.DeletedAt, PreviewOutgoing: saved.PreviewOutgoing, MuteUntil: saved.MuteUntil}
}

// Messages not taken back, each with its files, share, attachments and reactions, so a page carries them together
// and an import can take or refuse a message whole.
func (e *backupExport) messageBatch(ctx context.Context, after int64) ([]backupUnit, bool, error) {
	rows, err := e.db.QueryContext(ctx, `SELECT p.rowid, p.id, p.thread, p.sender, p.body, p.reply, p.at, p.encrypted,
		p.ai_updated_at, p.edited_at, p.card_text, p.notice FROM encrypted_message AS p
		WHERE p.rowid > ? AND NOT EXISTS (SELECT 1 FROM deleted_message AS d WHERE d.id = p.id)
		ORDER BY p.rowid LIMIT ?`, after, backupBatch)
	if err != nil {
		return nil, false, err
	}
	type held struct {
		rowid    int64
		message  backupMessage
		readable bool
	}
	var batch []held
	for rows.Next() {
		var h held
		var body, reply, card []byte
		if err := rows.Scan(&h.rowid, &h.message.ID, &h.message.Thread, &h.message.Sender, &body, &reply, &h.message.At,
			&h.message.Encrypted, &h.message.AIUpdatedAt, &h.message.EditedAt, &card, &h.message.Notice); err != nil {
			rows.Close()
			return nil, false, err
		}
		var bodyErr, replyErr, cardErr error
		h.message.Body, bodyErr = unprotectString(body)
		h.message.Reply, replyErr = unprotectString(reply)
		h.message.CardText, cardErr = unprotectString(card)
		h.readable = bodyErr == nil && replyErr == nil && cardErr == nil
		batch = append(batch, h)
	}
	if err := rows.Err(); err != nil {
		rows.Close()
		return nil, false, err
	}
	rows.Close()
	if len(batch) == 0 {
		return nil, true, nil
	}
	parts, unreadable, err := e.messageParts(ctx, after, batch[len(batch)-1].rowid)
	if err != nil {
		return nil, false, err
	}
	units := make([]backupUnit, 0, len(batch))
	for _, h := range batch {
		unit := backupUnit{rowid: h.rowid, tally: backupTally{Unreadable: 1}}
		if h.readable {
			m := h.message
			held := parts[m.ID]
			if held != nil {
				m.Media, m.Share, m.Attachments, m.Reactions = held.Media, held.Share, held.Attachments, held.Reactions
			}
			counted := backupTally{Messages: 1, Media: len(m.Media), Attachments: len(m.Attachments), Reactions: len(m.Reactions),
				Unreadable: unreadable[m.ID]}
			if m.Share != nil {
				counted.Shares = 1
			}
			unit.encode(m, counted)
		}
		units = append(units, unit)
	}
	return units, len(batch) < backupBatch, nil
}

// Everything hung on the messages in a rowid range, by message id, and how many of those parts would not unseal.
func (e *backupExport) messageParts(ctx context.Context, after, last int64) (map[string]*backupMessage, map[string]int, error) {
	parts := map[string]*backupMessage{}
	unreadable := map[string]int{}
	of := func(id string) *backupMessage {
		held := parts[id]
		if held == nil {
			held = &backupMessage{}
			parts[id] = held
		}
		return held
	}
	const inRange = `JOIN encrypted_message AS p ON p.id = x.message WHERE p.rowid > ? AND p.rowid <= ?`
	rows, err := e.db.QueryContext(ctx, `SELECT x.message, x.id, x.name, x.mime, x.size, x.width, x.height, x.video, x.animated,
		x.kind, x.transport, x.at, x.link FROM encrypted_media AS x `+inRange+` ORDER BY x.rowid`, after, last)
	if err != nil {
		return nil, nil, err
	}
	for rows.Next() {
		var owner string
		var item backupMedia
		var name, mime, kind, transport, link []byte
		if err := rows.Scan(&owner, &item.ID, &name, &mime, &item.Size, &item.Width, &item.Height, &item.Video, &item.Animated,
			&kind, &transport, &item.At, &link); err != nil {
			rows.Close()
			return nil, nil, err
		}
		var errs [5]error
		item.Name, errs[0] = unprotectString(name)
		item.Mime, errs[1] = unprotectString(mime)
		item.Kind, errs[2] = unprotectString(kind)
		item.Transport, errs[3] = unprotectLocal(transport)
		item.Link, errs[4] = unprotectString(link)
		if errors.Join(errs[:]...) != nil {
			unreadable[owner]++
			continue
		}
		of(owner).Media = append(of(owner).Media, item)
	}
	if err := closeRows(rows); err != nil {
		return nil, nil, err
	}
	rows, err = e.db.QueryContext(ctx, `SELECT x.message, x.payload FROM encrypted_share AS x `+inRange, after, last)
	if err != nil {
		return nil, nil, err
	}
	for rows.Next() {
		var owner string
		var sealed []byte
		if err := rows.Scan(&owner, &sealed); err != nil {
			rows.Close()
			return nil, nil, err
		}
		payload, err := unprotectLocal(sealed)
		if err != nil {
			unreadable[owner]++
			continue
		}
		of(owner).Share = payload
	}
	if err := closeRows(rows); err != nil {
		return nil, nil, err
	}
	rows, err = e.db.QueryContext(ctx, `SELECT x.message, x.id, x.name, x.mime, x.size, x.width, x.height, x.video, x.animated,
		x.source, x.preview, x.playable, x.previewSource, x.link, x.description, x.actionTitle FROM message_attachment AS x `+inRange+`
		ORDER BY x.rowid`, after, last)
	if err != nil {
		return nil, nil, err
	}
	for rows.Next() {
		var owner string
		var item backupAttachment
		var sealed [9][]byte
		if err := rows.Scan(&owner, &item.ID, &sealed[0], &sealed[1], &item.Size, &item.Width, &item.Height, &item.Video,
			&item.Animated, &sealed[2], &sealed[3], &sealed[4], &sealed[5], &sealed[6], &sealed[7], &sealed[8]); err != nil {
			rows.Close()
			return nil, nil, err
		}
		var plain [9]string
		readable := true
		for index := range sealed {
			var err error
			if plain[index], err = unprotectString(sealed[index]); err != nil {
				readable = false
				break
			}
		}
		if !readable {
			unreadable[owner]++
			continue
		}
		item.Name, item.Mime, item.Source, item.Preview, item.Playable, item.PreviewSource, item.Link, item.Description, item.ActionTitle =
			plain[0], plain[1], plain[2], plain[3], plain[4], plain[5], plain[6], plain[7], plain[8]
		of(owner).Attachments = append(of(owner).Attachments, item)
	}
	if err := closeRows(rows); err != nil {
		return nil, nil, err
	}
	rows, err = e.db.QueryContext(ctx, `SELECT x.message, x.actor, x.emoji FROM encrypted_reaction AS x `+inRange+`
		ORDER BY x.message, x.actor`, after, last)
	if err != nil {
		return nil, nil, err
	}
	for rows.Next() {
		var owner string
		var reaction backupReaction
		var sealed []byte
		if err := rows.Scan(&owner, &reaction.Actor, &sealed); err != nil {
			rows.Close()
			return nil, nil, err
		}
		if reaction.Emoji, err = unprotectString(sealed); err != nil {
			unreadable[owner]++
			continue
		}
		of(owner).Reactions = append(of(owner).Reactions, reaction)
	}
	if err := closeRows(rows); err != nil {
		return nil, nil, err
	}
	return parts, unreadable, nil
}

func closeRows(rows *sql.Rows) error {
	err := rows.Err()
	if closeErr := rows.Close(); err == nil {
		err = closeErr
	}
	return err
}

// Import writes only what is missing, by stable id, and never lets a backup undo what has happened here since:
//   - a message deleted here, or in a conversation deleted here after it, is refused with everything on it;
//   - a deletion the backup knows of removes the message here too and is recorded, so it stays deleted;
//   - a conversation, person or pairing known here keeps what it has, and the backup only fills gaps — except a later
//     conversation deletion, whose cutoff is applied;
//   - reactions come back only with a message that came back. One already here keeps its own, because a reaction
//     taken away leaves no record that could show the backup's copy is stale;
//   - a read or delivery position only ever moves forward, here as live, and only in a conversation held here.
//
// Every sensitive cell is sealed as the live writes seal it. Nothing here opens e2ee.db, touches encrypted.on, the
// session or cookies, or asks Facebook for anything.
type backupCounts struct {
	Added   int `json:"added"`
	Skipped int `json:"skipped"`
	Refused int `json:"refused"`
	Removed int `json:"removed,omitempty"`
}

type backupSections struct {
	Contacts    backupCounts `json:"contacts"`
	Threads     backupCounts `json:"threads"`
	Mappings    backupCounts `json:"mappings"`
	Deletions   backupCounts `json:"deletions"`
	Messages    backupCounts `json:"messages"`
	Media       backupCounts `json:"media"`
	Shares      backupCounts `json:"shares"`
	Attachments backupCounts `json:"attachments"`
	Reactions   backupCounts `json:"reactions"`
	Receipts    backupCounts `json:"receipts"`
}

func (s *backupSections) all() []*backupCounts {
	return []*backupCounts{&s.Contacts, &s.Threads, &s.Mappings, &s.Deletions, &s.Messages, &s.Media, &s.Shares, &s.Attachments, &s.Reactions,
		&s.Receipts}
}

type backupImportResult struct {
	Page     int            `json:"page"`
	Added    int            `json:"added"`
	Skipped  int            `json:"skipped"`
	Refused  int            `json:"refused"`
	Removed  int            `json:"removed"`
	Cutoffs  int            `json:"cutoffs"`
	Sections backupSections `json:"sections"`
}

func (a *adapter) backupImport(ctx context.Context, params json.RawMessage) (any, string) {
	var page backupPageIn
	if len(params) == 0 || json.Unmarshal(params, &page) != nil {
		return nil, "invalid_input"
	}
	if page.Format != backupFormat || page.Version != backupVersion {
		logf("backup import refused: format version=%d is not one this build reads", page.Version)
		return nil, "backup_unsupported"
	}
	kept, account := a.backupStore()
	if kept == nil {
		return nil, "backup_unavailable"
	}
	if page.Account != account {
		logf("backup import refused: the page belongs to another account")
		return nil, "backup_other_account"
	}
	a.backup.mu.Lock()
	defer a.backup.mu.Unlock()
	result, err := a.importPage(ctx, kept, &page)
	if err != nil {
		logf("backup import page=%d failed", page.Page)
		return nil, "backup_failed"
	}
	logf("backup import page=%d added=%d skipped=%d refused=%d removed=%d cutoffs=%d messages=%d/%d/%d receipts=%d/%d/%d",
		page.Page, result.Added, result.Skipped, result.Refused, result.Removed, result.Cutoffs,
		result.Sections.Messages.Added, result.Sections.Messages.Skipped, result.Sections.Messages.Refused,
		result.Sections.Receipts.Added, result.Sections.Receipts.Skipped, result.Sections.Receipts.Refused)
	return result, ""
}

const (
	planInsert = iota
	planExisting
	planRefused
)

// One message from a page and what is to happen to it. The sealed cells are prepared before the state lock is taken:
// sealing is DPAPI, a fraction of a millisecond a cell, and incoming messages should not wait on it.
type importPlan struct {
	message     *backupMessage
	state       int
	key         int64
	sealed      [][]byte
	media       map[string][][]byte
	share       []byte
	attachments map[string][][]byte
	reactions   map[int64][]byte
	// What actually went in, for putting back on screen.
	inserted         bool
	addedMedia       []*backupMedia
	addedAttachments []*backupAttachment
	addedShare       bool
}

func (a *adapter) importPage(ctx context.Context, kept *messageStore, page *backupPageIn) (*backupImportResult, error) {
	result := &backupImportResult{Page: page.Page}
	sections := &result.Sections
	plans, err := planImport(ctx, kept.db, page.Messages)
	if err != nil {
		return nil, err
	}
	a.state.Lock()
	defer a.state.Unlock()
	shown := &importChanges{}
	if len(page.Contacts)+len(page.Threads)+len(page.Mappings) > 0 {
		cut := a.chats.adoptBackupIdentity(page, result, shown)
		result.Cutoffs = len(cut)
		// Counted only if it reached the store: a page whose threads and contacts could not be saved fails, and is asked again.
		if _, saved := kept.saveIdentityChecked(ctx, a.chats); !saved {
			return nil, errors.New("the identity rows could not be saved")
		}
	}
	tx, err := kept.db.BeginTx(ctx, nil)
	if err != nil {
		return nil, err
	}
	committed := false
	defer func() {
		if !committed {
			_ = tx.Rollback()
		}
	}()
	var forgotten []string
	for _, deletion := range page.Deletions {
		if deletion.ID == "" {
			sections.Deletions.Refused++
			continue
		}
		key := a.chats.canonicalThread(deletion.Thread)
		written, err := tx.ExecContext(ctx, `INSERT INTO deleted_message (id, thread, at) VALUES (?, ?, ?) ON CONFLICT(id) DO NOTHING`,
			deletion.ID, key, deletion.At)
		if err != nil {
			return nil, err
		}
		if count, _ := written.RowsAffected(); count > 0 {
			sections.Deletions.Added++
		} else {
			sections.Deletions.Skipped++
		}
		removed, err := tx.ExecContext(ctx, `DELETE FROM encrypted_message WHERE id = ?`, deletion.ID)
		if err != nil {
			return nil, err
		}
		for _, statement := range []string{
			`DELETE FROM encrypted_media WHERE message = ?`, `DELETE FROM encrypted_share WHERE message = ?`,
			`DELETE FROM message_attachment WHERE message = ?`, `DELETE FROM encrypted_reaction WHERE message = ?`,
		} {
			if _, err := tx.ExecContext(ctx, statement, deletion.ID); err != nil {
				return nil, err
			}
		}
		if count, _ := removed.RowsAffected(); count > 0 {
			sections.Deletions.Removed++
		}
		forgotten = append(forgotten, deletion.ID)
	}
	for _, plan := range plans {
		if err := a.importMessage(ctx, tx, plan, sections); err != nil {
			return nil, err
		}
	}
	// Written with the same forward-only upsert as a live receipt, so a position this computer has moved further since
	// the backup keeps its own.
	var receipts []backupReceipt
	for _, offered := range page.Receipts {
		receipt, ok := a.chats.backupReceiptTarget(offered)
		if !ok {
			sections.Receipts.Refused++
			continue
		}
		if _, err := tx.ExecContext(ctx, keepWatermarkStatement, receipt.Thread, receipt.Contact, receipt.Read, receipt.ReadFrom, receipt.ReadAt,
			receipt.Delivered); err != nil {
			return nil, err
		}
		receipts = append(receipts, receipt)
	}
	if err := tx.Commit(); err != nil {
		return nil, err
	}
	committed = true
	for _, id := range forgotten {
		if key, found := a.chats.forgetDeleted(id); found {
			shown.deleted(key, id)
		}
	}
	added := false
	for _, plan := range plans {
		if a.showImported(plan, shown) {
			added = true
		}
	}
	if added {
		// Restored messages move a conversation's activity and preview, which belong in its identity row too.
		if _, saved := kept.saveIdentityChecked(ctx, a.chats); !saved {
			return nil, errors.New("the identity rows could not be saved")
		}
	}
	for _, receipt := range receipts {
		moved, advanced, ok := a.chats.adoptBackupReceipt(receipt)
		switch {
		case !ok:
			sections.Receipts.Refused++
		case advanced:
			sections.Receipts.Added++
		default:
			sections.Receipts.Skipped++
		}
		for _, id := range moved {
			shown.receipt(receipt.Thread, id)
		}
	}
	// Receipts a restored message settled (settleWaitingReceipts) moved positions in memory; they are kept as a live
	// receipt's are.
	a.keepReceipts(ctx)
	for _, counts := range sections.all() {
		result.Added += counts.Added
		result.Skipped += counts.Skipped
		result.Refused += counts.Refused
		result.Removed += counts.Removed
	}
	if touched := shown.changes(); !touched.empty() && a.out != nil && !a.offline {
		a.out.write(response{Event: "changed", Result: touched})
	}
	return result, nil
}

// What the page's messages meet in the store: which are here already, which were deleted here, and the sealed cells
// for everything that is not here yet.
func planImport(ctx context.Context, db *sql.DB, messages []backupMessage) ([]*importPlan, error) {
	var ids, mediaIDs, attachmentIDs []string
	for index := range messages {
		m := &messages[index]
		ids = append(ids, m.ID)
		for _, item := range m.Media {
			mediaIDs = append(mediaIDs, item.ID)
		}
		for _, item := range m.Attachments {
			attachmentIDs = append(attachmentIDs, item.ID)
		}
	}
	present, err := presentIDs(ctx, db, `SELECT id FROM encrypted_message WHERE id IN `, ids)
	if err != nil {
		return nil, err
	}
	deleted, err := presentIDs(ctx, db, `SELECT id FROM deleted_message WHERE id IN `, ids)
	if err != nil {
		return nil, err
	}
	shares, err := presentIDs(ctx, db, `SELECT message FROM encrypted_share WHERE message IN `, ids)
	if err != nil {
		return nil, err
	}
	media, err := presentIDs(ctx, db, `SELECT id FROM encrypted_media WHERE id IN `, mediaIDs)
	if err != nil {
		return nil, err
	}
	attachments, err := presentIDs(ctx, db, `SELECT id FROM message_attachment WHERE id IN `, attachmentIDs)
	if err != nil {
		return nil, err
	}
	plans := make([]*importPlan, 0, len(messages))
	for index := range messages {
		m := &messages[index]
		plan := &importPlan{message: m, media: map[string][][]byte{}, attachments: map[string][][]byte{}, reactions: map[int64][]byte{}}
		plans = append(plans, plan)
		switch {
		case m.ID == "" || m.Thread == 0 || deleted[m.ID]:
			plan.state = planRefused
			continue
		case present[m.ID]:
			plan.state = planExisting
		default:
			plan.state = planInsert
			if plan.sealed, err = protectStrings(m.Body, m.Reply, m.CardText); err != nil {
				return nil, err
			}
		}
		for _, item := range m.Media {
			if item.ID == "" || media[item.ID] {
				continue
			}
			sealed, err := protectStrings(item.Name, item.Mime, item.Kind, item.Link)
			if err != nil {
				return nil, err
			}
			transport, err := protectLocal(item.Transport)
			if err != nil {
				return nil, err
			}
			plan.media[item.ID] = append(sealed, transport)
		}
		for _, item := range m.Attachments {
			if item.ID == "" || attachments[item.ID] {
				continue
			}
			sealed, err := protectStrings(item.Name, item.Mime, item.Source, item.Preview, item.Playable, item.PreviewSource, item.Link, item.Description, item.ActionTitle)
			if err != nil {
				return nil, err
			}
			plan.attachments[item.ID] = sealed
		}
		if len(m.Share) > 0 && !shares[m.ID] {
			if plan.share, err = protectLocal(m.Share); err != nil {
				return nil, err
			}
		}
		// A message already here keeps its own reactions; see the rules above.
		if plan.state == planInsert {
			for _, reaction := range m.Reactions {
				if reaction.Actor == 0 || reaction.Emoji == "" {
					continue
				}
				sealed, err := protectString(reaction.Emoji)
				if err != nil {
					return nil, err
				}
				plan.reactions[reaction.Actor] = sealed
			}
		}
	}
	return plans, nil
}

// Which of these ids the store already has, asked a few hundred at a time.
func presentIDs(ctx context.Context, db *sql.DB, query string, ids []string) (map[string]bool, error) {
	found := map[string]bool{}
	for start := 0; start < len(ids); start += 400 {
		chunk := ids[start:min(start+400, len(ids))]
		args := make([]any, len(chunk))
		for index, id := range chunk {
			args[index] = id
		}
		rows, err := db.QueryContext(ctx, query+"("+strings.TrimSuffix(strings.Repeat("?,", len(chunk)), ",")+")", args...)
		if err != nil {
			return nil, err
		}
		for rows.Next() {
			var id string
			if err := rows.Scan(&id); err != nil {
				rows.Close()
				return nil, err
			}
			found[id] = true
		}
		if err := closeRows(rows); err != nil {
			return nil, err
		}
	}
	return found, nil
}

// One message and what hangs on it, written inside the page's transaction. Call with the state lock held: the
// conversation's cutoff and the revoked ids are read from memory, where the live paths keep them.
func (a *adapter) importMessage(ctx context.Context, tx *sql.Tx, plan *importPlan, sections *backupSections) error {
	m := plan.message
	refuse := func() {
		sections.Messages.Refused++
		sections.Media.Refused += len(m.Media)
		sections.Attachments.Refused += len(m.Attachments)
		sections.Reactions.Refused += len(m.Reactions)
		if len(m.Share) > 0 {
			sections.Shares.Refused++
		}
	}
	if plan.state == planRefused {
		refuse()
		return nil
	}
	plan.key = a.chats.canonicalThread(m.Thread)
	if plan.state == planInsert {
		if cutoff := a.chats.cutoffOf(plan.key); (cutoff > 0 && m.At <= cutoff) || a.chats.isRevoked(m.ID) {
			plan.state = planRefused
			refuse()
			return nil
		}
		written, err := tx.ExecContext(ctx, `INSERT INTO encrypted_message
			(id, thread, sender, body, reply, at, encrypted, ai_updated_at, edited_at, card_text, notice)
			SELECT ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ? WHERE NOT EXISTS (SELECT 1 FROM deleted_message WHERE id = ?)
			ON CONFLICT(id) DO NOTHING`,
			m.ID, plan.key, m.Sender, plan.sealed[0], plan.sealed[1], m.At, m.Encrypted, m.AIUpdatedAt, m.EditedAt, plan.sealed[2], m.Notice, m.ID)
		if err != nil {
			return err
		}
		if count, _ := written.RowsAffected(); count > 0 {
			plan.inserted = true
			sections.Messages.Added++
		} else {
			// Something arrived since the page was planned: a deletion refuses it, anything else means it is here now.
			var gone int
			if err := tx.QueryRowContext(ctx, `SELECT COUNT(*) FROM deleted_message WHERE id = ?`, m.ID).Scan(&gone); err != nil {
				return err
			}
			if gone > 0 {
				plan.state = planRefused
				refuse()
				return nil
			}
			plan.state = planExisting
			sections.Messages.Skipped++
		}
	} else {
		sections.Messages.Skipped++
	}
	for index := range m.Media {
		item := &m.Media[index]
		sealed, ok := plan.media[item.ID]
		if !ok {
			sections.Media.Skipped++
			continue
		}
		written, err := tx.ExecContext(ctx, `INSERT INTO encrypted_media
			(id, message, thread, name, mime, size, width, height, video, animated, kind, transport, at, link)
			VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) ON CONFLICT(id) DO NOTHING`,
			item.ID, m.ID, plan.key, sealed[0], sealed[1], item.Size, item.Width, item.Height, item.Video, item.Animated,
			sealed[2], sealed[4], item.At, sealed[3])
		if err != nil {
			return err
		}
		if count, _ := written.RowsAffected(); count > 0 {
			sections.Media.Added++
			plan.addedMedia = append(plan.addedMedia, item)
		} else {
			sections.Media.Skipped++
		}
	}
	for index := range m.Attachments {
		item := &m.Attachments[index]
		sealed, ok := plan.attachments[item.ID]
		if !ok {
			sections.Attachments.Skipped++
			continue
		}
		written, err := tx.ExecContext(ctx, `INSERT INTO message_attachment
			(id, message, thread, name, mime, size, width, height, video, animated, source, preview, playable, previewSource, link, description, actionTitle)
			VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) ON CONFLICT(id) DO NOTHING`,
			item.ID, m.ID, plan.key, sealed[0], sealed[1], item.Size, item.Width, item.Height, item.Video, item.Animated,
			sealed[2], sealed[3], sealed[4], sealed[5], sealed[6], sealed[7], sealed[8])
		if err != nil {
			return err
		}
		if count, _ := written.RowsAffected(); count > 0 {
			sections.Attachments.Added++
			plan.addedAttachments = append(plan.addedAttachments, item)
		} else {
			sections.Attachments.Skipped++
		}
	}
	if len(m.Share) > 0 {
		if plan.share == nil {
			sections.Shares.Skipped++
		} else {
			written, err := tx.ExecContext(ctx, `INSERT INTO encrypted_share (message, payload) VALUES (?, ?) ON CONFLICT(message) DO NOTHING`,
				m.ID, plan.share)
			if err != nil {
				return err
			}
			if count, _ := written.RowsAffected(); count > 0 {
				sections.Shares.Added++
				plan.addedShare = true
			} else {
				sections.Shares.Skipped++
			}
		}
	}
	for _, reaction := range m.Reactions {
		sealed, ok := plan.reactions[reaction.Actor]
		if !ok || !plan.inserted {
			sections.Reactions.Skipped++
			continue
		}
		written, err := tx.ExecContext(ctx, `INSERT INTO encrypted_reaction (message, actor, thread, emoji) VALUES (?, ?, ?, ?)
			ON CONFLICT(message, actor) DO NOTHING`, m.ID, reaction.Actor, plan.key, sealed)
		if err != nil {
			return err
		}
		if count, _ := written.RowsAffected(); count > 0 {
			sections.Reactions.Added++
		} else {
			sections.Reactions.Skipped++
		}
	}
	return nil
}

// What came back, put back into its conversation the way restore does at sign-in, so it shows without a restart. A
// message this session already holds keeps its own copy and only gains the parts it was missing. Reports whether a
// message was added.
func (a *adapter) showImported(plan *importPlan, shown *importChanges) bool {
	m := plan.message
	fresh := plan.inserted && !a.chats.hasMessage(plan.key, m.ID)
	if fresh {
		// A message put in beneath a reader's face can take the face over, and a receipt waiting for it settles and
		// moves positions (settleWaitingReceipts). The rows either changes are redrawn with it.
		faces := a.chats.facesFrom(plan.key, m.At)
		if touched := a.chats.addEncrypted(plan.key, m.ID, m.Sender, m.Body, m.Reply, m.At, m.Encrypted); touched.empty() {
			fresh = false
		} else {
			for chat, ids := range touched.Receipts {
				if key, err := strconv.ParseInt(chat, 10, 64); err == nil {
					for _, id := range ids {
						shown.receipt(key, id)
					}
				}
			}
			for _, id := range a.chats.facesMoved(plan.key, faces) {
				shown.receipt(plan.key, id)
			}
			if m.AIUpdatedAt > 0 || m.EditedAt > 0 || m.CardText != "" || m.Notice {
				a.chats.mu.Lock()
				if current := a.chats.messages[plan.key][m.ID]; current != nil {
					current.aiUpdatedAt, current.editedAt, current.cardText = m.AIUpdatedAt, m.EditedAt, m.CardText
					current.notice = m.Notice
				}
				a.chats.mu.Unlock()
			}
			// Like everything restored from the store, its pictures are only as good as the urls kept with it.
			a.chats.markFromStore(plan.key)
		}
	}
	changed := fresh
	for _, item := range plan.addedMedia {
		a.chats.attachSecure(plan.key, m.ID, item.At, &attachment{id: item.ID, name: item.Name, mime: item.Mime, size: item.Size,
			width: item.Width, height: item.Height, video: item.Video, animated: item.Animated, link: item.Link},
			&secureMedia{transport: item.Transport, kind: item.Kind})
		changed = true
	}
	for _, item := range plan.addedAttachments {
		a.chats.restoreAttachment(m.ID, &attachment{id: item.ID, name: item.Name, mime: item.Mime, size: item.Size, width: item.Width,
			height: item.Height, video: item.Video, animated: item.Animated, source: item.Source, preview: item.Preview,
			playable: item.Playable, link: item.Link, description: item.Description, actionTitle: item.ActionTitle}, item.PreviewSource)
		changed = true
	}
	if fresh && plan.addedShare {
		var content waArmadilloXMA.ExtendedContentMessage
		if proto.Unmarshal(m.Share, &content) == nil {
			items, text := encryptedShare(&content, m.ID)
			if text == "" {
				text = m.Body
			}
			a.chats.addEncrypted(plan.key, m.ID, m.Sender, text, m.Reply, m.At)
			for _, media := range items {
				if media.item != nil {
					a.chats.attachSecure(plan.key, m.ID, m.At, media.item, media.held)
				}
			}
		}
	}
	if fresh {
		for _, reaction := range m.Reactions {
			if _, kept := plan.reactions[reaction.Actor]; kept {
				a.chats.restoreReaction(m.ID, reaction.Actor, reaction.Emoji)
			}
		}
	}
	if changed {
		shown.message(plan.key, m.ID)
	}
	return fresh
}

// Conversations, people and pairings from a backup, where this computer knows nothing of them. Returns the
// conversations whose deletion cutoff the backup raised, with the messages that took out.
func (s *store) adoptBackupIdentity(page *backupPageIn, result *backupImportResult, shown *importChanges) map[int64][]string {
	s.mu.Lock()
	defer s.mu.Unlock()
	sections := &result.Sections
	for _, contact := range page.Contacts {
		if contact.ID == 0 {
			sections.Contacts.Refused++
			continue
		}
		changed := false
		if contact.Name != "" && s.contacts[contact.ID] == "" {
			s.contacts[contact.ID] = contact.Name
			changed = true
		}
		if contact.Picture != "" && s.pictures[contact.ID] == "" {
			s.pictures[contact.ID] = contact.Picture
			changed = true
		}
		if changed {
			sections.Contacts.Added++
		} else {
			sections.Contacts.Skipped++
		}
	}
	cut := map[int64][]string{}
	for _, saved := range page.Threads {
		if saved.Key == 0 {
			sections.Threads.Refused++
			continue
		}
		// An address this computer already pairs with another conversation is not a conversation of its own here.
		if mapped, ok := s.hybrid[saved.Key]; ok && mapped != saved.Key {
			sections.Threads.Skipped++
			continue
		}
		current, known := s.threads[saved.Key]
		if !known {
			current = s.get(saved.Key)
			current.name, current.picture, current.folder, current.snippet = saved.Name, saved.Picture, saved.Folder, saved.Snippet
			current.kind = table.ThreadType(saved.Kind)
			for _, person := range saved.Participants {
				if id, err := strconv.ParseInt(person, 10, 64); err == nil && id != 0 {
					current.participants[id] = struct{}{}
				}
			}
			current.lastActivity, current.readAt, current.anchorAt, current.anchorID = saved.Activity, saved.ReadAt, saved.AnchorAt, saved.AnchorID
			current.cannotReply, current.deleted, current.unresolved, current.deletedAt = saved.CannotReply, saved.Deleted, saved.Unresolved, saved.DeletedAt
			current.previewOutgoing = saved.PreviewOutgoing
			current.muteUntil = saved.MuteUntil
			sections.Threads.Added++
			if !saved.Deleted {
				shown.chat(saved.Key)
			}
			continue
		}
		sections.Threads.Skipped++
		// Deleted later than this computer knew: what the deletion covered goes here as well.
		if saved.DeletedAt > current.deletedAt {
			current.deletedAt = saved.DeletedAt
			removed := s.cutBefore(saved.Key, saved.DeletedAt)
			cut[saved.Key] = removed
			sections.Threads.Removed += len(removed)
			for _, id := range removed {
				shown.deleted(saved.Key, id)
			}
			shown.chat(saved.Key)
		}
	}
	for _, mapping := range page.Mappings {
		if mapping.JID == 0 || mapping.Thread == 0 {
			sections.Mappings.Refused++
			continue
		}
		// A pairing known here stands. So does a conversation held under its own number here: moving it is what
		// Facebook's pairing row does, and that will arrive on its own. A deleted one with nothing in it is not such a
		// conversation: it is the stand-in a chat started here leaves behind once Facebook pairs it (standIns, retire),
		// which the backup carries beside this very pairing. Without the pairing, that address would lead to the
		// stand-in rather than to the real conversation.
		if _, ok := s.hybrid[mapping.JID]; ok || len(s.messages[mapping.JID]) > 0 {
			sections.Mappings.Skipped++
			continue
		}
		if held := s.threads[mapping.JID]; held != nil && !held.deleted {
			sections.Mappings.Skipped++
			continue
		}
		s.hybrid[mapping.JID] = mapping.Thread
		sections.Mappings.Added++
	}
	return cut
}

// Call with the lock held. Takes out a conversation's messages up to a cutoff, with what hangs on them, and returns
// their ids. The stored copies go when the identity is next saved, which deletes everything at or before the cutoff.
func (s *store) cutBefore(key int64, cutoff int64) []string {
	var removed []string
	for id, held := range s.messages[key] {
		if held.timestamp > cutoff {
			continue
		}
		for _, item := range s.attached[id] {
			delete(s.media, item.id)
			delete(s.files, item.id)
		}
		delete(s.attached, id)
		delete(s.reactions, id)
		delete(s.shares, shareMessageKey{key, id})
		delete(s.messages[key], id)
		removed = append(removed, id)
	}
	sort.Strings(removed)
	return removed
}

// A message the backup says was taken back, wherever this session holds it. Revoked either way, so a late delivery
// cannot bring it back.
func (s *store) forgetDeleted(id string) (int64, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.revokeID(id)
	key, _, found := s.messageAnywhere(id)
	if found {
		delete(s.messages[key], id)
		delete(s.shares, shareMessageKey{key, id})
	}
	for _, item := range s.attached[id] {
		delete(s.media, item.id)
		delete(s.files, item.id)
	}
	delete(s.attached, id)
	delete(s.reactions, id)
	return key, found
}

// The conversation a stored row belongs to here: a backup may name an encrypted address this computer has since
// paired with its conversation.
func (s *store) canonicalThread(key int64) int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	if mapped, ok := s.hybrid[key]; ok && mapped != 0 {
		return mapped
	}
	return key
}

// Where a kept position from a backup goes here: its conversation as this computer numbers it, and only one this
// computer holds and has not deleted, as restoreReceipts places one at sign-in. A position is not reason enough to
// list a conversation, and this account's own reading is not a receipt. Where a reading started is a moment before
// it, or not known (-1); anything else is taken as not known, with no time, so it claims nothing.
func (s *store) backupReceiptTarget(receipt backupReceipt) (backupReceipt, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if mapped, ok := s.hybrid[receipt.Thread]; ok && mapped != 0 {
		receipt.Thread = mapped
	}
	current, held := s.threads[receipt.Thread]
	if !held || current.deleted || receipt.Thread == 0 || receipt.Contact == 0 || s.self != 0 && receipt.Contact == s.self ||
		receipt.Read < 0 || receipt.Delivered < 0 || receipt.Read == 0 && receipt.Delivered == 0 {
		return receipt, false
	}
	if receipt.ReadFrom <= 0 || receipt.ReadFrom >= receipt.Read || receipt.ReadAt < 0 {
		receipt.ReadFrom, receipt.ReadAt = -1, 0
	}
	return receipt, true
}

// A kept position from a backup, into memory after its row is written: each position only moves forward, and a read
// time comes with the reading it belongs to, as restoreReceipts has it. Returns the rows whose readers, faces or
// delivery changed, and whether anything moved; ok is false when the conversation has gone since.
func (s *store) adoptBackupReceipt(receipt backupReceipt) (moved []string, advanced, ok bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	key, contact := receipt.Thread, receipt.Contact
	current, held := s.threads[key]
	if !held || current.deleted {
		return nil, false, false
	}
	book := s.book()
	book.secure[key] = true
	if receipt.Read > current.readBy[contact] {
		moved = s.advanceRead(key, contact, receipt.Read)
		mark := s.markOf(key, contact)
		mark.readFrom, mark.readAt = receipt.ReadFrom, receipt.ReadAt
		advanced = true
	}
	delivered := int64(0)
	if mark := book.marks[key][contact]; mark != nil {
		delivered = mark.delivered
	}
	if receipt.Delivered > delivered {
		for _, id := range s.advanceDelivered(key, contact, receipt.Delivered) {
			if !slices.Contains(moved, id) {
				moved = append(moved, id)
			}
		}
		advanced = true
	}
	return moved, advanced, true
}

func (s *store) cutoffOf(key int64) int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	if current := s.threads[key]; current != nil {
		return current.deletedAt
	}
	return 0
}

func (s *store) isRevoked(id string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.revoked[id]
}

func (s *store) hasMessage(key int64, id string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.messages[key][id] != nil
}

// What an import changed, by conversation, for one "changed" event at the end of the page. Receipts are rows whose only
// change is who has received or read them, which is not a change to their conversation's place in the list.
type importChanges struct {
	chats    map[int64]bool
	messages map[int64]map[string]bool
	gone     map[int64]map[string]bool
	receipts map[int64]map[string]bool
}

func (c *importChanges) chat(key int64) {
	if c.chats == nil {
		c.chats = map[int64]bool{}
	}
	c.chats[key] = true
}

func (c *importChanges) mark(into *map[int64]map[string]bool, key int64, id string) {
	c.chat(key)
	if *into == nil {
		*into = map[int64]map[string]bool{}
	}
	if (*into)[key] == nil {
		(*into)[key] = map[string]bool{}
	}
	(*into)[key][id] = true
}

func (c *importChanges) message(key int64, id string) { c.mark(&c.messages, key, id) }
func (c *importChanges) deleted(key int64, id string) { c.mark(&c.gone, key, id) }

func (c *importChanges) receipt(key int64, id string) {
	if c.receipts == nil {
		c.receipts = map[int64]map[string]bool{}
	}
	if c.receipts[key] == nil {
		c.receipts[key] = map[string]bool{}
	}
	c.receipts[key][id] = true
}

func (c *importChanges) changes() *changes {
	touched := &changes{}
	keys := make([]int64, 0, len(c.chats)+len(c.receipts))
	for key := range c.chats {
		keys = append(keys, key)
	}
	for key := range c.receipts {
		if !c.chats[key] {
			keys = append(keys, key)
		}
	}
	sort.Slice(keys, func(i, j int) bool { return keys[i] < keys[j] })
	sorted := func(set map[string]bool) []string {
		ids := make([]string, 0, len(set))
		for id := range set {
			ids = append(ids, id)
		}
		sort.Strings(ids)
		return ids
	}
	for _, key := range keys {
		chat := strconv.FormatInt(key, 10)
		if c.chats[key] {
			touched.Chats = append(touched.Chats, chat)
		}
		// A row redrawn as new or gone is not announced again as a receipt.
		receipts := map[string]bool{}
		for id := range c.receipts[key] {
			if !c.messages[key][id] && !c.gone[key][id] {
				receipts[id] = true
			}
		}
		if len(receipts) > 0 {
			if touched.Receipts == nil {
				touched.Receipts = map[string][]string{}
			}
			touched.Receipts[chat] = sorted(receipts)
		}
		if set := c.messages[key]; len(set) > 0 {
			if touched.Messages == nil {
				touched.Messages = map[string][]string{}
			}
			touched.Messages[chat] = sorted(set)
		}
		if set := c.gone[key]; len(set) > 0 {
			if touched.Deleted == nil {
				touched.Deleted = map[string][]string{}
			}
			touched.Deleted[chat] = sorted(set)
		}
	}
	return touched
}
