package main

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/util/jsontime"
)

type fakeHistoryClient struct {
	fakeReadClient
	initial                    *slidetypes.Mailbox
	pages                      map[string]*slidetypes.MailboxPage
	details                    map[string]*slidetypes.ThreadInfo
	messages                   map[string]*slidetypes.SlideMessages
	mailboxCalls, historyCalls int
	// GetMailbox, the inbox's first page, counted apart from PaginateMailbox.
	firstCalls       int
	requestedFolders map[string]bool
	mailboxErr       error
}

func (c *fakeHistoryClient) GetOwnFBID() int64 { return 1234 }
func (c *fakeHistoryClient) GetMailbox(context.Context) (*slidetypes.MailboxResponse, error) {
	c.firstCalls++
	if c.mailboxErr != nil {
		return nil, c.mailboxErr
	}
	return &slidetypes.MailboxResponse{Mailbox: c.initial}, nil
}
func (c *fakeHistoryClient) PaginateMailbox(_ context.Context, r *slidetypes.PaginateMailboxRequest) (*slidetypes.PaginateMailboxResponse, error) {
	c.mailboxCalls++
	if c.requestedFolders == nil {
		c.requestedFolders = map[string]bool{}
	}
	c.requestedFolders[r.Folder] = true
	if r.ViewerFBID != 1234 {
		panic("wrong viewer identity")
	}
	page := c.pages[r.Folder+":"+r.Cursor]
	if page == nil {
		page = &slidetypes.MailboxPage{}
	}
	return &slidetypes.PaginateMailboxResponse{Mailbox: page}, nil
}
func (c *fakeHistoryClient) GetThread(_ context.Context, r *slidetypes.GetThreadInfoRequest) (*slidetypes.ThreadInfoResponse, error) {
	return &slidetypes.ThreadInfoResponse{ThreadInfo: slidetypes.WrappedThreadInfo{AsIGDirectThread: c.details[r.ThreadFBID]}}, nil
}
func (c *fakeHistoryClient) PaginateMessages(_ context.Context, r *slidetypes.PaginateMessagesRequest) (*slidetypes.PaginateMessagesResponse, error) {
	c.historyCalls++
	if r.BeforeCursor != nil || r.LastN != nil || r.NewerThanMessageID != nil || r.OlderThanMessageID != nil || r.FirstN != 20 {
		panic("incorrect history direction")
	}
	return &slidetypes.PaginateMessagesResponse{ThreadInfo: slidetypes.AsThread[slidetypes.MessagesOnlyThread]{AsIGDirectThread: &slidetypes.MessagesOnlyThread{ID: r.ThreadID, Messages: c.messages[*r.AfterCursor]}}}, nil
}

func threadPage(threads ...*slidetypes.ThreadInfo) slidetypes.Edged[slidetypes.Node[slidetypes.WrappedThreadInfo]] {
	page := slidetypes.Edged[slidetypes.Node[slidetypes.WrappedThreadInfo]]{}
	for _, thread := range threads {
		page.Edges = append(page.Edges, slidetypes.Node[slidetypes.WrappedThreadInfo]{Node: slidetypes.WrappedThreadInfo{AsIGDirectThread: thread}})
	}
	return page
}

func at(ms int64) jsontime.UnixMilliString {
	return jsontime.UnixMilliString(jsontime.UM(time.UnixMilli(ms)))
}

func person(fbid int64, full, username string) *slidetypes.User {
	return &slidetypes.User{ID: "ig-" + username, InteropMessagingUserFBID: fbid, FullName: full, Username: username}
}

func said(id, threadID string, sender int64, text string, ms int64) *slidetypes.Message {
	return &slidetypes.Message{ID: id, MessageID: id, ThreadFBID: threadID, SenderFBID: sender, TextBody: text, TimestampMS: at(ms)}
}

