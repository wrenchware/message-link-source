package main

import (
	"context"
	"errors"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

// The contact book's clock, moved by hand.
type voiceClock struct {
	mu sync.Mutex
	at time.Time
}

func (c *voiceClock) now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.at
}

func (c *voiceClock) advance(by time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.at = c.at.Add(by)
}

func within(t *testing.T, what string, run func()) {
	t.Helper()
	done := make(chan struct{})
	go func() {
		run()
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatalf("%s waited on a contacts request", what)
	}
}

// A failed whole-list read was never marked as read, so every conversation list asked Google for the whole list again
// during an outage. It is asked again only after contactsRetry.
func TestContactBookBacksOffAfterAFailedList(t *testing.T) {
	var lists atomic.Int32
	fail := true
	clock := &voiceClock{at: time.Date(2026, 11, 18, 12, 0, 0, 0, time.UTC)}
	book := &contactBook{now: clock.now,
		autocomplete: func(context.Context, *libgv.Client) (*gvproto.RespAutocompleteContacts, error) {
			lists.Add(1)
			if fail {
				return nil, errors.New("unavailable")
			}
			return &gvproto.RespAutocompleteContacts{Results: []*gvproto.PersonWrapper{person("Listed Person", true, "+15550001")}}, nil
		},
		lookup: func(context.Context, *libgv.Client, []string) (map[string]*gvproto.RespLookupContacts_Match, error) {
			return map[string]*gvproto.RespLookupContacts_Match{}, nil
		},
	}
	_, stats := book.infoFor(context.Background(), nil, nil)
	if stats["contactList"] != "failed" {
		t.Fatalf("the failed read was not reported: %v", stats)
	}
	fail = false
	for range 3 {
		clock.advance(time.Minute)
		book.infoFor(context.Background(), nil, nil)
	}
	if lists.Load() != 1 {
		t.Fatalf("a failed list was asked again %d times within the retry time", lists.Load()-1)
	}
	clock.advance(contactsRetry)
	if named, _ := book.infoFor(context.Background(), nil, []string{"+15550001"}); named["+15550001"].Name != "Listed Person" || lists.Load() != 2 {
		t.Fatalf("the list was not read again after the retry time: %v %d", named, lists.Load())
	}
}

// The book held its lock across Google's answer, which libgv retries for about two minutes: every conversation list,
// conversation and avatar waited behind it. A read now holds nothing while it waits, callers that arrive meanwhile share
// it, and a book already read answers from what it holds.
func TestContactBookReadsWithoutHoldingTheBook(t *testing.T) {
	var lists atomic.Int32
	release := make(chan struct{})
	started := make(chan struct{}, 4)
	clock := &voiceClock{at: time.Date(2026, 11, 18, 12, 0, 0, 0, time.UTC)}
	book := &contactBook{now: clock.now,
		autocomplete: func(context.Context, *libgv.Client) (*gvproto.RespAutocompleteContacts, error) {
			lists.Add(1)
			started <- struct{}{}
			<-release
			return &gvproto.RespAutocompleteContacts{Results: []*gvproto.PersonWrapper{person("Listed Person", true, "+15550001")}}, nil
		},
		lookup: func(context.Context, *libgv.Client, []string) (map[string]*gvproto.RespLookupContacts_Match, error) {
			return map[string]*gvproto.RespLookupContacts_Match{}, nil
		},
	}
	var waiting sync.WaitGroup
	results := make([]map[string]contactInfo, 2)
	for i := range results {
		waiting.Add(1)
		go func() {
			defer waiting.Done()
			results[i], _ = book.infoFor(context.Background(), nil, []string{"+15550001"})
		}()
	}
	<-started
	within(t, "an avatar", func() { book.avatarURL("avatar:none") })
	// A caller that gives up stops waiting even though the read goes on.
	gaveUp, cancel := context.WithCancel(context.Background())
	cancel()
	within(t, "a list the app gave up on", func() { book.infoFor(gaveUp, nil, []string{"+15550001"}) })
	close(release)
	waiting.Wait()
	for _, named := range results {
		if named["+15550001"].Name != "Listed Person" {
			t.Fatalf("a caller during the first read did not get its names: %v", named)
		}
	}
	if lists.Load() != 1 {
		t.Fatalf("callers during one read asked for the list %d times", lists.Load())
	}

	// Six hours on, the list is read again, and nobody waits for it.
	clock.advance(contactsRefresh + time.Minute)
	release = make(chan struct{})
	within(t, "a list during a refresh", func() {
		if named, _ := book.infoFor(context.Background(), nil, []string{"+15550001"}); named["+15550001"].Name != "Listed Person" {
			t.Fatalf("a refresh lost the names held: %v", named)
		}
	})
	<-started
	close(release)
}
