package main

import (
	"testing"
	"time"
)

// A finished download is let go once nothing has asked for it for a minute, whether or not later downloads come along
// to push it out: on the always-on server six of them could otherwise hold 150 MiB for good.
func TestIdleDownloadsAreLetGo(t *testing.T) {
	a := &adapter{}
	now := time.Unix(1_700_000_000, 0)
	a.mediaNow = func() time.Time { return now }
	a.keep(&mediaCache{id: "old", data: []byte("old")})
	now = now.Add(40 * time.Second)
	a.keep(&mediaCache{id: "new", data: []byte("new")})
	now = now.Add(30 * time.Second)
	if a.cached("old") != nil {
		t.Fatal("a download idle for over a minute was still held")
	}
	if a.cached("new") == nil {
		t.Fatal("a download kept 30 seconds ago was dropped")
	}
	// Being read is being used.
	now = now.Add(50 * time.Second)
	if a.cached("new") == nil {
		t.Fatal("a download still being read was dropped")
	}
	// And with nothing touching the cache at all, the pending sweep lets go of what is left.
	if !a.mediaSweep {
		t.Fatal("no sweep was pending while downloads were held")
	}
	now = now.Add(61 * time.Second)
	a.sweepMedia()
	if len(a.media) != 0 || a.mediaSweep {
		t.Fatalf("the sweep left %d idle downloads held", len(a.media))
	}
}

func TestMediaCacheKeepsSeveralDownloads(t *testing.T) {
	a := &adapter{}
	for _, id := range []string{"a", "b", "c"} {
		a.keep(&mediaCache{id: id, data: []byte(id)})
	}
	// Several pictures load at once and the app asks for one chunk at a time, so an earlier one must still be there.
	found := a.cached("a")
	if found == nil || string(found.data) != "a" {
		t.Fatalf("expected the first download to survive: %+v", found)
	}
	// The one just used moves to the front, so it is the last to be dropped.
	if a.media[0].id != "a" {
		t.Fatalf("unexpected order: %s", a.media[0].id)
	}
	if a.cached("nope") != nil {
		t.Fatal("an unknown id is not cached")
	}
	for _, id := range []string{"d", "e", "f", "g"} {
		a.keep(&mediaCache{id: id, data: []byte(id)})
	}
	if len(a.media) != mediaKeep {
		t.Fatalf("expected %d kept, got %d", mediaKeep, len(a.media))
	}
	// The oldest is gone, the ones since are not.
	if a.cached("b") != nil || a.cached("g") == nil || a.cached("a") == nil {
		t.Fatal("eviction dropped the wrong download")
	}
}
