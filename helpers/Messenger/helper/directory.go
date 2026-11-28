package main

import (
	"context"
	"encoding/json"
	"strconv"
	"strings"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

type viewContact struct {
	ID            string `json:"id"`
	Name          string `json:"name"`
	Username      string `json:"username,omitempty"`
	AvatarURL     string `json:"avatarUrl,omitempty"`
	CannotMessage bool   `json:"cannotMessage"`
	Self          bool   `json:"self"`
}

func messengerSearchTask(query string, secondary bool) *socket.SearchUserTask {
	return &socket.SearchUserTask{
		Query: query,
		SupportedTypes: []table.SearchType{
			table.SearchTypeContact, table.SearchTypeGroup, table.SearchTypePage, table.SearchTypeNonContact,
			table.SearchTypeIGContactFollowing, table.SearchTypeIGContactNonFollowing,
			table.SearchTypeIGNonContactFollowing, table.SearchTypeIGNonContactNonFollowing,
			table.SearchTypeCommunityMessagingThread,
		},
		SurfaceType: 5,
		Secondary:   secondary,
	}
}

func messageableSearchResult(row *table.LSInsertSearchResult, viewer int64) (int64, string, bool) {
	id := row.GetFBID()
	name := strings.TrimSpace(row.GetName())
	return id, name, id != 0 && id != viewer && name != "" && row.ThreadType.IsOneToOne() &&
		row.CanViewerMessage && !row.IsRestrictedByViewer && row.BlockedByViewerStatus == 0
}

func (a *adapter) searchContacts(ctx context.Context, r request) (any, string) {
	var p struct {
		Query string `json:"query"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	p.Query = strings.TrimSpace(p.Query)
	if p.Query == "" || len([]rune(p.Query)) > 1000 {
		return nil, "invalid_input"
	}
	if a.client == nil {
		return nil, "login_required"
	}
	if !a.readyToSend(ctx) {
		return nil, "not_connected"
	}
	// The primary picker response contains the people. Native Messenger also starts a secondary search, but upstream
	// deliberately ignores its rows; avoiding it here keeps lookup cancellation and result ordering deterministic.
	tbl, err := a.request(ctx, func() []socket.Task { return []socket.Task{messengerSearchTask(p.Query, false)} })
	if err != nil || tbl == nil || len(tbl.LSHandleFailedTask) > 0 {
		return nil, "search_failed"
	}
	viewer := a.chats.viewer()
	seen := map[int64]bool{}
	people := make([]viewContact, 0, len(tbl.LSInsertSearchResult))
	for _, row := range tbl.LSInsertSearchResult {
		id, name, eligible := messageableSearchResult(row, viewer)
		if !eligible || seen[id] {
			continue
		}
		seen[id] = true
		avatar := a.chats.rememberSearchContact(id, name, row.ProfilePicUrl)
		contextLine := strings.TrimSpace(row.ContextLine)
		if contextLine == "" {
			contextLine = "Messenger"
		}
		people = append(people, viewContact{ID: strconv.FormatInt(id, 10), Name: name, Username: contextLine, AvatarURL: avatar})
	}
	return people, ""
}

func directCreateTasks(person int64) []socket.Task {
	return []socket.Task{
		&socket.CreateThreadTask{ThreadFBID: person, SyncGroup: 1},
		&socket.CreateWhatsAppThreadTask{WAJID: person, OfflineThreadKey: methods.GenerateEpochID(),
			ThreadType: table.ENCRYPTED_OVER_WA_ONE_TO_ONE, FolderType: table.INBOX,
			BumpTimestampMS: time.Now().UnixMilli()},
	}
}

func (a *adapter) startDirect(ctx context.Context, r request) (any, string) {
	var p struct {
		PersonID string `json:"personId"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	person, err := strconv.ParseInt(p.PersonID, 10, 64)
	if err != nil || person <= 0 || person == a.chats.viewer() {
		return nil, "invalid_input"
	}
	if a.client == nil {
		return nil, "login_required"
	}
	if existing, ok := a.chats.directChat(person); ok && !existing.ReadOnly {
		return existing, ""
	}
	if !a.readyToSend(ctx) {
		return nil, "not_connected"
	}
	tasks := directCreateTasks(person)
	startedAt := tasks[1].(*socket.CreateWhatsAppThreadTask).BumpTimestampMS
	asked := a.chats.pairingCount(person)
	ordinary, err := a.request(ctx, func() []socket.Task { return tasks[:1] })
	if err != nil {
		return nil, "start_failed"
	}
	if ordinary == nil || len(ordinary.LSHandleFailedTask) > 0 || len(ordinary.LSIssueNewError) > 0 {
		return nil, "start_rejected"
	}
	logRows("start ordinary reply", ordinary, a.applyTable(ctx, ordinary))
	secure, err := a.request(ctx, func() []socket.Task { return tasks[1:] })
	if err != nil || secure == nil {
		return nil, "start_failed"
	}
	if len(secure.LSHandleFailedTask) > 0 || len(secure.LSIssueNewError) > 0 {
		return nil, "start_failed"
	}
	logRows("start encrypted reply", secure, a.applyTable(ctx, secure))
	if len(secure.LSIssueNewTask) > 0 {
		followups := make([]socket.Task, len(secure.LSIssueNewTask))
		for index, task := range secure.LSIssueNewTask {
			followups[index] = task
		}
		continued, followupErr := a.request(ctx, func() []socket.Task { return followups })
		if followupErr != nil || continued == nil {
			return nil, "start_failed"
		}
		if len(continued.LSHandleFailedTask) > 0 || len(continued.LSIssueNewError) > 0 {
			return nil, "start_failed"
		}
		logRows("start followup reply", continued, a.applyTable(ctx, continued))
	}
	a.awaitPairing(ctx, person, asked)
	// A successful encrypted-create task establishes this direct JID even when its response contains no mapping row.
	// Restore an intentionally deleted conversation as one atomic, persisted identity update: keeping deletedAt still
	// prevents old history from returning, while the self-mapping makes the new conversation immediately writable.
	a.state.Lock()
	key := a.chats.activateDirect(person, startedAt)
	a.mu.Lock()
	kept := a.kept
	a.mu.Unlock()
	kept.saveIdentity(ctx, a.chats)
	a.state.Unlock()
	found, ok := a.chats.chatByKey(key)
	if !ok || found.ReadOnly {
		logf("start direct remains unavailable chat=%s", tag(key))
		return nil, "start_unavailable"
	}
	if found.ID == "" {
		return nil, "start_failed"
	}
	return found, ""
}

// How long starting a direct chat waits for Facebook to name the encrypted thread it made. None of the create replies
// carry that pairing; it follows over the socket, a fraction of a second later in live use. Without it the chat opens
// under a stand-in that the pairing then replaces. The app speaks to the helper one call at a time, so this stays brief:
// a pairing that arrives after it gives up still replaces the stand-in, only with the stand-in briefly on screen.
const pairingWait = 2 * time.Second

func (a *adapter) awaitPairing(ctx context.Context, person int64, asked int) {
	deadline := time.Now().Add(pairingWait)
	for a.chats.pairingCount(person) == asked && time.Now().Before(deadline) {
		select {
		case <-ctx.Done():
			return
		case <-time.After(50 * time.Millisecond):
		}
	}
	if a.chats.pairingCount(person) == asked {
		logf("start direct pairing not seen within %dms", pairingWait.Milliseconds())
	}
}

// Creating a group conversation.
//
// This is the Lightspeed path, which is the only one Messenger offers for making a group: there is no encrypted
// create-group task the way there is for a one-to-one, where `directCreateTasks` makes both an ordinary thread and
// an encrypted one. A group made here is therefore an ordinary Messenger group and not an end-to-end encrypted one,
// which matches what the account already has.
//
// Messenger has no name field on the create, so naming is a second task. A group that is made and then cannot be
// named is still a group, so the name failing is reported rather than treated as the whole thing failing.
const maxGroupMembers = 50
const maxGroupTitle = 256

type viewCreatedGroup struct {
	Chat     *viewChat `json:"chat,omitempty"`
	ThreadID string    `json:"threadId"`
	Named    bool      `json:"named"`
}

func (a *adapter) startGroup(ctx context.Context, r request) (any, string) {
	var p struct {
		PersonIDs []string `json:"personIds"`
		Title     string   `json:"title"`
	}
	if json.Unmarshal(r.Params, &p) != nil || len(p.Title) > maxGroupTitle {
		return nil, "invalid_input"
	}
	// A group is more than two people talking, and every member has to be somebody Messenger can address.
	if len(p.PersonIDs) < 2 || len(p.PersonIDs) > maxGroupMembers {
		return nil, "invalid_input"
	}
	viewer := a.chats.viewer()
	people := make([]int64, 0, len(p.PersonIDs))
	seen := make(map[int64]bool, len(p.PersonIDs))
	for _, id := range p.PersonIDs {
		person, err := strconv.ParseInt(id, 10, 64)
		if err != nil || person <= 0 || seen[person] {
			return nil, "invalid_input"
		}
		// This account is in every group it creates; naming it as a member is a different group than the one asked for.
		if person == viewer {
			return nil, "invalid_input"
		}
		seen[person] = true
		people = append(people, person)
	}
	// The offline tests stand in for the socket, and a stand-in is a connection as far as this is concerned; in the
	// running helper executeTasks is nil and both of these are the real checks they have always been.
	if a.client == nil && a.executeTasks == nil {
		return nil, "login_required"
	}
	if a.executeTasks == nil && !a.readyToSend(ctx) {
		return nil, "not_connected"
	}
	// The thread id sent is this client's guess at one; Messenger answers with the real one and the two are matched
	// before anything is believed about what was made.
	optimistic := methods.GenerateEpochID()
	reply, err := a.request(ctx, func() []socket.Task {
		return []socket.Task{&socket.CreateGroupTask{
			Participants: people,
			SendPayload: socket.CreateGroupPayload{
				ThreadID: optimistic,
				OTID:     strconv.FormatInt(methods.GenerateEpochID(), 10),
				Source:   0,
				SendType: 8,
			},
		}}
	})
	if err != nil || reply == nil {
		return nil, "start_failed"
	}
	if len(reply.LSIssueNewError) > 0 || len(reply.LSHandleFailedTask) > 0 {
		return nil, "start_rejected"
	}
	// Messenger took the task but named no thread. The group may or may not exist, so this is reported as uncertain
	// and never retried here: retrying would make a second group.
	if len(reply.LSReplaceOptimisticThread) == 0 {
		return nil, "start_uncertain"
	}
	replaced := reply.LSReplaceOptimisticThread[0]
	if replaced.ThreadKey1 != optimistic || replaced.ThreadKey2 <= 0 {
		return nil, "start_uncertain"
	}
	created := replaced.ThreadKey2
	logRows("start group reply", reply, a.applyTable(ctx, reply))
	result := &viewCreatedGroup{ThreadID: strconv.FormatInt(created, 10)}
	// Everything past this point is about a group that already exists. No failure here may say it does not.
	if p.Title != "" {
		named, nameErr := a.request(ctx, func() []socket.Task {
			return []socket.Task{&socket.RenameThreadTask{ThreadKey: created, ThreadName: p.Title, SyncGroup: 1}}
		})
		if nameErr == nil && named != nil && len(named.LSIssueNewError) == 0 && len(named.LSHandleFailedTask) == 0 {
			result.Named = true
			logRows("name group reply", named, a.applyTable(ctx, named))
		}
	}
	if found, ok := a.chats.chatByKey(created); ok && found.ID != "" {
		result.Chat = &found
	}
	return result, ""
}
