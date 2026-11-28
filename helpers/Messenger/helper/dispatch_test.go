package main

import (
	"context"
	"encoding/json"
	"io"
	"sync"
	"testing"
	"time"
)

func waitDispatch(t *testing.T, ch <-chan struct{}) {
	t.Helper()
	select {
	case <-ch:
	case <-time.After(3 * time.Second):
		t.Fatal("request blocked")
	}
}

func TestConcurrentDispatchAndCancellation(t *testing.T) {
	input, writer := io.Pipe()
	outputReader, outputWriter := io.Pipe()
	started := make(chan struct{})
	finished := make(chan struct{})
	done := make(chan struct{})
	go func() {
		defer close(done)
		dispatch(input, &output{encoder: json.NewEncoder(outputWriter)}, func(ctx context.Context, r request) (any, string) {
			if r.Method == "ui.messages" {
				close(started)
				<-ctx.Done()
				close(finished)
				return nil, "request_cancelled"
			}
			return map[string]any{"method": r.Method}, ""
		})
	}()
	replies := make(chan response, 10)
	go func() {
		decoder := json.NewDecoder(outputReader)
		for {
			var r response
			if decoder.Decode(&r) != nil {
				return
			}
			replies <- r
		}
	}()
	send := json.NewEncoder(writer)
	send.Encode(request{ID: 1, Method: "ui.messages"})
	waitDispatch(t, started)
	for id, method := range []string{"ui.mediaRead", "ui.avatar", "ui.linkPreviews"} {
		send.Encode(request{ID: id + 2, Method: method})
	}
	seen := map[int]bool{}
	for range 3 {
		select {
		case r := <-replies:
			seen[r.ID] = true
		case <-time.After(3 * time.Second):
			t.Fatal("read categories blocked behind history")
		}
	}
	if !seen[2] || !seen[3] || !seen[4] {
		t.Fatal("out-of-order request IDs lost")
	}
	send.Encode(map[string]int{"cancel": 1})
	waitDispatch(t, finished)
	select {
	case r := <-replies:
		if r.ID != 1 || r.Error != "request_cancelled" {
			t.Fatal("wrong cancellation reply")
		}
	case <-time.After(3 * time.Second):
		t.Fatal("missing cancellation reply")
	}
	writer.Close()
	waitDispatch(t, done)
	outputWriter.Close()
	outputReader.Close()
}

func TestMutationOrderLifecycleAndEOF(t *testing.T) {
	input, writer := io.Pipe()
	outputReader, outputWriter := io.Pipe()
	var mu sync.Mutex
	order := []int{}
	started := make(chan struct{})
	release := make(chan struct{})
	done := make(chan struct{})
	go func() {
		defer close(done)
		dispatch(input, &output{encoder: json.NewEncoder(outputWriter)}, func(ctx context.Context, r request) (any, string) {
			if r.ID == 1 {
				close(started)
				<-release
			}
			mu.Lock()
			order = append(order, r.ID)
			mu.Unlock()
			return map[string]bool{"ok": true}, ""
		})
	}()
	replies := make(chan response, 10)
	go func() {
		decoder := json.NewDecoder(outputReader)
		for {
			var r response
			if decoder.Decode(&r) != nil {
				return
			}
			replies <- r
		}
	}()
	encoder := json.NewEncoder(writer)
	encoder.Encode(request{ID: 1, Method: "ui.mediaStage"})
	waitDispatch(t, started)
	encoder.Encode(request{ID: 2, Method: "ui.sendMedia"})
	barrierSent := make(chan struct{})
	go func() { encoder.Encode(request{ID: 3, Method: "snapshot"}); close(barrierSent) }()
	waitDispatch(t, barrierSent)
	mu.Lock()
	if len(order) != 0 {
		t.Fatal("mutation or snapshot overtook prior mutation")
	}
	mu.Unlock()
	close(release)
	for range 3 {
		select {
		case <-replies:
		case <-time.After(3 * time.Second):
			t.Fatal("barrier deadlocked")
		}
	}
	mu.Lock()
	if len(order) != 3 || order[0] != 1 || order[1] != 2 || order[2] != 3 {
		t.Fatal("mutation/session order changed")
	}
	mu.Unlock()
	writer.Close()
	waitDispatch(t, done)
	outputWriter.Close()
	outputReader.Close()
}

func TestEOFDoesNotExecuteQueuedMutation(t *testing.T) {
	input, writer := io.Pipe()
	outputReader, outputWriter := io.Pipe()
	go io.Copy(io.Discard, outputReader)
	started := make(chan struct{})
	done := make(chan struct{})
	var calls sync.Map
	go func() {
		defer close(done)
		dispatch(input, &output{encoder: json.NewEncoder(outputWriter)}, func(ctx context.Context, r request) (any, string) {
			calls.Store(r.ID, true)
			if r.ID == 1 {
				close(started)
				<-ctx.Done()
			}
			return nil, "request_cancelled"
		})
	}()
	encoder := json.NewEncoder(writer)
	encoder.Encode(request{ID: 1, Method: "ui.sendText"})
	waitDispatch(t, started)
	encoder.Encode(request{ID: 2, Method: "ui.markRead"})
	writer.Close()
	waitDispatch(t, done)
	if _, ran := calls.Load(2); ran {
		t.Fatal("queued read marker ran after EOF")
	}
	outputWriter.Close()
	outputReader.Close()
}
