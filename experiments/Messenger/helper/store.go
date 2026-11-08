package main

import (
	"fmt"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// Messenger sends state as incremental table rows rather than a conversation list, so the helper keeps the current
// threads itself. Identity and current summaries are also kept in messages.db across helper restarts.
type thread struct {
	key          int64
	name         string
	snippet      string
	lastActivity int64
	readAt       int64
	// Whether the message represented by snippet/lastActivity was sent by this account.
	// Facebook can advance the thread activity a few milliseconds beyond the message itself;
	// retaining the direction prevents that harmless skew from making an outgoing thread unread.
	previewOutgoing bool
	kind            table.ThreadType
	folder          string
	picture         string
	cannotReply     bool
	participants    map[int64]struct{}
	// How far each person has read, as the timestamp of the newest message they have seen. Facebook reports this as a
	// watermark per contact on the ordinary connection, and as named messages on the encrypted one; both land here.
	readBy map[int64]int64
	// The oldest message Facebook has named for this conversation. Asking for history needs a real message to page
	// back from, and for a conversation no message has arrived for, this is the only one we are given.
	anchorAt int64
	anchorID string
	// Deletion removes history, not identity. The cutoff also rejects delayed/replayed pre-deletion messages.
	deletedAt int64
	deleted   bool
	// An encrypted message can precede all metadata. Keep it, but wait for identity before showing a nameless row.
	unresolved bool
	// How this account muted the conversation in Messenger, on whatever device it did: 0 not muted, -1 until turned off,
	// otherwise when the mute ends (Unix ms). Received only; nothing here sends a mute (see groupstate.go).
	muteUntil int64
	// Who Messenger says can manage a group, and whether a new member waits for one of them to approve. They decide
	// which group changes this account is offered (see groupstate.go).
	admins           map[int64]struct{}
	approvalRequired bool
	// The last watermark Facebook sent below this copy's own, so each one is counted once (readDrift).
	lowerNoted int64
}

type message struct {
	id        string
	encrypted bool
	otid      string
	sender    int64
	text      string
	timestamp int64
	replyTo   string
	unsent    bool
	status    int64
	// When the words were last changed, if they have been. Facebook and the encrypted connection both report an edit
	// separately from the message, so this is set by that report rather than by anything the message itself carries.
	editedAt int64
	// Envelope timestamp of the last accepted AI text delivery, separate from row placement.
	aiUpdatedAt int64
	// Supplied title of an ordinary text-only AI card, separate from provider body text.
	cardText string
}

// What a batch of rows changed, so the app can refresh just those conversations rather than reloading everything.
// Ids only: no names, text or urls cross the pipe in an event.
type changes struct {
	Chats    []string            `json:"chats,omitempty"`
	Messages map[string][]string `json:"messages,omitempty"`
	Deleted  map[string][]string `json:"deleted,omitempty"`
	// Whole conversations deleted, here or on another of the account's devices.
	ChatsDeleted []string `json:"chatsDeleted,omitempty"`
	// Messages whose only change is who has received or read them. Redrawn like the rest, but not new messages, and
	// not a change to their conversation's place in the list.
	Receipts map[string][]string `json:"receipts,omitempty"`
	// Subset of the changed ids supplied by a history snapshot. Keep them in Messages/Deleted for redraw and
	// persistence, while allowing the app to learn their current state without announcing it as live activity.
	History map[string][]string `json:"history,omitempty"`
}

// Lightspeed upserts supply fetched history, including reactions bundled with those messages (the pinned upstream
// WrapMessages attaches them to its UpsertMessages). Inserts and explicit mutations remain live, even in a mixed
// table or on an old message. No age or conversation-wide refresh window is used to guess a reaction's origin.
func (c *changes) markHistory(tbl *table.LSTable) {
	if len(tbl.LSUpsertMessage) == 0 {
		return
	}
	historical := map[string]bool{}
	for _, row := range tbl.LSUpsertMessage {
		historical[row.MessageId] = true
	}
	for _, row := range tbl.LSInsertMessage {
		delete(historical, row.MessageId)
	}
	for _, row := range tbl.LSEditMessage {
		delete(historical, row.MessageID)
	}
	for _, row := range tbl.LSDeleteMessage {
		delete(historical, row.MessageId)
	}
	for _, row := range tbl.LSDeleteThenInsertMessage {
		delete(historical, row.MessageId)
	}
	for _, changed := range []map[string][]string{c.Messages, c.Deleted} {
		for chat, ids := range changed {
			for _, id := range ids {
				if id == "" || !historical[id] {
					continue
				}
				if c.History == nil {
					c.History = map[string][]string{}
				}
				c.History[chat] = append(c.History[chat], id)
			}
		}
	}
}

func (c *changes) empty() bool {
	return len(c.Chats) == 0 && len(c.Messages) == 0 && len(c.Deleted) == 0 && len(c.ChatsDeleted) == 0 && len(c.Receipts) == 0
}

type store struct {
	mu       sync.Mutex
	self     int64
	threads  map[int64]*thread
	contacts map[int64]string
	pictures map[int64]string
	messages map[int64]map[string]*message
	// Revoked stanza IDs remain rejected even if a delayed encrypted delivery arrives.
	revoked  map[string]bool
	media    map[string]*attachment
	attached map[string][]*attachment
	// Where an encrypted conversation's files can be fetched from, by attachment id. Those do not arrive with a url
	// the way ordinary ones do: what arrives is the place and the key, and the file is fetched only when asked for.
	files map[string]*secureMedia
	// Rich attachment rows belong to a message; items do not require a container to be drawable.
	shares map[shareMessageKey]*shareRows
	// Who reacted to a message with what, by message id and then by person. One reaction each, which is how Messenger
	// works, and kept apart from the message because a reaction arrives whether or not its message is known here.
	reactions map[string]map[int64]string
	// Conversations holding messages that came back from the local store rather than from Facebook this session. That
	// copy keeps a message's text but not its pictures, so such a conversation has to be asked for once before its
	// history can be trusted to be complete.
	fromStore map[int64]bool
	// How many batches have touched each conversation, so a request can tell that Facebook's answer has landed even
	// when the answer changes no counts.
	rev map[int64]int
	// Which pages of a restored conversation have already been asked of Facebook, by the cursor each page ends at.
	pages map[int64]map[int64]bool
	// How many background refreshes of each such page have finished without being cancelled, covered or not. The first
	// one decides whether the app still shows the page as updating; the count bounds how often an uncovered page is
	// asked of Facebook again in one helper session.
	refreshTries map[int64]map[int64]int
	// Offline threading id of a message this account sent, to the id Facebook gave it.
	sent map[string]string
	// People whose picture has already been asked for, so one without a picture is not asked for again.
	tried map[int64]bool
	// An encrypted conversation arrives on the other connection under a different number; this is Facebook's own
	// pairing of the two, so its messages join the conversation the list already shows.
	hybrid map[int64]int64
	// How many pairing rows have arrived for each encrypted address this session. Starting a direct chat watches this
	// to tell Facebook's answer from a pairing that was already known before it asked.
	pairings map[int64]int
	// Whether the encrypted connection is up, which decides whether encrypted conversations can be carried at all.
	secure bool
	// Delivery positions, read times and encrypted receipts still waiting for their message (receiptbook.go).
	receipts *receiptBook
}

type attachment struct {
	id       string
	name     string
	mime     string
	source   string
	preview  string
	playable string
	animated bool
	size     int64
	width    int64
	height   int64
	video    bool
	// Whether the picture stands for something that plays — a shared reel or video. The app draws a play badge on it;
	// there is still nothing to play in the app itself, so following it is what the badge promises.
	play bool
	// Where a shared reel, post or link leads. Set only for those: they have a picture and a destination but nothing
	// to play, and the app opens the destination in the browser the user is already signed in to.
	link string
	// What the linked page says about itself, for a preview of a link.
	description string
	// What the service's button on the card says ("View listing"), in the service's own words.
	actionTitle string
}

func newStore() *store {
	return &store{threads: map[int64]*thread{}, contacts: map[int64]string{}, pictures: map[int64]string{},
		messages: map[int64]map[string]*message{}, media: map[string]*attachment{}, attached: map[string][]*attachment{},
		files: map[string]*secureMedia{}, shares: map[shareMessageKey]*shareRows{}, reactions: map[string]map[int64]string{}, fromStore: map[int64]bool{}, rev: map[int64]int{}, pages: map[int64]map[int64]bool{}, refreshTries: map[int64]map[int64]int{},
		sent: map[string]string{}, tried: map[int64]bool{}, hybrid: map[int64]int64{}, pairings: map[int64]int{}}
}

// People in the most recent conversations whose picture Facebook has not sent, newest conversations first. Asking is
// what fills the avatars in: a participant row carries a name but never a picture.
func (s *store) missingPictures(limit int) []int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	known := make([]*thread, 0, len(s.threads))
	for _, current := range s.threads {
		known = append(known, current)
	}
	sort.Slice(known, func(i, j int) bool { return known[i].lastActivity > known[j].lastActivity })
	missing := make([]int64, 0, limit)
	seen := map[int64]bool{}
	for _, current := range known {
		ids := make([]int64, 0, len(current.participants))
		for id := range current.participants {
			ids = append(ids, id)
		}
		sort.Slice(ids, func(i, j int) bool { return ids[i] < ids[j] })
		for _, id := range ids {
			if id == 0 || id == s.self || seen[id] || s.tried[id] || (s.pictures[id] != "" && s.contacts[id] != "") {
				continue
			}
			seen[id] = true
			missing = append(missing, id)
			if len(missing) >= limit {
				return missing
			}
		}
	}
	return missing
}

