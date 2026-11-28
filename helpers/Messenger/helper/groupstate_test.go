package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// An ordinary Messenger group of this account (1) with two others, as Facebook describes one.
func ordinaryGroup(admin bool) *table.LSTable {
	return &table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 40, ThreadName: "Group", ThreadType: table.GROUP_THREAD, LastActivityTimestampMs: 100}},
		LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{
			{ThreadKey: 40, ContactId: 1, IsAdmin: admin}, {ThreadKey: 40, ContactId: 200}, {ThreadKey: 40, ContactId: 300}},
	}
}

func groupChangeAdapter(t *testing.T, admin bool) (*adapter, *[][]socket.Task) {
	t.Helper()
	a, sent := groupAdapter(t)
	a.applyTable(context.Background(), ordinaryGroup(admin))
	return a, sent
}

func chatView(t *testing.T, a *adapter, key int64) viewChat {
	t.Helper()
	found, ok := a.chats.chatByKey(key)
	if !ok {
		t.Fatalf("conversation %d is not shown", key)
	}
	return found
}

func call(a *adapter, method, params string) (any, string) {
	return a.callContext(context.Background(), request{ID: 1, Method: method, Params: json.RawMessage(params)})
}

// A mute set on the phone shows here: a whole conversation row carries it, the explicit mute change replaces it, and a
// partial update row, which leaves the mute out, does not clear it.
func TestAMuteFromAnotherDeviceIsReceived(t *testing.T) {
	a, _ := identityAdapter(t)
	future := time.Now().Add(time.Hour).UnixMilli()
	a.applyTable(context.Background(), &table.LSTable{LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
		{ThreadKey: 50, ThreadName: "Muted", ThreadType: table.ONE_TO_ONE, LastActivityTimestampMs: 100, MuteExpireTimeMs: -1}}})
	if got := chatView(t, a, 50).MutedUntil; got != -1 {
		t.Fatalf("a mute until turned off read as %d", got)
	}
	a.applyTable(context.Background(), &table.LSTable{LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{
		{ThreadKey: 50, LastActivityTimestampMs: 200, Snippet: "new"}}})
	if got := chatView(t, a, 50).MutedUntil; got != -1 {
		t.Fatal("a partial update row cleared the mute it does not carry")
	}
	a.applyTable(context.Background(), &table.LSTable{LSUpdateThreadMuteSetting: []*table.LSUpdateThreadMuteSetting{{ThreadKey: 50, MuteExpireTimeMS: future}}})
	if got := chatView(t, a, 50).MutedUntil; got != future {
		t.Fatalf("a timed mute read as %d, not its end", got)
	}
	a.applyTable(context.Background(), &table.LSTable{LSUpdateThreadMuteSetting: []*table.LSUpdateThreadMuteSetting{{ThreadKey: 50, MuteExpireTimeMS: 0}}})
	if got := chatView(t, a, 50).MutedUntil; got != 0 {
		t.Fatal("unmuting on the phone left the conversation muted")
	}
	a.applyTable(context.Background(), &table.LSTable{LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
		{ThreadKey: 50, ThreadName: "Muted", ThreadType: table.ONE_TO_ONE, LastActivityTimestampMs: 300, MuteExpireTimeMs: future}}})
	if got := chatView(t, a, 50).MutedUntil; got != future {
		t.Fatal("a whole conversation row did not carry its mute")
	}
}

// Facebook leaves an ended mute in place; it is no mute.
func TestAnEndedMuteIsNoMute(t *testing.T) {
	now := time.UnixMilli(1_000_000)
	if mutedUntil(999_000, now) != 0 || mutedUntil(1_000_000, now) != 0 {
		t.Fatal("an ended mute was reported")
	}
	if mutedUntil(1_000_001, now) != 1_000_001 || mutedUntil(-1, now) != -1 || mutedUntil(-5, now) != -1 || mutedUntil(0, now) != 0 {
		t.Fatal("a mute was misread")
	}
}

