package main

import (
	"context"
	"encoding/json"
	"sync"
	"time"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// A photo or video is readable only through the reference its message carried when this helper handed it out, and
// those references live in memory (mediaRefs). On 2026-10-12 a video sent days before the Messages Server existed could
// not be opened from it: nothing in this run had handed its message out, so the read answered media_unavailable. The
// host keeps which message each attachment came from in its search archive and, when a read misses, asks here to
// re-learn it: one ordinary page of that conversation's messages, the same read a history page makes, read only.
//
// Bounds, because a gallery of old tiles must not become a burst of reads from the phone: each attachment is re-learned
// at most once per relearnEvery, and at most relearnPerMinute times a minute overall. A second ask while one is under
// way waits for it rather than reading again.
const relearnEvery = 5 * time.Minute
const relearnPerMinute = 4

type relearnMark struct {
	at    time.Time
	found bool
}

type relearnState struct {
	mu       sync.Mutex
	marks    map[string]relearnMark
	recent   []time.Time
	inflight map[string]chan struct{}
}

// One page of a conversation's messages; stands in for libgm in tests.
type pageFetcher func(ctx context.Context, chatID string, count int64, cursor *gmproto.Cursor) (*gmproto.ListMessagesResponse, error)

// The reference behind an attachment id, if this helper holds one.
func (a *adapter) mediaRef(id string) *gmproto.MediaContent {
	a.mediaMu.Lock()
	defer a.mediaMu.Unlock()
	return a.mediaRefs[id]
}

// Outcomes: found (the reference is held now), missing (the page did not carry it), limited (a bound held, nothing was
// read), known (already held: the miss was the download itself, so reading the page again would change nothing), missed
// (the page was anchored at the message itself, a guess, and did not hold that message: the anchor was wrong, not the
// attachment, so its five minutes are not spent; the minute's allowance still is).
func (a *adapter) mediaRelearn(ctx context.Context, r request) (any, string) {
	var p struct {
		ID        string          `json:"id"`
		ChatID    string          `json:"chatId"`
		Cursor    *gmproto.Cursor `json:"cursor"`
		MessageID string          `json:"messageId"`
		Anchor    string          `json:"anchor"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.ID == "" || p.ChatID == "" {
		return nil, "invalid_input"
	}
	fetch := a.fetchPage
	if fetch == nil {
		if a.client == nil || a.client.AuthData.Browser == nil {
			return nil, "pairing_required"
		}
		fetch = a.client.FetchMessages
	}
	answer := func(outcome string) (any, string) { return map[string]string{"outcome": outcome}, "" }
	now := time.Now()
	if a.relearnNow != nil {
		now = a.relearnNow()
	}
	s := &a.relearn
	s.mu.Lock()
	if wait, busy := s.inflight[p.ID]; busy {
		s.mu.Unlock()
		select {
		case <-wait:
		case <-ctx.Done():
			return nil, "request_cancelled"
		}
		if a.mediaRef(p.ID) != nil {
			return answer("found")
		}
		return answer("missing")
	}
	mark, marked := s.marks[p.ID]
	if a.mediaRef(p.ID) != nil {
		s.mu.Unlock()
		// Held because a recent re-learn found it (another tile's ask finished first): the caller's retry will succeed.
		if marked && mark.found && now.Sub(mark.at) < relearnEvery {
			return answer("found")
		}
		return answer("known")
	}
	if marked && now.Sub(mark.at) < relearnEvery {
		s.mu.Unlock()
		return answer("limited")
	}
	kept := s.recent[:0]
	for _, at := range s.recent {
		if now.Sub(at) < time.Minute {
			kept = append(kept, at)
		}
	}
	s.recent = kept
	if len(s.recent) >= relearnPerMinute {
		s.mu.Unlock()
		return answer("limited")
	}
	s.recent = append(s.recent, now)
	if s.marks == nil || len(s.marks) >= 2000 {
		s.marks = make(map[string]relearnMark)
	}
	// Marked before the read, so a read that fails or is cancelled still counts against this attachment's bound.
	s.marks[p.ID] = relearnMark{at: now}
	if s.inflight == nil {
		s.inflight = make(map[string]chan struct{})
	}
	done := make(chan struct{})
	s.inflight[p.ID] = done
	s.mu.Unlock()

	found := false
	missed := false
	defer func() {
		s.mu.Lock()
		if missed {
			delete(s.marks, p.ID)
		} else {
			s.marks[p.ID] = relearnMark{at: now, found: found}
		}
		delete(s.inflight, p.ID)
		s.mu.Unlock()
		close(done)
	}()
	readCtx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	result, err := fetch(readCtx, p.ChatID, pageCount(0), p.Cursor)
	if err != nil {
		return nil, "history_failed"
	}
	// The page's other messages are registered too, as a history page would; the one carrying the attachment goes last,
	// so the reference map's clear-out at its size limit cannot drop it on the way.
	var target *gmproto.Message
	held := false
	for _, m := range result.GetMessages() {
		if p.MessageID != "" && m.GetMessageID() == p.MessageID {
			held = true
		}
		if target == nil && carriesMedia(m, p.ID) {
			target = m
			continue
		}
		a.viewMessage(m)
	}
	if target != nil {
		a.viewMessage(target)
	}
	found = a.mediaRef(p.ID) != nil
	if found {
		return answer("found")
	}
	// 2026-10-12 (code review): an anchor at the message itself is a guess (which part of a cursor the phone pages by
	// is not known); a page without that message says nothing about the attachment.
	if p.Anchor == "self" && p.MessageID != "" && !held {
		missed = true
		return answer("missed")
	}
	return answer("missing")
}

func carriesMedia(m *gmproto.Message, id string) bool {
	for i, part := range m.GetMessageInfo() {
		if part.GetMediaContent() != nil && mediaDescriptor(m, part, i).ID == id {
			return true
		}
	}
	return false
}
