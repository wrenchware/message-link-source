package main

import (
	"context"
	"time"
)

// Reopening the realtime connection after it drops, in --app mode only.
//
// The helper stops upstream's own reconnecting on purpose (handle returns the failure), and until typing nothing
// needed the connection once it had dropped: the list is kept fresh by the app's poll, not by this socket. Typing
// arrives on it, though, so without this typing stopped for good at the first sleep, wake or change of network. It
// reopens slowly — a minute, then doubling to fifteen — which is far below upstream's own pace of retrying within
// seconds, and only after a drop: never after Instagram refused the session, never after a resnapshot, whose cursor
// Instagram has said it will not continue from, and never after the flap guard stopped it (flap.go).

const (
	realtimeFirstWait   = time.Minute
	realtimeLongestWait = 15 * time.Minute
	// A connection that stayed up this long was not flapping, so the waits start over.
	realtimeSettled = 30 * time.Minute
	// The same patience connect has for Instagram's answer.
	realtimeConnectWait = 45 * time.Second
)

func reopenable(code string) bool { return code == "connection_failed" || code == "request_timeout" }

func realtimeWait(drops int) time.Duration {
	wait := realtimeFirstWait
	for i := 1; i < drops && wait < realtimeLongestWait; i++ {
		wait *= 2
	}
	return min(wait, realtimeLongestWait)
}

func (a *readOnlyAdapter) wait(d time.Duration) <-chan time.Time {
	if a.after != nil {
		return a.after(d)
	}
	return time.After(d)
}

// Watches one connection until it ends, then waits and opens another, for as long as this session lasts and is not
// refused. Ends when close() closes quit.
func (a *readOnlyAdapter) watchRealtime(generation uint64, done <-chan struct{}, quit <-chan struct{}) {
	opened, drops := time.Now(), 0
	for {
		select {
		case <-done:
		case <-quit:
			return
		}
		if time.Since(opened) >= realtimeSettled {
			drops = 0
		}
		drops++
		if !a.mayReopen(generation) {
			return
		}
		select {
		case <-a.wait(realtimeWait(drops)):
		case <-quit:
			return
		}
		next, current := a.reopenRealtime(generation)
		if !current {
			return
		}
		done, opened = next, time.Now()
	}
}

func (a *readOnlyAdapter) mayReopen(generation uint64) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.generation == generation && a.client != nil && !a.refusedRealtime && reopenable(a.snapshot.State)
}

// Starts the connection again and waits for Instagram's answer. a.op is held only to swap in the new connection's
// stop and done, never while waiting: a send or a conversation read must not stand behind a reconnect that may take
// most of a minute. A failure here ends only this attempt. It must never close the session, which is what connect()
// does with a failure and which would sign the app out over a network that happened to be down.
func (a *readOnlyAdapter) reopenRealtime(generation uint64) (<-chan struct{}, bool) {
	a.op.Lock()
	a.mu.Lock()
	if a.generation != generation || a.client == nil || a.refusedRealtime || !reopenable(a.snapshot.State) {
		a.mu.Unlock()
		a.op.Unlock()
		return nil, false
	}
	lifetime, stop := context.WithCancel(context.Background())
	ready, done := make(chan string, 1), make(chan struct{})
	a.ready, a.stop, a.done = ready, stop, done
	client := a.client
	a.mu.Unlock()
	a.op.Unlock()
	a.requests.typing.reopened.Add(1)
	go func() {
		defer close(done)
		defer func() {
			if recover() != nil {
				select {
				case ready <- "internal_error":
				default:
				}
			}
		}()
		client.Connect(lifetime)
	}()
	failed := ""
	select {
	case code := <-ready:
		// Connected, or handle() has recorded why not and stopped the connection already. A panic is not a drop and
		// is not tried again.
		if code != "internal_error" {
			return done, true
		}
		failed = code
	case <-done:
		select {
		case code := <-ready:
			if code != "internal_error" {
				return done, true
			}
			failed = code
		default:
			failed = "connection_failed"
		}
	case <-a.wait(realtimeConnectWait):
		failed = "request_timeout"
	}
	// Ended without a word from the connection, or without one in time: recorded here so the next attempt is judged
	// by it, and stopped so that nothing of this attempt lingers.
	a.mu.Lock()
	if a.generation == generation {
		a.snapshot.State = failed
	}
	a.mu.Unlock()
	stop()
	return done, true
}
