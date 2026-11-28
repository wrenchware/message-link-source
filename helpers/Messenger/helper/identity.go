package main

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"slices"
	"strconv"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	waTypes "go.mau.fi/whatsmeow/types"
)

// Identity is independent of history. In particular, a deleted thread and its encrypted address must survive while
// remaining absent from ui.chats/ui.chat. No message text or identifiers are written to diagnostics.
type savedThread struct {
	Key                              int64
	Name, Picture, Folder, Snippet   string
	Kind                             table.ThreadType
	Participants                     map[int64]struct{}
	Activity, ReadAt, AnchorAt       int64
	AnchorID                         string
	CannotReply, Deleted, Unresolved bool
	DeletedAt                        int64
	PreviewOutgoing                  bool
	// The mute Messenger reports (thread.muteUntil). Left out when there is none, so a row written before it existed
	// digests the same and is not rewritten for it.
	MuteUntil int64 `json:",omitempty"`
}

// How long an apply has to take before it is worth a line of its own. Every socket batch comes through here, so this
// stays quiet for the ordinary ones and speaks up for the one that holds the helper: a lifecycle call runs inline on
// the reader loop, so while login's first apply runs the helper cannot read or answer anything at all.
const slowApply = 500 * time.Millisecond

func (a *adapter) applyTable(ctx context.Context, tbl *table.LSTable) *changes {
	a.state.Lock()
	defer a.state.Unlock()
	entered := time.Now()
	touched := a.chats.apply(tbl)
	applied := time.Now()
	var kept keepCost
	defer func() {
		if total := time.Since(entered); total >= slowApply {
			// messages= used to count the conversations with touched messages, not the messages, which is how a
			// fifteen-second write came to be read as eighteen rows. ids= is the number of messages.
			logf("apply slow total=%dms rows=%dms kept=%dms identity=%dms deletions=%dms messages=%dms chats=%d ids=%d identityWrites=%d",
				total.Milliseconds(), applied.Sub(entered).Milliseconds(), time.Since(applied).Milliseconds(),
				kept.identity.Milliseconds(), kept.deletions.Milliseconds(), kept.messages.Milliseconds(),
				len(touched.Chats), kept.ids, kept.identityWrites)
		}
	}()
	if tbl != nil {
		active := map[int64]bool{}
		for rawKey := range activeThreadRows(tbl) {
			key := rawKey
			if mapped, ok := a.chats.threadForJID(rawKey); ok {
				key = mapped
			}
			active[key] = true
		}
		removed := map[int64]bool{}
		for _, rawKey := range deletedThreadKeys(tbl) {
			key := rawKey
			if mapped, ok := a.chats.threadForJID(rawKey); ok {
				key = mapped
			}
			if active[key] {
				continue
			}
			if removed[key] {
				continue
			}
			removed[key] = true
			a.forgetChatLocked(ctx, key)
			a.noteThreadDeletion(key)
			id := strconv.FormatInt(key, 10)
			delete(touched.Messages, id)
			delete(touched.Deleted, id)
			delete(touched.Receipts, id)
			delete(touched.History, id)
			delete(touched.cards, id)
			touched.Chats = slices.DeleteFunc(touched.Chats, func(value string) bool { return value == id })
		}
	}
	kept = a.keepMessages(ctx, touched)
	a.keepReceipts(ctx)
	// After the save, so a move confirmed to the app is already the folder a restart reads back.
	a.noteFolderMoves(tbl)
	return touched
}

func (s *store) visible(t *thread) bool {
	if t.deleted {
		return false
	}
	if !t.unresolved || t.name != "" {
		return true
	}
	for id := range t.participants {
		if id != s.self && s.contacts[id] != "" {
			return true
		}
	}
	return false
}