func (s *store) markTried(ids []int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, id := range ids {
		s.tried[id] = true
	}
}

// Call with the lock held. Turns the ids a batch touched into the order the app receives them in.
func (s *store) collect(touched *changes, chats map[int64]bool, messages, removed, seen map[int64]map[string]bool) {
	keys := make([]int64, 0, len(chats))
	for key := range chats {
		keys = append(keys, key)
	}
	sort.Slice(keys, func(i, j int) bool { return keys[i] < keys[j] })
	sorted := func(known map[string]bool) []string {
		ids := make([]string, 0, len(known))
		for id := range known {
			ids = append(ids, id)
		}
		sort.Strings(ids)
		return ids
	}
	for _, key := range keys {
		chat := strconv.FormatInt(key, 10)
		touched.Chats = append(touched.Chats, chat)
		if known, ok := messages[key]; ok && len(known) > 0 {
			// Only message and attachment rows move the revision on. A request waiting for Facebook's answer has no
			// other way to tell that it landed — the answer is rows for messages already known, which changes no
			// count — but counting anything about the conversation was too loose: a read watermark arriving on its
			// own was enough to report an answer that had not come.
			s.rev[key]++
			if touched.Messages == nil {
				touched.Messages = map[string][]string{}
			}
			touched.Messages[chat] = sorted(known)
		}
		if gone, ok := removed[key]; ok && len(gone) > 0 {
			if touched.Deleted == nil {
				touched.Deleted = map[string][]string{}
			}
			touched.Deleted[chat] = sorted(gone)
		}
	}
	// A message somebody has now received or read is redrawn, but that is not an answer to anything this end asked
	// for, so it leaves the revision alone; and it is not a new message, nor a change to the conversation's summary, so
	// it travels apart from both. A message that also changed in this batch is already being redrawn as one.
	readKeys := make([]int64, 0, len(seen))
	for key := range seen {
		readKeys = append(readKeys, key)
	}
	sort.Slice(readKeys, func(i, j int) bool { return readKeys[i] < readKeys[j] })
	for _, key := range readKeys {
		chat := strconv.FormatInt(key, 10)
		for _, id := range sorted(seen[key]) {
			if messages[key][id] || removed[key][id] {
				continue
			}
			if touched.Receipts == nil {
				touched.Receipts = map[string][]string{}
			}
			touched.Receipts[chat] = append(touched.Receipts[chat], id)
		}
	}
}

// Call with the lock held. Facebook sends names in several row shapes and any one of them may be the only one that
// arrives for a person, so each contributes what it has and a blank never overwrites a known name.
// Reports whether anything about the person changed, so their conversations can be redrawn.
func (s *store) remember(id int64, name, first, picture string) bool {
	if id == 0 {
		return false
	}
	if name == "" {
		name = first
	}
	changed := false
	if name != "" && s.contacts[id] != name {
		s.contacts[id] = name
		changed = true
	}
	if picture != "" && s.pictures[id] != picture {
		s.pictures[id] = picture
		changed = true
	}
	return changed
}

func (s *store) setSelf(id int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.self = id
}

// Call with the lock held.
func (s *store) get(key int64) *thread {
	existing, ok := s.threads[key]
	if !ok {
		existing = &thread{key: key, participants: map[int64]struct{}{}, readBy: map[int64]int64{}}
		s.threads[key] = existing
	}
	return existing
}

