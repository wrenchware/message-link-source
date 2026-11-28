package main

import (
	"context"
	"sort"
	"sync"
	"time"
)

// A login's inbox rows are a preview, not a complete newest message page. In particular, the last text can arrive
// without the photo immediately before it. Walking old history cannot repair that gap in a conversation already
// above the archive depth, so a small number of conversations that advanced while closed get their newest page read.
const recentCatchUpChats = 5
const recentCatchUpMessages = 50
const recentCatchUpPace = time.Second

type recentPosition struct {
	message, activity int64
}

type recentCatchUp struct {
	before map[int64]recentPosition
	once   sync.Once
}

// Called after restoring this account's kept history and before applying the login's first table. Keep the thread
// activity separately: Facebook can put it a few milliseconds ahead of its last message, even in an unchanged chat.
func (a *adapter) prepareRecentCatchUp() {
	a.chats.mu.Lock()
	before := make(map[int64]recentPosition, len(a.chats.threads))
	for key, current := range a.chats.threads {
		before[key] = recentPosition{a.chats.latestMessageTimestampLocked(key), current.lastActivity}
	}
	a.chats.mu.Unlock()
	a.mu.Lock()
	a.recentCatchUp = &recentCatchUp{before: before}
	a.mu.Unlock()
}

// The connected event follows the socket's initial synchronization, so both the login preview and any later startup
// tables contribute to selection. Reconnection does not start another pass, and selection never waits on the socket.
func (a *adapter) startRecentCatchUp() {
	a.mu.Lock()
	catchUp := a.recentCatchUp
	a.mu.Unlock()
	if catchUp != nil {
		catchUp.once.Do(func() { go catchUp.run(a.lifetime, a, wait) })
	}
}

func (c *recentCatchUp) candidates(s *store) []int64 {
	type candidate struct {
		key, at int64
	}
	s.mu.Lock()
	var candidates []candidate
	for key, current := range s.threads {
		if current.deleted || current.kind == 0 || current.kind.IsWhatsApp() {
			continue
		}
		latest := s.latestMessageTimestampLocked(key)
		// A fetch needs an actual message to anchor at. A thread with no rows is left to its ordinary first history
		// read, which can use the separately supplied range anchor to obtain the first rows.
		if latest == 0 {
			continue
		}
		before := c.before[key]
		if latest <= before.message && current.lastActivity <= before.activity {
			continue
		}
		candidates = append(candidates, candidate{key, max(latest, current.lastActivity)})
	}
	s.mu.Unlock()
	sort.Slice(candidates, func(i, j int) bool {
		if candidates[i].at != candidates[j].at {
			return candidates[i].at > candidates[j].at
		}
		return candidates[i].key < candidates[j].key
	})
	if len(candidates) > recentCatchUpChats {
		candidates = candidates[:recentCatchUpChats]
	}
	keys := make([]int64, len(candidates))
	for i, current := range candidates {
		keys[i] = current.key
	}
	return keys
}

func (s *store) recentCatchUpAllowed(key int64) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	current := s.threads[key]
	return current != nil && !current.deleted && current.kind != 0 && !current.kind.IsWhatsApp()
}

// One page at a time, with a pause between conversations. refreshBehind owns the same per-page guard as a foreground
// open, emits its usual change event, and applies the existing finite refresh/recovery policy. Nothing marks read.
func (c *recentCatchUp) run(ctx context.Context, a *adapter, pause func(context.Context, time.Duration) bool) {
	keys := c.candidates(a.chats)
	started, covered := 0, 0
	for _, key := range keys {
		if ctx.Err() != nil {
			break
		}
		if started > 0 && !pause(ctx, recentCatchUpPace) {
			break
		}
		// A conversation can have been deleted, become encrypted, or been opened successfully since selection.
		if !a.chats.recentCatchUpAllowed(key) || a.chats.pageRefreshed(key, 0) ||
			a.chats.pageRefreshTries(key, 0) >= maxPageRefreshTries {
			continue
		}
		// A new conversation has no restored rows to make a failed catch-up retry on its next ordinary open. Mark
		// it as needing reconciliation without clearing any successful page, just as restored conversations are.
		a.chats.markFromStore(key)
		if !a.refreshBehind(key, 0, recentCatchUpMessages) {
			continue
		}
		started++
		for a.refreshRunning(key, 0) {
			if !wait(ctx, 25*time.Millisecond) {
				logf("catchup recent selected=%d started=%d covered=%d cancelled=true", len(keys), started, covered)
				return
			}
		}
		if a.chats.pageRefreshed(key, 0) {
			covered++
		}
	}
	logf("catchup recent selected=%d started=%d covered=%d cancelled=%v", len(keys), started, covered, ctx.Err() != nil)
}
