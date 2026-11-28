package main

import (
	"context"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// What an anchored fetch asked for, recorded. The refresh runs on its own goroutine, so this is guarded.
type asks struct {
	mu   sync.Mutex
	seen []pageAnchor
}

func (a *asks) add(anchor pageAnchor) {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.seen = append(a.seen, anchor)
}

func (a *asks) count() int {
	a.mu.Lock()
	defer a.mu.Unlock()
	return len(a.seen)
}

func (a *asks) oldest() int64 {
	a.mu.Lock()
	defer a.mu.Unlock()
	if len(a.seen) == 0 {
		return 0
	}
	return a.seen[len(a.seen)-1].at
}

// The refresh happens behind the answer, so a test waits for it rather than expecting it to have already run.
func settle(t *testing.T, done func() bool) bool {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if done() {
			return true
		}
		time.Sleep(10 * time.Millisecond)
	}
	return false
}

// What Facebook sends back for an anchored fetch: the messages around the anchor, already known, carrying whatever
// attachments they have. The rows are what tells the helper its request landed.
func answerFor(key int64, anchor pageAnchor) *table.LSTable {
	return &table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{
		{ThreadKey: key, MessageId: anchor.id, SenderId: 1, Text: "synthetic", TimestampMs: anchor.at},
	}}
}

func restoredChat(count int) (*adapter, *asks) {
	a := newAdapter(&output{})
	for index := 0; index < count; index++ {
		a.chats.addEncrypted(9, id(index), 1, "synthetic", "", int64(1000+index))
	}
	a.chats.markFromStore(9)
	asked := &asks{}
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		asked.add(anchor)
		return answerFor(key, anchor), nil
	}
	return a, asked
}

// Reading an older page marks the whole conversation asked. That must not stand in for the newest page never having
// been refreshed, or background work — the archiver reads older pages — would leave the page on screen unrefreshed.
func TestOlderPageDoesNotSatisfyTheNewestOne(t *testing.T) {
	a, asked := restoredChat(120)
	a.chats.markPageRefreshed(9, 1100)
	if _, code := a.history(context.Background(), 9, 50, 1100); code != "" {
		t.Fatalf("older page: %s", code)
	}
	if asked.count() != 0 {
		t.Fatalf("a page already refreshed should not be asked for again, got %d asks", asked.count())
	}
	if _, code := a.history(context.Background(), 9, 50, 0); code != "" {
		t.Fatalf("newest page: %s", code)
	}
	if !settle(t, func() bool { return a.chats.pageRefreshed(9, 0) }) {
		t.Fatalf("the newest page was answered from the stored copy without being refreshed, asks=%d", asked.count())
	}
}

// A page is served straight away and refreshed behind it: the helper answers one call at a time, and holding a
// conversation back for several requests stalls the pictures and the faces waiting behind it.
func TestThePageIsNotHeldUpByItsRefresh(t *testing.T) {
	a, asked := restoredChat(120)
	release := make(chan struct{})
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		<-release
		asked.add(anchor)
		return answerFor(key, anchor), nil
	}
	started := time.Now()
	if _, code := a.history(context.Background(), 9, 50, 0); code != "" {
		t.Fatalf("history: %s", code)
	}
	if waited := time.Since(started); waited > time.Second {
		t.Errorf("the page waited %v for its refresh", waited)
	}
	close(release)
	if !settle(t, func() bool { return a.chats.pageRefreshed(9, 0) }) {
		t.Error("the refresh behind the page never finished")
	}
}

// A refresh that could not be carried out leaves the page to be tried again, rather than recording it as done.
func TestFailedRefreshStaysRetryable(t *testing.T) {
	a, _ := restoredChat(60)
	tried := make(chan struct{}, 4)
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		tried <- struct{}{}
		return nil, context.Canceled
	}
	if _, code := a.history(context.Background(), 9, 50, 0); code != "" {
		// The page is still served: a picture that could not be refreshed is no reason to show nothing.
		t.Fatalf("history should still answer, got %s", code)
	}
	select {
	case <-tried:
	case <-time.After(5 * time.Second):
		t.Fatal("the refresh never ran")
	}
	if !settle(t, func() bool { return !a.refreshRunning(9, 0) }) {
		t.Fatal("the refresh never finished")
	}
	if a.chats.pageRefreshed(9, 0) {
		t.Fatal("a refresh that failed was recorded as having covered the page")
	}
}