// One inbox conversation, one group, one message request, so folder, title fallback and ordering are all exercised.
func chatFixture() (*readOnlyAdapter, *fakeHistoryClient) {
	a, _, _ := testReadAdapter()
	a.chats = newChatStore()
	a.snapshot.IdentityVerified = true

	viewer := person(1234, "The Account", "account")
	other := person(456, "", "no_full_name")
	direct := &slidetypes.ThreadInfo{ID: "direct", ThreadFBID: "direct", ThreadKey: 456, ViewerID: "123",
		Viewer: viewer, Users: []*slidetypes.User{viewer, other}, LastActivityTimestampMS: at(3000),
		SlideMessages: &slidetypes.SlideMessages{
			Edges: []slidetypes.Node[*slidetypes.Message]{
				{Node: said("d2", "direct", 456, "newer", 3000)},
				{Node: said("d1", "direct", 1234, "older", 1000)},
			},
			PageInfo: slidetypes.PageInfo{HasNextPage: true, EndCursor: "back"},
		},
		// Read as far as the older message only, so the newer one is unread.
		SlideReadReceipts: []slidetypes.ReadReceipt{{ParticipantFBID: 1234, WatermarkTimestampMS: at(1000)}},
	}
	group := &slidetypes.ThreadInfo{ID: "group", ThreadFBID: "group", IsGroup: true, ThreadTitle: "A group",
		ViewerID: "123", Viewer: viewer, Users: []*slidetypes.User{viewer, other}, LastActivityTimestampMS: at(9000),
		SlideMessages:     &slidetypes.SlideMessages{Edges: []slidetypes.Node[*slidetypes.Message]{{Node: said("g1", "group", 456, "hello", 9000)}}},
		SlideReadReceipts: []slidetypes.ReadReceipt{{ParticipantFBID: 1234, WatermarkTimestampMS: at(9000)}},
	}
	// No read receipt for this account at all, which is a different case from being behind the watermark.
	quiet := &slidetypes.ThreadInfo{ID: "quiet", ThreadFBID: "quiet", ViewerID: "123", Viewer: viewer,
		Users: []*slidetypes.User{viewer, other}, LastActivityTimestampMS: at(500), ThreadTitle: "Quiet",
		SlideMessages: &slidetypes.SlideMessages{Edges: []slidetypes.Node[*slidetypes.Message]{{Node: said("q1", "quiet", 456, "hello", 500)}}},
	}
	// A message request, which the app does not draw yet. It stays in the fixture so the helper can be held to not
	// spending round trips reading it.
	request := &slidetypes.ThreadInfo{ID: "request", ThreadFBID: "request", ViewerID: "123", Viewer: viewer,
		Users: []*slidetypes.User{viewer, person(999, "A Stranger", "stranger")}, LastActivityTimestampMS: at(2000),
		SlideMessages: &slidetypes.SlideMessages{Edges: []slidetypes.Node[*slidetypes.Message]{{Node: said("r1", "request", 999, "let me in", 2000)}}},
	}
	spam := &slidetypes.ThreadInfo{ID: "spam", ThreadFBID: "spam", ViewerID: "123", Viewer: viewer,
		Users: []*slidetypes.User{viewer, person(888, "Spammer", "spammer")}, LastActivityTimestampMS: at(100),
		SlideMessages: &slidetypes.SlideMessages{Edges: []slidetypes.Node[*slidetypes.Message]{{Node: said("s1", "spam", 888, "buy this", 100)}}},
	}
	older := &slidetypes.SlideMessages{Edges: []slidetypes.Node[*slidetypes.Message]{{Node: said("d0", "direct", 456, "oldest", 500)}}}

	client := &fakeHistoryClient{
		initial: &slidetypes.Mailbox{UQSeqID: 1, ThreadsByFolder: threadPage(direct, group, quiet)},
		pages: map[string]*slidetypes.MailboxPage{
			"PENDING:": {ThreadsByFolder: threadPage(request)},
			"SPAM:":    {ThreadsByFolder: threadPage(spam)},
		},
		details:  map[string]*slidetypes.ThreadInfo{direct.ID: direct, group.ID: group, request.ID: request, quiet.ID: quiet, spam.ID: spam},
		messages: map[string]*slidetypes.SlideMessages{"back": older},
	}
	a.client = client
	return a, client
}

func chatsOf(t *testing.T, a *readOnlyAdapter, params string) []*uiChat {
	t.Helper()
	result, code := a.uiChats(context.Background(), json.RawMessage(params))
	if code != "" {
		t.Fatalf("ui.chats: %s", code)
	}
	chats, ok := result.([]*uiChat)
	if !ok {
		t.Fatal("ui.chats did not answer with conversations")
	}
	return chats
}

func TestChatsMapIdentityFolderAndOrder(t *testing.T) {
	a, client := chatFixture()
	chats := chatsOf(t, a, `{}`)
	// The inbox and spam, and not the message requests the app does not draw.
	if len(chats) != 4 {
		t.Fatalf("expected the shown folders' conversations, got %d", len(chats))
	}
	// Newest first, which is the order the list is drawn in.
	if chats[0].ID != "group" || chats[1].ID != "direct" || chats[2].ID != "quiet" || chats[3].ID != "spam" {
		t.Fatalf("conversations are not newest first: %s %s %s %s", chats[0].ID, chats[1].ID, chats[2].ID, chats[3].ID)
	}
	if chats[0].Folder != "inbox" || chats[3].Folder != "spam" {
		t.Fatalf("folders not carried: %s %s", chats[0].Folder, chats[3].Folder)
	}
	if !chats[0].Group || chats[1].Group {
		t.Fatal("group and direct conversations are not distinguished")
	}
	// A direct conversation Instagram gives no title to is named after the person, by username when there is no
	// full name; leaving it blank would draw an unusable row.
	if chats[1].Title != "no_full_name" || chats[0].Title != "A group" {
		t.Fatalf("titles wrong: %q %q", chats[1].Title, chats[0].Title)
	}
	// Participants carry the messaging FBID, because that is what a message's sender carries. The Instagram id is a
	// different number, and mixing the two is the identity mistake stage 3 went looking for.
	direct := chats[1]
	if len(direct.Participants) != 2 {
		t.Fatalf("expected both participants, got %d", len(direct.Participants))
	}
	var self, them uiPerson
	for _, p := range direct.Participants {
		if p.Self {
			self = p
		} else {
			them = p
		}
	}
	if self.ID != "1234" || them.ID != "456" {
		t.Fatalf("participant identity wrong: self %q other %q", self.ID, them.ID)
	}
	if self.Username != "account" || them.Username != "no_full_name" {
		t.Fatal("thread usernames did not survive participant mapping")
	}
	if direct.Preview != "newer" {
		t.Fatalf("preview is not the newest message: %q", direct.Preview)
	}
	if client.mailboxCalls == 0 {
		t.Fatal("the request folders were never queried")
	}
}