// The mute is kept with the conversation's identity, so a restarted helper does not show a muted conversation unmuted
// until Facebook describes it again.
func TestAMuteSurvivesARestart(t *testing.T) {
	a, _ := identityAdapter(t)
	a.applyTable(context.Background(), &table.LSTable{LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
		{ThreadKey: 51, ThreadName: "Muted", ThreadType: table.ONE_TO_ONE, LastActivityTimestampMs: 100, MuteExpireTimeMs: -1}}})
	a.kept.saveIdentity(context.Background(), a.chats)
	back := newStore()
	a.kept.restoreIdentity(context.Background(), back)
	if current, ok := back.threads[51]; !ok || current.muteUntil != -1 {
		t.Fatal("the mute was not restored with the conversation")
	}
}

// Nothing in this helper sends a mute: a mute is received only.
func TestNoMuteIsEverSent(t *testing.T) {
	for _, method := range []string{"ui.mute", "ui.muteChat", "ui.setMute"} {
		if mutationMethod(method) {
			t.Fatalf("%s is dispatched as a mutation", method)
		}
	}
	a, sent := groupChangeAdapter(t, true)
	if _, code := call(a, "ui.updateChat", `{"chatId":"40","muted":true}`); code != "invalid_input" || len(*sent) != 0 {
		t.Fatalf("a mute asked of the conversation update answered %q and sent %v", code, *sent)
	}
}

// Which changes an ordinary group offers follows what Messenger said about this account's place in it.
func TestGroupChangesAreOfferedOnlyWhereMessengerAllowsThem(t *testing.T) {
	a, _ := groupChangeAdapter(t, false)
	member := chatView(t, a, 40)
	if !member.CanRename || !member.CanAddMembers || member.CanRemoveMembers {
		t.Fatalf("a member who is not an admin was offered %+v", member)
	}
	a.applyTable(context.Background(), &table.LSTable{LSUpdateThreadParticipantAdminStatus: []*table.LSUpdateThreadParticipantAdminStatus{{ThreadKey: 40, ContactId: 1, IsAdmin: true}}})
	if admin := chatView(t, a, 40); !admin.CanRemoveMembers {
		t.Fatal("an admin was not offered removing somebody")
	}
	a.applyTable(context.Background(), &table.LSTable{LSOverwriteAllThreadParticipantsAdminStatus: []*table.LSOverwriteAllThreadParticipantsAdminStatus{{ThreadKey: 40, IsAdmin: false}}})
	if demoted := chatView(t, a, 40); demoted.CanRemoveMembers {
		t.Fatal("removing stayed offered after the admins were cleared")
	}
	a.applyTable(context.Background(), &table.LSTable{LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{{ThreadKey: 40, NeedsAdminApprovalForNewParticipant: true}}})
	if approval := chatView(t, a, 40); approval.CanAddMembers {
		t.Fatal("adding was offered where a new member waits for an admin this account is not")
	}

	// An encrypted group, a one-to-one, a group this account is not in and a conversation moved to encryption: none.
	a.applyTable(context.Background(), &table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: 41, ThreadName: "Secure", ThreadType: table.ENCRYPTED_OVER_WA_GROUP, LastActivityTimestampMs: 100},
			{ThreadKey: 42, ThreadName: "Direct", ThreadType: table.ONE_TO_ONE, LastActivityTimestampMs: 100},
			{ThreadKey: 43, ThreadName: "Not mine", ThreadType: table.GROUP_THREAD, LastActivityTimestampMs: 100},
			{ThreadKey: 44, ThreadName: "Moved", ThreadType: table.GROUP_THREAD, LastActivityTimestampMs: 100, FolderName: "e2ee_cutover"},
		},
		LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{
			{ThreadKey: 41, ContactId: 1, IsAdmin: true}, {ThreadKey: 41, ContactId: 200},
			{ThreadKey: 42, ContactId: 1}, {ThreadKey: 42, ContactId: 200},
			{ThreadKey: 43, ContactId: 200}, {ThreadKey: 43, ContactId: 300},
			{ThreadKey: 44, ContactId: 1, IsAdmin: true}, {ThreadKey: 44, ContactId: 200},
		},
	})
	for _, key := range []int64{41, 42, 43, 44} {
		if found, ok := a.chats.chatByKey(key); ok && (found.CanRename || found.CanAddMembers || found.CanRemoveMembers) {
			t.Fatalf("conversation %d was offered a group change: %+v", key, found)
		}
	}
}

