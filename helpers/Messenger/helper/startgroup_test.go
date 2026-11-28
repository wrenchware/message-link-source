package main

import (
	"context"
	"encoding/json"
	"errors"
	"slices"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// Creating a group is the one Lightspeed-only create here: there is no encrypted create-group task the way there is
// for a one-to-one, so what these hold is that the ordinary task is sent, that the thread Facebook names back is the
// one believed, and that a group which exists is never reported as one that does not.
func groupAdapter(t *testing.T) (*adapter, *[][]socket.Task) {
	t.Helper()
	a, _ := identityAdapter(t)
	var sent [][]socket.Task
	a.executeTasks = func(_ context.Context, tasks ...socket.Task) (*table.LSTable, error) {
		sent = append(sent, tasks)
		return &table.LSTable{}, nil
	}
	return a, &sent
}

func startGroup(t *testing.T, a *adapter, params string) (*viewCreatedGroup, string) {
	t.Helper()
	result, code := a.startGroup(context.Background(), request{Method: "ui.startGroup", Params: json.RawMessage(params)})
	if code != "" {
		return nil, code
	}
	created, ok := result.(*viewCreatedGroup)
	if !ok {
		t.Fatalf("expected a created group, got %T", result)
	}
	return created, ""
}

// The optimistic thread id this client invents is matched against the one Facebook answers with, and only the
// answered one is believed. Anything else would name a thread nobody made.
func TestCreatingAGroupSendsTheOrdinaryTaskAndBelievesTheAnsweredThread(t *testing.T) {
	a, sent := groupAdapter(t)
	a.executeTasks = func(_ context.Context, tasks ...socket.Task) (*table.LSTable, error) {
		*sent = append(*sent, tasks)
		create, ok := tasks[0].(*socket.CreateGroupTask)
		if !ok {
			return &table.LSTable{}, nil
		}
		return &table.LSTable{LSReplaceOptimisticThread: []*table.LSReplaceOptimisticThread{
			{ThreadKey1: create.SendPayload.ThreadID, ThreadKey2: 9001},
		}}, nil
	}

	created, code := startGroup(t, a, `{"personIds":["200","300"]}`)
	if code != "" {
		t.Fatalf("start group: %s", code)
	}
	if len(*sent) != 1 || len((*sent)[0]) != 1 {
		t.Fatalf("expected one task, got %v", *sent)
	}
	task, ok := (*sent)[0][0].(*socket.CreateGroupTask)
	if !ok {
		t.Fatalf("expected a create-group task, got %T", (*sent)[0][0])
	}
	if !slices.Equal(task.Participants, []int64{200, 300}) {
		t.Fatalf("the members were not carried: %v", task.Participants)
	}
	if task.SendPayload.SendType != 8 || task.SendPayload.Source != 0 || task.SendPayload.OTID == "" {
		t.Fatalf("unexpected create payload: %+v", task.SendPayload)
	}
	if task.SendPayload.ThreadID == 9001 {
		t.Fatal("the invented thread id was the answered one, so nothing was actually matched")
	}
	if created.ThreadID != "9001" {
		t.Fatalf("the answered thread was not reported: %+v", created)
	}
	if created.Named {
		t.Fatal("an unnamed group reported a name")
	}
}

// Facebook has no name field on the create, so naming is a second task against the thread it answered with.
func TestANamedGroupIsRenamedAfterItExists(t *testing.T) {
	a, sent := groupAdapter(t)
	a.executeTasks = func(_ context.Context, tasks ...socket.Task) (*table.LSTable, error) {
		*sent = append(*sent, tasks)
		if create, ok := tasks[0].(*socket.CreateGroupTask); ok {
			return &table.LSTable{LSReplaceOptimisticThread: []*table.LSReplaceOptimisticThread{
				{ThreadKey1: create.SendPayload.ThreadID, ThreadKey2: 9002},
			}}, nil
		}
		return &table.LSTable{}, nil
	}

	created, code := startGroup(t, a, `{"personIds":["200","300"],"title":"The Group"}`)
	if code != "" {
		t.Fatalf("start group: %s", code)
	}
	if len(*sent) != 2 {
		t.Fatalf("expected a create and a rename, got %d", len(*sent))
	}
	rename, ok := (*sent)[1][0].(*socket.RenameThreadTask)
	if !ok {
		t.Fatalf("expected a rename task, got %T", (*sent)[1][0])
	}
	if rename.ThreadKey != 9002 || rename.ThreadName != "The Group" || rename.SyncGroup != 1 {
		t.Fatalf("unexpected rename: %+v", rename)
	}
	if !created.Named {
		t.Fatal("an applied name was not reported")
	}
}

// A group that is made and then cannot be named is still a group. Reporting the whole thing as failed would invite
// making a second one.
func TestAGroupThatCannotBeNamedIsStillReported(t *testing.T) {
	a, _ := groupAdapter(t)
	a.executeTasks = func(_ context.Context, tasks ...socket.Task) (*table.LSTable, error) {
		if create, ok := tasks[0].(*socket.CreateGroupTask); ok {
			return &table.LSTable{LSReplaceOptimisticThread: []*table.LSReplaceOptimisticThread{
				{ThreadKey1: create.SendPayload.ThreadID, ThreadKey2: 9003},
			}}, nil
		}
		return nil, errors.New("rename refused")
	}

	created, code := startGroup(t, a, `{"personIds":["200","300"],"title":"The Group"}`)
	if code != "" {
		t.Fatalf("a made group was reported as the failure %q", code)
	}
	if created.ThreadID != "9003" || created.Named {
		t.Fatalf("unexpected result: %+v", created)
	}
}

// Facebook took the task but named no thread, or named one nobody asked for. The group may or may not exist, so it
// is reported as uncertain and never retried here.
func TestAGroupFacebookDoesNotNameIsUncertain(t *testing.T) {
	for name, reply := range map[string]*table.LSTable{
		"no thread":    {},
		"wrong thread": {LSReplaceOptimisticThread: []*table.LSReplaceOptimisticThread{{ThreadKey1: 1, ThreadKey2: 9004}}},
		"no real id":   {LSReplaceOptimisticThread: []*table.LSReplaceOptimisticThread{{ThreadKey1: 1, ThreadKey2: 0}}},
	} {
		t.Run(name, func(t *testing.T) {
			a, _ := groupAdapter(t)
			answer := reply
			calls := 0
			a.executeTasks = func(_ context.Context, _ ...socket.Task) (*table.LSTable, error) {
				calls++
				return answer, nil
			}
			if _, code := startGroup(t, a, `{"personIds":["200","300"]}`); code != "start_uncertain" {
				t.Fatalf("expected start_uncertain, got %q", code)
			}
			if calls != 1 {
				t.Fatalf("an uncertain create was attempted %d times", calls)
			}
		})
	}
}

func TestAGroupFacebookRefusesIsRejected(t *testing.T) {
	a, _ := groupAdapter(t)
	calls := 0
	a.executeTasks = func(_ context.Context, _ ...socket.Task) (*table.LSTable, error) {
		calls++
		return &table.LSTable{LSIssueNewError: []*table.LSIssueNewError{{ErrorMessage: "no"}}}, nil
	}
	if _, code := startGroup(t, a, `{"personIds":["200","300"]}`); code != "start_rejected" {
		t.Fatalf("expected start_rejected, got %q", code)
	}
	if calls != 1 {
		t.Fatalf("a rejected create was attempted %d times", calls)
	}
}

// Everything refused before the task must not reach Facebook at all.
func TestAGroupIsNotAttemptedWhenTheMembersAreWrong(t *testing.T) {
	a, sent := groupAdapter(t)
	for _, params := range []string{
		`{}`, `{"personIds":[]}`, `{"personIds":["200"]}`, `{"personIds":["200","nonsense"]}`,
		`{"personIds":["200","200"]}`, `{"personIds":["200","0"]}`, `{"personIds":["200","-3"]}`,
		// This account is in every group it makes; naming it as a member is a different group than the one asked for.
		`{"personIds":["200","1"]}`,
	} {
		if _, code := startGroup(t, a, params); code != "invalid_input" {
			t.Fatalf("%s was accepted as %q", params, code)
		}
	}
	if len(*sent) != 0 {
		t.Fatalf("a refused create still reached Facebook: %v", *sent)
	}
}