func TestChatsUnreadFollowsTheViewersWatermark(t *testing.T) {
	a, _ := chatFixture()
	chats := chatsOf(t, a, `{}`)
	byID := map[string]*uiChat{}
	for _, chat := range chats {
		byID[chat.ID] = chat
	}
	// Read as far as the older message, so the newer one is unread.
	if !byID["direct"].Unread {
		t.Fatal("a message past the watermark should be unread")
	}
	// Read to the newest message: nothing is waiting.
	if byID["group"].Unread {
		t.Fatal("a conversation read to its newest message should not be unread")
	}
	// No watermark for this account at all is not evidence of unread messages; unknown stays unavailable.
	if byID["quiet"].Unread {
		t.Fatal("a missing watermark must not be reported as unread")
	}
}

// Reading a folder the app does not draw spent round trips on rows that were then discarded, which is most of what
// made the first conversation list slow.
func TestMessageRequestsAreNotReadUntilThereIsSomewhereToPutThem(t *testing.T) {
	a, client := chatFixture()
	for _, chat := range chatsOf(t, a, `{}`) {
		if chat.Folder == "pending" {
			t.Fatal("a message request reached the app, which does not draw one")
		}
	}
	if client.requestedFolders["PENDING"] {
		t.Fatal("the pending folder was paged for rows nothing shows")
	}
	if !client.requestedFolders["SPAM"] {
		t.Fatal("spam is shown in its own view and must still be read")
	}
}

func TestChatsMarkedUnreadOverridesTheWatermark(t *testing.T) {
	a, client := chatFixture()
	client.details["group"].MarkedAsUnread = true
	client.initial.ThreadsByFolder = threadPage(client.details["direct"], client.details["group"])
	chats := chatsOf(t, a, `{}`)
	for _, chat := range chats {
		if chat.ID == "group" && !chat.Unread {
			t.Fatal("a conversation marked unread stays unread whatever the watermark says")
		}
	}
}

func TestChatsAreCachedUntilStaleAndRefreshIsExplicit(t *testing.T) {
	a, client := chatFixture()
	chatsOf(t, a, `{}`)
	calls := client.mailboxCalls
	chatsOf(t, a, `{}`)
	if client.mailboxCalls != calls {
		t.Fatal("a fresh list was read again; the list is asked for far more often than it changes")
	}
	chatsOf(t, a, `{"refresh":true}`)
	if client.mailboxCalls == calls {
		t.Fatal("an explicit refresh did not reach Instagram")
	}
	a.chats.stale()
	before, first := client.mailboxCalls, client.firstCalls
	chatsOf(t, a, `{}`)
	// Stale after a send: the conversation sent to has risen to the top, so the first page is what is read again.
	if client.firstCalls != first+1 || client.mailboxCalls != before {
		t.Fatalf("a stale list was not read again by its first page alone: first %d→%d, pages %d→%d", first, client.firstCalls, before, client.mailboxCalls)
	}
}

// The poll's two reads. A first-page poll is one request and lays the inbox's first page over what the last whole
// read found; a whole read pages every folder shown and replaces the list, which is what removes a conversation.
func TestPollReadsTheFirstPageAndSweepsOnRequest(t *testing.T) {
	a, client := chatFixture()
	// A first page asked for before any whole read is a whole read: there is nothing yet to lay it over.
	chatsOf(t, a, `{"refresh":true,"pages":"first"}`)
	if client.mailboxCalls == 0 || !a.chats.swept() {
		t.Fatal("a new helper's first read was not a whole one")
	}
	counts := a.requests.counts()
	pages, calls := client.mailboxCalls, client.firstCalls

	// Below the first page, a conversation the first page does not carry stays as it was.
	spamOnly := chatsOf(t, a, `{"refresh":true,"pages":"first"}`)
	if client.mailboxCalls != pages || client.firstCalls != calls+1 {
		t.Fatalf("a first-page poll paged the mailbox: pages %d→%d", pages, client.mailboxCalls)
	}
	after := a.requests.counts()
	if after.Mailbox != counts.Mailbox+1 || after.MailboxPages != counts.MailboxPages {
		t.Fatalf("a first-page poll was not counted as one mailbox request: %+v", after)
	}
	if !hasChat(spamOnly, "spam") {
		t.Fatal("a conversation below the first page was dropped by a first-page poll")
	}

	// Something new rises to the first page, and the first-page poll brings it.
	direct := client.initial.ThreadsByFolder.Edges[0].Node.AsIGDirectThread
	direct.SlideMessages.Edges = append(direct.SlideMessages.Edges, slidetypes.Node[*slidetypes.Message]{Node: said("d3", "direct", 456, "newest", 12000)})
	direct.LastActivityTimestampMS = at(12000)
	fresh := chatsOf(t, a, `{"refresh":true,"pages":"first"}`)
	if fresh[0].ID != "direct" || fresh[0].Last == nil || fresh[0].Last.ID != "d3" {
		t.Fatal("a new message on the first page did not reach the list")
	}

	// A conversation gone from Instagram goes at the next whole read, not before.
	delete(client.pages, "SPAM:")
	if !hasChat(chatsOf(t, a, `{"refresh":true,"pages":"first"}`), "spam") {
		t.Fatal("a first-page poll removed a conversation it could not see")
	}
	if hasChat(chatsOf(t, a, `{"refresh":true,"pages":"all"}`), "spam") {
		t.Fatal("a whole read kept a conversation Instagram no longer lists")
	}
	if client.mailboxCalls == pages {
		t.Fatal("a sweep did not page the mailbox")
	}
	if _, code := a.uiChats(context.Background(), json.RawMessage(`{"refresh":true,"pages":"some"}`)); code != "invalid_params" {
		t.Fatalf("an unknown read was accepted: %q", code)
	}
}