// Why ui.chat holds no such conversation, as one line of counts and states. Four conversations the server asks for at
// every start were refused this way from 0.1.445 on without anyone telling which of visible's reasons it was. server= is
// the tag the server's own lines give the conversation, so the two logs can be matched. paired says the key is an
// encrypted address another conversation was paired with (its messages moved there); pairs says it is one.
func (s *store) refusal(key int64) string {
	s.mu.Lock()
	defer s.mu.Unlock()
	current, held := s.threads[key]
	reason := "absent"
	if held {
		reason = "nameless"
		if current.deleted {
			reason = "deleted"
		}
	}
	_, paired := s.hybrid[key]
	pairs := false
	for _, mapped := range s.hybrid {
		if mapped == key {
			pairs = true
			break
		}
	}
	line := fmt.Sprintf("ui.chat refused chat=%s server=%s reason=%s paired=%t pairs=%t messages=%d",
		tag(key), serverTag(key), reason, paired, pairs, len(s.messages[key]))
	if !held {
		return line
	}
	known := 0
	for id := range current.participants {
		if id != s.self && s.contacts[id] != "" {
			known++
		}
	}
	folder := current.folder
	if folder != "" && folder != "inbox" && folder != "archived" {
		folder = "other"
	}
	return line + fmt.Sprintf(" kind=%d unresolved=%t named=%t participants=%d known=%d activity=%t cutoff=%t folder=%s",
		current.kind, current.unresolved, current.name != "", len(current.participants), known, current.lastActivity > 0,
		current.deletedAt > 0, folder)
}

// The pinned upstream bridge also uses GetGroupInfo when an encrypted group arrives without ordinary metadata.
// One bounded read per group per helper session; failed requests retain the message for later metadata/restart.
func (a *adapter) recoverGroupIdentity(jid int64, address waTypes.JID) {
	if address.Server != waTypes.GroupServer {
		return
	}
	key, ok := a.chats.threadForJID(jid)
	if !ok {
		return
	}
	a.chats.mu.Lock()
	t := a.chats.threads[key]
	missing := t != nil && !t.deleted && t.name == "" && len(t.participants) == 0
	a.chats.mu.Unlock()
	if !missing {
		return
	}
	a.mu.Lock()
	getter := a.groupInfo
	if getter == nil && a.crypto != nil && a.crypto.client != nil {
		getter = a.crypto.client.GetGroupInfo
	}
	if getter == nil || a.identityRequests[jid] {
		a.mu.Unlock()
		return
	}
	if a.identityRequests == nil {
		a.identityRequests = map[int64]bool{}
	}
	a.identityRequests[jid] = true
	a.mu.Unlock()
	go func() {
		ctx, cancel := context.WithTimeout(a.lifetime, 10*time.Second)
		defer cancel()
		info, err := getter(ctx, address)
		if err != nil || info == nil {
			logf("encrypted group identity unavailable")
			return
		}
		a.state.Lock()
		defer a.state.Unlock()
		// A pairing or deletion may have arrived while the read was in flight. Use the current key and never revive.
		key, ok := a.chats.threadForJID(jid)
		if !ok {
			return
		}
		a.chats.mu.Lock()
		t := a.chats.threads[key]
		if t == nil || t.deleted || t.name != "" || len(t.participants) != 0 {
			a.chats.mu.Unlock()
			return
		}
		t.name = info.Name
		for _, person := range info.Participants {
			id, err := strconv.ParseInt(person.JID.User, 10, 64)
			if err == nil && id != 0 {
				t.participants[id] = struct{}{}
			}
		}
		a.chats.mu.Unlock()
		a.kept.saveIdentity(ctx, a.chats)
		if a.out != nil {
			a.out.write(response{Event: "changed", Result: &changes{Chats: []string{strconv.FormatInt(key, 10)}}})
		}
	}()
}

