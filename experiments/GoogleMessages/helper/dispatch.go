package main

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"io"
	"sync"
	"sync/atomic"
	"time"
)

// A typing ping still queued or on its way when a message is sent. Typing waits in the mutation queue so a late one
// does not reach the phone after the message it led to (Google has no stop to take it back); a send queued behind one
// that was waiting out a slow relay waited with it, for up to its three seconds. One not started yet is dropped. One
// already posted keeps typingGrace to be answered: letting go of it cannot take back what the relay accepted, so a
// stalled one may still arrive after the message, as it could when its own three seconds ran out.
var errTypingSuperseded = errors.New("typing superseded by a send")

var typingGrace = 500 * time.Millisecond

type typingWait struct {
	supersede context.CancelCauseFunc
	started   atomic.Bool
}

// What ends any typing still waiting: the message that typing was for.
func supersedesTyping(method string) bool {
	return method == "ui.sendText" || method == "ui.sendMedia"
}

func lifecycleMethod(method string) bool {
	switch method {
	case "hello", "login", "connect", "disconnect", "session", "snapshot", "pair.start", "pair.finish", "pair.qrStart", "reauthenticate", "ui.encrypted":
		return true
	}
	return false
}

func mutationMethod(method string) bool {
	switch method {
	case "ui.sendText", "ui.sendMedia", "ui.mediaStage", "ui.react", "ui.markRead", "ui.typing", "ui.updateChat", "ui.deleteChat", "ui.deleteMessage", "ui.editMessage", "ui.startDirect", "ui.startGroup", "probe.receipt":
		return true
	}
	return false
}

func requestBudget(method string) time.Duration {
	// Google advertises the phone emoji challenge for five minutes. Keep a little
	// transport headroom instead of aborting the helper's finish request at the
	// generic two-minute deadline while the user is still choosing on the phone.
	if method == "pair.finish" {
		return 6 * time.Minute
	}
	// Six codes of thirty seconds (qr_pairing.go), with a little headroom. Not a lifecycle method: it must be cancellable
	// while it waits, which a request the reader runs itself cannot be.
	if method == "pair.qrWait" {
		return 4 * time.Minute
	}
	if method == "ui.typing" {
		return 3 * time.Second
	}
	return 2 * time.Minute
}

func dispatch(in io.Reader, out *output, call func(context.Context, request) (any, string)) {
	ctx, stop := context.WithCancel(context.Background())
	defer stop()
	var workers sync.WaitGroup
	var pending sync.Map // request ID -> cancel; includes queued mutations
	var typings sync.Map // request ID -> *typingWait, for ui.typing queued or running
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
		} else if errors.Is(context.Cause(deadline), errTypingSuperseded) {
			result = map[string]bool{"skipped": true}
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
			deadline, cancel := context.WithTimeout(ctx, requestBudget(r.Method))
			run(deadline, r)
			cancel()
			continue
		}
		if supersedesTyping(r.Method) {
			typings.Range(func(_, value any) bool {
				waiting := value.(*typingWait)
				if waiting.started.Load() {
					time.AfterFunc(typingGrace, func() { waiting.supersede(errTypingSuperseded) })
				} else {
					waiting.supersede(errTypingSuperseded)
				}
				return true
			})
		}
		slots <- struct{}{}
		budget := requestBudget(r.Method)
		deadline, cancel := context.WithTimeout(ctx, budget)
		var waiting *typingWait
		if r.Method == "ui.typing" {
			waiting = &typingWait{}
			deadline, waiting.supersede = context.WithCancelCause(deadline)
			typings.Store(r.ID, waiting)
		}
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
			defer typings.Delete(r.ID)
			defer func() { <-slots }()
			if finished != nil {
				defer close(finished)
				<-previous
			}
			if waiting != nil {
				waiting.started.Store(true)
			}
			run(deadline, r)
		}(r, previous, finished)
	}
	// EOF is teardown, not permission to finish queued contact-visible actions.
	stop()
	workers.Wait()
}
