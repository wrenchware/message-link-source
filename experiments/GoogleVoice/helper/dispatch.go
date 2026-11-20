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
	case "hello", "login", "connect", "disconnect", "session":
		return true
	}
	return false
}

// Changes to the account run one at a time, in the order asked: marking read, archiving and deleting a conversation,
// which change only this account, and sending, which reaches other people.
func mutationMethod(method string) bool {
	switch method {
	case "ui.markRead", "ui.updateChat", "ui.deleteChat", "ui.sendText", "ui.mediaStage", "ui.sendMedia":
		return true
	}
	return false
}

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
			result, code = guardedCall(call, deadline, r)
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
			workers.Wait()
			deadline, cancel := context.WithTimeout(ctx, 2*time.Minute)
			run(deadline, r)
			cancel()
			continue
		}
		slots <- struct{}{}
		deadline, cancel := context.WithTimeout(ctx, 2*time.Minute)
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

// A panic inside one request answers that request with a fixed code instead of ending the helper, and every other
// request and the session with it. Neither the panic's value nor its stack leaves the process: either can carry
// message text or protocol data.
func guardedCall(call func(context.Context, request) (any, string), ctx context.Context, r request) (result any, code string) {
	defer func() {
		if recover() != nil {
			result, code = nil, "internal_error"
		}
	}()
	return call(ctx, r)
}