// Returns which conversations and messages the rows touched.
func (s *store) apply(tbl *table.LSTable) *changes {
	touched := &changes{}
	if tbl == nil {
		return touched
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	chats := map[int64]bool{}
	messages := map[int64]map[string]bool{}
	removed := map[int64]map[string]bool{}
	touch := func(key int64) {
		if key != 0 {
			chats[key] = true
		}
	}
	note := func(key int64, id string) {
		if key == 0 || id == "" {
			return
		}
		touch(key)
		known, ok := messages[key]
		if !ok {
			known = map[string]bool{}
			messages[key] = known
		}
		known[id] = true
	}
	// A message removed on any of this account's devices goes from the conversation here too.
	drop := func(key int64, id string) {
		if key == 0 || id == "" {
			return
		}
		touch(key)
		s.revokeID(id)
		if thread, ok := s.messages[key]; ok {
			delete(thread, id)
		}
		if known, ok := messages[key]; ok {
			delete(known, id)
		}
		gone, ok := removed[key]
		if !ok {
			gone = map[string]bool{}
			removed[key] = gone
		}
		gone[id] = true
		delete(s.shares, shareMessageKey{key, id})
	}
	seen := map[int64]map[string]bool{}
	// A message somebody has received or read. Kept apart from the messages Facebook sent rows for: both are redrawn,
	// only the latter means a request for history has been answered, and only the latter can change the conversation's
	// row in the list — so this alone does not touch the conversation.
	noteSeen := func(key int64, id string) {
		if key == 0 || id == "" {
			return
		}
		known, ok := seen[key]
		if !ok {
			known = map[string]bool{}
			seen[key] = known
		}
		known[id] = true
	}
	defer func() {
		s.collect(touched, chats, messages, removed, seen)
		touched.markHistory(tbl)
	}()
	applyFolderRows(s, tbl, touch)
	// Facebook describes a conversation with either of two rows and the bridge treats them the same. The insert row
	// is the whole conversation; the update row is a change to it, so a field it leaves empty is not a field cleared.
	record := func(key int64, name, snippet, folder, picture string, lastActivity, readAt int64, kind table.ThreadType, cannotReply, whole bool) {
		if key == 0 {
			return
		}
		touch(key)
		current := s.get(key)
		if name != "" {
			current.name = name
		}
		if kind != 0 {
			current.kind = kind
		}
		if picture != "" {
			current.picture = picture
		}
		if (current.kind == table.ENCRYPTED_OVER_WA_ONE_TO_ONE || current.kind == table.ENCRYPTED_OVER_WA_GROUP) && current.name == "" && len(current.participants) == 0 {
			current.unresolved = true
		}
		if current.deleted {
			return
		}
		if lastActivity >= current.lastActivity && snippet != "" {
			current.snippet = snippet
		}
		if lastActivity > current.lastActivity {
			current.lastActivity = lastActivity
		}
		if readAt > current.readAt {
			current.readAt = readAt
		} else if readAt > 0 && readAt < current.readAt && lastActivity >= current.lastActivity && readAt != current.lowerNoted {
			// Facebook says less has been read than this copy does, in a row as new as any it holds: marked unread on
			// another device, or a read it took and then lost. Only counted for now (owner, 2026-10-22: measure before
			// following it), since an older row can look the same.
			current.lowerNoted = readAt
			logf("read drift chat=%s facebook lower ms=%d", tag(key), current.readAt-readAt)
		}
		if (whole || folder != "") && lastActivity >= current.lastActivity {
			current.folder = folder
		}
		current.cannotReply = cannotReply
	}
	for _, row := range tbl.LSDeleteThenInsertThread {
		record(row.ThreadKey, row.ThreadName, row.Snippet, rowFolder(row.FolderName, row.ParentThreadKey), row.ThreadPictureUrl,
			row.LastActivityTimestampMs, row.LastReadWatermarkTimestampMs, row.ThreadType, row.CannotReplyReason != nil, true)
	}
	for _, row := range tbl.LSUpdateOrInsertThread {
		record(row.ThreadKey, row.ThreadName, row.Snippet, rowFolder(row.FolderName, row.ParentThreadKey), row.ThreadPictureUrl,
			row.LastActivityTimestampMs, row.LastReadWatermarkTimestampMs, row.ThreadType, row.CannotReplyReason != 0, false)
	}
	applyThreadSettings(s, tbl, touch)
	for _, row := range tbl.LSAddParticipantIdToGroupThread {
		touch(row.ThreadKey)
		current := s.get(row.ThreadKey)
		current.participants[row.ContactId] = struct{}{}
		noteAdmin(current, row.ContactId, row.IsAdmin || row.IsSuperAdmin)
		// A nickname only fills in for someone whose contact row has not arrived.
		if _, known := s.contacts[row.ContactId]; !known && row.Nickname != "" {
			s.contacts[row.ContactId] = row.Nickname
		}
	}
	// Explicit identity mutations are authoritative, including deliberately clearing a group name/picture.
	for _, row := range tbl.LSSyncUpdateThreadName {
		if row.ThreadKey == 0 {
			continue
		}
		touch(row.ThreadKey)
		s.get(row.ThreadKey).name = row.ThreadName
	}
	for _, row := range tbl.LSSetThreadImageURL {
		if row.ThreadKey == 0 {
			continue
		}
		touch(row.ThreadKey)
		s.get(row.ThreadKey).picture = row.ImageURL
	}
	for _, row := range tbl.LSRemoveParticipantFromThread {
		if row.ThreadKey == 0 {
			continue
		}
		touch(row.ThreadKey)
		current := s.get(row.ThreadKey)
		delete(current.participants, row.ParticipantId)
		delete(current.admins, row.ParticipantId)
		delete(current.readBy, row.ParticipantId)
		s.forgetReceipts(row.ThreadKey, row.ParticipantId)
	}
	// A name or picture arriving late belongs to every conversation that person is in.
	member := func(id int64, changed bool) {
		if !changed || id == 0 {
			return
		}
		for key, current := range s.threads {
			if _, ok := current.participants[id]; ok {
				touch(key)
			}
		}
	}
	for _, row := range tbl.LSDeleteThenInsertContact {
		member(row.Id, s.remember(row.Id, row.Name, row.FirstName, row.ProfilePictureUrl))
	}
	for _, row := range tbl.LSVerifyContactRowExists {
		member(row.ContactId, s.remember(row.ContactId, row.Name, row.FirstName, row.ProfilePictureUrl))
		// Facebook marks the viewer here, which is more reliable than trusting the cookie alone.
		if row.IsSelf && row.ContactId != 0 {
			s.self = row.ContactId
		}
	}
	for _, row := range tbl.LSVerifyContactParticipantExist {
		member(row.ContactID, s.remember(row.ContactID, row.Name, "", ""))
		// One-to-one threads get their participants this way rather than through the group rows.
		if row.ThreadKey != 0 && row.ContactID != 0 {
			touch(row.ThreadKey)
			s.get(row.ThreadKey).participants[row.ContactID] = struct{}{}
		}
	}
	// Messages arrive as inserts or upserts depending on where they came from; both say the same thing, and a later
	// upsert is the newer word on a message that is already known.
	rows := make([]*table.LSInsertMessage, 0, len(tbl.LSInsertMessage)+len(tbl.LSUpsertMessage))
	rows = append(rows, tbl.LSInsertMessage...)
	for _, row := range tbl.LSUpsertMessage {
		rows = append(rows, row.ToInsert())
	}
	// A newer message moves its thread up the list even when no thread row arrives with it.
	for _, row := range rows {
		if row.ThreadKey == 0 {
			continue
		}
		if row.SenderId == metaAIMessengerID {
			logf("meta ai shape connection=lightspeed id=%s text=%v botResponse=%v unsent=%v",
				tagOf(row.MessageId), strings.TrimSpace(row.Text) != "", row.BotResponseID != 0, row.IsUnsent)
		}
		if s.revoked[row.MessageId] {
			continue
		}
		if previous := s.messages[row.ThreadKey][row.MessageId]; previous != nil && previous.aiUpdatedAt > 0 {
			// Lightspeed can carry a bare projection of an encrypted AI response.
			if row.IsUnsent {
				drop(row.ThreadKey, row.MessageId)
			}
			continue
		}
		current := s.get(row.ThreadKey)
		if current.deletedAt > 0 && row.TimestampMs <= current.deletedAt {
			continue
		}
		current.deleted = false
		touch(row.ThreadKey)
		if row.TimestampMs > current.lastActivity {
			current.lastActivity = row.TimestampMs
			if row.Text != "" {
				current.snippet = row.Text
			}
		}
		if row.TimestampMs >= s.latestMessageTimestampLocked(row.ThreadKey) {
			current.previewOutgoing = s.self != 0 && row.SenderId == s.self
		}
		if row.MessageId == "" {
			continue
		}
		note(row.ThreadKey, row.MessageId)
		thread, ok := s.messages[row.ThreadKey]
		if !ok {
			thread = map[string]*message{}
			s.messages[row.ThreadKey] = thread
		}
		s.noteSendStatus(row.SendStatusV2, s.self != 0 && row.SenderId == s.self)
		cardText := ""
		replyTo := row.ReplySourceId
		if previous := thread[row.MessageId]; previous != nil && previous.sender == row.SenderId && !row.IsUnsent {
			cardText = previous.cardText
			if row.SenderId == metaAIMessengerID && replyTo == "" {
				replyTo = previous.replyTo
			}
		}
		thread[row.MessageId] = &message{
			id:        row.MessageId,
			otid:      row.OfflineThreadingId,
			sender:    row.SenderId,
			text:      row.Text,
			timestamp: row.TimestampMs,
			replyTo:   replyTo,
			unsent:    row.IsUnsent,
			status:    row.SendStatusV2,
			cardText:  cardText,
		}
	}
	hybridRows := append([]*table.LSVerifyHybridThreadExists{}, tbl.LSVerifyHybridThreadExists...)
	for _, row := range tbl.LSUpdateThreadAuthorityAndMappingWithOTIDFromJID {
		hybridRows = append(hybridRows, &table.LSVerifyHybridThreadExists{ThreadKey: row.ThreadKey, ThreadJID: row.ThreadJID})
	}
	for _, row := range hybridRows {
		if row.ThreadJID == 0 || row.ThreadKey == 0 {
			continue
		}
		s.hybrid[row.ThreadJID] = row.ThreadKey
		s.pairings[row.ThreadJID]++
		current := s.get(row.ThreadKey)
		if current.kind == 0 && current.name == "" {
			current.unresolved = true
		}
		if row.ThreadType != 0 {
			current.kind = row.ThreadType
		}
		if current.kind == 0 {
			current.unresolved = true
			if orphan := s.threads[row.ThreadJID]; orphan != nil {
				current.kind = orphan.kind
			}
			if row.IsGroupThread {
				current.kind = table.ENCRYPTED_OVER_WA_GROUP
			}
		}
		if current.kind.IsOneToOne() {
			current.participants[row.ThreadJID] = struct{}{}
			if s.self != 0 {
				current.participants[s.self] = struct{}{}
			}
		}
		touch(row.ThreadKey)
		// Messages the encrypted connection delivered before this pairing was known are held under the number they
		// arrived with. Now that the two are known to be the same conversation, they belong together.
		if stray, ok := s.messages[row.ThreadJID]; ok && row.ThreadJID != row.ThreadKey {
			if orphan := s.threads[row.ThreadJID]; orphan != nil {
				current.unresolved = current.unresolved || orphan.unresolved
				if current.name == "" {
					current.name = orphan.name
				}
				for id := range orphan.participants {
					current.participants[id] = struct{}{}
				}
			}
			thread, exists := s.messages[row.ThreadKey]
			if !exists {
				thread = map[string]*message{}
				s.messages[row.ThreadKey] = thread
			}
			for id, held := range stray {
				if current.deletedAt > 0 && held.timestamp <= current.deletedAt {
					for _, item := range s.attached[id] {
						delete(s.media, item.id)
						delete(s.files, item.id)
					}
					delete(s.attached, id)
					delete(s.reactions, id)
					delete(s.shares, shareMessageKey{row.ThreadJID, id})
					continue
				}
				thread[id] = held
				current.deleted = false
				if held.timestamp > current.lastActivity {
					current.lastActivity = held.timestamp
					current.snippet = held.text
				}
				if held.timestamp >= s.latestMessageTimestampLocked(row.ThreadKey) {
					current.previewOutgoing = s.self != 0 && held.sender == s.self
				}
				note(row.ThreadKey, id)
			}
			for pair, held := range s.shares {
				if pair.thread == row.ThreadJID {
					delete(s.shares, pair)
					s.shares[shareMessageKey{row.ThreadKey, pair.message}] = held
				}
			}
			delete(s.messages, row.ThreadJID)
			s.adoptReceipts(s.threads[row.ThreadJID], row.ThreadJID, row.ThreadKey)
			delete(s.threads, row.ThreadJID)
			delete(s.pages, row.ThreadJID)
			delete(s.refreshTries, row.ThreadJID)
			delete(s.rev, row.ThreadJID)
			if s.fromStore[row.ThreadJID] {
				s.fromStore[row.ThreadKey] = true
				delete(s.fromStore, row.ThreadJID)
			}
			delete(chats, row.ThreadJID)
			delete(messages, row.ThreadJID)
			touched.ChatsDeleted = append(touched.ChatsDeleted, strconv.FormatInt(row.ThreadJID, 10))
			touch(row.ThreadKey)
		}
		if current.kind == table.ENCRYPTED_OVER_WA_ONE_TO_ONE {
			for _, key := range s.standIns(row.ThreadJID, row.ThreadKey) {
				s.retire(key, current)
				delete(chats, key)
				delete(messages, key)
				touched.ChatsDeleted = append(touched.ChatsDeleted, strconv.FormatInt(key, 10))
				touch(row.ThreadKey)
			}
		}
	}
	// Which messages a conversation has, named without sending them. This is what makes it possible to ask for the
	// history of a conversation none of whose messages have arrived.
	for _, row := range tbl.LSInsertNewMessageRange {
		if row.ThreadKey == 0 || row.MinMessageId == "" {
			continue
		}
		current := s.get(row.ThreadKey)
		if current.anchorAt == 0 || row.MinTimestampMs < current.anchorAt {
			current.anchorAt, current.anchorID = row.MinTimestampMs, row.MinMessageId
		}
	}
	// Removed from the conversation, or unsent for everyone. Facebook reports the second as a rewritten message row
	// with nothing left in it, which is a deletion as far as the app is concerned.
	for _, row := range tbl.LSDeleteMessage {
		drop(row.ThreadKey, row.MessageId)
	}
	for _, row := range tbl.LSDeleteThenInsertMessage {
		if row.IsUnsent {
			drop(row.ThreadKey, row.MessageId)
		}
	}
	// Reactions arrive as rows of their own, one per person: reacting again replaces what that person had, and an
	// empty one takes it away. They belong to a message that may not be in this batch, which is why they are kept by
	// message id rather than on the message itself.
	for _, row := range tbl.LSUpsertReaction {
		s.setReaction(row.MessageId, row.ActorId, row.Reaction)
		note(row.ThreadKey, row.MessageId)
	}
	for _, row := range tbl.LSDeleteReaction {
		s.setReaction(row.MessageId, row.ActorId, "")
		note(row.ThreadKey, row.MessageId)
	}
	// A message whose words were corrected, on any of this account's devices or by whoever sent it. Without this the
	// conversation keeps showing what it said before the edit until it is fetched again.
	for _, row := range tbl.LSEditMessage {
		// The row names the message and not its conversation, so the conversation has to be found by the message.
		key, current, ok := s.messageAnywhere(row.MessageID)
		if !ok || current.unsent {
			continue
		}
		current.text = row.Text
		current.editedAt = time.Now().UnixMilli()
		if thread, ok := s.threads[key]; ok && thread.lastActivity == current.timestamp {
			thread.snippet = row.Text
		}
		note(key, row.MessageID)
	}
	// How far each person has read and received. Apply after message rows so markers can resolve within this same
	// batch (receiptbook.go).
	s.applyReceipts(tbl, touch, noteSeen)
	// A sent message's own id arrives before its row does; remembering the pairing lets the app match its pending
	// message to the real one either way round.
	for _, row := range tbl.LSReplaceOptimsiticMessage {
		if row.OfflineThreadingId != "" && row.MessageId != "" {
			s.sent[row.OfflineThreadingId] = row.MessageId
		}
	}
	// Photos, videos and files arrive as their own rows, after the message they belong to.
	for _, row := range tbl.LSInsertBlobAttachment {
		if row.MessageId == "" {
			continue
		}
		note(row.ThreadKey, row.MessageId)
		id := row.AttachmentFbid
		if id == "" {
			id = row.MessageId + ":" + strconv.FormatInt(row.AttachmentIndex, 10)
		}
		// Facebook supplies a playable url for photos as well, so the kind comes from the attachment type and mime.
		video := row.AttachmentType == table.AttachmentTypeVideo || row.AttachmentType == table.AttachmentTypeEphemeralVideo ||
			strings.HasPrefix(row.AttachmentMimeType, "video/")
		// Facebook serves gifs as short mp4 clips; the app loops those rather than showing a play button.
		animated := row.AttachmentType == table.AttachmentTypeAnimatedImage || row.AttachmentMimeType == "image/gif"
		image := row.AttachmentType == table.AttachmentTypeImage || row.AttachmentType == table.AttachmentTypeAnimatedImage ||
			row.AttachmentType == table.AttachmentTypeEphemeralImage || row.AttachmentType == table.AttachmentTypeSticker ||
			strings.HasPrefix(row.AttachmentMimeType, "image/")
		source, mime := row.PreviewUrl, row.PreviewUrlMimeType
		// Real gif bytes animate as an image. Only when Facebook sends none does a gif play as a clip.
		gif := animated && row.PreviewUrl != "" && (row.PreviewUrlMimeType == "image/gif" || row.AttachmentMimeType == "image/gif")
		if animated && !gif && row.PlayableUrl != "" {
			video = true
		}
		if gif {
			source, mime, video = row.PreviewUrl, "image/gif", false
		} else if row.PlayableUrl != "" {
			// The real thing, for a photo as much as for a video: Facebook's preview url is a smaller copy, and
			// opening a photo showed that copy rather than the photograph. Upstream's bridge takes the playable url
			// for every attachment and falls back to the preview only when there is none.
			source, mime = row.PlayableUrl, row.PlayableUrlMimeType
		}
		if source == "" && row.PlayableUrl != "" {
			source, mime = row.PlayableUrl, row.PlayableUrlMimeType
		}
		if source == "" {
			source, mime = row.PreviewUrlFallback, row.PreviewUrlMimeType
		}
		if mime == "" {
			mime = row.AttachmentMimeType
		}
		if mime == "" && image {
			mime = "image/jpeg"
		}
		// Without somewhere to fetch it from, an attachment would only be an empty box in the conversation.
		if source == "" {
			continue
		}
		// A video keeps its thumbnail as its own media id, so the conversation can show a still with a play button
		// rather than a grey box, and opening it still fetches the real stream.
		// The smaller copy, kept alongside. A photo is drawn from this in the conversation and fetched in full only
		// when it is opened, so a wall of pictures does not pull down the originals to show them an inch high.
		preview := ""
		if row.PreviewUrl != "" && row.PreviewUrl != source {
			preview = s.rememberMedia(id+":preview", row.PreviewUrl)
		}
		s.attach(row.MessageId, &attachment{
			id: id, name: row.Filename, mime: mime, source: source, preview: preview, playable: row.PlayableUrl,
			size: row.Filesize, width: row.PreviewWidth, height: row.PreviewHeight, video: video, animated: animated,
		})
		// The message row of a picture and nothing else has no words, so the list went on showing the message before it
		// under the new time. Named as the encrypted connection names its own (encryptedMedia, attachSecure).
		if current, ok := s.threads[row.ThreadKey]; ok {
			if found, ok := s.messages[row.ThreadKey][row.MessageId]; ok && found.text == "" && found.timestamp >= current.lastActivity {
				current.snippet = blobLabel(row.AttachmentType, row.Filename, image, video, animated)
			}
		}
	}
	// Playable XMA rows are still GIFs/videos. Shared links use picture fields in applyShares, never video bytes
	// labelled as a thumbnail.
	for _, row := range tbl.LSInsertXmaAttachment {
		if row.MessageId == "" || row.PlayableUrl == "" {
			continue
		}
		id := row.AttachmentFbid
		if id == "" {
			id = row.MessageId + ":xma:" + strconv.FormatInt(row.AttachmentIndex, 10)
		}
		note(row.ThreadKey, row.MessageId)
		animated := row.ShouldAutoplayVideo || row.AttachmentType == table.AttachmentTypeAnimatedImage
		mime, source, video := row.PlayableUrlMimeType, row.PlayableUrl, true
		if mime == "" {
			mime = "video/mp4"
		}
		// A picked gif delivered as gif bytes animates as an image instead of playing.
		if animated && row.PlayableUrlMimeType == "image/gif" {
			mime, video = "image/gif", false
		}
		preview := ""
		if video && row.PreviewUrl != "" {
			preview = s.rememberMedia(id+":preview", row.PreviewUrl)
		}
		s.attach(row.MessageId, &attachment{
			id: id, name: row.Filename, mime: mime, source: source, preview: preview, playable: row.PlayableUrl,
			size: row.Filesize, width: row.PreviewWidth, height: row.PreviewHeight, video: video, animated: animated,
		})
	}
	s.applyShares(tbl, note)
	// Facebook flags a GIF here rather than on the attachment itself, often after it.
	for _, row := range tbl.LSUpdateExtraAttachmentColumns {
		if !row.ShouldAutoplayVideo || row.AttachmentFBID == "" {
			continue
		}
		if item, ok := s.media[row.AttachmentFBID]; ok {
			item.animated = true
			// Gif bytes animate on their own; anything else needs its clip, and without one it stays an image.
			if item.mime != "image/gif" && item.playable != "" {
				item.video = true
				item.source = item.playable
			}
		}
	}
	return touched
}

// The list's words for a message that is only an attachment.
func blobLabel(kind table.AttachmentType, filename string, image, video, animated bool) string {
	switch {
	case kind == table.AttachmentTypeSticker || kind == table.AttachmentTypeSelfieSticker || kind == table.AttachmentTypeThirdPartySticker:
		return "Sticker"
	case animated:
		return "GIF"
	case video:
		return "Video"
	case image:
		return "Photo"
	case kind == table.AttachmentTypeAudio || kind == table.AttachmentTypeSoundBite:
		return "Voice message"
	case filename != "":
		return filename
	}
	return "File"
}

// Where a downloadable attachment lives, for the media call.
func (s *store) mediaSource(id string) (string, string, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	found, ok := s.media[id]
	if !ok {
		return "", "", false
	}
	return found.source, found.mime, true
}

// The size an attachment declares for itself in bytes: Lightspeed's Filesize, or an encrypted file's length. 0 where
// nothing was said, as for a preview.
func (s *store) mediaSize(id string) int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	if found, ok := s.media[id]; ok {
		return found.size
	}
	return 0
}

