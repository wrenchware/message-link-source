package main

import (
	"context"
	"time"
)

// Facebook serves a conversation's history right up until it moves that conversation to end-to-end encryption, and
// then never again — the messages are still there, but only in a form this protocol cannot read. That change is
// happening to this account conversation by conversation, so every conversation not yet encrypted is holding history
// on a deadline nobody announces.
//
// This walks those conversations in the background and pages each one back as far as Facebook will go, writing what
// it finds to the local database. It is slow and unhurried on purpose: the app is using the same pipe.
const archiveDepth = 500
const archivePages = 12

func (a *adapter) archive(ctx context.Context) {
	defer func() { _ = recover() }()
	// Behind the first load and the list paging; there is no hurry within a session, only across weeks.
	if !wait(ctx, 45*time.Second) {
		return
	}
	for _, chat := range a.chats.chats(0) {
		if ctx.Err() != nil {
			return
		}
		if chat.Encrypted {
			continue
		}
		key, err := parseKey(chat.ID)
		if err != nil {
			continue
		}
		// A conversation walked to its end before does not need walking again; only new messages arrive now, and
		// those come on their own.
		a.mu.Lock()
		kept := a.kept
		a.mu.Unlock()
		if kept.complete(ctx, key) {
			continue
		}
		a.archiveOne(ctx, key)
		if !wait(ctx, 3*time.Second) {
			return
		}
	}
	logf("archive pass finished")
}

// Pages one conversation back until Facebook stops answering, this build's depth is reached, or it runs out of pages.
func (a *adapter) archiveOne(ctx context.Context, key int64) {
	// Start from the oldest message already known, not from the newest page: asking for the newest is answered from
	// what is in memory, which fetches nothing and looks exactly like having reached the end.
	before, _ := a.chats.oldestBefore(key, 0)
	if before == 0 {
		// Nothing known yet: everything older than now, which is everything. Asking for the newest page instead is
		// answered from memory, fetches nothing, and looks exactly like having reached the end.
		before = time.Now().UnixMilli()
	}
	fetched := 0
	for page := 0; page < archivePages; page++ {
		if ctx.Err() != nil || a.chats.messageCount(key) >= archiveDepth {
			return
		}
		known := a.chats.messageCount(key)
		if _, code := a.history(ctx, key, 50, before); code != "" {
			return
		}
		fetched++
		// Where to continue from: the oldest message now known.
		at, _ := a.chats.oldestBefore(key, before)
		if at == 0 || a.chats.messageCount(key) == known {
			logf("archive chat=%s reached the end at %d messages after %d pages", tag(key), a.chats.messageCount(key), fetched)
			// Only a conversation that was actually asked for is finished with; one that answered from memory was
			// never asked at all.
			if fetched > 0 {
				a.mu.Lock()
				kept := a.kept
				a.mu.Unlock()
				kept.markComplete(ctx, key)
			}
			return
		}
		before = at
		if !wait(ctx, 2*time.Second) {
			return
		}
	}
	logf("archive chat=%s stopped at %d messages", tag(key), a.chats.messageCount(key))
}
