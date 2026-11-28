package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"sync"
	"testing"
)

func TestConcurrentMediaCachePublishesImmutableEntries(t *testing.T) {
	a := newAdapter(&output{encoder: json.NewEncoder(&bytes.Buffer{})})
	defer a.stop()
	var workers sync.WaitGroup
	for worker := range 12 {
		workers.Add(1)
		go func(worker int) {
			defer workers.Done()
			for range 100 {
				id := fmt.Sprintf("synthetic-%d", worker)
				held := &mediaCache{id: id, data: []byte(id)}
				a.keep(held)
				if read := a.cached(id); read != nil && (read.id != id || string(read.data) != id) {
					t.Error("cache entry changed identity")
				}
				if string(held.data) != id {
					t.Error("published bytes mutated")
				}
			}
		}(worker)
	}
	workers.Wait()
	a.mediaMu.Lock()
	defer a.mediaMu.Unlock()
	if len(a.media) > mediaKeep {
		t.Fatal("cache became unbounded")
	}
	seen := map[string]bool{}
	for _, held := range a.media {
		if seen[held.id] {
			t.Fatal("concurrent cache duplicated identity")
		}
		seen[held.id] = true
	}
}

func TestWaitingHistoryCancellationDoesNotAffectAnotherConversation(t *testing.T) {
	a := newAdapter(&output{encoder: json.NewEncoder(&bytes.Buffer{})})
	defer a.stop()
	occupied := make(chan struct{}, 1)
	occupied <- struct{}{}
	a.historyLocks.Store(int64(1), occupied)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, code := a.history(ctx, 1, 50, 0); code != "request_cancelled" {
		t.Fatal("waiting history ignored cancellation")
	}
	if len(occupied) != 1 {
		t.Fatal("cancelled waiter released another request's lock")
	}
	if _, code := a.history(context.Background(), 2, 50, 0); code != "" {
		t.Fatal("another conversation blocked behind occupied history")
	}
}

func TestMediaBudgetLeavesAvatarAndPreviewSlotsAvailable(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	for range 4 {
		release, ok := a.acquireDownload(context.Background(), false, false)
		if !ok {
			t.Fatal("missing original slot")
		}
		defer release()
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, ok := a.acquireDownload(ctx, false, false); ok {
		t.Fatal("cancelled original acquired slot")
	}
	for _, kind := range []int{1, 2} {
		release, ok := a.acquireDownload(context.Background(), kind == 1, kind == 2)
		if !ok {
			t.Fatal("avatar/preview blocked behind originals")
		}
		release()
	}
}
