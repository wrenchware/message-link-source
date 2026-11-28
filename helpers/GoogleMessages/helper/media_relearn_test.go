package main

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

func relearnMessage(chat, id, media string) *gmproto.Message {
	c := &gmproto.MediaContent{MediaID: media, MimeType: "video/mp4", DecryptionKey: []byte("key")}
	return &gmproto.Message{ConversationID: chat, MessageID: id, MessageInfo: []*gmproto.MessageInfo{{Data: &gmproto.MessageInfo_MediaContent{MediaContent: c}}}}
}

func relearnID(m *gmproto.Message) string { return mediaDescriptor(m, m.GetMessageInfo()[0], 0).ID }

type relearnRig struct {
	a     *adapter
	reads int
	asked []*gmproto.Cursor
	page  []*gmproto.Message
	now   time.Time
}

func newRelearnRig(page ...*gmproto.Message) *relearnRig {
	rig := &relearnRig{page: page, now: time.Date(2026, 10, 12, 12, 0, 0, 0, time.UTC)}
	rig.a = &adapter{relearnNow: func() time.Time { return rig.now }}
	rig.a.fetchPage = func(ctx context.Context, chatID string, count int64, cursor *gmproto.Cursor) (*gmproto.ListMessagesResponse, error) {
		rig.reads++
		rig.asked = append(rig.asked, cursor)
		if count != 20 {
			panic("not an ordinary page")
		}
		return &gmproto.ListMessagesResponse{Messages: rig.page}, nil
	}
	return rig
}

func (rig *relearnRig) ask(id string, params map[string]any) (string, string) {
	if params == nil {
		params = map[string]any{"id": id, "chatId": "chat", "cursor": map[string]any{"lastItemID": "newer", "lastItemTimestamp": 1700000000123}}
	}
	raw, _ := json.Marshal(params)
	result, code := rig.a.callContext(context.Background(), request{Method: "ui.mediaRelearn", Params: raw})
	if code != "" {
		return "", code
	}
	return result.(map[string]string)["outcome"], ""
}

// A video handed out in an earlier run (or before the reference map's clear-out) is found again by one page read of its
// conversation, after which the read resolves it. The cursor the host gives is the page's, as a history page passes it.
func TestMediaRelearnFindsAnAttachmentByOnePageRead(t *testing.T) {
	video := relearnMessage("chat", "old", "video")
	rig := newRelearnRig(relearnMessage("chat", "other", "photo"), video)
	id := relearnID(video)
	if rig.a.mediaRef(id) != nil {
		t.Fatal("held before the re-learn")
	}
	if outcome, code := rig.ask(id, nil); outcome != "found" || code != "" {
		t.Fatalf("outcome %q code %q", outcome, code)
	}
	if rig.reads != 1 || rig.asked[0].GetLastItemID() != "newer" || rig.asked[0].GetLastItemTimestamp() != 1700000000123 {
		t.Fatal("not one page read at the given cursor")
	}
	if c := rig.a.mediaRef(id); c == nil || c.GetMediaID() != "video" {
		t.Fatal("the read does not resolve it afterwards")
	}
	// Held now: asking again reads nothing.
	if outcome, _ := rig.ask(id, nil); outcome != "found" || rig.reads != 1 {
		t.Fatal("a held attachment was read again")
	}
}

// The reference map's clear-out at its size limit must not drop the very attachment the page was read for.
func TestMediaRelearnKeepsTheTargetThroughTheMapsClearOut(t *testing.T) {
	video := relearnMessage("chat", "old", "video")
	page := []*gmproto.Message{video}
	for i := range 30 {
		page = append(page, relearnMessage("chat", "m"+string(rune('a'+i)), "p"+string(rune('a'+i))))
	}
	rig := newRelearnRig(page...)
	rig.a.mediaRefs = map[string]*gmproto.MediaContent{}
	rig.a.fullSizeTargets = map[string]fullSizeTarget{}
	for i := range 1990 {
		rig.a.mediaRefs[string(rune(0x1000+i))] = &gmproto.MediaContent{}
	}
	if outcome, _ := rig.ask(relearnID(video), nil); outcome != "found" || rig.a.mediaRef(relearnID(video)) == nil {
		t.Fatal("the clear-out dropped the target")
	}
}

