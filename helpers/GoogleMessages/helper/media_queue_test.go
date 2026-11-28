package main

import (
	"context"
	"encoding/json"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestSlowImageDoesNotBlockHelperCommands(t *testing.T) {
	release := make(chan struct{})
	job := startMediaDownload("id", func() ([]byte, error) { <-release; return []byte("synthetic"), nil })
	a := adapter{client: &libgm.Client{AuthData: libgm.NewAuthData()}, mediaJobs: map[string]*mediaDownload{"id": job}, mediaRefs: map[string]*gmproto.MediaContent{"id": {}}}
	params, _ := json.Marshal(map[string]any{"id": "id", "offset": 0})
	result, code := a.mediaRead(context.Background(), request{Params: params})
	if code != "" || result.(map[string]any)["pending"] != true {
		t.Fatal("download blocked instead of returning pending")
	}
	if _, code = a.call(request{Method: "hello"}); code != "" {
		t.Fatal("control request blocked")
	}
	close(release)
	<-job.done
	result, code = a.mediaRead(context.Background(), request{Params: params})
	if code != "" || string(result.(map[string]any)["data"].([]byte)) != "synthetic" {
		t.Fatal("completed media lost")
	}
}

func TestIndependentMediaJobsCoalesceAndKeepChunkIdentity(t *testing.T) {
	a := &adapter{}
	release := make(chan struct{})
	var fetches atomic.Int32
	fetch := func() ([]byte, error) { fetches.Add(1); <-release; return []byte("first-payload"), nil }
	var readers sync.WaitGroup
	for range 20 {
		readers.Add(1)
		go func() {
			defer readers.Done()
			result, code := a.downloadRead("first", 0, fetch)
			if code != "" || result.(map[string]any)["pending"] != true {
				t.Error("slow job blocked polling")
			}
		}()
	}
	readers.Wait()
	result, code := a.downloadRead("second", 0, func() ([]byte, error) { return []byte("second-payload"), nil })
	if code != "" || result.(map[string]any)["pending"] != true {
		t.Fatal("independent job not started")
	}
	a.downloads.Lock()
	second := a.mediaJobs["second"]
	a.downloads.Unlock()
	select {
	case <-second.done:
	case <-time.After(time.Second):
		t.Fatal("second image blocked behind first")
	}
	result, code = a.downloadRead("second", 7, func() ([]byte, error) { t.Error("completed image refetched"); return nil, nil })
	if code != "" || string(result.(map[string]any)["data"].([]byte)) != "payload" {
		t.Fatal("wrong interleaved chunk")
	}
	close(release)
	a.downloads.Lock()
	first := a.mediaJobs["first"]
	a.downloads.Unlock()
	<-first.done
	if fetches.Load() != 1 {
		t.Fatal("same-ID download was duplicated")
	}
	result, code = a.downloadRead("first", 0, fetch)
	if code != "" || string(result.(map[string]any)["data"].([]byte)) != "first-payload" {
		t.Fatal("first payload overwritten by second")
	}
}

func TestMediaJobBoundDoesNotEvictActiveJobs(t *testing.T) {
	a := &adapter{}
	release := make(chan struct{})
	// Declared small: the count bound is what holds the fifth back here. Undeclared ones reserve downloadLimit each
	// (TestUnknownSizeDownloadsStayWithinBudget).
	for _, id := range []string{"a", "b", "c", "d", "e"} {
		a.downloadReadJob(id, 0, false, int64(len("bounded")), func() ([]byte, error) { <-release; return []byte("bounded"), nil })
	}
	a.downloads.Lock()
	if len(a.mediaJobs) != 4 || a.mediaJobs["e"] != nil {
		t.Fatal("unbounded download jobs")
	}
	jobs := make([]*mediaDownload, 0, 4)
	for _, job := range a.mediaJobs {
		jobs = append(jobs, job)
	}
	a.downloads.Unlock()
	a.downloadReadKind("preview", 0, true, func() ([]byte, error) { return []byte("card"), nil })
	a.downloads.Lock()
	preview := a.mediaJobs["preview"]
	a.downloads.Unlock()
	if preview == nil {
		t.Fatal("website card blocked behind originals")
	}
	select {
	case <-preview.done:
	case <-time.After(time.Second):
		t.Fatal("website card waited for originals")
	}
	close(release)
	for _, job := range jobs {
		<-job.done
	}
}

// Polls as the app does until the download answers with bytes.
func readWhenReady(t *testing.T, read func() (any, string)) map[string]any {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for {
		result, code := read()
		if code != "" {
			t.Fatal(code)
		}
		if result.(map[string]any)["pending"] != true {
			return result.(map[string]any)
		}
		if time.Now().After(deadline) {
			t.Fatal("the download never finished")
		}
		time.Sleep(time.Millisecond)
	}
}

// Two 60 MB videos read at once: B finishing must not evict A while A's chunks are still being read, or A's next
// chunk fetches the whole file again and evicts B in turn (code review, round 2, 2026-10-12).
func TestDownloadBeingReadIsNotEvictedByAnother(t *testing.T) {
	a := &adapter{}
	video := make([]byte, 60*1024*1024)
	var fetchesA, fetchesB atomic.Int32
	fetchA := func() ([]byte, error) { fetchesA.Add(1); return video, nil }
	fetchB := func() ([]byte, error) { fetchesB.Add(1); return video, nil }
	read := func(id string, offset int, fetch func() ([]byte, error)) map[string]any {
		return readWhenReady(t, func() (any, string) { return a.downloadRead(id, offset, fetch) })
	}
	if first := read("a", 0, fetchA); first["next"] != mediaChunk {
		t.Fatal("first chunk of A")
	}
	read("b", 0, fetchB)
	if next := read("a", mediaChunk, fetchA); next["next"] != 2*mediaChunk || next["total"] != len(video) {
		t.Fatal("second chunk of A")
	}
	read("b", mediaChunk, fetchB)
	if fetchesA.Load() != 1 || fetchesB.Load() != 1 {
		t.Fatalf("a download being read was fetched again: A %d, B %d", fetchesA.Load(), fetchesB.Load())
	}
}

// The bytes stay bounded: a third large download waits while two are being read, and starts once one has been read to
// its end or left idle; what finished downloads hold never passes the budget.
func TestMediaJobsStayWithinBudget(t *testing.T) {
	a := &adapter{}
	video := make([]byte, downloadLimit)
	fetch := func() ([]byte, error) { return video, nil }
	read := func(id string, offset int) map[string]any {
		return readWhenReady(t, func() (any, string) { return a.downloadReadJob(id, offset, false, downloadLimit, fetch) })
	}
	state := func() (held int, ids map[string]bool) {
		a.downloads.Lock()
		defer a.downloads.Unlock()
		ids = map[string]bool{}
		for id, job := range a.mediaJobs {
			ids[id] = true
			select {
			case <-job.done:
				held += len(job.data)
			default:
			}
		}
		return held, ids
	}
	read("a", 0)
	read("b", 0)
	if result, _ := a.downloadReadJob("c", 0, false, downloadLimit, fetch); result.(map[string]any)["pending"] != true {
		t.Fatal("a third 100 MB download was answered while two were being read")
	}
	if held, ids := state(); ids["c"] || held > mediaJobBudget {
		t.Fatal("a third 100 MB download started while two were being read")
	}
	read("a", len(video)-mediaChunk)
	read("c", 0)
	if held, ids := state(); held > mediaJobBudget || ids["a"] || !ids["b"] || !ids["c"] {
		t.Fatalf("after A was read to its end: %d bytes held, %v", held, ids)
	}
	a.downloads.Lock()
	a.mediaJobs["b"].lastUse = time.Now().Add(-mediaJobIdle - time.Second)
	a.downloads.Unlock()
	read("d", 0)
	if held, ids := state(); held > mediaJobBudget || ids["b"] || !ids["c"] || !ids["d"] {
		t.Fatalf("after B's reader went quiet: %d bytes held, %v", held, ids)
	}
}

// What finished downloads in the map hold. Called without downloads held.
func heldMediaBytes(a *adapter) int {
	a.downloads.Lock()
	defer a.downloads.Unlock()
	total := 0
	for _, job := range a.mediaJobs {
		select {
		case <-job.done:
			total += len(job.data)
		default:
		}
	}
	return total
}

// Four originals whose size the message does not give, each 100 MiB, read at once: what is held for readers never
// passes the budget, and every one is still read in the end (code review, round 3, 2026-10-13: each reserved nothing,
// so all four ran and were held, 400 MiB against 200).
func TestUnknownSizeDownloadsStayWithinBudget(t *testing.T) {
	a := &adapter{}
	video := make([]byte, downloadLimit)
	// All four are asked for while the first are still downloading, as a conversation of videos opening does.
	release := make(chan struct{})
	var fetches atomic.Int32
	fetch := func() ([]byte, error) { fetches.Add(1); <-release; return video, nil }
	ids := []string{"a", "b", "c", "d"}
	for _, id := range ids {
		a.downloadRead(id, 0, fetch)
	}
	close(release)
	served := map[string]bool{}
	offsets := map[string]int{}
	deadline := time.Now().Add(20 * time.Second)
	for len(served) < len(ids) {
		if time.Now().After(deadline) {
			t.Fatalf("undeclared downloads never all finished: %v", served)
		}
		for _, id := range ids {
			if served[id] {
				continue
			}
			result, code := a.downloadRead(id, offsets[id], fetch)
			if code != "" {
				t.Fatal(code)
			}
			if held := heldMediaBytes(a); held > mediaJobBudget {
				t.Fatalf("four undeclared downloads held %d bytes against a budget of %d", held, mediaJobBudget)
			}
			if result.(map[string]any)["pending"] == true {
				continue
			}
			// Each reader takes its first chunk in turn, then its last: in use, then done with.
			if result.(map[string]any)["next"].(int) == len(video) {
				served[id] = true
			} else {
				offsets[id] = len(video) - mediaChunk
			}
		}
		time.Sleep(time.Millisecond)
	}
	// Reserved up front, none of them had to be let go and fetched again once it turned out large.
	if n := fetches.Load(); n != int32(len(ids)) {
		t.Fatalf("four undeclared downloads took %d fetches", n)
	}
}

// A download that comes back larger than the message declared is not held past the budget: its reader waits, and the
// next read reserves the real size (code review, round 3, 2026-10-13).
func TestUnderDeclaredDownloadWaitsForRoom(t *testing.T) {
	a := &adapter{}
	video := make([]byte, downloadLimit)
	small := make([]byte, 1024*1024)
	read := func(id string, offset int, declared int64, data []byte) map[string]any {
		return readWhenReady(t, func() (any, string) {
			return a.downloadReadJob(id, offset, false, declared, func() ([]byte, error) { return data, nil })
		})
	}
	read("a", 0, downloadLimit, video)
	read("d", 0, int64(len(small)), small)
	// C says 1 KiB and is 100 MiB: A and D being read leave room for what it said, not for what it is.
	var fetchesC atomic.Int32
	fetchC := func() ([]byte, error) { fetchesC.Add(1); return video, nil }
	a.downloadReadJob("c", 0, false, 1024, fetchC)
	deadline := time.Now().Add(5 * time.Second)
	for {
		if held := heldMediaBytes(a); held > mediaJobBudget {
			t.Fatalf("an under-declared download pushed what is held to %d", held)
		}
		a.downloads.Lock()
		c := a.mediaJobs["c"]
		a.downloads.Unlock()
		if c == nil && fetchesC.Load() == 1 {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("an under-declared download was kept past the budget")
		}
		time.Sleep(time.Millisecond)
	}
	if result, _ := a.downloadReadJob("c", 0, false, 1024, fetchC); result.(map[string]any)["pending"] != true {
		t.Fatal("an under-declared download was answered while A was still being read")
	}
	read("a", len(video)-mediaChunk, downloadLimit, video)
	if first := readWhenReady(t, func() (any, string) { return a.downloadReadJob("c", 0, false, 1024, fetchC) }); first["total"] != len(video) {
		t.Fatal("the under-declared download was never read")
	}
	if held := heldMediaBytes(a); held > mediaJobBudget {
		t.Fatalf("held %d after the under-declared download was read", held)
	}
}