// Call with the lock held.
func (s *store) attach(messageID string, item *attachment) {
	if item.link != "" {
		list := s.attached[messageID]
		kept := make([]*attachment, 0, len(list))
		for _, old := range list {
			if generatedLinkPreview(item.id) && serviceLinkCard(old.id, old.link) {
				return
			}
			if generatedLinkPreview(old.id) && serviceLinkCard(item.id, item.link) {
				delete(s.media, old.id)
				continue
			}
			kept = append(kept, old)
		}
		s.attached[messageID] = kept
	}
	s.media[item.id] = item
	list := s.attached[messageID]
	for index, existing := range list {
		if existing.id == item.id {
			list[index] = item
			s.attached[messageID] = list
			return
		}
	}
	s.attached[messageID] = append(list, item)
}

// How many conversations are known, so paging the list can stop once there are enough.
func (s *store) threadCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	known := 0
	for _, current := range s.threads {
		if s.visible(current) && (current.lastActivity > 0 || current.name != "") {
			known++
		}
	}
	return known
}

// How many messages are known for a conversation, so a caller can tell whether a fetch has landed.
func (s *store) messageCount(key int64) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.messages[key])
}

// How many messages are known older than a cursor (0 means the whole conversation).
func (s *store) countBefore(key int64, before int64) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	if before == 0 {
		return len(s.messages[key])
	}
	total := 0
	for _, current := range s.messages[key] {
		if current.timestamp < before {
			total++
		}
	}
	return total
}

