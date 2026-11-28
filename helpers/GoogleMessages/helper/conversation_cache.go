package main

import (
	"context"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"sync"
	"time"
)

// The phone's latest description of each conversation this helper has seen: from a conversation lookup, a list, or the
// event the phone sends whenever a conversation changes, each kept with when it arrived. The typing ping takes it at any
// age. A send and a reaction take it only while it is younger than conversationTrust (trustedConversation).
type conversationCache struct {
	mu    sync.Mutex
	chats map[string]cachedConversation
	// Stands in for the clock in tests.
	now func() time.Time
}

type cachedConversation struct {
	conversation *gmproto.Conversation
	at           time.Time
}

// More conversations than the app reads from any phone; past it the cache starts again, as the helper's other maps do.
const conversationCacheLimit = 2000

// How long a send or a reaction takes the phone's last description of a conversation as it stands, instead of asking the
// phone again first. Chosen 2026-10-14 ("trust it for a minute"): the lookup was about 250 ms of a 628 ms
// median send. A conversation the phone changes sends an event, which replaces the description and restarts the minute.
const conversationTrust = time.Minute

func (cache *conversationCache) clock() time.Time {
	if cache.now != nil {
		return cache.now()
	}
	return time.Now()
}

func (cache *conversationCache) note(c *gmproto.Conversation) {
	id := c.GetConversationID()
	if id == "" {
		return
	}
	cache.mu.Lock()
	defer cache.mu.Unlock()
	if _, held := cache.chats[id]; cache.chats == nil || (len(cache.chats) >= conversationCacheLimit && !held) {
		cache.chats = make(map[string]cachedConversation)
	}
	cache.chats[id] = cachedConversation{c, cache.clock()}
}

func (cache *conversationCache) get(id string) *gmproto.Conversation {
	cache.mu.Lock()
	defer cache.mu.Unlock()
	return cache.chats[id].conversation
}

// The description, only while it is younger than conversationTrust. A time that runs backwards counts as too old.
func (cache *conversationCache) recent(id string) *gmproto.Conversation {
	cache.mu.Lock()
	defer cache.mu.Unlock()
	entry, held := cache.chats[id]
	if age := cache.clock().Sub(entry.at); !held || age < 0 || age >= conversationTrust {
		return nil
	}
	return entry.conversation
}

// Where the conversation a send or a reaction was checked against came from, as the fixed word its answer carries for
// the app's log: the phone's description from the last minute, or a lookup made for it.
const (
	conversationCached  = "cached"
	conversationFetched = "fetched"
)

// The conversation a send or a reaction is checked against: the phone's description when it is younger than
// conversationTrust, otherwise asked for now (fetchedConversation).
func (a *adapter) trustedConversation(ctx context.Context, chatID string) (*gmproto.Conversation, string, error) {
	if c := a.conversations.recent(chatID); c != nil {
		return c, conversationCached, nil
	}
	return a.fetchedConversation(ctx, chatID)
}

// The phone's answer to a conversation lookup, kept as the conversation's description from now on.
func (a *adapter) fetchedConversation(ctx context.Context, chatID string) (*gmproto.Conversation, string, error) {
	c, err := a.client.GetConversation(ctx, chatID)
	if err == nil && c.GetConversationID() == chatID {
		a.conversations.note(c)
	}
	return c, conversationFetched, err
}