func hasChat(chats []*uiChat, id string) bool {
	for _, chat := range chats {
		if chat.ID == id {
			return true
		}
	}
	return false
}

func TestChatDetailKeepsTheFolderTheListEstablished(t *testing.T) {
	a, _ := chatFixture()
	chatsOf(t, a, `{}`)
	result, code := a.uiChat(context.Background(), json.RawMessage(`{"chatId":"spam"}`))
	if code != "" {
		t.Fatal(code)
	}
	chat := result.(*uiChat)
	// The fixture's detail names no folder. Taking that as the inbox would quietly promote a spam thread, which is
	// exactly the distinction the app needs to keep.
	if chat.Folder != "spam" {
		t.Fatalf("detail lost the folder: %q", chat.Folder)
	}
}

// A message request the list never read (requests are not paged) and whose detail names no folder has no known
// standing. It must not come back as the inbox, which is what allows a reply or a read mark.
func TestChatDetailWithNoKnownFolderIsNotTheInbox(t *testing.T) {
	a, _ := chatFixture()
	chatsOf(t, a, `{}`)
	result, code := a.uiChat(context.Background(), json.RawMessage(`{"chatId":"request"}`))
	if code != "" {
		t.Fatal(code)
	}
	if folder := result.(*uiChat).Folder; folder != "" {
		t.Fatalf("an unknown folder was filed as %q", folder)
	}
}

// A folder the detail names is the newer fact, over what the list held and over no list at all.
func TestChatDetailTakesTheFolderItNames(t *testing.T) {
	a, client := chatFixture()
	chatsOf(t, a, `{}`)
	client.details["request"].SystemFolder = "PENDING"
	client.details["direct"].SystemFolder = "SPAM"
	for id, want := range map[string]string{"request": "pending", "direct": "spam"} {
		result, code := a.uiChat(context.Background(), json.RawMessage(`{"chatId":"`+id+`"}`))
		if code != "" {
			t.Fatal(code)
		}
		if folder := result.(*uiChat).Folder; folder != want {
			t.Fatalf("%s: detail named %q, filed as %q", id, want, folder)
		}
	}
	client.details["spam"].SystemFolder = "INBOX"
	result, _ := a.uiChat(context.Background(), json.RawMessage(`{"chatId":"spam"}`))
	if folder := result.(*uiChat).Folder; folder != "inbox" {
		t.Fatalf("a thread Instagram filed in the inbox was kept as %q", folder)
	}
}

// A spam page that fails keeps the spam rows already held. Dropping them let a later detail read, which names no
// folder, decide the thread's standing on its own.
func TestFailedSpamPageKeepsTheSpamRowsHeld(t *testing.T) {
	a, client := chatFixture()
	chatsOf(t, a, `{}`)
	failing := &failingSpamClient{fakeHistoryClient: client}
	a.client = failing
	chats := chatsOf(t, a, `{"refresh":true}`)
	if !failing.asked {
		t.Fatal("the spam folder was not read again")
	}
	spam, ok := a.chats.get("spam")
	if !ok || spam.Folder != "spam" || !hasChat(chats, "spam") {
		t.Fatal("a failed spam page dropped the spam thread")
	}
	if direct, _ := a.chats.get("direct"); direct == nil || direct.Folder != "inbox" {
		t.Fatal("the inbox was not read")
	}
	// A spam page that answers again replaces the store as before, so a thread no longer in spam goes.
	client.pages["SPAM:"] = &slidetypes.MailboxPage{}
	a.client = client
	chatsOf(t, a, `{"refresh":true}`)
	if _, ok := a.chats.get("spam"); ok {
		t.Fatal("a whole read that succeeded kept a thread no folder listed")
	}
}

type failingSpamClient struct {
	*fakeHistoryClient
	asked bool
}

func (c *failingSpamClient) PaginateMailbox(ctx context.Context, r *slidetypes.PaginateMailboxRequest) (*slidetypes.PaginateMailboxResponse, error) {
	if r.Folder == "SPAM" {
		c.asked = true
		return nil, errors.New("spam folder unavailable")
	}
	return c.fakeHistoryClient.PaginateMailbox(ctx, r)
}

