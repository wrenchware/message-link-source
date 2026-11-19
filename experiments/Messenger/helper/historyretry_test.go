package main

import (
	"context"
	"errors"
	"sync/atomic"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func historyMarked(a *adapter, key int64) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	_, marked := a.asked[key]
	return marked
}

// A first fetch that failed leaves a conversation that holds messages to be asked again. The mark used to be set
// before the fetch, so every later open answered from memory, with only the message the list had named, until a restart.
func TestFailedFirstHistoryFetchIsTriedAgain(t *testing.T) {
	a := newAdapter(&output{})
	defer a.stop()
	a.chats.get(5).kind = table.ONE_TO_ONE
	a.chats.addEncrypted(5, "newest", 1, "synthetic", "", 1000)
	var asked atomic.Int32
	a.executeTasks = func(context.Context, ...socket.Task) (*table.LSTable, error) {
		asked.Add(1)
		return nil, errors.New("synthetic failure")
	}
	if _, code := a.history(context.Background(), 5, 50, 0); code != "history_failed" {
		t.Fatalf("first fetch answered %q", code)
	}
	if _, code := a.history(context.Background(), 5, 50, 0); code != "history_failed" {
		t.Fatalf("second open answered %q instead of asking again", code)
	}
	if asked.Load() != 2 {
		t.Fatalf("expected two fetches, got %d", asked.Load())
	}
}

// A conversation with nothing held keeps its wait after a failed fetch. The chat list asks about every such
// conversation after each change, and a failure that took the mark away sent one fetch per conversation each time.
func TestFailedFetchOfAnEmptyConversationWaitsBeforeAskingAgain(t *testing.T) {
	a := newAdapter(&output{})
	defer a.stop()
	thread := a.chats.get(5)
	thread.kind = table.ONE_TO_ONE
	thread.anchorAt, thread.anchorID = 1000, "named"
	var asked atomic.Int32
	a.executeTasks = func(context.Context, ...socket.Task) (*table.LSTable, error) {
		asked.Add(1)
		return nil, errors.New("synthetic failure")
	}
	if _, code := a.history(context.Background(), 5, 50, 0); code != "history_failed" {
		t.Fatalf("first fetch answered %q", code)
	}
	if _, code := a.history(context.Background(), 5, 50, 0); code != "" {
		t.Fatalf("second open answered %q instead of waiting", code)
	}
	if asked.Load() != 1 {
		t.Fatalf("an empty conversation was fetched %d times inside its wait", asked.Load())
	}
}

// One cancelled before Facebook answered is not taken as having been asked either.
func TestCancelledHistoryFetchIsTriedAgain(t *testing.T) {
	a := newAdapter(&output{})
	defer a.stop()
	a.chats.get(5).kind = table.ONE_TO_ONE
	a.chats.addEncrypted(5, "newest", 1, "synthetic", "", 1000)
	ctx, cancel := context.WithCancel(context.Background())
	a.executeTasks = func(ctx context.Context, _ ...socket.Task) (*table.LSTable, error) {
		cancel()
		return nil, ctx.Err()
	}
	a.history(ctx, 5, 50, 0)
	if historyMarked(a, 5) {
		t.Fatal("a fetch cancelled before its answer marked the conversation asked")
	}
}

// But one Facebook already answered keeps its mark when the caller goes away afterwards: it was asked.
func TestAnsweredHistoryFetchKeepsItsMarkWhenCancelled(t *testing.T) {
	a := newAdapter(&output{})
	defer a.stop()
	a.chats.get(5).kind = table.ONE_TO_ONE
	a.chats.addEncrypted(5, "newest", 1, "synthetic", "", 1000)
	ctx, cancel := context.WithCancel(context.Background())
	a.executeTasks = func(context.Context, ...socket.Task) (*table.LSTable, error) {
		cancel()
		return &table.LSTable{}, nil
	}
	a.history(ctx, 5, 50, 0)
	if !historyMarked(a, 5) {
		t.Fatal("a fetch Facebook answered lost its mark to a cancel")
	}
}

// A failed fetch of a conversation with nothing held waits under a mark of its own. As the asked mark, it turned into
// "fetched" the moment a live message arrived, and every later open answered from memory with that message alone.
func TestFailedEmptyFetchIsTriedAgainOnceAMessageArrives(t *testing.T) {
	a := newAdapter(&output{})
	defer a.stop()
	thread := a.chats.get(5)
	thread.kind = table.ONE_TO_ONE
	thread.anchorAt, thread.anchorID = 1000, "named"
	var asked atomic.Int32
	a.executeTasks = func(context.Context, ...socket.Task) (*table.LSTable, error) {
		asked.Add(1)
		return nil, errors.New("synthetic failure")
	}
	if _, code := a.history(context.Background(), 5, 50, 0); code != "history_failed" {
		t.Fatalf("first fetch answered %q", code)
	}
	a.chats.apply(&table.LSTable{
		LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 5, MessageId: "live", SenderId: 42, TimestampMs: 2000, Text: "synthetic"}},
	})
	a.history(context.Background(), 5, 50, 0)
	if asked.Load() != 2 {
		t.Fatalf("the open after a live message answered from memory (%d fetches)", asked.Load())
	}
}
