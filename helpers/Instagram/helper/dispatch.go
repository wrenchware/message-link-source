package main

import (
	"bufio"
	"context"
	"encoding/json"
	"io"
	"sync"
	"sync/atomic"
	"time"
)

func lifecycleMethod(method string) bool {
	switch method {
	case "hello", "login", "connect", "disconnect", "session", "snapshot",
		"ui.chats", "ui.chat", "ui.messages", "ui.mediaRead", "ui.sendText", "ui.markRead", "ui.react", "ui.editMessage", "ui.deleteMessage", "ui.mediaStage", "ui.sendMedia",
		"ui.searchUsers", "ui.startDirect", "ui.startDirectWithText", "ui.createGroup":
		return true
	}
	return false
}

// Calls that change the account, or stage a file for one that will. Once started, one runs to the end under its own
// budget whatever happens to the pipe: cutting it short would leave a message that may or may not have gone, which
// is exactly the uncertainty nothing here may create. One still waiting when input ends is never started — the end of
// input is teardown, not permission to finish somebody's queued message.
func mutationMethod(method string) bool {
	switch method {
	case "ui.sendText", "ui.sendMedia", "ui.mediaStage", "ui.markRead", "ui.react", "ui.editMessage", "ui.deleteMessage",
		"ui.startDirectWithText", "ui.createGroup":
		return true
	}
	return false
}

// How long one call in the queue may take.
const callBudget = 2 * time.Minute

// A call answered beside the serial queue rather than in it. Only typing is one: it is worth nothing late, so it must
// never wait behind a send or a history read, and a send must never wait behind it.
type sideCall func(context.Context, request) (any, string)

// How long a call beside the queue may take. Opening the typing socket alone may wait five seconds.
const sideBudget = 6 * time.Second

type job struct {
	r        request
	ctx      context.Context
	cancel   context.CancelFunc
	mutation bool
	refusal  string // answered without calling anything: a malformed frame or a method this helper does not have
}

// Calls still execute one at a time and in the order they arrived, so a.op and every ordering the app relies on are
// what they were. What changed is that the pipe is read while one runs. A read the app has stopped waiting for — it
// sends {"cancel":id} for exactly those — is aborted if it is running and never started if it is queued, instead of
// still asking Instagram and holding the conversation actually on screen behind it; and the end of input is seen at
// once, where it used to wait up to two minutes for whatever was running. Every request still gets exactly one reply.
// cancelled, when set, counts each request answered request_cancelled, however it came to be.
func dispatch(in io.Reader, out *output, call func(context.Context, request) (any, string), side map[string]sideCall, cancelled func()) error {
	root, stop := context.WithCancel(context.Background())
	defer stop()
	var closing atomic.Bool
	var failed error
	var pending sync.Map // request ID -> *job, until its reply is written
	work := make(chan *job, 256)
	finished := make(chan struct{})
	go func() {
		defer close(finished)
		for j := range work {
			if failed != nil {
				pending.Delete(j.r.ID) // The app is gone; draining only keeps the reader from blocking.
				continue
			}
			reply := response{ID: j.r.ID, Error: j.refusal}
			if reply.Error == "" {
				if j.ctx.Err() != nil || j.mutation && closing.Load() {
					reply.Error = "request_cancelled"
				} else {
					ctx, cancel := context.WithTimeout(j.ctx, callBudget)
					reply.Result, reply.Error = call(ctx, j.r)
					cancel()
				}
			}
			j.cancel()
			pending.Delete(j.r.ID)
			if reply.Error == "request_cancelled" && cancelled != nil {
				cancelled()
			}
			if err := out.write(reply); err != nil {
				failed = err
				stop()
			}
		}
	}()
	beside := newSideQueue(out)
	scanner := bufio.NewScanner(in)
	scanner.Buffer(make([]byte, 4096), 1024*1024)
	for scanner.Scan() {
		var cancellation struct {
			Cancel int `json:"cancel"`
		}
		if json.Unmarshal(scanner.Bytes(), &cancellation) == nil && cancellation.Cancel > 0 {
			// Only a read is ever cancelled, whatever the frame names. The app never cancels a mutation, and one that
			// said so would still be answered with what happened rather than cut off halfway.
			if found, ok := pending.Load(cancellation.Cancel); ok && !found.(*job).mutation {
				found.(*job).cancel()
			}
			continue
		}
		// Unmarshal copies what it keeps, so the scanner's buffer may be reused by the next line.
		var r request
		j := &job{}
		if json.Unmarshal(scanner.Bytes(), &r) != nil || r.ID <= 0 || r.Method == "" {
			j.refusal = "invalid_request"
			r = request{}
		} else if handler, ok := side[r.Method]; ok {
			beside.submit(r, handler)
			continue
		} else if !lifecycleMethod(r.Method) {
			j.refusal = "unknown_method"
		}
		j.r, j.mutation = r, mutationMethod(r.Method)
		parent := root
		if j.mutation {
			parent = context.Background() // Never cut off once started; see mutationMethod.
		}
		j.ctx, j.cancel = context.WithCancel(parent)
		if r.ID > 0 {
			pending.Store(r.ID, j)
		}
		work <- j
	}
	// Teardown: reads stop now, queued work is answered without being started, and a mutation already running
	// finishes under its own budget.
	closing.Store(true)
	stop()
	close(work)
	<-finished
	beside.close()
	if failed != nil {
		return failed
	}
	if scanner.Err() != nil {
		if err := out.write(response{Error: "input_failed"}); err != nil {
			return err
		}
		return scanner.Err()
	}
	return nil
}