// The encrypted envelope proves the transport and group/direct type even if no ordinary thread row was resent.
// A direct chat JID names the peer (including for our own outgoing echoes); a group sender does not name its group.
func (s *store) encryptedIdentity(key, jid int64, server string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	t := s.get(key)
	if t.kind == 0 {
		t.unresolved = true
	}
	switch server {
	case waTypes.GroupServer:
		t.kind = table.ENCRYPTED_OVER_WA_GROUP
	case waTypes.MessengerServer, waTypes.DefaultUserServer:
		t.kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
		t.participants[jid] = struct{}{}
		if s.self != 0 {
			t.participants[s.self] = struct{}{}
		}
	}
}

// Hold the store lock through the transaction so older snapshots cannot overwrite a concurrent deletion/new message.
// Reports how many rows it actually wrote, so the plaintext memo below can be seen working rather than assumed to be.
func (m *messageStore) saveIdentity(ctx context.Context, s *store) (written int) {
	written, _ = m.saveIdentityChecked(ctx, s)
	return written
}

// The same, saying whether the transaction committed: an import must fail a page whose threads and contacts did not reach the store, not count them.
func (m *messageStore) saveIdentityChecked(ctx context.Context, s *store) (written int, committed bool) {
	if m == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	m.purgeMu.Lock()
	writesBefore := m.writes
	m.purgeMu.Unlock()
	tx, err := m.db.BeginTx(ctx, nil)
	if err != nil {
		logf("thread identity could not be saved")
		return
	}
	defer func() {
		_ = tx.Rollback()
		if err != nil {
			logf("thread identity could not be saved")
		}
	}()
	write := func(query string, args ...any) bool { _, err = tx.ExecContext(ctx, query, args...); return err == nil }
	// Rows whose plaintext moved, remembered only once the transaction commits: a rolled back one must not be able
	// to convince the next call that it already wrote them.
	threads, contacts := map[int64][32]byte{}, map[int64][32]byte{}
	// Stand-in rows deleted below. Their memo goes with them, or an address that reappeared with the same content
	// would be taken as already written to a row that no longer exists.
	var dropped []int64
	// Conversations purged below, and those a remap just moved messages into, which have to be purged whatever the memo says.
	purged, remapped := map[int64]int64{}, map[int64]bool{}
	for key, t := range s.threads {
		data, e := json.Marshal(savedThread{key, t.name, t.picture, t.folder, t.snippet, t.kind, t.participants,
			t.lastActivity, t.readAt, t.anchorAt, t.anchorID, t.cannotReply, t.deleted, t.unresolved, t.deletedAt, t.previewOutgoing, t.muteUntil})
		if e != nil {
			return
		}
		digest := sha256.Sum256(data)
		if held, ok := m.savedThreads[key]; ok && held == digest {
			continue
		}
		if data, e = protectLocal(data); e != nil {
			return
		}
		if !write(`INSERT INTO thread_identity (thread, identity) VALUES (?, ?)
			ON CONFLICT(thread) DO UPDATE SET identity = excluded.identity WHERE identity != excluded.identity`, key, data) {
			return
		}
		threads[key] = digest
		written++
	}
	// One memo covers both loops below, because both write thread_contact. The kind goes into the digest so a
	// picture-only row that gains a name, or loses one, is always seen to have changed.
	for id, name := range s.contacts {
		digest := namedContactDigest(name, s.pictures[id])
		if held, ok := m.savedContacts[id]; ok && held == digest {
			continue
		}
		sealed, e := protectStrings(name, s.pictures[id])
		if e != nil {
			err = e
			return
		}
		upsert := `INSERT INTO thread_contact (id, name, picture) VALUES (?, ?, ?)
			ON CONFLICT(id) DO UPDATE SET name = excluded.name, picture = excluded.picture
			WHERE name != excluded.name OR picture != excluded.picture`
		if s.pictures[id] == "" {
			// Nothing clears a picture in memory (remember and a backup import only fill one), so an empty one is a picture
			// never loaded, such as a row whose picture did not open at login. The row keeps the picture it holds.
			upsert = `INSERT INTO thread_contact (id, name, picture) VALUES (?, ?, ?)
				ON CONFLICT(id) DO UPDATE SET name = excluded.name WHERE name != excluded.name`
		}
		if !write(upsert, id, sealed[0], sealed[1]) {
			return
		}
		contacts[id] = digest
		written++
	}
	for id, picture := range s.pictures {
		if _, exists := s.contacts[id]; !exists {
			digest := pictureContactDigest(picture)
			if held, ok := m.savedContacts[id]; ok && held == digest {
				continue
			}
			sealed, e := protectStrings("", picture)
			if e != nil {
				err = e
				return
			}
			if !write(`INSERT INTO thread_contact (id, name, picture) VALUES (?, ?, ?)
				ON CONFLICT(id) DO UPDATE SET picture = excluded.picture WHERE picture != excluded.picture`, id, sealed[0], sealed[1]) {
				return
			}
			contacts[id] = digest
			written++
		}
	}
	for jid, key := range s.hybrid {
		result, e := tx.ExecContext(ctx, `INSERT INTO thread_mapping (jid, thread) VALUES (?, ?)
			ON CONFLICT(jid) DO UPDATE SET thread = excluded.thread WHERE thread != excluded.thread`, jid, key)
		if e != nil {
			err = e
			return
		}
		if jid != key && s.threads[jid] == nil {
			if !write(`DELETE FROM thread_identity WHERE thread = ?`, jid) {
				return
			}
			dropped = append(dropped, jid)
		}
		changed, _ := result.RowsAffected()
		if jid == key || changed == 0 {
			continue
		}
		// Move every persisted part of a message that arrived before its pairing. Updating body alone used to leave
		// ON CONFLICT(id) holding that message permanently under the temporary address after a restart.
		for _, name := range []string{"encrypted_message", "encrypted_media", "message_attachment", "encrypted_reaction", "deleted_message"} {
			if !write(`UPDATE `+name+` SET thread = ? WHERE thread = ?`, key, jid) {
				return
			}
		}
		remapped[key] = true
	}
	for key, t := range s.threads {
		if t.deletedAt == 0 || !remapped[key] && m.purgedThrough(key, t.deletedAt) {
			continue
		}
		for _, statement := range []string{
			`DELETE FROM encrypted_share WHERE message IN (SELECT id FROM encrypted_message WHERE thread = ? AND at <= ?)`,
			`DELETE FROM message_attachment WHERE message IN (SELECT id FROM encrypted_message WHERE thread = ? AND at <= ?)`,
			`DELETE FROM encrypted_reaction WHERE message IN (SELECT id FROM encrypted_message WHERE thread = ? AND at <= ?)`,
			`DELETE FROM encrypted_media WHERE thread = ? AND at <= ?`,
			`DELETE FROM encrypted_message WHERE thread = ? AND at <= ?`,
		} {
			if !write(statement, key, t.deletedAt) {
				return
			}
		}
		purged[key] = t.deletedAt
	}
	if tx.Commit() != nil {
		logf("thread identity could not be saved")
		return
	}
	m.purgeMu.Lock()
	if m.writes == writesBefore {
		if m.purged == nil {
			m.purged = map[int64]int64{}
		}
		for key, at := range purged {
			m.purged[key] = at
		}
	}
	m.purgeMu.Unlock()
	if m.savedThreads == nil {
		m.savedThreads, m.savedContacts = map[int64][32]byte{}, map[int64][32]byte{}
	}
	for key, digest := range threads {
		m.savedThreads[key] = digest
	}
	for id, digest := range contacts {
		m.savedContacts[id] = digest
	}
	for _, jid := range dropped {
		delete(m.savedThreads, jid)
	}
	committed = true
	return
}