func TestMessagesPageOldestFirstAndBackwards(t *testing.T) {
	a, _ := chatFixture()
	result, code := a.uiMessages(context.Background(), json.RawMessage(`{"chatId":"direct"}`))
	if code != "" {
		t.Fatal(code)
	}
	page := result.(uiMessagePage)
	if len(page.Messages) != 2 {
		t.Fatalf("expected the newest page, got %d", len(page.Messages))
	}
	// Oldest first: the conversation is drawn downwards and the provider does not promise an order.
	if page.Messages[0].ID != "d1" || page.Messages[1].ID != "d2" {
		t.Fatalf("messages are not oldest first: %s %s", page.Messages[0].ID, page.Messages[1].ID)
	}
	if !page.Messages[0].Outgoing || page.Messages[1].Outgoing {
		t.Fatal("outgoing is not decided by the account's own messaging id")
	}
	if !page.HasMore || page.Cursor != "back" {
		t.Fatalf("paging back was not offered: %v %q", page.HasMore, page.Cursor)
	}
	result, code = a.uiMessages(context.Background(), json.RawMessage(`{"chatId":"direct","olderThan":"back"}`))
	if code != "" {
		t.Fatal(code)
	}
	older := result.(uiMessagePage)
	if len(older.Messages) != 1 || older.Messages[0].ID != "d0" {
		t.Fatal("paging back did not return the earlier page")
	}
	if older.HasMore {
		t.Fatal("a page with no next cursor still offered more")
	}
}

// Most Instagram messages are not text. A shared reel arrives with an empty body, and reporting it as an empty text
// message is what left the conversation full of blanks.
func TestMessageKindAndShareDetailAreReported(t *testing.T) {
	for _, test := range []struct{ typeName, kind string }{
		{"SlideMessageText", "text"},
		{"SlideMessageImageContent", "photo"},
		{"SlideMessageRavenImageContent", "photo"},
		{"SlideMessageVideosContent", "video"},
		{"SlideMessageAudiosContent", "voice"},
		{"SlideMessageAnimatedMediaContent", "gif"},
		{"SlideMessageCutoutStickerXMAContent", "sticker"},
		{"SlideMessageXMAContent", "share"},
		{"SlideMessageAdminText", "admin"},
		{"SomethingUpstreamAddedLater", "unknown"},
		{"", ""},
	} {
		if got := messageKind(test.typeName); got != test.kind {
			t.Fatalf("%q mapped to %q, expected %q", test.typeName, got, test.kind)
		}
	}

	message := said("x1", "direct", 456, "", 1000)
	message.Content.TypeName = "SlideMessageXMAContent"
	message.Content.Content = &slidetypes.MessageContentXMA{XMA: &slidetypes.XMAContent{
		TitleText: "A reel", TargetURL: "https://www.instagram.com/reel/abc/",
	}}
	message.IGDSnippet = "Someone sent an attachment."
	store := newMediaStore()
	mapped := mapMessage(message, "direct", 1234, store)
	if mapped.Kind != "share" || mapped.Title != "A reel" || mapped.Link != "https://www.instagram.com/reel/abc/" {
		t.Fatalf("share detail lost: %q %q %q", mapped.Kind, mapped.Title, mapped.Link)
	}
	if mapped.Snippet != "Someone sent an attachment." {
		t.Fatalf("Instagram's own wording was dropped: %q", mapped.Snippet)
	}

	// Content this build does not recognize carries no invented title or destination.
	plain := said("x2", "direct", 456, "words", 1000)
	plain.Content.TypeName = "SlideMessageText"
	other := mapMessage(plain, "direct", 1234, store)
	if other.Title != "" || other.Link != "" {
		t.Fatalf("a text message gained a share: %q %q", other.Title, other.Link)
	}
}

// An admin notice has an empty body and its words in fragments. Reading the body alone left the app nothing to draw,
// and it showed its placeholder "[admin]" in a bubble.
func TestAdminNoticeCarriesItsFragmentWords(t *testing.T) {
	notice := said("a1", "direct", 456, "", 1000)
	notice.Content.TypeName = "SlideMessageAdminText"
	notice.Content.Content = &slidetypes.MessageContentAdminText{TextFragments: []slidetypes.TextFragment{
		{Plaintext: "A Person"}, {Plaintext: " changed the theme. "},
	}}
	mapped := mapMessage(notice, "direct", 1234, newMediaStore())
	if mapped.Kind != "admin" || mapped.Text != "A Person changed the theme." || mapped.ReactionLog {
		t.Fatalf("admin notice mapped to %q %q %v", mapped.Kind, mapped.Text, mapped.ReactionLog)
	}
	// A reaction's notice says so, for the app to leave out: the reaction is already on its message.
	notice.Content.Content = &slidetypes.MessageContentAdminText{IsReactionActionLog: true,
		TextFragments: []slidetypes.TextFragment{{Plaintext: "Reacted to a message"}}}
	if !mapMessage(notice, "direct", 1234, newMediaStore()).ReactionLog {
		t.Fatal("a reaction's notice was not marked")
	}
	// A body, where there is one, is not replaced.
	notice.TextBody = "words"
	if got := mapMessage(notice, "direct", 1234, newMediaStore()).Text; got != "words" {
		t.Fatalf("the body was replaced by fragments: %q", got)
	}
	// Ordinary text carries no mark.
	plain := said("a2", "direct", 456, "hello", 1000)
	plain.Content.TypeName = "SlideMessageText"
	if mapMessage(plain, "direct", 1234, newMediaStore()).ReactionLog {
		t.Fatal("a text message was marked as a reaction's notice")
	}
}