// Where to start asking for a conversation's history when none of its messages are known: the oldest message Facebook
// has named, and failing that its last activity, which is at least a real moment in the conversation.
func (s *store) anchor(key int64) (int64, string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.threads[key]
	if !ok {
		return 0, ""
	}
	if current.anchorID != "" {
		return current.anchorAt, current.anchorID
	}
	return current.lastActivity, ""
}

// When a conversation was last active, which is all Facebook gives for one it has sent no messages for.
func (s *store) activity(key int64) int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	if current, ok := s.threads[key]; ok {
		return current.lastActivity
	}
	return 0
}

// People whose picture has not arrived are asked about once; this gives them another turn, after the list has
// finished filling in and Facebook has had time to send the contacts it was going to send.
func (s *store) retryPictures() {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.tried = map[int64]bool{}
}

// The oldest message known at or before a cursor, which is where paging further back starts from.
func (s *store) oldestBefore(key int64, before int64) (int64, string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	var timestamp int64
	var id string
	for _, current := range s.messages[key] {
		if before != 0 && current.timestamp >= before {
			continue
		}
		if timestamp == 0 || current.timestamp < timestamp {
			timestamp, id = current.timestamp, current.id
		}
	}
	return timestamp, id
}

// Oldest first, like the app expects. count <= 0 returns everything known.
func (s *store) history(key int64, count int) []viewMessage { return s.historyBefore(key, 0, count) }

// A conversation's messages older than a cursor (0 for all of them) that have something to draw, oldest first.
// Facebook will answer for a conversation it has not synced with rows that have an id and a time and nothing else — no
// text, no attachment, not even a deletion. There is nothing to draw for one of those, and a conversation of them reads
// as a wall of timestamps, so they are not part of its history. The caller holds s.mu.
func (s *store) drawableLocked(key int64, before int64) []*message {
	known := make([]*message, 0, len(s.messages[key]))
	for _, current := range s.messages[key] {
		if before != 0 && current.timestamp >= before {
			continue
		}
		if current.text == "" && !current.unsent && len(s.attached[current.id]) == 0 && (current.sender != metaAIMessengerID || current.sender == s.self) {
			continue
		}
		known = append(known, current)
	}
	sort.Slice(known, func(i, j int) bool {
		if known[i].timestamp != known[j].timestamp {
			return known[i].timestamp < known[j].timestamp
		}
		return known[i].id < known[j].id
	})
	return known
}

// Messages either side of a moment, oldest first: up to `older` strictly before `at` and up to `newer` at or after it,
// from what is known and nothing else. It is how the app shows a window of history around a search result, and pages
// away from that window in either direction (older with newer=0; newer with at just past the window's newest and
// older=0). Like historyBefore, neither end splits one moment: a page asked for from either side of it would otherwise
// miss the messages left outside. moreOlder and moreNewer say whether anything drawable lies beyond each end.
func (s *store) historyAround(key int64, at int64, older int, newer int) (page []viewMessage, moreOlder bool, moreNewer bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	known := s.drawableLocked(key, 0)
	split := sort.Search(len(known), func(i int) bool { return known[i].timestamp >= at })
	start := split - older
	if start < 0 {
		start = 0
	}
	for start > 0 && start < split && known[start-1].timestamp == known[start].timestamp {
		start--
	}
	end := split + newer
	if end > len(known) {
		end = len(known)
	}
	for end > split && end < len(known) && known[end].timestamp == known[end-1].timestamp {
		end++
	}
	page = make([]viewMessage, 0, end-start)
	for _, current := range known[start:end] {
		page = append(page, s.viewOf(key, current))
	}
	return page, start > 0, end < len(known)
}

// The newest `count` messages older than a cursor, oldest first.
func (s *store) historyBefore(key int64, before int64, count int) []viewMessage {
	s.mu.Lock()
	defer s.mu.Unlock()
	known := s.drawableLocked(key, before)
	if count > 0 && len(known) > count {
		// A page never splits one moment. The next page asks for what is strictly older than this one's oldest time,
		// so a message sharing that time but left just outside would be on neither page. Encrypted times are whole
		// seconds and a photo set is one message per photo sent back to back, so that is an ordinary case there.
		start := len(known) - count
		for start > 0 && known[start-1].timestamp == known[start].timestamp {
			start--
		}
		known = known[start:]
	}
	history := make([]viewMessage, 0, len(known))
	for _, current := range known {
		history = append(history, s.viewOf(key, current))
	}
	return history
}

// Call with the lock held.
func (s *store) viewOf(key int64, current *message) viewMessage {
	media := s.mediaFor(current.id)
	visible := media[:0]
	for _, item := range media {
		if generatedLinkPreview(item.ID) && (current.unsent || item.Link != firstPreviewURL(current.text)) {
			continue
		}
		visible = append(visible, item)
	}
	text := current.text
	if current.sender == metaAIMessengerID && current.sender != s.self && !current.unsent &&
		strings.TrimSpace(text) == "" && len(visible) == 0 {
		text = current.cardText
		if strings.TrimSpace(text) == "" || current.editedAt > 0 {
			text = metaAIUnavailableReply
		}
	}
	return viewMessage{
		ID:       current.id,
		ChatID:   strconv.FormatInt(key, 10),
		Sender:   strconv.FormatInt(current.sender, 10),
		Name:     s.contacts[current.sender],
		Text:     text,
		At:       time.UnixMilli(current.timestamp).UTC(),
		Outgoing: s.self != 0 && current.sender == s.self,
		Deleted:  current.unsent,
		ReplyTo:  current.replyTo,
		// Facebook numbers these; anything other than a plain delivered state stays pending for now.
		Status:    current.status,
		Media:     visible,
		Reactions: s.reactionsFor(current.id),
		SeenBy:    s.readersOf(key, current),
		ReadMark:  s.readMarkOf(key, current),
		EditedAt:  current.editedAt,
		Delivered: s.deliveredTo(key, current),
		SeenAt:    s.seenAt(key, current),
	}
}

// One message by the id Facebook gave it, or by the offline threading id this account sent it with. A message just
// sent is known by its otid before its row arrives, so both forms resolve to the same message.
func (s *store) messageByID(key int64, id string) (viewMessage, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	thread := s.messages[key]
	if found, ok := thread[id]; ok {
		return s.viewOf(key, found), true
	}
	if real, ok := s.sent[id]; ok {
		if found, ok := thread[real]; ok {
			return s.viewOf(key, found), true
		}
	}
	for _, current := range thread {
		if current.otid != "" && current.otid == id {
			return s.viewOf(key, current), true
		}
	}
	return viewMessage{}, false
}

// One conversation, whether or not it is in the most recent page of the list.
func (s *store) chatByKey(key int64) (viewChat, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.threads[key]
	if !ok || !s.visible(current) {
		return viewChat{}, false
	}
	return s.view(current), true
}

// A message this account just sent moves its conversation up the list, whether or not Facebook's reply carried the
// message row itself. Only the conversation's own activity is touched; no message is invented.
func (s *store) markSent(key int64, text string, timestamp int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	current := s.get(key)
	if timestamp >= current.lastActivity {
		current.lastActivity = timestamp
		if text != "" {
			current.snippet = text
		}
		// Sending in a conversation reads it, which is the task that went out with the message.
		current.readAt = timestamp
		current.previewOutgoing = true
	}
}

