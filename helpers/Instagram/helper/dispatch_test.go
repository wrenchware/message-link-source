package main

import (
	"context"
	"encoding/json"
	"io"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// A dispatcher on pipes, driven one line at a time, with its answers collected by id as they arrive.
type liveDispatch struct {
	t       *testing.T
	in      *io.PipeWriter
	replies chan response
	served  chan error
}

func startDispatch(t *testing.T, call func(context.Context, request) (any, string), side map[string]sideCall, cancelled func()) *liveDispatch {
	t.Helper()
	inRead, inWrite := io.Pipe()
	outRead, outWrite := io.Pipe()
	d := &liveDispatch{t: t, in: inWrite, replies: make(chan response, 64), served: make(chan error, 1)}
	go func() {
		err := dispatch(inRead, &output{encoder: json.NewEncoder(outWrite)}, call, side, cancelled)
		outWrite.Close()
		d.served <- err
	}()
	go func() {
		decoder := json.NewDecoder(outRead)
		for {
			var reply response
			if decoder.Decode(&reply) != nil {
				close(d.replies)
				return
			}
			d.replies <- reply
		}
	}()
	return d
}

func (d *liveDispatch) send(line string) {
	d.t.Helper()
	if _, err := d.in.Write([]byte(line + "\n")); err != nil {
		d.t.Fatal(err)
	}
}

func (d *liveDispatch) next() response {
	d.t.Helper()
	select {
	case reply, ok := <-d.replies:
		if !ok {
			d.t.Fatal("the dispatcher stopped answering")
		}
		return reply
	case <-time.After(5 * time.Second):
		d.t.Fatal("no answer")
	}
	return response{}
}

// Ends input and says how long the dispatcher took to return.
func (d *liveDispatch) end() time.Duration {
	d.t.Helper()
	started := time.Now()
	d.in.Close()
	select {
	case err := <-d.served:
		if err != nil {
			d.t.Fatal(err)
		}
	case <-time.After(5 * time.Second):
		d.t.Fatal("the dispatcher did not return at the end of input")
	}
	return time.Since(started)
}

// A call that stands still until it is released or its context ends, and says which it was.
type heldCall struct {
	mu       sync.Mutex
	started  map[int]chan struct{}
	release  map[int]chan struct{}
	finished map[int]string
}

func newHeldCall() *heldCall {
	return &heldCall{started: map[int]chan struct{}{}, release: map[int]chan struct{}{}, finished: map[int]string{}}
}

func (h *heldCall) channels(id int) (chan struct{}, chan struct{}) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.started[id] == nil {
		h.started[id], h.release[id] = make(chan struct{}), make(chan struct{})
	}
	return h.started[id], h.release[id]
}

func (h *heldCall) call(ctx context.Context, r request) (any, string) {
	started, release := h.channels(r.ID)
	close(started)
	outcome := "released"
	select {
	case <-release:
	case <-ctx.Done():
		outcome = "cancelled"
	}
	h.mu.Lock()
	h.finished[r.ID] = outcome
	h.mu.Unlock()
	if outcome == "cancelled" {
		return nil, "request_cancelled"
	}
	return true, ""
}

func (h *heldCall) waitStarted(t *testing.T, id int) {
	t.Helper()
	started, _ := h.channels(id)
	select {
	case <-started:
	case <-time.After(5 * time.Second):
		t.Fatalf("call %d never started", id)
	}
}

func (h *heldCall) ran(id int) (string, bool) {
	h.mu.Lock()
	defer h.mu.Unlock()
	outcome, ok := h.finished[id]
	return outcome, ok
}

// The app cancels a read it has stopped waiting for. One still queued is answered without ever reaching Instagram,
// and one running is aborted, so clicking through conversations no longer leaves a queue of reads for ones already
// gone from the screen. Each still gets exactly one answer.
func TestACancelledReadIsSkippedOrAborted(t *testing.T) {
	held := newHeldCall()
	var cancelled atomic.Int32
	d := startDispatch(t, held.call, nil, func() { cancelled.Add(1) })
	d.send(`{"id":1,"method":"ui.messages"}`)
	held.waitStarted(t, 1)
	d.send(`{"id":2,"method":"ui.chat"}`)
	d.send(`{"cancel":2}`)
	d.send(`{"cancel":1}`)
	first, second := d.next(), d.next()
	if first.ID != 1 || first.Error != "request_cancelled" || second.ID != 2 || second.Error != "request_cancelled" {
		t.Fatalf("cancelled reads were not answered in order as cancelled: %+v %+v", first, second)
	}
	if outcome, _ := held.ran(1); outcome != "cancelled" {
		t.Fatalf("the running read was not aborted: %q", outcome)
	}
	if _, ran := held.ran(2); ran {
		t.Fatal("a read cancelled while queued still ran")
	}
	if cancelled.Load() != 2 {
		t.Fatalf("cancelled answers were not counted: %d", cancelled.Load())
	}
	// A cancellation that names a mutation changes nothing: the app never sends one, and a send cut off halfway is
	// the uncertainty nothing here may create.
	d.send(`{"id":3,"method":"ui.sendText"}`)
	held.waitStarted(t, 3)
	d.send(`{"cancel":3}`)
	_, release := held.channels(3)
	close(release)
	if reply := d.next(); reply.ID != 3 || reply.Error != "" {
		t.Fatalf("a mutation was cut off by a cancellation: %+v", reply)
	}
	d.end()
}