// A page Facebook never covers stops showing as updating after its first try, and is asked for again only a bounded
// number of times however often the app reads it.
func TestUncoveredPageRefreshIsBounded(t *testing.T) {
	a, _ := restoredChat(60)
	var tries sync.Mutex
	fetches := 0
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		tries.Lock()
		fetches++
		tries.Unlock()
		return nil, context.Canceled
	}
	if !a.pageFirstRefreshPending(9, 0) {
		t.Fatal("a restored page should report its first refresh as pending")
	}
	for read := 0; read < maxPageRefreshTries+3; read++ {
		if _, code := a.history(context.Background(), 9, 50, 0); code != "" {
			t.Fatalf("history: %s", code)
		}
		if !settle(t, func() bool { return !a.refreshRunning(9, 0) }) {
			t.Fatal("the refresh never finished")
		}
		if a.pageFirstRefreshPending(9, 0) {
			t.Fatal("the page still reports updating after a finished try")
		}
	}
	if got := a.chats.pageRefreshTries(9, 0); got != maxPageRefreshTries {
		t.Fatalf("expected %d tries, got %d", maxPageRefreshTries, got)
	}
	if a.chats.pageRefreshed(9, 0) {
		t.Fatal("an uncovered page was recorded as covered")
	}
	tries.Lock()
	defer tries.Unlock()
	if fetches != maxPageRefreshTries {
		t.Fatalf("expected %d fetches, got %d", maxPageRefreshTries, fetches)
	}
}

// A preview read that starts the refresh must not end the updating signal for the conversation opened afterwards.
func TestPageStaysUpdatingWhileItsFirstRefreshRuns(t *testing.T) {
	a, _ := restoredChat(60)
	release := make(chan struct{})
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		<-release
		return answerFor(key, anchor), nil
	}
	for read := 0; read < 2; read++ {
		if _, code := a.history(context.Background(), 9, 50, 0); code != "" {
			t.Fatalf("history: %s", code)
		}
		if !a.pageFirstRefreshPending(9, 0) {
			t.Fatal("a second read hid a refresh that is still running")
		}
	}
	close(release)
	if !settle(t, func() bool { return !a.pageFirstRefreshPending(9, 0) }) {
		t.Fatal("the page kept reporting updating after its refresh")
	}
}

// An expired picture sends the conversation back to Facebook, and the page shows as updating again.
func TestRefreshAgainResetsTries(t *testing.T) {
	s := newStore()
	s.markFromStore(7)
	s.notePageRefreshTry(7, 0)
	s.refreshAgain(7)
	if s.pageRefreshTries(7, 0) != 0 {
		t.Fatal("refreshing a conversation again should start its tries over")
	}
}

// A page showing fewer messages than were asked for has no far end to stop at: the messages missing from it are
// photos with no caption, which are filtered out until their attachments come back. Stopping at the oldest one
// visible would leave those unreachable for good.
func TestShortPageIsWalkedPastWhatItShows(t *testing.T) {
	a := newAdapter(&output{})
	for index := 0; index < 60; index++ {
		body := ""
		if index >= 50 {
			body = "synthetic"
		}
		a.chats.addEncrypted(9, id(index), 1, body, "", int64(1000+index))
	}
	a.chats.markFromStore(9)
	asked := &asks{}
	a.fetch = func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
		asked.add(anchor)
		return answerFor(key, anchor), nil
	}
	if len(a.chats.historyBefore(9, 0, 50)) != 10 {
		t.Fatal("only the ten messages with text are in the page yet")
	}
	if _, code := a.history(context.Background(), 9, 50, 0); code != "" {
		t.Fatalf("history: %s", code)
	}
	if !settle(t, func() bool { return asked.count() >= 3 }) {
		t.Fatalf("60 stored messages at about 20 an ask needs 3 asks, got %d", asked.count())
	}
	if oldest := asked.oldest(); oldest > 1020 {
		t.Errorf("the walk stopped at %d, short of the older photo messages", oldest)
	}
}

