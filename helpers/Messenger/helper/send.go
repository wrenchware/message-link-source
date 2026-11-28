package main

import (
	"context"
	"sort"
	"strconv"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// How long Facebook is given to answer with the id it gave the message.
const sendWait = 20 * time.Second

// Sends one text message, quoting another one when replyTo names it. The reply carries the id Facebook gave it, so the
// app can match the row it shows to the real message when that arrives over the socket. Nothing is ever sent twice: a
// send whose outcome is unknown stays unknown.
func (a *adapter) sendText(ctx context.Context, key int64, text, replyTo string) (any, string) {
	if key == 0 || text == "" {
		return nil, "invalid_input"
	}
	// A conversation Facebook has moved to end-to-end encryption is not reachable on this connection at all; its
	// messages, in both directions, belong to the other one.
	if encrypted, _ := a.chats.encryptedThread(key); encrypted {
		return a.sendEncrypted(ctx, key, text, replyTo)
	}
	var quoted *socket.ReplyMetaData
	if replyTo != "" {
		// A quote names a message this conversation actually holds; anything else would be a quote of nothing.
		target, ok := a.chats.messageByID(key, replyTo)
		if !ok {
			return nil, "reply_unavailable"
		}
		sender, _ := strconv.ParseInt(target.Sender, 10, 64)
		quoted = &socket.ReplyMetaData{ReplyMessageId: target.ID, ReplySourceType: 1, ReplyType: 0, ReplySender: sender}
	}
	if !a.readyToSend(ctx) {
		return nil, "not_connected"
	}
	otid := methods.GenerateEpochID()
	pending := strconv.FormatInt(otid, 10)
	tbl, err := a.request(ctx, func() []socket.Task {
		return []socket.Task{
			&socket.SendMessageTask{
				ThreadId:         key,
				Otid:             otid,
				Source:           table.MESSENGER_INBOX_IN_THREAD,
				InitiatingSource: table.FACEBOOK_INBOX,
				SendType:         table.TEXT,
				SyncGroup:        1,
				Text:             text,
				ReplyMetaData:    quoted,
			},
			// Sending in a conversation means it has been read, which is what Messenger itself does.
			&socket.ThreadMarkReadTask{ThreadId: key, SyncGroup: 1, LastReadWatermarkTs: time.Now().UnixMilli()},
		}
	})
	if err != nil {
		// The request failed on the way out or on the way back, so whether Facebook took it is not known.
		logf("send chat=%s request failed", tag(key))
		return nil, "send_failed"
	}
	a.applyTable(ctx, tbl)
	id, known := a.chats.sentAs(pending)
	if !known {
		// Some replies carry the message itself rather than the pairing.
		if sent, ok := a.chats.messageByID(key, pending); ok && sent.ID != "" {
			id, known = sent.ID, true
		}
	}
	if known {
		// The message is in the conversation now, and no socket event follows for a message this account sent, so the
		// list and the open conversation are told here as they would be for anything arriving.
		chat := strconv.FormatInt(key, 10)
		a.chats.markSent(key, text, time.Now().UnixMilli())
		a.out.write(response{Event: "changed", Result: &changes{Chats: []string{chat}, Messages: map[string][]string{chat: {id}}}})
		logf("send chat=%s accepted message=%s", tag(key), tagOf(id))
		return map[string]any{"id": id, "otid": pending}, ""
	}
	// No id came back. Facebook says why when it refuses a message outright.
	for _, row := range tbl.LSMarkOptimisticMessageFailed {
		if row.OTID == "" || row.OTID == pending {
			return nil, "send_rejected"
		}
	}
	for _, row := range tbl.LSHandleFailedTask {
		if row.OTID == "" || row.OTID == pending {
			return nil, "send_rejected"
		}
	}
	return nil, "send_failed"
}

// Facebook names the people in a conversation but often sends no picture for them, which leaves the conversation
// showing initials. Their pictures are asked for a batch at a time, behind the list the app is already showing, and
// the conversations they belong to are announced as they arrive. Someone Facebook has no picture for is asked once.
func (a *adapter) faces(ctx context.Context, batch int) {
	defer func() { _ = recover() }()
	defer a.facing.Store(false)
	missing := a.chats.missingPictures(batch)
	if len(missing) == 0 || ctx.Err() != nil {
		return
	}
	// One person per request, as Facebook's own client asks: several of these in one payload share a queue name.
	asked := make([]int64, 0, len(missing))
	touched := map[string]bool{}
	for _, id := range missing {
		if ctx.Err() != nil {
			break
		}
		tbl, err := a.request(ctx, func() []socket.Task {
			return []socket.Task{&socket.GetContactsFullTask{ContactID: id}}
		})
		if err != nil {
			// Leave the rest for the next round rather than marking people as asked when nothing was.
			break
		}
		asked = append(asked, id)
		for _, chat := range a.applyTable(ctx, tbl).Chats {
			touched[chat] = true
		}
	}
	// Asked for, whether or not Facebook had a picture: otherwise the same people are asked forever.
	a.chats.markTried(asked)
	logf("faces asked=%d of %d, conversations changed=%d", len(asked), len(missing), len(touched))
	if len(touched) == 0 {
		return
	}
	ids := make([]string, 0, len(touched))
	for chat := range touched {
		ids = append(ids, chat)
	}
	sort.Strings(ids)
	a.out.write(response{Event: "changed", Result: &changes{Chats: ids}})
}

// Facebook sends the conversation list a page at a time, newest first, so without this the list stops at the first
// page. The pages are fetched in the background and announced as they land rather than holding up the connect.
func (a *adapter) deepen(ctx context.Context, target int) {
	// Paging the list back is the least important thing the helper does; it must never be what stops it serving.
	defer func() { _ = recover() }()
	a.setPaging("waiting")
	// The app is loading the list, its pictures and the open conversation over the same pipe just now; the older
	// conversations can wait for that to settle.
	if !wait(ctx, 4*time.Second) {
		return
	}
	// Facebook answers for the inbox on one sync group and keeps more conversations on another; the first one running
	// out does not mean the list has. The initial fetch asks both, so paging asks both too.
	state := a.page(ctx, 1, target)
	if state != "done" && a.chats.threadCount() < target && ctx.Err() == nil {
		state = a.page(ctx, 95, target)
	}
	a.setPaging(state)
	// The conversations that arrived last were asked about while Facebook was still sending contacts, so anyone still
	// without a picture gets one more turn now that the list has stopped moving.
	if ctx.Err() == nil {
		a.chats.retryPictures()
		if a.facing.CompareAndSwap(false, true) {
			a.faces(ctx, 20)
		}
	}
}

// Pages one sync group back to the target, returning how it ended.
func (a *adapter) page(ctx context.Context, group int64, target int) string {
	var previous int64
	pages, empty, stale := 0, 0, 0
	for pages < 16 && empty < 8 && stale < 3 {
		if ctx.Err() != nil {
			return "stopped"
		}
		known := a.chats.threadCount()
		if known >= target {
			return "done"
		}
		tbl, err := a.fetchThreads(ctx, group)
		if err != nil {
			return "failed"
		}
		// Nothing came back: either there are no older conversations, or the cursor has not arrived yet. It arrives
		// when the socket delivers the ranges that come with a page, so early on it is worth asking again.
		if tbl == nil {
			empty++
			if !wait(ctx, 3*time.Second) {
				return "stopped"
			}
			continue
		}
		pages++
		a.setPaging("running")
		// Older conversations, not new messages: the list is told which ones exist, and nothing is announced as an
		// arrival, so a backfilled page never reads as unread activity.
		touched := a.applyTable(ctx, tbl)
		logRows("thread page", tbl, touched)
		if len(touched.Chats) > 0 {
			a.out.write(response{Event: "changed", Result: &changes{Chats: touched.Chats}})
		}
		// The page's own ranges may come over the socket rather than in the reply, so how far back the list now
		// reaches is only known once those have landed. Judging progress before that call it finished on page one.
		if !wait(ctx, 2*time.Second) {
			return "stopped"
		}
		cursor, more := a.threadCursor(group)
		logf("page group=%d n=%d threads=%d->%d cursor=%d more=%v", group, pages, known, a.chats.threadCount(), cursor, more)
		if !more {
			return "complete"
		}
		// A page that brought no conversations and did not move the cursor is not necessarily the end; Facebook
		// sometimes answers with nothing and continues afterwards. Several in a row is the end.
		if a.chats.threadCount() == known && cursor == previous {
			stale++
		} else {
			stale = 0
		}
		previous = cursor
	}
	// Either Facebook stopped answering with anything new, or the list is as deep as this build takes it.
	if stale >= 3 {
		return "settled"
	}
	return "stopped"
}

// True unless the helper is shutting down.
func wait(ctx context.Context, delay time.Duration) bool {
	select {
	case <-time.After(delay):
		return true
	case <-ctx.Done():
		return false
	}
}