// Each attachment at most once per five minutes, at most four a minute overall; a page without it answers missing.
func TestMediaRelearnBounds(t *testing.T) {
	rig := newRelearnRig(relearnMessage("chat", "unrelated", "photo"))
	if outcome, _ := rig.ask("gone", nil); outcome != "missing" || rig.reads != 1 {
		t.Fatal("a page without it is missing")
	}
	rig.now = rig.now.Add(4 * time.Minute)
	if outcome, _ := rig.ask("gone", nil); outcome != "limited" || rig.reads != 1 {
		t.Fatal("re-learned twice within five minutes")
	}
	rig.now = rig.now.Add(2 * time.Minute)
	if outcome, _ := rig.ask("gone", nil); outcome != "missing" || rig.reads != 2 {
		t.Fatal("not asked again after five minutes")
	}
	// Three more distinct tiles fill the minute; the fifth is limited without a read.
	for _, id := range []string{"t1", "t2", "t3"} {
		if outcome, _ := rig.ask(id, nil); outcome != "missing" {
			t.Fatal("within the minute's allowance")
		}
	}
	if outcome, _ := rig.ask("t4", nil); outcome != "limited" || rig.reads != 5 {
		t.Fatalf("burst not limited: reads %d", rig.reads)
	}
	rig.now = rig.now.Add(61 * time.Second)
	if outcome, _ := rig.ask("t4", nil); outcome != "missing" || rig.reads != 6 {
		t.Fatal("a limited tile is not tried after the minute")
	}
}

// An ask with no conversation costs nothing, and one already held reads nothing: its miss was the download itself.
func TestMediaRelearnWithoutARecordReadsNothing(t *testing.T) {
	held := relearnMessage("chat", "held", "photo")
	rig := newRelearnRig(held)
	if _, code := rig.ask("x", map[string]any{"id": "x"}); code != "invalid_input" {
		t.Fatal("no conversation accepted")
	}
	if _, code := rig.ask("", map[string]any{"chatId": "chat"}); code != "invalid_input" {
		t.Fatal("no id accepted")
	}
	rig.a.viewMessage(held)
	if outcome, _ := rig.ask(relearnID(held), nil); outcome != "known" {
		t.Fatal("a held attachment is not known")
	}
	if rig.reads != 0 {
		t.Fatalf("read %d pages", rig.reads)
	}
	// Without a test stand-in or a pairing, nothing reaches the network.
	bare := &adapter{}
	raw, _ := json.Marshal(map[string]any{"id": "x", "chatId": "chat"})
	if _, code := bare.callContext(context.Background(), request{Method: "ui.mediaRelearn", Params: raw}); code != "pairing_required" {
		t.Fatal("unpaired")
	}
}

// Two tiles of one attachment asking at once make one read; the second waits and shares the answer.
func TestMediaRelearnSharesAReadUnderWay(t *testing.T) {
	video := relearnMessage("chat", "old", "video")
	id := relearnID(video)
	release := make(chan struct{})
	entered := make(chan struct{})
	rig := newRelearnRig(video)
	inner := rig.a.fetchPage
	rig.a.fetchPage = func(ctx context.Context, chatID string, count int64, cursor *gmproto.Cursor) (*gmproto.ListMessagesResponse, error) {
		close(entered)
		<-release
		return inner(ctx, chatID, count, cursor)
	}
	first := make(chan string)
	go func() { outcome, _ := rig.ask(id, nil); first <- outcome }()
	<-entered
	second := make(chan string)
	go func() { outcome, _ := rig.ask(id, nil); second <- outcome }()
	time.Sleep(20 * time.Millisecond)
	close(release)
	if <-first != "found" || <-second != "found" || rig.reads != 1 {
		t.Fatalf("reads %d", rig.reads)
	}
}