// What a contact row holds, digested the one way both saveIdentity's memo and restoreIdentity's seed use.
func namedContactDigest(name, picture string) [32]byte {
	return sha256.Sum256([]byte("named\x00" + name + "\x00" + picture))
}

func pictureContactDigest(picture string) [32]byte {
	return sha256.Sum256([]byte("picture\x00" + picture))
}

func (m *messageStore) restoreIdentity(ctx context.Context, s *store) {
	if m == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	// The memo saveIdentity consults starts out as what each row holds now. Left empty, the first apply after every
	// login re-sealed and rewrote each conversation and contact it had just read back unchanged, which was most of a
	// cold start's 1.5 s first apply. Each seed is the row's own decoded plaintext and never the merged state in s,
	// which can already hold more than the row (a second login in one process): a seed that claimed the row held that
	// would skip a write the row still needs. A row that does not open is not seeded, and is rewritten as before.
	if m.savedThreads == nil {
		m.savedThreads, m.savedContacts = map[int64][32]byte{}, map[int64][32]byte{}
	}
	seededThreads, seededContacts := 0, 0
	defer func() { logf("kept identity seeded threads=%d contacts=%d", seededThreads, seededContacts) }()
	rows, err := m.db.QueryContext(ctx, `SELECT thread, identity FROM thread_identity`)
	if err != nil {
		return
	}
	for rows.Next() {
		var key int64
		var data []byte
		var t savedThread
		if rows.Scan(&key, &data) != nil {
			continue
		}
		plain, openErr := unprotectLocal(data)
		if openErr != nil || json.Unmarshal(plain, &t) != nil || t.Key == 0 {
			continue
		}
		// saveIdentity keys by the conversation and digests exactly these marshalled bytes. A row written by an older
		// shape of savedThread digests differently and is rewritten once, which costs a write and never skips one.
		if key == t.Key {
			m.savedThreads[key] = sha256.Sum256(plain)
			seededThreads++
		}
		current := s.get(t.Key)
		current.name, current.picture, current.folder, current.snippet = t.Name, t.Picture, t.Folder, t.Snippet
		current.kind = t.Kind
		if t.Participants != nil {
			current.participants = t.Participants
		}
		current.lastActivity, current.readAt, current.anchorAt, current.anchorID = t.Activity, t.ReadAt, t.AnchorAt, t.AnchorID
		current.cannotReply, current.deleted, current.unresolved, current.deletedAt = t.CannotReply, t.Deleted, t.Unresolved, t.DeletedAt
		current.previewOutgoing = t.PreviewOutgoing
		current.muteUntil = t.MuteUntil
	}
	rows.Close()
	rows, err = m.db.QueryContext(ctx, `SELECT id, name, picture FROM thread_contact`)
	if err != nil {
		return
	}
	for rows.Next() {
		var id int64
		var protectedName, protectedPicture []byte
		if rows.Scan(&id, &protectedName, &protectedPicture) == nil {
			name, nameErr := unprotectString(protectedName)
			picture, pictureErr := unprotectString(protectedPicture)
			if nameErr != nil || pictureErr != nil {
				// The field that opened is kept; the other stays on disk as it is, since saveIdentity writes neither an
				// empty name nor an empty picture over a row. Not seeded, so the opened field is written back once.
				if nameErr != nil {
					name = ""
				}
				if pictureErr != nil {
					picture = ""
				}
				s.remember(id, name, "", picture)
				continue
			}
			// The same digest whichever of saveIdentity's loops will meet this contact: remember below files a named
			// row under contacts, and a nameless one under pictures alone.
			if name != "" {
				m.savedContacts[id] = namedContactDigest(name, picture)
			} else {
				m.savedContacts[id] = pictureContactDigest(picture)
			}
			seededContacts++
			s.remember(id, name, "", picture)
		}
	}
	rows.Close()
	rows, err = m.db.QueryContext(ctx, `SELECT jid, thread FROM thread_mapping`)
	if err != nil {
		return
	}
	for rows.Next() {
		var jid, key int64
		if rows.Scan(&jid, &key) == nil {
			s.hybrid[jid] = key
		}
	}
	rows.Close()
}