// Renaming sends the task group creation names a group with, once, and shows the name Messenger took.
func TestRenamingAGroupSendsTheRenameTaskOnce(t *testing.T) {
	a, sent := groupChangeAdapter(t, false)
	result, code := call(a, "ui.renameGroup", `{"chatId":"40","title":"  New name  "}`)
	if code != "" {
		t.Fatalf("rename: %s", code)
	}
	if len(*sent) != 1 || len((*sent)[0]) != 1 {
		t.Fatalf("expected one task, got %v", *sent)
	}
	rename, ok := (*sent)[0][0].(*socket.RenameThreadTask)
	if !ok || rename.ThreadKey != 40 || rename.ThreadName != "New name" || rename.SyncGroup != 1 {
		t.Fatalf("unexpected rename: %#v", (*sent)[0][0])
	}
	if chat, _ := result.(map[string]any)["chat"].(viewChat); chat.Title != "New name" {
		t.Fatalf("the taken name was not shown: %+v", result)
	}
}

// A change Messenger refused is said to be refused; one whose answer never came may have happened, and is reported as
// unknown. Neither is tried again, and neither changes what is shown.
func TestAGroupChangeIsNeverRetried(t *testing.T) {
	for _, tc := range []struct {
		reply *table.LSTable
		err   error
		want  string
	}{
		{nil, errors.New("socket closed"), "change_uncertain"},
		{nil, nil, "change_uncertain"},
		{&table.LSTable{LSIssueNewError: []*table.LSIssueNewError{{}}}, nil, "change_rejected"},
		{&table.LSTable{LSHandleFailedTask: []*table.LSHandleFailedTask{{}}}, nil, "change_rejected"},
	} {
		a, sent := groupChangeAdapter(t, true)
		a.executeTasks = func(_ context.Context, tasks ...socket.Task) (*table.LSTable, error) {
			*sent = append(*sent, tasks)
			return tc.reply, tc.err
		}
		for _, attempt := range []struct{ method, params string }{
			{"ui.renameGroup", `{"chatId":"40","title":"Other"}`},
			{"ui.addGroupMembers", `{"chatId":"40","personIds":["400"]}`},
			{"ui.removeGroupMember", `{"chatId":"40","personId":"300"}`},
		} {
			before := len(*sent)
			if _, code := call(a, attempt.method, attempt.params); code != tc.want {
				t.Fatalf("%s answered %q, want %q", attempt.method, code, tc.want)
			}
			if len(*sent)-before != 1 {
				t.Fatalf("%s sent %d times", attempt.method, len(*sent)-before)
			}
		}
		shown := chatView(t, a, 40)
		if shown.Title != "Group" || len(shown.Participants) != 3 {
			t.Fatalf("a change that was not confirmed was shown: %+v", shown)
		}
	}
}