// A phone that pages by time: newest first, older than the cursor's timestamp (milliseconds), a page at a time.
func timePagedPhone(rig *relearnRig, conversation []*gmproto.Message) {
	rig.a.fetchPage = func(ctx context.Context, chatID string, count int64, cursor *gmproto.Cursor) (*gmproto.ListMessagesResponse, error) {
		rig.reads++
		rig.asked = append(rig.asked, cursor)
		var page []*gmproto.Message
		for i := len(conversation) - 1; i >= 0 && int64(len(page)) < count; i-- {
			m := conversation[i]
			if cursor == nil || m.GetTimestamp()/1000 < cursor.GetLastItemTimestamp() {
				page = append(page, m)
			}
		}
		return &gmproto.ListMessagesResponse{Messages: page}, nil
	}
}

// 2026-10-12 (code review): the attachment's message is the newest the archive saved, but Google holds more than a page
// of newer ones. The host anchors the page at the message itself, a millisecond after it, and that page holds it.
func TestMediaRelearnNewestArchivedBehindAFullPage(t *testing.T) {
	const at = int64(1700000000123456) // microseconds, as Google stamps a message
	video := relearnMessage("chat", "old", "video")
	video.Timestamp = at
	conversation := []*gmproto.Message{video}
	for i := range 25 {
		m := relearnMessage("chat", "n"+string(rune('a'+i)), "p"+string(rune('a'+i)))
		m.Timestamp = at + int64(i+1)*1000000
		conversation = append(conversation, m)
	}
	id := relearnID(video)
	// The newest page, which a null cursor read, does not reach it.
	before := newRelearnRig()
	timePagedPhone(before, conversation)
	if outcome, _ := before.ask(id, map[string]any{"id": id, "chatId": "chat"}); outcome != "missing" {
		t.Fatalf("the newest page reached it: %q", outcome)
	}
	rig := newRelearnRig()
	timePagedPhone(rig, conversation)
	self := map[string]any{"id": id, "chatId": "chat", "messageId": "old", "anchor": "self",
		"cursor": map[string]any{"lastItemID": "old", "lastItemTimestamp": at/1000 + 1}}
	if outcome, _ := rig.ask(id, self); outcome != "found" || rig.reads != 1 {
		t.Fatalf("outcome %q reads %d", outcome, rig.reads)
	}
}

// A page anchored at the message itself that does not hold it was a wrong guess, not a missing attachment: it answers
// missed and does not spend the attachment's five minutes. A page that held the message, or one after a newer saved
// message, keeps the bound.
func TestMediaRelearnAGuessThatMissesSpendsNoBound(t *testing.T) {
	rig := newRelearnRig(relearnMessage("chat", "unrelated", "photo"))
	self := map[string]any{"id": "gone", "chatId": "chat", "messageId": "old", "anchor": "self",
		"cursor": map[string]any{"lastItemID": "old", "lastItemTimestamp": 1700000000124}}
	if outcome, _ := rig.ask("gone", self); outcome != "missed" || rig.reads != 1 {
		t.Fatalf("a guess that missed: %q", outcome)
	}
	if outcome, _ := rig.ask("gone", self); outcome != "missed" || rig.reads != 2 {
		t.Fatalf("a guess that missed spent the five minutes: %q reads %d", outcome, rig.reads)
	}
	rig.page = append(rig.page, relearnMessage("chat", "old", "other"))
	if outcome, _ := rig.ask("gone", self); outcome != "missing" || rig.reads != 3 {
		t.Fatalf("a page holding the message: %q", outcome)
	}
	if outcome, _ := rig.ask("gone", self); outcome != "limited" || rig.reads != 3 {
		t.Fatalf("not bounded after a page that held the message: %q", outcome)
	}
	newer := map[string]any{"id": "also-gone", "chatId": "chat", "messageId": "absent", "anchor": "newer",
		"cursor": map[string]any{"lastItemID": "newer", "lastItemTimestamp": 1700000000123}}
	if outcome, _ := rig.ask("also-gone", newer); outcome != "missing" {
		t.Fatalf("a page after a newer message: %q", outcome)
	}
}