// The app is given ids, never Instagram addresses, and can only ask for one this helper handed out. That is what
// stops a url reaching the app and stops the app naming an address of its own for the helper to fetch.
func TestMediaIsAddressedByIdTheHelperIssued(t *testing.T) {
	store := newMediaStore()
	photo := &slidetypes.MessageContentImage{Attachments: []*slidetypes.Attachment{{
		AttachmentFBID: "fbid-1", AttachmentCDNURL: "https://cdn.example/full.jpg",
		PreviewCDNURL: "https://cdn.example/small.jpg", PreviewWidth: 640, PreviewHeight: 480,
	}}}
	items := mapMedia(photo, "msg-1", store)
	if len(items) != 1 {
		t.Fatalf("expected one picture, got %d", len(items))
	}
	// An attachment keeps the id Instagram gave it, so the same picture has the same name in the next session and the
	// app's cache still recognizes it.
	if items[0].ID != "fbid-1" || items[0].Preview == "" || items[0].Preview == items[0].ID {
		t.Fatalf("attachment identity wrong: %q preview %q", items[0].ID, items[0].Preview)
	}
	if items[0].Width != 640 || items[0].Height != 480 || items[0].Video {
		t.Fatal("a photo's shape was not carried")
	}
	// No address may appear in anything handed to the app.
	for _, id := range []string{items[0].ID, items[0].Preview} {
		if strings.Contains(id, "http") || strings.Contains(id, "example") {
			t.Fatalf("an address reached the app as an id: %q", id)
		}
	}
	if url, ok := store.address("fbid-1"); !ok || url != "https://cdn.example/full.jpg" {
		t.Fatal("the helper did not keep the address it issued an id for")
	}
	if _, ok := store.address("an-id-never-issued"); ok {
		t.Fatal("an id the helper never handed out resolved to something")
	}

	// A shared reel has a preview image but no attachment id of its own.
	reel := &slidetypes.MessageContentXMA{XMA: &slidetypes.XMAContent{
		XMAPreviewImage: &slidetypes.XMAPreviewImage{URL: "https://cdn.example/reel.jpg", Width: 320, Height: 320},
	}}
	// A share is a still, not a clip: calling it a video sent it down the app.s video path, which draws an empty
	// play button when there is no frame to put behind it.
	if got := mapMedia(reel, "msg-2", store); len(got) != 1 || got[0].ID == "" || got[0].Video || !got[0].Play {
		t.Fatal("a shared reel is not a still that stands for something that plays")
	}
	// Nothing addressable is not a picture that failed to load; it is not a picture.
	if got := mapMedia(&slidetypes.MessageContentImage{Attachments: []*slidetypes.Attachment{{AttachmentFBID: "no-url"}}}, "msg-3", store); got != nil {
		t.Fatal("a picture with no address was reported as one")
	}
	if got := mapMedia(&slidetypes.MessageContentText{}, "msg-4", store); got != nil {
		t.Fatal("a text message carried media")
	}
}

func TestMediaReadRefusesAnIdItNeverIssued(t *testing.T) {
	a, _ := chatFixture()
	a.media = newMediaStore()
	for _, params := range []string{`{"id":"never-issued","offset":0}`, `{"id":"","offset":0}`, `{"id":"x","offset":-1}`} {
		if _, code := a.mediaRead(context.Background(), json.RawMessage(params)); code == "" {
			t.Fatalf("media read accepted %s", params)
		}
	}
	a.snapshot.IdentityVerified = false
	if _, code := a.mediaRead(context.Background(), json.RawMessage(`{"id":"x","offset":0}`)); code != "login_required" {
		t.Fatalf("expected login_required, got %q", code)
	}
}

func TestConversationReadsRefuseBeforeIdentityIsVerified(t *testing.T) {
	a, _ := chatFixture()
	a.snapshot.IdentityVerified = false
	for _, call := range []func() (any, string){
		func() (any, string) { return a.uiChats(context.Background(), json.RawMessage(`{}`)) },
		func() (any, string) { return a.uiChat(context.Background(), json.RawMessage(`{"chatId":"direct"}`)) },
		func() (any, string) {
			return a.uiMessages(context.Background(), json.RawMessage(`{"chatId":"direct"}`))
		},
	} {
		if _, code := call(); code != "login_required" {
			t.Fatalf("expected login_required, got %q", code)
		}
	}
}

func TestConversationReadsRejectMalformedParameters(t *testing.T) {
	a, _ := chatFixture()
	cases := []struct {
		call   func(json.RawMessage) (any, string)
		params string
	}{
		{func(p json.RawMessage) (any, string) { return a.uiChats(context.Background(), p) }, `{"count":-1}`},
		{func(p json.RawMessage) (any, string) { return a.uiChats(context.Background(), p) }, `{"count":1001}`},
		{func(p json.RawMessage) (any, string) { return a.uiChats(context.Background(), p) }, `{"count":"many"}`},
		{func(p json.RawMessage) (any, string) { return a.uiChat(context.Background(), p) }, `{}`},
		{func(p json.RawMessage) (any, string) { return a.uiMessages(context.Background(), p) }, `{}`},
		{func(p json.RawMessage) (any, string) { return a.uiMessages(context.Background(), p) }, `{"chatId":"direct","count":101}`},
	}
	for _, test := range cases {
		if _, code := test.call(json.RawMessage(test.params)); code != "invalid_params" {
			t.Fatalf("expected invalid_params for %s, got %q", test.params, code)
		}
	}
}