// Adding sends Messenger's add task for exactly the people chosen; removing sends its remove task for one person.
func TestAddingAndRemovingMembersSendTheirTasks(t *testing.T) {
	a, sent := groupChangeAdapter(t, true)
	if _, code := call(a, "ui.addGroupMembers", `{"chatId":"40","personIds":["400","500"]}`); code != "" {
		t.Fatalf("add: %s", code)
	}
	add, ok := (*sent)[0][0].(*socket.AddParticipantsTask)
	if !ok || add.ThreadKey != 40 || !slices.Equal(add.ContactIDs, []int64{400, 500}) || add.SyncGroup != 1 {
		t.Fatalf("unexpected add: %#v", (*sent)[0][0])
	}
	if got := len(chatView(t, a, 40).Participants); got != 5 {
		t.Fatalf("the added people were not shown: %d members", got)
	}
	if _, code := call(a, "ui.removeGroupMember", `{"chatId":"40","personId":"300"}`); code != "" {
		t.Fatalf("remove: %s", code)
	}
	remove, ok := (*sent)[1][0].(*socket.RemoveParticipantTask)
	if !ok || remove.ThreadID != 40 || remove.ContactID != 300 {
		t.Fatalf("unexpected remove: %#v", (*sent)[1][0])
	}
	for _, person := range chatView(t, a, 40).Participants {
		if person.ID == "300" {
			t.Fatal("the removed person is still shown")
		}
	}
}

// What is refused before anything is sent: this account, somebody already in it or not in it, an empty name, a
// conversation that is not an ordinary group, and removing somebody without being an admin.
func TestGroupChangesRefuseWhatMessengerWouldNotMean(t *testing.T) {
	a, sent := groupChangeAdapter(t, false)
	a.applyTable(context.Background(), &table.LSTable{
		LSDeleteThenInsertThread:        []*table.LSDeleteThenInsertThread{{ThreadKey: 41, ThreadName: "Secure", ThreadType: table.ENCRYPTED_OVER_WA_GROUP, LastActivityTimestampMs: 100}},
		LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{{ThreadKey: 41, ContactId: 1, IsAdmin: true}, {ThreadKey: 41, ContactId: 200}},
	})
	for _, refused := range []struct{ method, params, want string }{
		{"ui.renameGroup", `{"chatId":"40","title":"   "}`, "invalid_input"},
		{"ui.renameGroup", `{"chatId":"41","title":"Name"}`, "not_allowed"},
		{"ui.renameGroup", `{"chatId":"99","title":"Name"}`, "unknown_chat"},
		{"ui.addGroupMembers", `{"chatId":"40","personIds":["1"]}`, "invalid_input"},
		{"ui.addGroupMembers", `{"chatId":"40","personIds":["200"]}`, "invalid_input"},
		{"ui.addGroupMembers", `{"chatId":"40","personIds":["400","400"]}`, "invalid_input"},
		{"ui.addGroupMembers", `{"chatId":"40","personIds":[]}`, "invalid_input"},
		{"ui.removeGroupMember", `{"chatId":"40","personId":"300"}`, "not_allowed"},
	} {
		if _, code := call(a, refused.method, refused.params); code != refused.want {
			t.Fatalf("%s %s answered %q, want %q", refused.method, refused.params, code, refused.want)
		}
	}
	a.applyTable(context.Background(), &table.LSTable{LSUpdateThreadParticipantAdminStatus: []*table.LSUpdateThreadParticipantAdminStatus{{ThreadKey: 40, ContactId: 1, IsAdmin: true}}})
	for _, refused := range []string{`{"chatId":"40","personId":"1"}`, `{"chatId":"40","personId":"999"}`} {
		if _, code := call(a, "ui.removeGroupMember", refused); code != "invalid_input" {
			t.Fatalf("removing %s answered %q", refused, code)
		}
	}
	if len(*sent) != 0 {
		t.Fatalf("a refused change was sent: %v", *sent)
	}
}

// Group changes are contact-visible, so they queue behind every other one and are refused to the observer.
func TestGroupChangesAreMutations(t *testing.T) {
	for _, method := range []string{"ui.renameGroup", "ui.addGroupMembers", "ui.removeGroupMember"} {
		if !mutationMethod(method) {
			t.Fatalf("%s is not dispatched as a mutation", method)
		}
	}
	a, _ := groupChangeAdapter(t, true)
	a.observeReceipts = true
	if _, code := call(a, "ui.renameGroup", `{"chatId":"40","title":"x"}`); code != "observation_only" {
		t.Fatalf("the observer ran a group change: %s", code)
	}
}