// The attachments on a message are kept with it. Without this a photo with no caption comes back as a row with
// nothing in it and is dropped from the conversation altogether — the fault behind a day of missing pictures.
func TestAttachmentsSurviveRestart(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	a := newAdapter(&output{})
	a.kept = kept
	touched := a.chats.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "m1", SenderId: 5, TimestampMs: 1700}},
		LSInsertBlobAttachment: []*table.LSInsertBlobAttachment{
			{ThreadKey: 7, MessageId: "m1", AttachmentFbid: "a1", Filename: "photo.jpg",
				PreviewUrl: "https://example.test/p.jpg", PreviewWidth: 4, PreviewHeight: 3},
		},
	})
	a.keepMessages(ctx, touched)
	if len(a.chats.historyBefore(7, 0, 10)) != 1 {
		t.Fatal("the message with a picture should be in the conversation")
	}

	back := newStore()
	kept.restore(ctx, back)
	page := back.historyBefore(7, 0, 10)
	if len(page) != 1 {
		t.Fatalf("a photo message with no caption disappeared on restore, got %d rows", len(page))
	}
	if len(page[0].Media) != 1 {
		t.Fatalf("the attachment did not come back: %+v", page[0].Media)
	}
}

// A reaction on an encrypted conversation is delivered once and never replayed as history, so the only copy is the
// one written down. This is the path the helper actually uses: the adapter records it as it arrives.
func TestEncryptedReactionSurvivesRestart(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	a := newAdapter(&output{})
	a.kept = kept
	a.chats.addEncrypted(7, "m1", 42, "synthetic", "", 1700)
	kept.remember(ctx, 7, "m1", 42, "synthetic", "", 1700)
	a.remember(ctx, 7, "m1", 43, "ok")
	a.chats.react(7, "m1", 43, "ok")

	back := newStore()
	kept.restore(ctx, back)
	message, ok := back.messageByID(7, "m1")
	if !ok {
		t.Fatal("the message itself did not come back")
	}
	if len(message.Reactions) != 1 || message.Reactions[0].Key != "ok" {
		t.Fatalf("restored reactions = %+v, want one", message.Reactions)
	}

	// Taking it away has to survive too, or a removed reaction returns at the next start.
	a.remember(ctx, 7, "m1", 43, "")
	cleared := newStore()
	kept.restore(ctx, cleared)
	after, _ := cleared.messageByID(7, "m1")
	if len(after.Reactions) != 0 {
		t.Errorf("a reaction taken away came back: %+v", after.Reactions)
	}
}

// A message taken back stays taken back: the stored copy predates the deletion and would otherwise restore it.
func TestDeletedMessageStaysDeleted(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	kept.remember(ctx, 7, "m1", 42, "synthetic", "", 1700)
	kept.forget(ctx, 7, "m1")
	back := newStore()
	kept.restore(ctx, back)
	if _, ok := back.messageByID(7, "m1"); ok {
		t.Error("a deleted message came back from the store")
	}
}

// A stored url outlives its usefulness: Facebook expires them. A fetch that fails on one has to send the conversation
// back to Facebook, or the picture stays broken for the rest of the session.
func TestExpiredPictureSendsTheChatBackToFacebook(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "m1", SenderId: 5, TimestampMs: 1700}},
		LSInsertBlobAttachment: []*table.LSInsertBlobAttachment{
			{ThreadKey: 7, MessageId: "m1", AttachmentFbid: "a1", PreviewUrl: "https://example.test/p.jpg"},
		},
	})
	s.markPageRefreshed(7, 0)
	key, ok := s.chatForMedia("a1")
	if !ok || key != 7 {
		t.Fatalf("the attachment should be traced back to its conversation, got %d %v", key, ok)
	}
	s.refreshAgain(key)
	if s.pageRefreshed(7, 0) || !s.restoredFrom(7) {
		t.Error("the conversation should be asked for again after an expired picture")
	}
}