// Call with the lock held. Thread activity can be a few milliseconds ahead of its message,
// so preview direction follows the newest actual message rather than the thread timestamp.
func (s *store) latestMessageTimestampLocked(key int64) int64 {
	var latest int64
	for _, current := range s.messages[key] {
		if !current.unsent && current.timestamp > latest {
			latest = current.timestamp
		}
	}
	return latest
}

// The id Facebook gave a message sent with this offline threading id, if it has answered yet.
func (s *store) sentAs(otid string) (string, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	id, ok := s.sent[otid]
	return id, ok
}

// Newest first. limit <= 0 returns everything known.
func (s *store) chats(limit int) []viewChat {
	s.mu.Lock()
	defer s.mu.Unlock()
	known := make([]*thread, 0, len(s.threads))
	for _, current := range s.threads {
		if s.visible(current) && (current.lastActivity > 0 || current.name != "") {
			known = append(known, current)
		}
	}
	sort.Slice(known, func(i, j int) bool {
		if known[i].lastActivity != known[j].lastActivity {
			return known[i].lastActivity > known[j].lastActivity
		}
		return known[i].key > known[j].key
	})
	if limit > 0 && len(known) > limit {
		known = known[:limit]
	}
	chats := make([]viewChat, 0, len(known))
	kinds := map[table.ThreadType]int{}
	encryptedWithout := 0
	for _, current := range known {
		chats = append(chats, s.view(current))
		kinds[current.kind]++
		// A conversation Facebook has moved to end-to-end encryption keeps its messages somewhere this protocol
		// cannot read; counting them beside the ones with no messages says whether that is the whole story.
		if current.kind == table.ENCRYPTED_OVER_WA_ONE_TO_ONE || current.kind == table.ENCRYPTED_OVER_WA_GROUP {
			if len(s.messages[current.key]) == 0 {
				encryptedWithout++
			}
		}
	}
	shape := make([]string, 0, len(kinds))
	for kind, count := range kinds {
		shape = append(shape, fmt.Sprintf("%d:%d", kind, count))
	}
	sort.Strings(shape)
	logf("chat kinds %s; encrypted with no messages=%d", strings.Join(shape, ","), encryptedWithout)
	return chats
}

// Call with the lock held. Pictures are served through the media call too, so the app never fetches a Facebook url
// itself and never needs the session cookies.
func (s *store) rememberMedia(id, source string) string {
	if source == "" {
		return ""
	}
	s.media[id] = &attachment{id: id, mime: "image/jpeg", source: source}
	return id
}

// Call with the lock held.
func (s *store) mediaFor(messageID string) []viewMedia {
	list := s.attached[messageID]
	media := make([]viewMedia, 0, len(list))
	for _, item := range list {
		media = append(media, viewMedia{ID: item.id, Name: item.name, Mime: item.mime, Size: item.size,
			Width: item.width, Height: item.height, Video: item.video, Preview: item.preview, Animated: item.animated,
			Link: item.link, Play: item.play, Description: item.description, ActionTitle: item.actionTitle})
	}
	return media
}

// Call with the lock held.
func (s *store) view(current *thread) viewChat {
	ids := make([]int64, 0, len(current.participants))
	for id := range current.participants {
		ids = append(ids, id)
	}
	sort.Slice(ids, func(i, j int) bool { return ids[i] < ids[j] })
	participants := make([]viewParticipant, 0, len(ids))
	names := make([]string, 0, len(ids))
	for _, id := range ids {
		name := s.contacts[id]
		if name == "" {
			name = "Participant"
		}
		key := strconv.FormatInt(id, 10)
		self := id == s.self && s.self != 0
		participants = append(participants, viewParticipant{ID: key, Name: name, Self: self, NativeID: key,
			AvatarURL: s.rememberMedia("avatar:"+key, s.pictures[id])})
		if !self {
			names = append(names, name)
		}
	}
	// A group keeps its own picture; a one-to-one borrows the other person's.
	avatar := s.rememberMedia("thread:"+strconv.FormatInt(current.key, 10), current.picture)
	if avatar == "" {
		others := make([]int64, 0, len(ids))
		for _, id := range ids {
			if id != s.self || s.self == 0 {
				others = append(others, id)
			}
		}
		// A one-to-one borrows the other person's picture. A group leaves this empty on purpose: the app builds a
		// composite from the members' own pictures, which are on the participants above.
		if len(others) == 1 {
			avatar = s.rememberMedia("avatar:"+strconv.FormatInt(others[0], 10), s.pictures[others[0]])
		}
	}
	title := current.name
	if title == "" {
		title = strings.Join(names, ", ")
	}
	if title == "" {
		title = "Conversation"
	}
	encrypted := current.kind == table.ENCRYPTED_OVER_WA_ONE_TO_ONE || current.kind == table.ENCRYPTED_OVER_WA_GROUP
	addressKnown := false
	for _, key := range s.hybrid {
		if key == current.key {
			addressKnown = true
			break
		}
	}
	previewOutgoing := current.previewOutgoing
	// Older saved identities predate previewOutgoing. Recover it from the newest actual message when its timestamp
	// matches the thread activity closely enough to be the provider's preview; retain markSent's value before an echo.
	var previewSender int64
	if !previewOutgoing {
		var latest *message
		for _, candidate := range s.messages[current.key] {
			if !candidate.unsent && (latest == nil || candidate.timestamp > latest.timestamp) {
				latest = candidate
			}
		}
		if latest != nil && current.lastActivity >= latest.timestamp && current.lastActivity-latest.timestamp <= 1000 {
			if latest.sender == s.self {
				previewOutgoing = true
			} else if !current.kind.IsOneToOne() {
				// A group's line names who wrote it, as the conversation's own messages name them: the phone's line gained
				// the name when the conversation was read and lost it when the phone let it go (its diary, 2026-10-28).
				previewSender = latest.sender
			}
		}
	}
	previewSenderID, previewName := "", ""
	if previewSender != 0 {
		previewSenderID, previewName = strconv.FormatInt(previewSender, 10), s.contacts[previewSender]
	}
	manage := s.manageable(current)
	return viewChat{
		MutedUntil:       mutedUntil(current.muteUntil, time.Now()),
		CanRename:        manage.rename,
		CanAddMembers:    manage.add,
		CanRemoveMembers: manage.remove,
		Transport:        "MESSENGER",
		OutgoingID:       strconv.FormatInt(s.self, 10),
		// An encrypted conversation can only be read or replied to while the encrypted connection is up.
		ReadOnly:        current.cannotReply || (encrypted && (!s.secure || !addressKnown)),
		Encrypted:       encrypted,
		ID:              strconv.FormatInt(current.key, 10),
		Title:           title,
		Participants:    participants,
		Preview:         current.snippet,
		PreviewSender:   previewSenderID,
		PreviewName:     previewName,
		PreviewOutgoing: previewOutgoing,
		At:              time.UnixMilli(current.lastActivity).UTC(),
		// A watermark of zero means Messenger has not told us; treat that as read rather than inventing unread chats.
		Unread:    current.readAt > 0 && current.lastActivity > current.readAt && !previewOutgoing,
		ReadAt:    current.readAt,
		Group:     !current.kind.IsOneToOne(),
		Archived:  archivedFolder(current.folder),
		AvatarURL: avatar,
		Members:   s.membersOf(current),
	}
}

// The account this session belongs to, as Facebook's own id.
func (s *store) viewer() int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.self
}

func (s *store) rememberSearchContact(id int64, name, picture string) string {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.remember(id, name, "", picture)
	return s.rememberMedia("avatar:"+strconv.FormatInt(id, 10), picture)
}

// An already synchronized one-to-one wins over creating another conversation for the same person.
func (s *store) directChat(person int64) (viewChat, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, current := range s.threads {
		if current.deleted || !current.kind.IsOneToOne() {
			continue
		}
		if _, ok := current.participants[person]; ok {
			return s.view(current), true
		}
	}
	return viewChat{}, false
}

func (s *store) pairingCount(jid int64) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.pairings[jid]
}

func (s *store) activateDirect(person, startedAt int64) int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	key := person
	if mapped := s.hybrid[person]; mapped != 0 {
		key = mapped
	}
	current := s.get(key)
	current.deleted = false
	current.cannotReply = false
	current.unresolved = false
	current.kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	current.folder = "inbox"
	current.lastActivity = max(current.lastActivity, startedAt)
	current.participants[person] = struct{}{}
	if s.self != 0 {
		current.participants[s.self] = struct{}{}
	}
	// CreateWhatsAppThreadTask addresses a direct thread with the other person's Facebook id. Server responses do not
	// always repeat the hybrid mapping, so remember the mapping proved by the successful task.
	s.hybrid[person] = key
	return key
}

