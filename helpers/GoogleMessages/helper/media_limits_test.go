package main

import (
	"errors"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"io"
	"testing"
	"time"
)

// A stream of zeros that counts what was read from it.
type countingStream struct{ left, read int }

func (s *countingStream) Read(p []byte) (int, error) {
	if s.left == 0 {
		return 0, io.EOF
	}
	n := min(len(p), s.left)
	s.left -= n
	s.read += n
	return n, nil
}

func (s *countingStream) Close() error { return nil }

// A download longer than downloadLimit is refused after one byte past it, not read whole and then refused.
func TestMediaStreamReadStopsAtTheLimit(t *testing.T) {
	long := &countingStream{left: downloadLimit + 8*1024*1024}
	if data, err := readMedia(long, nil); !errors.Is(err, errMediaTooLarge) || data != nil {
		t.Fatalf("a stream past the limit was answered with %d bytes, %v", len(data), err)
	}
	if long.read > downloadLimit+1 {
		t.Fatalf("read %d bytes of a stream past the %d byte limit", long.read, downloadLimit)
	}
	short := &countingStream{left: 1234}
	if data, err := readMedia(short, nil); err != nil || len(data) != 1234 {
		t.Fatalf("a short stream was answered with %d bytes, %v", len(data), err)
	}
}

// A thumbnail's message gives it no size. Reserving the most a download may be for each, two video thumbnails held the
// whole budget and every other download waited behind them.
func TestThumbnailsLeaveRoomForOtherDownloads(t *testing.T) {
	video := &gmproto.MediaContent{Size: 60 * 1024 * 1024}
	if mediaDeclared(video, false) != video.GetSize() || mediaDeclared(video, true) != thumbnailReserve {
		t.Error("an original reserves its size and a thumbnail a little")
	}
	a := &adapter{}
	release := make(chan struct{})
	defer close(release)
	for _, id := range []string{"first-thumbnail", "second-thumbnail"} {
		a.downloadReadJob(id, 0, false, mediaDeclared(video, true), func() ([]byte, error) { <-release; return []byte("thumbnail"), nil })
	}
	a.downloadReadJob("original", 0, false, downloadLimit, func() ([]byte, error) { <-release; return []byte("original"), nil })
	a.downloads.Lock()
	started := a.mediaJobs["original"] != nil
	a.downloads.Unlock()
	if !started {
		t.Fatal("a 100 MiB original waited behind two thumbnails")
	}
}

func finishedMediaJob(id string, lastUse time.Time, served bool) *mediaDownload {
	job := &mediaDownload{id: id, done: make(chan struct{}), data: []byte(id), lastUse: lastUse, served: served}
	close(job.done)
	return job
}

// Finished downloads were let go only when the count or byte bounds were passed, so a few large ones stayed held for as
// long as nothing else was downloaded. Each goes once nobody has read it for a minute.
func TestIdleFinishedDownloadsExpire(t *testing.T) {
	now := time.Now()
	a := &adapter{mediaJobs: map[string]*mediaDownload{
		"served":  finishedMediaJob("served", now, true),
		"unused":  finishedMediaJob("unused", now, false),
		"recent":  finishedMediaJob("recent", now.Add(30*time.Second), true),
		"running": {id: "running", done: make(chan struct{})},
	}}
	a.downloads.Lock()
	a.trimMediaJobs(now.Add(mediaJobExpiry+time.Second), nil, 0)
	_, served := a.mediaJobs["served"]
	_, unused := a.mediaJobs["unused"]
	_, recent := a.mediaJobs["recent"]
	_, running := a.mediaJobs["running"]
	a.downloads.Unlock()
	if served || unused || !recent || !running {
		t.Fatalf("after a minute idle: served %v, unused %v, recent %v, running %v", served, unused, recent, running)
	}
}

// Nothing else needs to happen for idle downloads to go: a finished download arms a sweep, which lets go of what has
// been idle for a minute and stays armed while anything is held.
func TestSweepLetsIdleDownloadsGo(t *testing.T) {
	a := &adapter{}
	defer func() {
		a.downloads.Lock()
		if a.mediaSweep != nil {
			a.mediaSweep.Stop()
		}
		a.downloads.Unlock()
	}()
	readWhenReady(t, func() (any, string) {
		return a.downloadReadJob("picture", 0, false, 16, func() ([]byte, error) { return []byte("picture"), nil })
	})
	a.downloads.Lock()
	armed := a.mediaSweep != nil
	a.mediaJobs["picture"].lastUse = time.Now().Add(-mediaJobExpiry - time.Second)
	a.mediaJobs["running"] = &mediaDownload{id: "running", done: make(chan struct{})}
	a.mediaSweep.Stop()
	a.downloads.Unlock()
	if !armed {
		t.Fatal("a finished download armed no sweep")
	}
	a.sweepMediaJobs()
	a.downloads.Lock()
	_, picture := a.mediaJobs["picture"]
	rearmed := a.mediaSweep != nil
	a.downloads.Unlock()
	if picture || !rearmed {
		t.Fatalf("after the sweep: idle download held %v, sweep armed %v", picture, rearmed)
	}
}
