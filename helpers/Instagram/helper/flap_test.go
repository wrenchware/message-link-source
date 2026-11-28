package main

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"
)

// The hook listens for upstream's own words. If an update rewords them, the guard would never fire again, so this
// reads them where upstream writes them.
func TestFlapGuardListensForUpstreamsExactWords(t *testing.T) {
	source, err := os.ReadFile("../../../third_party/mautrix-meta/pkg/instameow/streamcontroller.go")
	if err != nil {
		t.Fatal(err)
	}
	for _, words := range []string{streamReopenedAfterDrop, streamReopenedAfterFailure} {
		if !strings.Contains(string(source), `Msg("`+words+`")`) {
			t.Fatalf("upstream no longer says %q when it reopens the typing stream", words)
		}
	}
	heard := 0
	logger := appLogger(func() { heard++ })
	logger.Debug().Msg(streamReopenedAfterDrop)
	logger.Warn().Int("sequential_failures", 2).Msg(streamReopenedAfterFailure)
	logger.Error().Str("value", "private").Msg("Unexpected data in stream controller payload")
	logger.Debug().Msg("Socket closed cleanly")
	if heard != 2 {
		t.Fatalf("the hook heard %d reopenings, expected 2", heard)
	}
}

// Thirty reopenings within an hour are tolerated; the thirty-first stops the realtime connection for good this
// session. An hour-old one no longer counts.
func TestFlapGuardStopsAConnectionThatKeepsReopening(t *testing.T) {
	var flaps streamFlaps
	start := time.Now()
	for i := 0; i < streamFlapLimit; i++ {
		if flaps.note(start.Add(time.Duration(i) * time.Minute)) {
			t.Fatalf("tripped at %d", i+1)
		}
	}
	if !flaps.note(start.Add(59 * time.Minute)) {
		t.Fatal("the thirty-first reopening within the hour did not trip")
	}
	var spaced streamFlaps
	for i := 0; i < 3*streamFlapLimit; i++ {
		if spaced.note(start.Add(time.Duration(i) * 2 * time.Minute)) {
			t.Fatal("one reopening every two minutes tripped")
		}
	}

	a, _, buffer := testReadAdapter()
	stopped := false
	a.mu.Lock()
	a.snapshot.State, a.stop = "connected", context.CancelFunc(func() { stopped = true })
	generation := a.generation
	a.mu.Unlock()
	for i := 0; i < streamFlapLimit; i++ {
		a.streamReopened(generation)
	}
	if stopped || a.snapshot.State != "connected" || buffer.Len() != 0 {
		t.Fatal("stopped before the limit")
	}
	a.streamReopened(generation)
	if !stopped || a.snapshot.State != "typing_unstable" || !strings.Contains(buffer.String(), `"state":"typing_unstable"`) {
		t.Fatalf("not stopped past the limit: stopped=%v state=%s", stopped, a.snapshot.State)
	}
	a.client = &fakeReadClient{}
	if a.mayReopen(generation) {
		t.Fatal("an unstable connection would be reopened")
	}
	if got := a.requests.typing.counts().Reconnects; got != streamFlapLimit+1 {
		t.Fatalf("reconnects=%d", got)
	}
	// A connection still connecting is left to finish: stopping it would only make the app sign in again.
	a.mu.Lock()
	a.snapshot.State, stopped = "session_loaded", false
	a.mu.Unlock()
	a.streamReopened(generation)
	if stopped || a.snapshot.State != "session_loaded" {
		t.Fatal("a connection still connecting was stopped")
	}
}