// Call with the lock held. Starting a direct chat that Facebook has not paired yet stands in for it under the
// person's address, or under the pairing a deleted one used to have. When Facebook then names a different thread for
// that person, the stand-in would stay behind as a second, read-only copy with nowhere to send. These are the other
// encrypted one-to-ones with this person that nothing is paired with any more. One holding messages is left alone:
// hiding it would hide them.
func (s *store) standIns(jid, paired int64) []int64 {
	var found []int64
	for key, candidate := range s.threads {
		if key == paired || candidate.deleted || candidate.kind != table.ENCRYPTED_OVER_WA_ONE_TO_ONE || len(s.messages[key]) > 0 {
			continue
		}
		if _, with := candidate.participants[jid]; !with {
			continue
		}
		target := false
		for _, other := range s.hybrid {
			if other == key {
				target = true
				break
			}
		}
		if !target {
			found = append(found, key)
		}
	}
	return found
}

// Call with the lock held. The real thread takes over the stand-in's deletion cutoff and, when the stand-in was started
// after that deletion, its place in the list.
func (s *store) retire(key int64, successor *thread) {
	stale := s.threads[key]
	successor.deletedAt = max(successor.deletedAt, stale.deletedAt)
	if stale.lastActivity > successor.deletedAt {
		successor.deleted = false
		if stale.lastActivity > successor.lastActivity {
			successor.lastActivity = stale.lastActivity
			successor.folder = stale.folder
		}
	}
	stale.deleted = true
}

// Whether the conversation is archived as Messenger last stated it; known is false for one not held or deleted.
func (s *store) archivedNow(key int64) (archived, known bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	current := s.threads[key]
	if current == nil || current.deleted {
		return false, false
	}
	return archivedFolder(current.folder), true
}

// Whether a message arrived on the encrypted connection, rather than as a Lightspeed row.
func (s *store) messageEncrypted(key int64, id string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	current := s.messages[key][id]
	return current != nil && current.encrypted
}

// One received or restored message. Restored ordinary history retains its own channel.
func (s *store) addEncrypted(key int64, id string, sender int64, text, replyTo string, at int64, channel ...bool) *changes {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.revoked[id] {
		return &changes{}
	}
	if previous := s.messages[key][id]; previous != nil && previous.aiUpdatedAt > 0 && previous.sender != sender {
		return &changes{}
	}
	encrypted := len(channel) == 0 || channel[0]
	next := &message{id: id, encrypted: encrypted, sender: sender, text: text, replyTo: replyTo, timestamp: at}
	if encrypted && sender == metaAIMessengerID && sender != s.self {
		next.aiUpdatedAt = at
		if previous := s.messages[key][id]; previous != nil {
			previousUpdateAt := previous.aiUpdatedAt
			if previousUpdateAt == 0 {
				previousUpdateAt = previous.timestamp
			}
			// There is no documented fragment/final revision in this schema. Equal-timestamp
			// conflicting text is ambiguous: retain readable content rather than guess.
			accepted := previous.sender == sender && !previous.unsent && previous.editedAt == 0 &&
				at >= previousUpdateAt && strings.TrimSpace(text) != "" && (strings.TrimSpace(previous.text) == "" || at > previousUpdateAt)
			logf("meta ai update connection=encrypted id=%s existing=true text=%v newer=%v equal=%v accepted=%v",
				tagOf(id), strings.TrimSpace(text) != "", at > previousUpdateAt, at == previousUpdateAt, accepted)
			if !accepted {
				return &changes{}
			}
			copied := *previous
			next = &copied
			next.encrypted = true
			next.text, next.aiUpdatedAt = text, at
			if replyTo != "" {
				next.replyTo = replyTo
			}
		}
	}
	current := s.get(key)
	if current.deletedAt > 0 && at <= current.deletedAt {
		return &changes{}
	}
	current.deleted = false
	if at > current.lastActivity {
		current.lastActivity = at
		if text != "" {
			current.snippet = text
		}
	} else if previous := s.messages[key][id]; previous != nil && previous.text == unreadableText && at == current.lastActivity && text != "" {
		// A resend that decrypted after all, in place of the line that stood for it: same message, same time.
		current.snippet = text
	}
	if at >= s.latestMessageTimestampLocked(key) {
		current.previewOutgoing = s.self != 0 && sender == s.self
	}
	thread, ok := s.messages[key]
	if !ok {
		thread = map[string]*message{}
		s.messages[key] = thread
	}
	thread[id] = next
	chat := strconv.FormatInt(key, 10)
	return s.settleWaitingReceipts(key, next, &changes{Chats: []string{chat}, Messages: map[string][]string{chat: {id}}})
}

// Whether this conversation already holds the message.
func (s *store) holdsMessage(key int64, id string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.messages[key][id] != nil
}

// Call with the lock held. One person's reaction to one message; an empty one takes theirs away.
func (s *store) setReaction(messageID string, actor int64, key string) {
	if messageID == "" || actor == 0 {
		return
	}
	if key == "" {
		if held, ok := s.reactions[messageID]; ok {
			delete(held, actor)
			if len(held) == 0 {
				delete(s.reactions, messageID)
			}
		}
		return
	}
	held, ok := s.reactions[messageID]
	if !ok {
		held = map[int64]string{}
		s.reactions[messageID] = held
	}
	held[actor] = key
}

// One person's reaction, from outside the lock. Reports the conversation it belongs to so it can be announced.
func (s *store) react(key int64, messageID string, actor int64, reaction string) *changes {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.setReaction(messageID, actor, reaction)
	chat := strconv.FormatInt(key, 10)
	return &changes{Chats: []string{chat}, Messages: map[string][]string{chat: {messageID}}}
}

// Call with the lock held.
func (s *store) reactionsFor(messageID string) []viewReaction {
	held, ok := s.reactions[messageID]
	if !ok || len(held) == 0 {
		return nil
	}
	people := make([]int64, 0, len(held))
	for id := range held {
		people = append(people, id)
	}
	sort.Slice(people, func(i, j int) bool { return people[i] < people[j] })
	list := make([]viewReaction, 0, len(people))
	for _, id := range people {
		list = append(list, viewReaction{Key: held[id], Sender: strconv.FormatInt(id, 10)})
	}
	return list
}

// The file an encrypted message came with, hung on the message that was just added, along with the directions for
// fetching it when the conversation is looked at.
func (s *store) attachSecure(key int64, messageID string, at int64, item *attachment, held *secureMedia) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.attach(messageID, item)
	if held != nil {
		s.files[item.id] = held
	}
	// A conversation whose newest message is a picture and nothing else reads as blank in the list without this.
	if current, ok := s.threads[key]; ok && at >= current.lastActivity {
		if found, ok := s.messages[key][messageID]; ok && found.text == "" {
			current.snippet = item.name
		}
	}
}

type pageAnchor struct {
	at int64
	id string
}

// How far back the page the app is showing actually reaches. A photo with no caption is not in that page at all yet —
// it has no text and no attachment, so it is filtered out — which means the page spans more messages than it shows,
// and the more photos there are the further back it reaches. Refreshing a fixed number of messages would stop short of
// exactly the ones that are missing.
func (s *store) oldestDisplayed(key int64, before int64, count int) int64 {
	page := s.historyBefore(key, before, count)
	if len(page) == 0 {
		return 0
	}
	return page[0].At.UnixMilli()
}

// The messages to anchor successive refresh requests at, walking back through the page the app is showing as far as
// until. Facebook answers about twenty messages at a time and ignores a request that cannot name a real message, so a
// page takes several asks, each naming a message further back through it.
func (s *store) pageAnchors(key int64, before int64, until int64, step int) []pageAnchor {
	s.mu.Lock()
	defer s.mu.Unlock()
	known := make([]*message, 0, len(s.messages[key]))
	for _, current := range s.messages[key] {
		if before != 0 && current.timestamp >= before {
			continue
		}
		if current.timestamp < until {
			continue
		}
		known = append(known, current)
	}
	sort.Slice(known, func(i, j int) bool {
		if known[i].timestamp != known[j].timestamp {
			return known[i].timestamp > known[j].timestamp
		}
		return known[i].id > known[j].id
	})
	anchors := make([]pageAnchor, 0, len(known)/step+1)
	for index := 0; index < len(known); index += step {
		anchors = append(anchors, pageAnchor{at: known[index].timestamp, id: known[index].id})
	}
	return anchors
}

// An attachment read back from the store, hung on the message it belongs to. A video's still image is a fetchable
// thing in its own right, so its url is put back alongside.
func (s *store) restoreAttachment(messageID string, item *attachment, previewSource string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	// A card kept before linkDestination existed still holds Facebook's redirect.
	item.link = linkDestination(item.link)
	s.attach(messageID, item)
	if item.preview != "" && previewSource != "" {
		s.rememberMedia(item.preview, previewSource)
	}
}

// Which conversation an attachment belongs to, found by looking rather than by another map: this is asked only when a
// download has already failed, which is rare, and there is enough bookkeeping here as it is.
func (s *store) chatForMedia(id string) (int64, bool) {
	key, _, ok := s.anchorForMedia(id)
	return key, ok
}