// The end of input is seen at once, not after whatever is running: a read in progress is aborted and the helper
// returns promptly, where it used to wait out the call's two-minute budget.
func TestEndOfInputAbortsARunningRead(t *testing.T) {
	held := newHeldCall()
	d := startDispatch(t, held.call, nil, nil)
	d.send(`{"id":1,"method":"ui.chats"}`)
	held.waitStarted(t, 1)
	if took := d.end(); took > 2*time.Second {
		t.Fatalf("the end of input waited for the read: %v", took)
	}
	if outcome, _ := held.ran(1); outcome != "cancelled" {
		t.Fatalf("the read kept running after the end of input: %q", outcome)
	}
}

// A send already running when input ends finishes under its own budget, so it is never left uncertain because the
// pipe closed; one still queued behind it is never started.
func TestEndOfInputLetsARunningSendFinishAndStartsNoOther(t *testing.T) {
	held := newHeldCall()
	d := startDispatch(t, held.call, nil, nil)
	d.send(`{"id":1,"method":"ui.sendText"}`)
	held.waitStarted(t, 1)
	d.send(`{"id":2,"method":"ui.sendText"}`)
	d.in.Close()
	time.Sleep(100 * time.Millisecond)
	if _, done := held.ran(1); done {
		t.Fatal("the running send was cut off by the end of input")
	}
	_, release := held.channels(1)
	close(release)
	select {
	case err := <-d.served:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("the dispatcher did not return once the send finished")
	}
	if outcome, _ := held.ran(1); outcome != "released" {
		t.Fatalf("the running send did not finish: %q", outcome)
	}
	if _, ran := held.ran(2); ran {
		t.Fatal("a send queued at the end of input was started")
	}
	if first, second := d.next(), d.next(); first.ID != 1 || first.Error != "" || second.ID != 2 || second.Error != "request_cancelled" {
		t.Fatalf("each send was not answered once with what happened to it: %+v %+v", first, second)
	}
}

// Typing is answered beside the queue: never behind a slow call, and a send never behind it. One runs at a time; a
// newer one for the same conversation replaces the one waiting, but a stop is never lost behind the start still
// being sent.
func TestTypingRunsBesideASlowCall(t *testing.T) {
	held := newHeldCall()
	typing := newHeldCall()
	var order []string
	var orderMu sync.Mutex
	side := map[string]sideCall{"ui.typing": func(ctx context.Context, r request) (any, string) {
		var p struct {
			Typing bool `json:"typing"`
		}
		_ = json.Unmarshal(r.Params, &p)
		orderMu.Lock()
		order = append(order, map[bool]string{true: "start", false: "stop"}[p.Typing])
		orderMu.Unlock()
		return typing.call(ctx, r)
	}}
	d := startDispatch(t, held.call, side, nil)
	d.send(`{"id":1,"method":"ui.messages"}`)
	held.waitStarted(t, 1)
	d.send(`{"id":2,"method":"ui.typing","params":{"chatId":"a","typing":true}}`)
	typing.waitStarted(t, 2)
	_, release := typing.channels(2)
	close(release)
	if reply := d.next(); reply.ID != 2 {
		t.Fatalf("typing waited behind the slow call: %+v", reply)
	}
	// A start in flight, then a start and a stop for the same conversation while it is: the start waiting is
	// replaced and answered skipped, and the stop runs once the first is done.
	d.send(`{"id":3,"method":"ui.typing","params":{"chatId":"a","typing":true}}`)
	typing.waitStarted(t, 3)
	d.send(`{"id":4,"method":"ui.typing","params":{"chatId":"a","typing":true}}`)
	d.send(`{"id":5,"method":"ui.typing","params":{"chatId":"a","typing":false}}`)
	if reply := d.next(); reply.ID != 4 || reply.Error != "" {
		t.Fatalf("the replaced start was not answered skipped: %+v", reply)
	} else if result, _ := reply.Result.(map[string]any); result["skipped"] != true {
		t.Fatalf("the replaced start did not say it was skipped: %+v", reply.Result)
	}
	_, release = typing.channels(3)
	close(release)
	typing.waitStarted(t, 5)
	_, release = typing.channels(5)
	close(release)
	got := map[int]bool{}
	for len(got) < 2 {
		got[d.next().ID] = true
	}
	if !got[3] || !got[5] {
		t.Fatalf("typing answers went missing: %v", got)
	}
	orderMu.Lock()
	if len(order) != 3 || order[2] != "stop" {
		t.Fatalf("the stop did not follow the start it replaced: %v", order)
	}
	orderMu.Unlock()
	_, release = held.channels(1)
	close(release)
	if reply := d.next(); reply.ID != 1 || reply.Error != "" {
		t.Fatalf("the slow call was disturbed: %+v", reply)
	}
	d.end()
}
