package main

import (
	"context"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

// Voice answers a conversation list 20 conversations at a time, with the next page's token in the answer's field 2 and
// taken back in the request's field 4. libgv reads and sends both since upstream 4f5ca46 (2026-10-14); before that the
// helper read the token out of the answer's unknown fields. A live probe confirmed the token: the next page held 20 other
// conversations and a new token.
func (a *adapter) listPage(ctx context.Context, client *libgv.Client, token string) (*gvproto.RespListThreads, error) {
	if a.listPageAfter != nil {
		return a.listPageAfter(ctx, token)
	}
	if client == nil {
		return nil, errLoggedOut
	}
	return client.ListThreads(ctx, gvproto.ThreadFolder_ALL_THREADS, token, "")
}

type chatPage struct {
	Chats []voiceChat `json:"chats"`
	Next  string      `json:"next,omitempty"`
}

// One page of the conversation list. The first page is the main list with the archived folder; each later page is the
// main list after the given token. Conversations are named from contacts, and their read state is remembered for change detection.
func (a *adapter) chatPage(ctx context.Context, client *libgv.Client, cursor string) (*chatPage, error) {
	var resp *gvproto.RespListThreads
	var err error
	if cursor == "" {
		resp, err = a.listWithArchivedFor(ctx, client)
	} else {
		resp, err = a.listPage(ctx, client, cursor)
	}
	if err != nil {
		return nil, err
	}
	next := resp.GetPaginationToken()
	// A page that points back at itself would never end.
	if next == cursor {
		next = ""
	}
	if cursor != "" {
		a.diagnose(map[string]any{"olderPage": "ok", "threads": len(resp.GetThreads()), "nextToken": map[bool]string{true: "new", false: "none"}[next != ""]})
	}
	if cursor == "" {
		a.noteThreadIds(resp.GetThreads())
	}
	named, stats := a.contacts.infoFor(ctx, client, participantNumbers(resp.GetThreads()))
	if stats != nil {
		a.diagnose(stats)
	}
	page := &chatPage{Chats: make([]voiceChat, 0, len(resp.GetThreads())), Next: next}
	a.mu.Lock()
	for _, thread := range resp.GetThreads() {
		chat := convertThreadNamed(thread, named)
		a.lastRead[chat.ID] = !chat.Unread
		page.Chats = append(page.Chats, chat)
	}
	// Only change detection moves what counts as new. The first list sets the starting point, so a message that lands
	// before the first detection is still announced and what was already there is not.
	if cursor == "" && !a.watermarked {
		for _, chat := range page.Chats {
			a.seenThrough = max(a.seenThrough, chat.LastActivity)
		}
		a.watermarked = true
	}
	a.mu.Unlock()
	return page, nil
}

// Whether one-to-one conversation ids are "t." and the other person's number, which New chat relies on to start a
// conversation. Reported once per helper, as counts.
func (a *adapter) noteThreadIds(threads []*gvproto.Thread) {
	a.mu.Lock()
	done := a.idsNoted
	a.idsNoted = true
	a.mu.Unlock()
	if done {
		return
	}
	direct, matching := 0, 0
	for _, thread := range threads {
		numbers := map[string]bool{}
		for _, contact := range thread.GetContacts() {
			if contact.GetPhoneNumber() != "" {
				numbers[contact.GetPhoneNumber()] = true
			}
		}
		for _, number := range thread.GetPhoneNumbers() {
			if number != "" {
				numbers[number] = true
			}
		}
		if len(numbers) != 1 {
			continue
		}
		direct++
		for number := range numbers {
			if thread.GetID() == "t."+number {
				matching++
			}
		}
	}
	a.diagnose(map[string]any{"direct": direct, "directIdsMatch": matching})
}