func TestOfflineModeStillRefusesConversationReads(t *testing.T) {
	// The default helper mode constructs no client and must not grow a content path by having one added beside it.
	a := &adapter{out: &output{}}
	for _, method := range []string{"ui.chats", "ui.chat", "ui.messages"} {
		if _, code := a.call(context.Background(), request{ID: 1, Method: method}); code != "unknown_method" {
			t.Fatalf("offline mode answered %s with %q", method, code)
		}
	}
}

// The mailbox and history scans are gone from both signed-in modes. A method this helper does not have is refused
// before the client is touched, so nothing pages the live account's mailbox for a name it no longer answers.
func TestRemovedScansAreUnknownInBothModes(t *testing.T) {
	for _, canMutate := range []bool{true, false} {
		a, client := chatFixture()
		a.canMutate = canMutate
		for _, method := range []string{"probe.scan", "probe.observe"} {
			if _, code := a.call(context.Background(), request{ID: 1, Method: method}); code != "unknown_method" {
				t.Fatalf("%s answered %q (canMutate %v)", method, code, canMutate)
			}
		}
		if client.firstCalls != 0 || client.mailboxCalls != 0 || client.historyCalls != 0 {
			t.Fatal("a refused method reached the client")
		}
	}
}

// Everybody's read position rides on the thread the poll already reads. The app decides from these who has read which
// message, so they must name the other people only, once each, and never this account.
func TestChatsCarryEveryOtherPersonsReadPosition(t *testing.T) {
	a, client := chatFixture()
	group := client.details["group"]
	group.Users = append(group.Users, person(789, "Third", "third"))
	group.SlideReadReceipts = []slidetypes.ReadReceipt{
		{ParticipantFBID: 1234, WatermarkTimestampMS: at(9000)},
		{ParticipantFBID: 789, WatermarkTimestampMS: at(4000)},
		// Instagram repeating a person keeps the furthest position, not whichever came last.
		{ParticipantFBID: 456, WatermarkTimestampMS: at(8000)},
		{ParticipantFBID: 456, WatermarkTimestampMS: at(2000)},
		// No position and no person are not readers.
		{ParticipantFBID: 555},
		{ParticipantFBID: 0, WatermarkTimestampMS: at(9000)},
	}
	client.initial.ThreadsByFolder = threadPage(client.details["direct"], group)
	byID := map[string]*uiChat{}
	for _, chat := range chatsOf(t, a, `{}`) {
		byID[chat.ID] = chat
	}
	reads := byID["group"].Reads
	if len(reads) != 2 || reads[0] != (uiRead{Person: "456", At: 8000}) || reads[1] != (uiRead{Person: "789", At: 4000}) {
		t.Fatalf("group read positions wrong: %+v", reads)
	}
	// The direct fixture carries only this account's own watermark, which decides Unread and is nobody's reader.
	if len(byID["direct"].Reads) != 0 {
		t.Fatalf("this account's own watermark was reported as a reader: %+v", byID["direct"].Reads)
	}
	if !byID["direct"].Unread {
		t.Fatal("reporting read positions changed how Unread is decided")
	}
	// Nothing on the pipe for a conversation nobody else has read.
	raw, _ := json.Marshal(byID["direct"])
	if strings.Contains(string(raw), `"reads"`) {
		t.Fatalf("an empty set of positions was sent: %s", raw)
	}
}

// The newest page is read with its whole thread, so the positions that came with it are the freshest the app has.
// An older page arrives alone and has none of its own to give.
func TestNewestPageCarriesReadPositionsAndOlderPagesDoNot(t *testing.T) {
	a, client := chatFixture()
	client.details["direct"].SlideReadReceipts = append(client.details["direct"].SlideReadReceipts,
		slidetypes.ReadReceipt{ParticipantFBID: 456, WatermarkTimestampMS: at(1000)})
	result, code := a.uiMessages(context.Background(), json.RawMessage(`{"chatId":"direct"}`))
	if code != "" {
		t.Fatal(code)
	}
	newest := result.(uiMessagePage)
	if len(newest.Reads) != 1 || newest.Reads[0] != (uiRead{Person: "456", At: 1000}) {
		t.Fatalf("the newest page lost its read positions: %+v", newest.Reads)
	}
	calls := client.mailboxCalls
	result, code = a.uiMessages(context.Background(), json.RawMessage(`{"chatId":"direct","olderThan":"back"}`))
	if code != "" {
		t.Fatal(code)
	}
	if older := result.(uiMessagePage); len(older.Reads) != 0 {
		t.Fatalf("an older page invented read positions: %+v", older.Reads)
	}
	// Positions come from answers already being read; nothing extra is asked of Instagram for them.
	if client.mailboxCalls != calls || client.historyCalls != 1 {
		t.Fatalf("reading positions cost extra requests: mailbox %d history %d", client.mailboxCalls-calls, client.historyCalls)
	}
}