// Diagnostics for a group change hold a one-way tag, a count and an outcome word; never a name or a person.
func TestGroupChangeLogsHoldNoNamesOrPeople(t *testing.T) {
	path := filepath.Join(t.TempDir(), "helper.log")
	t.Setenv("MESSAGELINK_MESSENGER_LOG", path)
	openLog()
	a, _ := groupChangeAdapter(t, true)
	call(a, "ui.renameGroup", `{"chatId":"40","title":"Secret Name"}`)
	call(a, "ui.addGroupMembers", `{"chatId":"40","personIds":["4242"]}`)
	call(a, "ui.removeGroupMember", `{"chatId":"40","personId":"300"}`)
	written := readHelperLog(t, path)
	if !strings.Contains(written, "group rename chat=") || !strings.Contains(written, "group add members=1") {
		t.Fatalf("the group changes were not logged:\n%s", written)
	}
	for _, private := range []string{"Secret Name", "4242", "=300", "=40 ", "chat=40"} {
		if strings.Contains(written, private) {
			t.Fatalf("the log holds %q:\n%s", private, written)
		}
	}
}

func TestEncryptedStateWords(t *testing.T) {
	for _, tc := range []struct {
		started, ready, gone bool
		since                time.Duration
		want                 string
	}{
		{false, false, false, 0, "off"},
		{true, false, false, 0, "connecting"},
		{true, false, false, encryptedGrace - time.Second, "connecting"},
		{true, false, false, encryptedGrace, "down"},
		{true, true, false, time.Hour, "ready"},
		{true, true, true, 0, "device_gone"},
		{false, false, true, 0, "device_gone"},
	} {
		if got := encryptedStateWord(tc.started, tc.ready, tc.gone, tc.since); got != tc.want {
			t.Fatalf("%+v read as %q, want %q", tc, got, tc.want)
		}
	}
}

// The app is told when the encrypted connection's word changes, once per change and never twice for the same word, so
// it does not have to ask "counts" to learn a connection came up.
func TestEncryptedStateIsAnnouncedWhenItChanges(t *testing.T) {
	var wire bytes.Buffer
	a := newAdapter(&output{encoder: json.NewEncoder(&wire)})
	defer a.stop()
	words := func() []string {
		var told []string
		decoder := json.NewDecoder(bytes.NewReader(wire.Bytes()))
		for {
			var line struct {
				Event  string `json:"event"`
				Result struct {
					Encrypted string `json:"encrypted"`
				} `json:"result"`
			}
			if decoder.Decode(&line) != nil {
				return told
			}
			if line.Event == "encrypted" {
				told = append(told, line.Result.Encrypted)
			}
		}
	}
	a.announceEncrypted()
	a.announceEncrypted()
	a.crypto = &encrypted{}
	a.encryptedStateChanged()
	a.encryptedStateChanged()
	a.encryptedGone.Store(true)
	a.announceEncrypted()
	if got := strings.Join(words(), ","); got != "off,connecting,device_gone" {
		t.Fatalf("the app was told %q", got)
	}
}

// "counts" carries the encrypted connection's state, so Settings can say it is down instead of calling it connected.
func TestCountsCarryTheEncryptedState(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	result, code := call(a, "counts", "")
	if code != "" || result.(map[string]any)["encrypted"] != "off" {
		t.Fatalf("an adapter with nothing started reported %v", result)
	}
	a.crypto = &encrypted{}
	a.encryptedChanged.Store(time.Now().Add(-time.Hour).UnixMilli())
	if state := a.encryptedState(time.Now()); state != "down" {
		t.Fatalf("an encrypted connection that never came up read as %q", state)
	}
	a.encryptedChanged.Store(time.Now().UnixMilli())
	if state := a.encryptedState(time.Now()); state != "connecting" {
		t.Fatalf("an encrypted connection just asked for read as %q", state)
	}
	a.encryptedGone.Store(true)
	if state := a.encryptedState(time.Now()); state != "device_gone" {
		t.Fatalf("a removed device read as %q", state)
	}
}