// A reel or post shared into an encrypted conversation arrives as text with a thumbnail and somewhere it leads, and
// the link has to survive a restart with it — the picture alone leads nowhere.
func TestEncryptedShareKeepsItsLink(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, filepath.Join(t.TempDir(), "messages.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	a := newAdapter(&output{})
	a.kept = kept
	a.chats.addEncrypted(7, "m1", 42, "", "", 1700)
	kept.remember(ctx, 7, "m1", 42, "", "", 1700)
	item := &attachment{id: "e2ee:m1", name: "A reel", mime: "image/jpeg", width: 9, height: 16,
		link: "https://example.test/reel"}
	held := &secureMedia{transport: []byte("directions"), kind: "image"}
	a.chats.attachSecure(7, "m1", 1700, item, held)
	kept.rememberMedia(ctx, 7, "m1", 1700, item, held)

	back := newStore()
	kept.restore(ctx, back)
	page := back.historyBefore(7, 0, 10)
	if len(page) != 1 || len(page[0].Media) != 1 {
		t.Fatalf("the share should come back on its message: %+v", page)
	}
	if page[0].Media[0].Link != "https://example.test/reel" {
		t.Errorf("the link did not survive the restart, got %q", page[0].Media[0].Link)
	}
}

// An encrypted conversation is never asked of the ordinary connection. Facebook answers such a fetch with nothing at
// all, so the refresh could never cover the page: it would leave the page stale, which costs the conversation a
// foreground fetch and a background one on each of its next few opens, and it would show the app a conversation that
// is updating until a refresh that was never going to finish gave up.
func TestEncryptedConversationIsNotRefreshedOverLightspeed(t *testing.T) {
	a, asked := restoredChat(60)
	a.chats.get(9).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	if a.pageFirstRefreshPending(9, 0) {
		t.Fatal("an encrypted conversation reported a refresh the ordinary connection cannot carry out")
	}
	if _, code := a.history(context.Background(), 9, 50, 0); code != "" {
		t.Fatalf("history: %s", code)
	}
	if !settle(t, func() bool { return !a.refreshRunning(9, 0) }) {
		t.Fatal("a refresh was started for an encrypted conversation")
	}
	if asked.count() != 0 {
		t.Fatalf("an encrypted conversation was asked of Facebook %d times", asked.count())
	}
	// And the page it is not refreshing is no longer stale, so the next read answers from memory instead of paying
	// for another round of fetches.
	if a.pageNeedsRefresh(9, 0) {
		t.Fatal("an encrypted page stayed stale, which costs it a fetch on every open")
	}
}

// The ordinary conversation beside it still refreshes: the gate is about which connection carries the messages, not
// about restored pages in general.
func TestOrdinaryConversationStillRefreshes(t *testing.T) {
	a, asked := restoredChat(60)
	a.chats.get(9).kind = table.ONE_TO_ONE
	if !a.pageFirstRefreshPending(9, 0) {
		t.Fatal("a restored ordinary page should report its first refresh as pending")
	}
	if _, code := a.history(context.Background(), 9, 50, 0); code != "" {
		t.Fatalf("history: %s", code)
	}
	if !settle(t, func() bool { return a.chats.pageRefreshed(9, 0) }) {
		t.Fatalf("the ordinary conversation was not refreshed, asks=%d", asked.count())
	}
}

// An encrypted conversation is never asked of the ordinary connection, in front of the page or behind it. Facebook
// answers such a fetch with nothing at all, and the asking is not free: the first open of each encrypted conversation
// in a session spent about 1.6 seconds on it and returned exactly the messages it already had.
func TestEncryptedConversationIsNotAskedInTheForeground(t *testing.T) {
	a, asked := restoredChat(12)
	a.chats.get(9).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	// Fewer messages than a page, which is what sends an ordinary conversation to Facebook for more.
	page, code := a.history(context.Background(), 9, 50, 0)
	if code != "" {
		t.Fatalf("history: %s", code)
	}
	if len(page.([]viewMessage)) != 12 {
		t.Fatalf("expected the twelve it holds, got %d", len(page.([]viewMessage)))
	}
	if !settle(t, func() bool { return !a.refreshRunning(9, 0) }) {
		t.Fatal("a refresh was started for an encrypted conversation")
	}
	if asked.count() != 0 {
		t.Fatalf("an encrypted conversation was asked of Facebook %d times", asked.count())
	}
}

// And the decision itself, which is the part worth pinning: an ordinary conversation is still asked, one holding
// nothing is asked twice, and only an encrypted one is not asked at all.
func TestHistoryRoundsAskOrdinaryConversationsAndNeverEncryptedOnes(t *testing.T) {
	if historyRounds(12, true) != 0 || historyRounds(0, true) != 0 {
		t.Fatal("an encrypted conversation was asked of the ordinary connection")
	}
	if historyRounds(12, false) == 0 || historyRounds(0, false) == 0 {
		t.Fatal("an ordinary conversation was not asked for more of its history")
	}
	if historyRounds(0, false) >= historyRounds(12, false) {
		t.Fatal("a conversation holding nothing should be asked fewer times, not more")
	}
}