// Somebody in a group with no messaging id can never be matched to a position, so the app has to know they exist or
// it would call the group read before they had. This account's own record is not one of them however it is listed.
func TestPeopleWithoutAMessagingIdAreCounted(t *testing.T) {
	a, client := chatFixture()
	group := client.details["group"]
	nameless := &slidetypes.User{ID: "ig-nameless", Username: "nameless"}
	viewerByIGID := &slidetypes.User{ID: group.ViewerID, Username: "account"}
	viewerByPK := &slidetypes.User{ID: "ig-other-form", PK: "pk-viewer", Username: "account"}
	group.Viewer = &slidetypes.User{ID: "ig-account", PK: "pk-viewer", InteropMessagingUserFBID: 1234}
	group.Users = []*slidetypes.User{person(456, "", "no_full_name"), nameless, viewerByIGID, viewerByPK, nil}
	client.initial.ThreadsByFolder = threadPage(client.details["direct"], group)
	for _, chat := range chatsOf(t, a, `{}`) {
		switch chat.ID {
		case "group":
			if chat.Unnamed != 1 || len(chat.Participants) != 1 {
				t.Fatalf("expected one named and one unnamed person, got %d and %d", len(chat.Participants), chat.Unnamed)
			}
		case "direct":
			if chat.Unnamed != 0 {
				t.Fatalf("a conversation where everybody has an id reported %d unnamed", chat.Unnamed)
			}
		}
	}
}

// The list already carries each conversation's newest message, which is how the app can tell something arrived without
// reading the conversation. Not its pictures: the list registering a message's address under the attachment id the
// conversation uses would replace the address the conversation read supplied.
func TestListCarriesTheNewestMessageWithoutItsPictures(t *testing.T) {
	a, client := chatFixture()
	a.media = newMediaStore()
	photo := said("d3", "direct", 456, "", 4000)
	photo.Content.TypeName = "SlideMessageImageContent"
	photo.Content.Content = &slidetypes.MessageContentImage{Attachments: []*slidetypes.Attachment{{
		AttachmentFBID: "fbid-list", AttachmentCDNURL: "https://cdn.example/list.jpg",
	}}}
	direct := client.details["direct"]
	direct.SlideMessages.Edges = append(direct.SlideMessages.Edges, slidetypes.Node[*slidetypes.Message]{Node: photo})
	byID := map[string]*uiChat{}
	for _, chat := range chatsOf(t, a, `{}`) {
		byID[chat.ID] = chat
	}
	last := byID["direct"].Last
	if last == nil || last.ID != "d3" || last.ChatID != "direct" || last.Sender != "456" || last.Outgoing || last.At != 4000 || last.Kind != "photo" {
		t.Fatalf("the newest message was not carried: %+v", last)
	}
	if last.Media != nil || last.Title != "" || last.Link != "" {
		t.Fatal("the list's newest message described what it shows")
	}
	if _, ok := a.media.address("fbid-list"); ok {
		t.Fatal("the list registered a message's picture")
	}
	if group := byID["group"].Last; group == nil || group.ID != "g1" || group.Text != "hello" {
		t.Fatalf("a text message was not carried whole: %+v", group)
	}
	// The conversation, read in full, still names the picture.
	if mapped := mapMessage(photo, "direct", 1234, a.media); len(mapped.Media) != 1 {
		t.Fatal("the conversation lost the picture the list left out")
	}
}

// Only the app's poll asks for a refresh by name, and it has to hear that Instagram refused: a checkpoint answered with
// the last list looked like a quiet inbox and the poll asked again every twenty seconds. Any other read still falls
// back to the list it has.
func TestARefreshThatFailsSaysSoAndAnOrdinaryReadKeepsTheList(t *testing.T) {
	a, client := chatFixture()
	chatsOf(t, a, `{}`)
	client.mailboxErr = httpclient.ErrCheckpointRequired
	if _, code := a.uiChats(context.Background(), json.RawMessage(`{"refresh":true}`)); code != "checkpoint_required" {
		t.Fatalf("a refused refresh answered %q", code)
	}
	a.chats.stale()
	if chats := chatsOf(t, a, `{}`); len(chats) == 0 {
		t.Fatal("an ordinary read lost the list it had")
	}
	if counts := a.requests.counts(); counts.Mailbox != 3 || counts.Failures != 2 {
		t.Fatalf("the refused reads were not counted: %+v", counts)
	}
}

// The poll stops on a refusal without telling the helper, so the refusal itself has to stop the realtime connection
// reopening: a checkpointed account was otherwise redialled after every ordinary drop. A read that merely failed does not.
func TestARefusedListReadStopsTheRealtimeReopening(t *testing.T) {
	a, client := chatFixture()
	chatsOf(t, a, `{}`)
	a.snapshot.State = "connection_failed"
	client.mailboxErr = errors.New("synthetic-private-error")
	if _, code := a.uiChats(context.Background(), json.RawMessage(`{"refresh":true}`)); code != "connection_failed" {
		t.Fatalf("a failed refresh answered %q", code)
	}
	if !a.mayReopen(a.generation) {
		t.Fatal("a read that failed stopped the reopening")
	}
	client.mailboxErr = httpclient.ErrCheckpointRequired
	if _, code := a.uiChats(context.Background(), json.RawMessage(`{"refresh":true}`)); code != "checkpoint_required" {
		t.Fatalf("a refused refresh answered %q", code)
	}
	if a.mayReopen(a.generation) {
		t.Fatal("the realtime connection would reopen a session Instagram refused")
	}
}
