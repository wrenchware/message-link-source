package main

import (
	"io"
	"sync"
	"time"

	"github.com/rs/zerolog"
)

// A guard against the typing stream flapping.
//
// The typing stream is upstream's own socket, and upstream reopens it by itself: after a failure it waits one second,
// then two, doubling to a minute, but after a connection that had started it reopens at once. A stream Instagram keeps
// accepting and dropping would therefore be redialled back to back for as long as the process lives, which is exactly
// the kind of traffic that risks a checkpoint on the real account. Nothing upstream reports those reopenings except its
// log, so the application's client has a logger that keeps nothing and only listens for them.
//
// More than thirty in an hour ends the realtime connection for this session, with the state typing_unstable, which is
// not reopened. The poll keeps the list fresh without it. The count is kept for the life of the process, so a new
// sign-in into a stream still flapping stops again at the next reopening rather than after thirty more.

const (
	streamFlapWindow = time.Hour
	streamFlapLimit  = 30
)

// Upstream's words for reopening the stream (instameow/streamcontroller.go), the only thing the hook reads. A test
// reads them in upstream's source, so an update that rewords them fails there rather than silently disarming this.
const (
	streamReopenedAfterDrop    = "Socket disconnected, reconnecting immediately"
	streamReopenedAfterFailure = "Connection failed, reconnecting..."
)

// The application's client logger: written to nothing, at the level upstream reports an immediate reopening. The hook
// sees each message's fixed text and never its fields, which is where anything private would be.
func appLogger(reopened func()) zerolog.Logger {
	return zerolog.New(io.Discard).Level(zerolog.DebugLevel).Hook(streamHook{reopened})
}

type streamHook struct{ reopened func() }

func (h streamHook) Run(_ *zerolog.Event, _ zerolog.Level, message string) {
	if message == streamReopenedAfterDrop || message == streamReopenedAfterFailure {
		h.reopened()
	}
}

// The reopenings within the last hour.
type streamFlaps struct {
	mu     sync.Mutex
	recent []time.Time
}

// Records one reopening at a time and says whether the hour now holds more than the limit.
func (f *streamFlaps) note(at time.Time) bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	kept := f.recent[:0]
	for _, earlier := range f.recent {
		if at.Sub(earlier) < streamFlapWindow {
			kept = append(kept, earlier)
		}
	}
	f.recent = append(kept, at)
	return len(f.recent) > streamFlapLimit
}

// Upstream reopened the typing stream. Past the limit, the realtime connection of that session is stopped and said
// to be unstable, which the reopening watcher does not reopen; the app hears it in a counts frame, as any change of
// state. Called on upstream's goroutine, holding none of this adapter's locks.
func (a *readOnlyAdapter) streamReopened(generation uint64) {
	a.requests.typing.reconnects.Add(1)
	if !a.flaps.note(time.Now()) {
		return
	}
	a.mu.Lock()
	// Only a connection already up is stopped. One still connecting would fail its connect, and the app would sign in
	// again to try once more: more traffic, not less. It is stopped at the first reopening after it is up.
	if generation != a.generation || a.snapshot.State != "connected" {
		a.mu.Unlock()
		return
	}
	a.snapshot.State = "typing_unstable"
	stop := a.stop
	counts := a.snapshot
	counts.Account, counts.AccountName = "", ""
	a.mu.Unlock()
	if stop != nil {
		stop()
	}
	_ = a.out.write(response{Event: "counts", Result: counts})
}