// Calls beside the queue: one runs at a time, and at most one more waits per conversation. A newer one for the same
// conversation replaces the one waiting, which is answered skipped, because only the latest state is worth sending —
// but it is never dropped in favour of an older one, so a "stopped typing" cannot be lost behind the "started typing"
// still being sent, which would leave the other person watching dots until Instagram's own timeout.
type sideQueue struct {
	mu      sync.Mutex
	out     *output
	order   []string
	waiting map[string]sideItem
	busy    bool
	closed  bool
	idle    sync.WaitGroup
}

type sideItem struct {
	r       request
	handler sideCall
}

func newSideQueue(out *output) *sideQueue {
	return &sideQueue{out: out, waiting: map[string]sideItem{}}
}

func sideKey(r request) string {
	var params struct {
		ChatID string `json:"chatId"`
	}
	_ = json.Unmarshal(r.Params, &params)
	return r.Method + "\x00" + params.ChatID
}

func (q *sideQueue) submit(r request, handler sideCall) {
	key := sideKey(r)
	q.mu.Lock()
	defer q.mu.Unlock()
	if earlier, ok := q.waiting[key]; ok {
		_ = q.out.write(response{ID: earlier.r.ID, Result: map[string]any{"sent": false, "skipped": true}})
	} else {
		q.order = append(q.order, key)
	}
	q.waiting[key] = sideItem{r, handler}
	if !q.busy && !q.closed {
		q.busy = true
		q.idle.Add(1)
		go q.drain()
	}
}

func (q *sideQueue) drain() {
	defer q.idle.Done()
	for {
		q.mu.Lock()
		if len(q.order) == 0 || q.closed {
			q.busy = false
			q.mu.Unlock()
			return
		}
		key := q.order[0]
		q.order = q.order[1:]
		item := q.waiting[key]
		delete(q.waiting, key)
		q.mu.Unlock()
		ctx, cancel := context.WithTimeout(context.Background(), sideBudget)
		result, code := func() (result any, code string) {
			defer func() {
				if recover() != nil {
					result, code = nil, "internal_error"
				}
			}()
			return item.handler(ctx, item.r)
		}()
		cancel()
		_ = q.out.write(response{ID: item.r.ID, Result: result, Error: code})
	}
}

// At the end of input the one running finishes, within its budget, and nothing waiting is started.
func (q *sideQueue) close() {
	q.mu.Lock()
	q.closed = true
	for _, key := range q.order {
		_ = q.out.write(response{ID: q.waiting[key].r.ID, Error: "request_cancelled"})
	}
	q.order, q.waiting = nil, map[string]sideItem{}
	q.mu.Unlock()
	q.idle.Wait()
}