// The exact message is needed: refreshing the newest page cannot renew an older video's address.
func (s *store) anchorForMedia(id string) (int64, pageAnchor, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for messageID, list := range s.attached {
		for _, item := range list {
			if item.id != id && item.preview != id {
				continue
			}
			for key, thread := range s.messages {
				if message, ok := thread[messageID]; ok {
					return key, pageAnchor{at: message.timestamp, id: messageID}, true
				}
			}
		}
	}
	return 0, pageAnchor{}, false
}

// Direction 0 fetches the messages BEFORE its anchor. The attachment's own message therefore gets left out.
// Use the closest known newer message so an older attachment is inside the returned page, including timestamp ties.
func (s *store) mediaRefreshAnchor(key int64, target pageAnchor) pageAnchor {
	s.mu.Lock()
	defer s.mu.Unlock()
	var next *message
	for _, current := range s.messages[key] {
		if current.timestamp < target.at || current.timestamp == target.at && current.id <= target.id {
			continue
		}
		if next == nil || current.timestamp < next.timestamp || current.timestamp == next.timestamp && current.id < next.id {
			next = current
		}
	}
	if next != nil {
		return pageAnchor{at: next.timestamp, id: next.id}
	}
	return target
}

// Sends a conversation back to Facebook the next time it is read. Used when a stored url has expired: what is held is
// no longer good enough, and only Facebook can supply a fresh one.
func (s *store) refreshAgain(key int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.fromStore[key] = true
	delete(s.pages, key)
	delete(s.refreshTries, key)
}

// The attachments on a message and the urls of their still images, for writing down.
func (s *store) attachmentsOf(messageID string) ([]*attachment, map[string]string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	list := s.attached[messageID]
	if len(list) == 0 {
		return nil, nil
	}
	previews := map[string]string{}
	copies := make([]*attachment, 0, len(list))
	for _, item := range list {
		duplicate := *item
		copies = append(copies, &duplicate)
		if item.preview != "" {
			if held, ok := s.media[item.preview]; ok {
				previews[item.preview] = held.source
			}
		}
	}
	return copies, previews
}

// A reaction read back from the store, without announcing anything: this runs before the app is listening.
func (s *store) restoreReaction(messageID string, actor int64, emoji string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.setReaction(messageID, actor, emoji)
}

// A message taken back, removed from the conversation it was in. Used when restoring: the stored copy predates the
// deletion and would otherwise put it back.
func (s *store) revokeID(id string) {
	if s.revoked == nil {
		s.revoked = map[string]bool{}
	}
	s.revoked[id] = true
}

func (s *store) forget(key int64, id string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.revokeID(id)
	if thread, ok := s.messages[key]; ok {
		delete(thread, id)
	}
	for _, item := range s.attached[id] {
		delete(s.media, item.id)
		delete(s.files, item.id)
	}
	delete(s.attached, id)
	delete(s.reactions, id)
}

// Which conversation holds a message, for the rows Facebook sends naming only the message. Call with the lock held.
func (s *store) messageAnywhere(id string) (int64, *message, bool) {
	if id == "" {
		return 0, nil, false
	}
	for key, thread := range s.messages {
		if found, ok := thread[id]; ok {
			return key, found, true
		}
	}
	return 0, nil, false
}

// A message whose words have changed. Returns what to redraw, so the conversation shows the correction without
// being refetched. An edit that names a message this build has never seen changes nothing rather than inventing one.
func (s *store) applyEdit(key int64, id, text string, at int64) *changes {
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.messages[key][id]
	if !ok || current.unsent {
		return &changes{}
	}
	if current.aiUpdatedAt > 0 && (at < current.aiUpdatedAt || at < current.editedAt) {
		return &changes{}
	}
	if current.text == text && current.editedAt >= at {
		return &changes{}
	}
	current.text = text
	if at > current.editedAt {
		current.editedAt = at
	}
	chat := strconv.FormatInt(key, 10)
	// The conversation list shows the newest message; a correction to it belongs there too.
	if thread, ok := s.threads[key]; ok && thread.lastActivity == current.timestamp {
		thread.snippet = text
	}
	return &changes{Chats: []string{chat}, Messages: map[string][]string{chat: {id}}}
}

// A message taken back, with the conversation told what to remove. `forget` does the same thing silently, for
// restoring; this is the live version.
func (s *store) removeEncrypted(key int64, id string) *changes {
	s.mu.Lock()
	s.revokeID(id)
	found := false
	if thread, ok := s.messages[key]; ok {
		_, found = thread[id]
		delete(thread, id)
	}
	for _, item := range s.attached[id] {
		delete(s.media, item.id)
		delete(s.files, item.id)
	}
	delete(s.attached, id)
	delete(s.reactions, id)
	s.mu.Unlock()
	if !found {
		return &changes{}
	}
	chat := strconv.FormatInt(key, 10)
	return &changes{Chats: []string{chat}, Deleted: map[string][]string{chat: {id}}}
}

func (s *store) revision(key int64) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.rev[key]
}

// Marks a conversation as holding messages that came back from the local store. Those keep their text but not their
// pictures, so the conversation has to be asked of Facebook once before its history is complete.
func (s *store) markFromStore(key int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.fromStore[key] = true
}

func (s *store) restoredFrom(key int64) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.fromStore[key]
}

// Which pages of a restored conversation have already been asked of Facebook, so each is asked once and no more. A
// page is named by the cursor it ends at, which is how the app asks for it.
func (s *store) pageRefreshed(key int64, before int64) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.pages[key][before]
}

func (s *store) pageRefreshTries(key int64, before int64) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.refreshTries[key][before]
}

func (s *store) notePageRefreshTry(key int64, before int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	tries, ok := s.refreshTries[key]
	if !ok {
		tries = map[int64]int{}
		s.refreshTries[key] = tries
	}
	tries[before]++
}

func (s *store) markPageRefreshed(key int64, before int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	known, ok := s.pages[key]
	if !ok {
		known = map[int64]bool{}
		s.pages[key] = known
	}
	known[before] = true
}

func (s *store) secureFor(id string) (*secureMedia, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	held, ok := s.files[id]
	return held, ok
}

// The conversation an encrypted message belongs to. Facebook numbers those conversations differently on the encrypted
// connection and tells us the pairing; without it a message would start a conversation of its own beside the real one.
func (s *store) threadForJID(jid int64) (int64, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if key, ok := s.hybrid[jid]; ok {
		return key, true
	}
	// The pairing may not have arrived, but the conversation itself might already be known under this number.
	if _, ok := s.threads[jid]; ok {
		return jid, true
	}
	return 0, false
}

// The number an encrypted conversation goes by on the encrypted connection.
func (s *store) jidForThread(key int64) (int64, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for jid, thread := range s.hybrid {
		if thread == key {
			return jid, true
		}
	}
	return 0, false
}

// Whether this conversation is one Facebook has moved to end-to-end encryption, and whether it is a group.
// What kind of conversation this is, and whether that kind means its messages travel on the encrypted connection. For
// the log: a conversation whose pictures never arrive is a different question depending on which connection carries it.
func (s *store) kindOf(key int64) (int64, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.threads[key]
	if !ok {
		return -1, false
	}
	return int64(current.kind), current.kind == table.ENCRYPTED_OVER_WA_ONE_TO_ONE || current.kind == table.ENCRYPTED_OVER_WA_GROUP
}

func (s *store) encryptedThread(key int64) (bool, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.threads[key]
	if !ok {
		return false, false
	}
	return current.kind == table.ENCRYPTED_OVER_WA_ONE_TO_ONE || current.kind == table.ENCRYPTED_OVER_WA_GROUP,
		current.kind == table.ENCRYPTED_OVER_WA_GROUP
}

// Set once the encrypted connection is up, so those conversations stop being described as ones we cannot carry.
func (s *store) setSecure(ready bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.secure = ready
}

// One message as it is held, for writing down. Call without the lock.
func (s *store) rawMessage(key int64, id string) (int64, string, string, int64, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	found, ok := s.messages[key][id]
	if !ok {
		return 0, "", "", 0, false
	}
	return found.sender, found.text, found.replyTo, found.timestamp, true
}

// What a shared reel or post is called. Facebook gives these a caption of sorts across several fields and none of them
// reliably; the accessibility summary is the one written for a person to read.
// The first of these with anything in it.
func firstOf(candidates ...string) string {
	for _, candidate := range candidates {
		if candidate != "" {
			return candidate
		}
	}
	return ""
}

func shareName(row *table.LSInsertXmaAttachment, item *table.LSInsertAttachmentItem) string {
	candidates := []string{row.AccessibilitySummaryText, row.DefaultCtaTitle, row.Cta1Title, row.Filename}
	if item != nil {
		candidates = append([]string{item.TitleText, item.SubtitleText}, candidates...)
	}
	for _, candidate := range candidates {
		if candidate != "" {
			return candidate
		}
	}
	return "Shared link"
}

// A preview picture is a picture whatever the row says, and rows for these often say nothing at all.
func previewType(mime string) string {
	if mime == "" {
		return "image/jpeg"
	}
	return mime
}
