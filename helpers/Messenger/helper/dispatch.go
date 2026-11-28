package main

import (
	"bufio"
	"context"
	"encoding/json"
	"io"
	"sync"
	"time"
)

func lifecycleMethod(method string) bool {
	switch method {
	case "hello", "login", "connect", "disconnect", "session", "snapshot", "pair.start", "pair.finish", "reauthenticate", "ui.encrypted", "backup.open":
		return true
	}
	return false
}

func mutationMethod(method string) bool {
	switch method {
	case "ui.sendText", "ui.sendMedia", "ui.sendMediaBatch", "ui.mediaStage", "ui.mediaBatchStage", "ui.mediaBatchDiscard", "ui.react", "ui.markRead", "ui.typing", "ui.updateChat", "ui.deleteChat", "ui.deleteMessage", "ui.editMessage", "ui.startDirect", "ui.startGroup", "ui.renameGroup", "ui.addGroupMembers", "ui.removeGroupMember", "probe.receipt":
		return true
	}
	return false
}

// How long a lifecycle call has to stall before it is worth saying so. Short ones are the normal case.
const slowLifecycle = 500 * time.Millisecond

func dispatch(in io.Reader, out *output, call func(context.Context, request) (any, string)) {
	ctx, stop := context.WithCancel(context.Background())
	defer stop()
	var workers sync.WaitGroup
	var pending sync.Map // request ID -> cancel; includes queued mutations
	slots := make(chan struct{}, 64)
	tail := make(chan struct{})
	close(tail)
	scanner := bufio.NewScanner(in)
	scanner.Buffer(make([]byte, 4096), 1024*1024)
	run := func(deadline context.Context, r request) {
		var result any
		var code string
		if deadline.Err() == nil {
			result, code = call(deadline, r)
		} else {
			code = "request_cancelled"
		}
		out.write(response{ID: r.ID, Result: result, Error: code})
	}
	for scanner.Scan() {
		var cancellation struct {
			Cancel int `json:"cancel"`
		}
		if json.Unmarshal(scanner.Bytes(), &cancellation) == nil && cancellation.Cancel > 0 {
			if cancel, ok := pending.Load(cancellation.Cancel); ok {
				cancel.(context.CancelFunc)()
			}
			continue
		}
		var r request
		if json.Unmarshal(scanner.Bytes(), &r) != nil || r.ID <= 0 {
			out.write(response{Error: "invalid_request"})
			continue
		}
		if lifecycleMethod(r.Method) {
			// A lifecycle call stands still until every request already in flight is done, and then runs on this
			// loop rather than beside it — so nothing is read or answered for the whole of it. Both halves of that
			// are worth a number when they are long, because on a cold start the app is showing every conversation
			// as still updating until this sequence finishes.
			queued := time.Now()
			workers.Wait()
			waited := time.Since(queued)
			deadline, cancel := context.WithTimeout(ctx, 2*time.Minute)
			ran := time.Now()
			run(deadline, r)
			if held := time.Since(ran); waited >= slowLifecycle || held >= slowLifecycle {
				logf("lifecycle %s waited=%dms ran=%dms", r.Method, waited.Milliseconds(), held.Milliseconds())
			}
			cancel()
			continue
		}
		slots <- struct{}{}
		budget := 2 * time.Minute
		if r.Method == "ui.typing" {
			budget = 3 * time.Second
		}
		deadline, cancel := context.WithTimeout(ctx, budget)
		pending.Store(r.ID, cancel)
		workers.Add(1)
		previous := tail
		var finished chan struct{}
		if mutationMethod(r.Method) {
			finished = make(chan struct{})
			tail = finished
		}
		go func(r request, previous, finished chan struct{}) {
			defer workers.Done()
			defer cancel()
			defer pending.Delete(r.ID)
			defer func() { <-slots }()
			if finished != nil {
				defer close(finished)
				<-previous
			}
			run(deadline, r)
		}(r, previous, finished)
	}
	// EOF is teardown, not permission to finish queued contact-visible actions.
	stop()
	workers.Wait()
}
