package main

import (
	"context"
	"encoding/json"
	"strconv"
	"strings"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// What Messenger says about a conversation beyond its messages, received from whichever of the account's devices
// changed it (2026-10-04): this account's mute, who manages a group, and whether a new member needs approving.
//
// The mute is received only. Nothing here sends a mute to Messenger: the app's own mute stays on this PC, and a mute
// set on the phone is shown here as it is.
//
// Rows are believed the way the bridge believes them. A whole conversation row (delete-then-insert) carries the mute
// outright, zero included; the partial update row leaves the mute out of what it changes, so its zero means "not said"
// and is ignored. updateThreadMuteSetting is the explicit change, made on any device.
func applyThreadSettings(s *store, tbl *table.LSTable, touch func(int64)) {
	for _, row := range tbl.LSDeleteThenInsertThread {
		if row.ThreadKey == 0 {
			continue
		}
		current := s.get(row.ThreadKey)
		current.muteUntil = row.MuteExpireTimeMs
		current.approvalRequired = row.NeedsAdminApprovalForNewParticipant
	}
	for _, row := range tbl.LSUpdateOrInsertThread {
		// A partial row's false is not a setting cleared; only a true is believed.
		if row.ThreadKey != 0 && row.NeedsAdminApprovalForNewParticipant {
			s.get(row.ThreadKey).approvalRequired = true
		}
	}
	for _, row := range tbl.LSUpdateThreadMuteSetting {
		if row.ThreadKey == 0 {
			continue
		}
		touch(row.ThreadKey)
		s.get(row.ThreadKey).muteUntil = row.MuteExpireTimeMS
	}
	for _, row := range tbl.LSUpdateThreadParticipantAdminStatus {
		if row.ThreadKey == 0 || row.ContactId == 0 {
			continue
		}
		touch(row.ThreadKey)
		noteAdmin(s.get(row.ThreadKey), row.ContactId, row.IsAdmin)
	}
	for _, row := range tbl.LSOverwriteAllThreadParticipantsAdminStatus {
		if row.ThreadKey == 0 {
			continue
		}
		touch(row.ThreadKey)
		current := s.get(row.ThreadKey)
		current.admins = nil
		if row.IsAdmin {
			for id := range current.participants {
				noteAdmin(current, id, true)
			}
		}
	}
}

func noteAdmin(current *thread, person int64, admin bool) {
	if person == 0 {
		return
	}
	if !admin {
		delete(current.admins, person)
		return
	}
	if current.admins == nil {
		current.admins = map[int64]struct{}{}
	}
	current.admins[person] = struct{}{}
}

// The mute as the app is told it: -1 until turned off, the moment it ends, or 0 when there is none or it has ended.
// Facebook leaves an ended mute in place rather than clearing it, so the end is judged here as well as in the app.
func mutedUntil(raw int64, now time.Time) int64 {
	switch {
	case raw < 0:
		return -1
	case raw > 0 && raw > now.UnixMilli():
		return raw
	default:
		return 0
	}
}

// Changing an existing group: its name, and who is in it (2026-10-04).
//
// Each of these is visible to everybody in the group the moment Messenger takes it, so the helper never makes one by
// itself and never repeats one. A change whose answer did not arrive may have happened, so it is reported as unknown
// rather than tried again; the app says to check Messenger first.
//
// Only an ordinary Messenger group is offered them. These are the Lightspeed tasks the bridge uses for the same
// changes. An end-to-end encrypted group changes its members on the encrypted connection instead, which this helper
// does not do, and a conversation moved to encryption keeps its old ordinary thread only as history, so neither is
// offered anything. Messenger leaves removing people to a group's admins, and holds a new member for an admin's
// approval where the group asks for that, so those two follow what Messenger has said about this account's place in
// the group: unknown means not offered.
type groupManagement struct {
	rename, add, remove bool
}

// Call with the store lock held.
func (s *store) manageable(current *thread) groupManagement {
	if current == nil || current.deleted || current.cannotReply || current.kind != table.GROUP_THREAD || s.self == 0 {
		return groupManagement{}
	}
	if strings.HasPrefix(current.folder, "e2ee_cutover") {
		return groupManagement{}
	}
	for _, key := range s.hybrid {
		if key == current.key {
			return groupManagement{}
		}
	}
	if _, member := current.participants[s.self]; !member {
		return groupManagement{}
	}
	_, admin := current.admins[s.self]
	return groupManagement{rename: true, add: !current.approvalRequired || admin, remove: admin}
}

func (s *store) management(key int64) (groupManagement, map[int64]bool, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.threads[key]
	if !ok || !s.visible(current) {
		return groupManagement{}, nil, false
	}
	members := make(map[int64]bool, len(current.participants))
	for id := range current.participants {
		members[id] = true
	}
	return s.manageable(current), members, true
}

// What Messenger took, written into the conversation now rather than when its own rows arrive, so the app shows the
// change it was told succeeded. Messenger's rows say the same a moment later; they replace this, never the reverse.
func (s *store) settleRename(key int64, title string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if current, ok := s.threads[key]; ok {
		current.name = title
	}
}

func (s *store) settleMembers(key int64, added []int64, removed int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	current, ok := s.threads[key]
	if !ok {
		return
	}
	for _, id := range added {
		current.participants[id] = struct{}{}
	}
	if removed != 0 {
		delete(current.participants, removed)
		delete(current.admins, removed)
		delete(current.readBy, removed)
		s.forgetReceipts(key, removed)
	}
}

// What a change's answer says. An error or no answer at all may still mean the change was made.
func groupChangeOutcome(reply *table.LSTable, err error) string {
	if err != nil || reply == nil {
		return "change_uncertain"
	}
	if len(reply.LSIssueNewError) > 0 || len(reply.LSHandleFailedTask) > 0 {
		return "change_rejected"
	}
	return ""
}

// The checks every change shares, before anything is sent. Returns the conversation's key and members.
func (a *adapter) groupChangeTarget(ctx context.Context, chatID string, allowed func(groupManagement) bool) (int64, map[int64]bool, string) {
	key, err := strconv.ParseInt(chatID, 10, 64)
	if err != nil || key <= 0 {
		return 0, nil, "invalid_input"
	}
	// The offline tests stand in for the socket, as group creation's do.
	if a.client == nil && a.executeTasks == nil {
		return 0, nil, "login_required"
	}
	manage, members, ok := a.chats.management(key)
	if !ok {
		return 0, nil, "unknown_chat"
	}
	if !allowed(manage) {
		return 0, nil, "not_allowed"
	}
	if a.executeTasks == nil && !a.readyToSend(ctx) {
		return 0, nil, "not_connected"
	}
	return key, members, ""
}

// Sends one change, once, and applies what Messenger answered. settle runs only when Messenger took it.
func (a *adapter) sendGroupChange(ctx context.Context, kind string, key int64, task socket.Task, settle func()) (any, string) {
	reply, err := a.request(ctx, func() []socket.Task { return []socket.Task{task} })
	outcome := groupChangeOutcome(reply, err)
	word := outcome
	if word == "" {
		word = "ok"
	}
	logf("group %s chat=%s outcome=%s", kind, tag(key), word)
	if outcome != "" {
		return nil, outcome
	}
	logRows("group "+kind+" reply", reply, a.applyTable(ctx, reply))
	settle()
	a.state.Lock()
	a.mu.Lock()
	kept := a.kept
	a.mu.Unlock()
	kept.saveIdentity(ctx, a.chats)
	a.state.Unlock()
	// The conversation is redrawn wherever it is shown, as for any change Facebook sends over the socket.
	if a.out != nil {
		a.out.write(response{Event: "changed", Result: &changes{Chats: []string{strconv.FormatInt(key, 10)}}})
	}
	found, ok := a.chats.chatByKey(key)
	if !ok {
		// Taken, but the conversation is not one the list shows any more. The change still happened.
		return map[string]any{"changed": true}, ""
	}
	return map[string]any{"changed": true, "chat": found}, ""
}

func (a *adapter) renameGroup(ctx context.Context, r request) (any, string) {
	var p struct {
		ChatID string `json:"chatId"`
		Title  string `json:"title"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	// Clearing a name is a different change from naming, and not one the app offers.
	title := strings.TrimSpace(p.Title)
	if title == "" || len(title) > maxGroupTitle {
		return nil, "invalid_input"
	}
	key, _, code := a.groupChangeTarget(ctx, p.ChatID, func(m groupManagement) bool { return m.rename })
	if code != "" {
		return nil, code
	}
	return a.sendGroupChange(ctx, "rename", key, &socket.RenameThreadTask{ThreadKey: key, ThreadName: title, SyncGroup: 1},
		func() { a.chats.settleRename(key, title) })
}

func (a *adapter) addGroupMembers(ctx context.Context, r request) (any, string) {
	var p struct {
		ChatID    string   `json:"chatId"`
		PersonIDs []string `json:"personIds"`
	}
	if json.Unmarshal(r.Params, &p) != nil || len(p.PersonIDs) == 0 || len(p.PersonIDs) > maxGroupMembers {
		return nil, "invalid_input"
	}
	key, members, code := a.groupChangeTarget(ctx, p.ChatID, func(m groupManagement) bool { return m.add })
	if code != "" {
		return nil, code
	}
	viewer := a.chats.viewer()
	people := make([]int64, 0, len(p.PersonIDs))
	seen := map[int64]bool{}
	for _, id := range p.PersonIDs {
		person, err := strconv.ParseInt(id, 10, 64)
		// Somebody already in the group is not added again, and this account is in it already.
		if err != nil || person <= 0 || person == viewer || seen[person] || members[person] {
			return nil, "invalid_input"
		}
		seen[person] = true
		people = append(people, person)
	}
	logf("group add members=%d", len(people))
	return a.sendGroupChange(ctx, "add", key, &socket.AddParticipantsTask{ThreadKey: key, ContactIDs: people, SyncGroup: 1},
		func() { a.chats.settleMembers(key, people, 0) })
}

func (a *adapter) removeGroupMember(ctx context.Context, r request) (any, string) {
	var p struct {
		ChatID   string `json:"chatId"`
		PersonID string `json:"personId"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	person, err := strconv.ParseInt(p.PersonID, 10, 64)
	if err != nil || person <= 0 {
		return nil, "invalid_input"
	}
	key, members, code := a.groupChangeTarget(ctx, p.ChatID, func(m groupManagement) bool { return m.remove })
	if code != "" {
		return nil, code
	}
	// Leaving a group is a different action from removing somebody from it, and not this one.
	if person == a.chats.viewer() || !members[person] {
		return nil, "invalid_input"
	}
	return a.sendGroupChange(ctx, "remove", key, &socket.RemoveParticipantTask{ThreadID: key, ContactID: person},
		func() { a.chats.settleMembers(key, nil, person) })
}
