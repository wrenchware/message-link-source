package main

import (
	"context"
	"time"
)

// A photo sent into an ordinary (non-encrypted) group was delivered and paired with its id (LSReplaceOptimsiticMessage),
// yet its row never came over the socket, so the app's check for the message answered unknown_message every time and
// the send stayed "not confirmed" (observed 2026-10-12). The row is a read away, so a lookup for an id this account
// sent, whose row is missing, asks Facebook for the newest page once. Reading never sends, and nothing here is reached
// by a send: the message is already out, this only fetches what the account already has.
//
// It sits in the lookup rather than after the send so the send's own answer is never held up, and so a send nobody
// asks about again costs nothing. It is bounded per id (a gap between asks and a total), because the app asks four
// times in a few seconds and then at 30, 60 and 120.
const sentRowGap = 10 * time.Second
const sentRowAsks = 3

// How far past now the fetch is anchored. Direction 0 returns the messages before the anchor, never the anchor
// itself, so anchoring at the newest message known would leave out the one being looked for; anchoring just past the
// present returns the newest page, which holds it. The margin covers Facebook's clock being ahead of this PC's.
const sentRowAhead = 10 * time.Minute

// Bounds one fetch so a stalled socket cannot hold the lookup past what the app waits for.
const sentRowWait = 8 * time.Second

type sentLook struct {
	last time.Time
	asks int
}

// The id Facebook gave a message this account sent, given either that id or the offline threading id it was sent
// with. Whether its row is present is the caller's question.
func (s *store) sentPairing(id string) (string, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if real, ok := s.sent[id]; ok {
		return real, true
	}
	for _, real := range s.sent {
		if real == id {
			return real, true
		}
	}
	return "", false
}

// Whether this lookup may spend a fetch, and records it if so.
func (a *adapter) sentRowMayAsk(id string) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.sentLooks == nil {
		a.sentLooks = map[string]*sentLook{}
	}
	look := a.sentLooks[id]
	if look == nil {
		look = &sentLook{}
		a.sentLooks[id] = look
	}
	if look.asks >= sentRowAsks || !look.last.IsZero() && time.Since(look.last) < sentRowGap {
		return false
	}
	look.asks++
	look.last = time.Now()
	return true
}

// A sent message whose id is paired but whose row is absent, fetched with one read of the conversation's newest page.
// False when nothing applies (not a message this account sent, an encrypted conversation whose messages are drawn by
// the helper itself, or the bound is spent) or the row still did not come.
func (a *adapter) findSentRow(ctx context.Context, key int64, id string) (viewMessage, bool) {
	real, paired := a.chats.sentPairing(id)
	if !paired || !a.pageRefreshable(key) || !a.sentRowMayAsk(real) {
		return viewMessage{}, false
	}
	readCtx, cancel := context.WithTimeout(ctx, sentRowWait)
	defer cancel()
	revision := a.chats.revision(key)
	tbl, err := a.fetchFrom(readCtx, key, pageAnchor{at: time.Now().Add(sentRowAhead).UnixMilli(), id: real})
	if err != nil {
		logf("sent row chat=%s message=%s fetch failed", tag(key), tagOf(real))
		return viewMessage{}, false
	}
	a.observeHistoryTable(tbl)
	if touched := a.applyTable(readCtx, tbl); !touched.empty() {
		a.out.write(response{Event: "changed", Result: touched})
	}
	// The page can arrive over the socket a moment after the reply, as for any history ask.
	if _, ok := a.chats.messageByID(key, real); !ok {
		a.waitForAnswer(readCtx, key, revision)
	}
	found, ok := a.chats.messageByID(key, real)
	logf("sent row chat=%s message=%s fetched found=%v", tag(key), tagOf(real), ok)
	return found, ok
}
