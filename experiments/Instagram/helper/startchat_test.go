package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"slices"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/instameow"
	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
)

type fakeDirectoryClient struct {
	fakeWriteClient
	threads       map[int64]*instameow.ThreadIGIDs
	createdOnSend map[int64]*instameow.ThreadIGIDs
	threadErr     error
	results       []*slidetypes.SearchResult
	searchErr     error
	searched      []string
	looked        []int64
	grouped       []*slidetypes.CreateGroupRequest
	renamed       []string
	groupErr      error
	titleErr      error
	nilOnGroup    bool
	// The thread a first message makes appears without that message in it (another thread arriving meanwhile).
	notHoldingSent bool
	// It holds, instead, somebody else's message carrying the same offline threading id under another message id.
	othersOfflineID bool
	// It holds the sent message under a message id other than the one the send was answered with.
	sentUnderOtherID bool
	// Instagram answers a send without naming the message.
	unconfirmedSend bool
}

func (c *fakeDirectoryClient) SearchUsers(_ context.Context, query string) (*slidetypes.SearchResponse, error) {
	c.searched = append(c.searched, query)
	if c.searchErr != nil {
		return nil, c.searchErr
	}
	response := &slidetypes.SearchResponse{}
	response.Data.Results = c.results
	return response, nil
}

func (c *fakeDirectoryClient) FetchThreadID(_ context.Context, fbid int64) (*instameow.ThreadIGIDs, error) {
	c.looked = append(c.looked, fbid)
	if c.threadErr != nil {
		return nil, c.threadErr
	}
	if ids, ok := c.threads[fbid]; ok {
		return ids, nil
	}
	return nil, instameow.ErrThreadNotFound
}

func (c *fakeDirectoryClient) CreateGroup(_ context.Context, r *slidetypes.CreateGroupRequest) (*slidetypes.CreateGroupResponse, error) {
	c.grouped = append(c.grouped, r)
	if c.groupErr != nil {
		return nil, c.groupErr
	}
	if c.nilOnGroup {
		return nil, nil
	}
	response := &slidetypes.CreateGroupResponse{}
	response.Data.ID = "made"
	return response, nil
}

func (c *fakeDirectoryClient) EditGroupTitle(_ context.Context, threadID, title string) error {
	c.renamed = append(c.renamed, threadID+"="+title)
	return c.titleErr
}

// Instagram makes the thread as a side effect of the first message, so a conversation listed here does not exist
// until a send has happened and then does, holding that message unless the test says it appeared without it.
func (c *fakeDirectoryClient) SendMessage(ctx context.Context, r *slidetypes.SendTextRequest) (*slidetypes.SendTextResponse, error) {
	response, err := c.fakeWriteClient.SendMessage(ctx, r)
	if err == nil {
		for fbid, ids := range c.createdOnSend {
			c.threads[fbid] = ids
			delete(c.createdOnSend, fbid)
			if thread := c.details[ids.ShortID]; thread != nil && !c.notHoldingSent {
				if thread.SlideMessages == nil {
					thread.SlideMessages = &slidetypes.SlideMessages{}
				}
				message := &slidetypes.Message{
					ID: "sent-1", MessageID: "sent-1", OfflineThreadingID: r.OfflineThreadingID, SenderFBID: 1234, TextBody: r.Text.Value, TimestampMS: at(4000),
				}
				if c.sentUnderOtherID {
					message.ID, message.MessageID = "echo-1", "echo-1"
				}
				if c.othersOfflineID {
					message = &slidetypes.Message{
						ID: "theirs-1", MessageID: "theirs-1", OfflineThreadingID: r.OfflineThreadingID, SenderFBID: fbid, TextBody: "hi", TimestampMS: at(4000),
					}
				}
				thread.SlideMessages.Edges = append(thread.SlideMessages.Edges, slidetypes.Node[*slidetypes.Message]{Node: message})
			}
		}
	}
	if response != nil && c.unconfirmedSend {
		response.Message.MessageID = ""
	}
	return response, err
}

func directoryFixture(t *testing.T) (*readOnlyAdapter, *fakeDirectoryClient) {
	t.Helper()
	a, write := writeFixture(t)
	client := &fakeDirectoryClient{fakeWriteClient: *write, threads: map[int64]*instameow.ThreadIGIDs{
		// The fixture's direct conversation is with 456, and Instagram answers the route lookup with its two ids.
		456: {ShortID: "direct", LongID: "long-direct"},
	}, createdOnSend: map[int64]*instameow.ThreadIGIDs{}}
	a.client = client
	return a, client
}

func startDirect(t *testing.T, a *readOnlyAdapter, params string) (*uiChat, string) {
	t.Helper()
	result, code := a.uiStartDirect(context.Background(), json.RawMessage(params))
	if code != "" {
		return nil, code
	}
	chat, ok := result.(*uiChat)
	if !ok {
		t.Fatalf("expected a conversation, got %T", result)
	}
	return chat, ""
}

func startWithText(t *testing.T, a *readOnlyAdapter, params string) (*uiStartedChat, string) {
	t.Helper()
	result, code := a.uiStartDirectWithText(context.Background(), json.RawMessage(params))
	if code != "" {
		return nil, code
	}
	started, ok := result.(*uiStartedChat)
	if !ok {
		t.Fatalf("expected a started conversation, got %T", result)
	}
	return started, ""
}

func searchUsers(t *testing.T, a *readOnlyAdapter, params string) ([]uiCandidate, string) {
	t.Helper()
	result, code := a.uiSearchUsers(context.Background(), json.RawMessage(params))
	if code != "" {
		return nil, code
	}
	found, ok := result.([]uiCandidate)
	if !ok {
		t.Fatalf("expected candidates, got %T", result)
	}
	return found, ""
}

// The whole point of starting a chat by lookup: what comes back is the conversation Instagram already has, carrying
// Instagram's own two ids, so a send into it addresses the short one exactly as a send into any other conversation
// does. If this ever returned something invented, the send would be addressed to a guess.
func TestStartDirectOpensTheConversationInstagramAlreadyHas(t *testing.T) {
	a, client := directoryFixture(t)

	chat, code := startDirect(t, a, `{"personId":"456"}`)
	if code != "" {
		t.Fatalf("start: %s", code)
	}
	if chat.ID != "direct" {
		t.Fatalf("named the conversation %q", chat.ID)
	}
	if chat.shortID != "short-direct" || chat.longID != "long-direct" {
		t.Fatalf("the conversation did not carry Instagram's own ids: %q / %q", chat.shortID, chat.longID)
	}
	if len(client.looked) != 1 || client.looked[0] != 456 {
		t.Fatalf("looked up %v", client.looked)
	}

	// Opened, therefore writable: the send addresses the short id like every other send.
	if _, code := a.uiSendText(context.Background(), json.RawMessage(`{"chatId":"direct","text":"hello"}`)); code != "" {
		t.Fatalf("send after start: %s", code)
	}
	sent := client.sent[len(client.sent)-1]
	if sent.IGThreadIGID == nil || *sent.IGThreadIGID != "short-direct" {
		t.Fatalf("send used the wrong thread id: %v", sent.IGThreadIGID)
	}
	// A conversation that exists is never addressed by recipient. That field is the one upstream itself is unsure
	// about, and nothing here may start using it by accident.
	if len(sent.RecipientIGIDs) != 0 {
		t.Fatalf("send addressed recipients instead of the thread: %v", sent.RecipientIGIDs)
	}
}

// Instagram has no call that creates an empty conversation, so a pair with no thread yet is refused by name rather
// than conjured. The app can explain a named refusal; it cannot explain a conversation that does not exist.
func TestStartDirectRefusesWhenInstagramHasNoThreadYet(t *testing.T) {
	a, _ := directoryFixture(t)

	if _, code := startDirect(t, a, `{"personId":"99999"}`); code != "chat_not_started" {
		t.Fatalf("expected chat_not_started, got %q", code)
	}
}

// The route lookup is addressed by one person, so the thread it answers with must be that person's direct
// conversation. A group, or somebody else's thread, means the question and the answer disagree about who is being
// written to.
func TestStartDirectRefusesAThreadThatIsNotThatPersons(t *testing.T) {
	a, client := directoryFixture(t)
	client.threads[456] = &instameow.ThreadIGIDs{ShortID: "group", LongID: "long-group"}

	if _, code := startDirect(t, a, `{"personId":"456"}`); code != "invalid_response" {
		t.Fatalf("expected invalid_response for a group, got %q", code)
	}

	client.threads[777] = &instameow.ThreadIGIDs{ShortID: "direct", LongID: "long-direct"}
	if _, code := startDirect(t, a, `{"personId":"777"}`); code != "invalid_response" {
		t.Fatalf("expected invalid_response for somebody else's thread, got %q", code)
	}
}

// Opening a message request from the picker must not quietly promote it to the inbox: accepting a request is a
// separate decision, and the send path refuses a thread that is not filed in the inbox.
func TestStartDirectKeepsAMessageRequestARequest(t *testing.T) {
	a, client := directoryFixture(t)
	client.threads[999] = &instameow.ThreadIGIDs{ShortID: "request", LongID: "long-request"}

	chat, code := startDirect(t, a, `{"personId":"999"}`)
	if code != "" {
		t.Fatalf("start: %s", code)
	}
	// The fixture's detail names no folder and the list never read requests, so its standing is unknown.
	if chat.Folder == "inbox" {
		t.Fatalf("a request was filed as %q", chat.Folder)
	}
	if _, code := a.uiSendText(context.Background(), json.RawMessage(`{"chatId":"request","text":"hi"}`)); code != "chat_not_writable" {
		t.Fatalf("a request accepted a send: %q", code)
	}
	if _, code := a.uiMarkRead(context.Background(), json.RawMessage(`{"chatId":"request","messageId":"r1","at":2000}`)); code != "chat_not_writable" {
		t.Fatalf("a request was marked read: %q", code)
	}
	if _, code := startWithText(t, a, `{"personId":"999","text":"hi"}`); code != "chat_not_writable" {
		t.Fatalf("a first message to a request's sender went through: %q", code)
	}
	if len(client.sent) != 0 || len(client.read) != 0 {
		t.Fatal("a refused request still reached Instagram")
	}
}

func TestStartDirectRefusesThisAccount(t *testing.T) {
	a, _ := directoryFixture(t)

	if _, code := startDirect(t, a, `{"personId":"1234"}`); code != "chat_is_self" {
		t.Fatalf("expected chat_is_self, got %q", code)
	}
	for _, params := range []string{`{}`, `{"personId":""}`, `{"personId":"nonsense"}`, `{"personId":"-5"}`, `{"personId":"0"}`} {
		if _, code := startDirect(t, a, params); code != "invalid_params" {
			t.Fatalf("%s was accepted as %q", params, code)
		}
	}
}

// Search returns the messaging FBID, because that is the number a thread's participants carry and the number the
// route lookup takes. Instagram's own user id is a different number for the same person, and returning it would put
// the two-id confusion right back into the pipe.
func TestSearchReturnsTheMessagingIdAndNeverTheInstagramOne(t *testing.T) {
	a, client := directoryFixture(t)
	client.results = []*slidetypes.SearchResult{
		{InteropMessagingUserFBID: 456, Username: "no_full_name", FullName: "A Person", PK: "9999",
			ProfilePicURL: "https://instagram.example/pic.jpg", IsVerified: true},
	}

	found, code := searchUsers(t, a, `{"query":"person"}`)
	if code != "" {
		t.Fatalf("search: %s", code)
	}
	if len(found) != 1 {
		t.Fatalf("expected one result, got %d", len(found))
	}
	if found[0].ID != "456" {
		t.Fatalf("returned %q rather than the messaging id", found[0].ID)
	}
	if found[0].Name != "A Person" || found[0].Username != "no_full_name" || !found[0].Verified {
		t.Fatalf("the person was not described: %+v", found[0])
	}
	// The app never receives an Instagram address; it receives an id the helper issued and can resolve.
	if found[0].Avatar == "" || found[0].Avatar == "https://instagram.example/pic.jpg" {
		t.Fatalf("the avatar leaked an address: %q", found[0].Avatar)
	}
	encoded, err := json.Marshal(found)
	if err != nil {
		t.Fatal(err)
	}
	for _, leaked := range []string{"9999", "instagram.example"} {
		if bytes.Contains(encoded, []byte(leaked)) {
			t.Fatalf("%q reached the pipe: %s", leaked, encoded)
		}
	}
	if len(client.searched) != 1 || client.searched[0] != "person" {
		t.Fatalf("searched %v", client.searched)
	}
}

// An account with no full name set is common on Instagram, and a nameless row is unusable in a picker.
func TestSearchFallsBackToTheHandleAndDropsWhatCannotBeAddressed(t *testing.T) {
	a, client := directoryFixture(t)
	client.results = []*slidetypes.SearchResult{
		{InteropMessagingUserFBID: 456, Username: "just_a_handle"},
		// This account, which is not somebody to start a conversation with.
		{InteropMessagingUserFBID: 1234, Username: "account", FullName: "The Account"},
		// No messaging id: cannot be addressed, so it is not offered.
		{InteropMessagingUserFBID: 0, Username: "ghost", FullName: "No Id"},
		// Nothing to call it.
		{InteropMessagingUserFBID: 789},
		nil,
	}

	found, code := searchUsers(t, a, `{"query":"anything"}`)
	if code != "" {
		t.Fatalf("search: %s", code)
	}
	if len(found) != 1 {
		t.Fatalf("expected one usable result, got %d: %+v", len(found), found)
	}
	if found[0].Name != "just_a_handle" {
		t.Fatalf("the handle did not stand in for the missing name: %q", found[0].Name)
	}
}

func TestSearchRejectsQueriesItShouldNotForward(t *testing.T) {
	a, client := directoryFixture(t)

	oversize := make([]byte, maxSearchLength+1)
	for i := range oversize {
		oversize[i] = 'a'
	}
	encoded, err := json.Marshal(map[string]string{"query": string(oversize)})
	if err != nil {
		t.Fatal(err)
	}
	// Under three characters is refused here too (security review, 2026-10-11): the pacing rules are not only the application window's.
	for _, params := range []string{`{}`, `{"query":""}`, `{"query":"a"}`, `{"query":"ab"}`, `{"query":"  a  "}`, `{"query":"é"}`, string(encoded)} {
		if _, code := searchUsers(t, a, params); code != "invalid_params" {
			t.Fatalf("%.40s was accepted as %q", params, code)
		}
	}
	if len(client.searched) != 0 {
		t.Fatalf("a rejected query still reached Instagram: %v", client.searched)
	}
}

// The diagnostics run in a mode that cannot change the account, and these two exist only to serve the app's compose
// flow. A mode that merely never asks is not the same as one that would refuse.
func TestSearchAndStartAreRefusedWhenTheModeCannotMutate(t *testing.T) {
	a, client := directoryFixture(t)
	a.canMutate = false

	if _, code := searchUsers(t, a, `{"query":"person"}`); code != "read_only" {
		t.Fatalf("search in read-only mode: %q", code)
	}
	if _, code := startDirect(t, a, `{"personId":"456"}`); code != "read_only" {
		t.Fatalf("start in read-only mode: %q", code)
	}
	if len(client.searched) != 0 || len(client.looked) != 0 {
		t.Fatal("a read-only mode reached Instagram")
	}
}

// A person this account has no conversation with. Instagram only makes the thread when the first message lands, so
// the route lookup finds nothing until the send has happened.
func newcomerFixture(t *testing.T) (*readOnlyAdapter, *fakeDirectoryClient) {
	t.Helper()
	a, client := directoryFixture(t)
	viewer := person(1234, "The Account", "account")
	newcomer := person(777, "A Newcomer", "newcomer")
	client.details["fresh"] = &slidetypes.ThreadInfo{
		ID: "fresh", ThreadFBID: "short-fresh", ThreadID: "long-fresh", ThreadKey: 777,
		ViewerID: "123", Viewer: viewer, Users: []*slidetypes.User{viewer, newcomer},
		LastActivityTimestampMS: at(6000),
	}
	client.createdOnSend[777] = &instameow.ThreadIGIDs{ShortID: "fresh", LongID: "long-fresh"}
	return a, client
}

// The one path that addresses a person instead of a thread. The conversation does not exist beforehand, so the send
// carries the recipient and no thread id, and the conversation is looked up afterwards because Instagram's send
// answer names no thread at all.
func TestFirstMessageToSomebodyNewAddressesThePersonAndThenFindsTheConversation(t *testing.T) {
	a, client := newcomerFixture(t)

	started, code := startWithText(t, a, `{"personId":"777","text":"hello there"}`)
	if code != "" {
		t.Fatalf("start with text: %s", code)
	}
	if len(client.sent) != 1 {
		t.Fatalf("expected exactly one send, got %d", len(client.sent))
	}
	sent := client.sent[0]
	if sent.IGThreadIGID != nil {
		t.Fatalf("a conversation that did not exist was addressed by thread: %v", *sent.IGThreadIGID)
	}
	if len(sent.RecipientIGIDs) != 1 || sent.RecipientIGIDs[0] != "777" {
		t.Fatalf("the recipient was not carried: %v", sent.RecipientIGIDs)
	}
	if sent.Text.Value != "hello there" || sent.OfflineThreadingID == "" {
		t.Fatal("the message or its threading id was not carried")
	}
	if started.MessageID != "sent-1" || started.At == 0 {
		t.Fatalf("the sent message was not reported back: %+v", started)
	}
	if started.Chat == nil || started.Chat.ID != "fresh" {
		t.Fatalf("the new conversation was not found: %+v", started.Chat)
	}
	if started.Chat.shortID != "short-fresh" || started.Chat.longID != "long-fresh" {
		t.Fatalf("the new conversation did not carry Instagram's own ids: %+v", started.Chat)
	}
	// Found, stored and ordinary: the next message goes to the thread, not to the person again.
	if _, code := a.uiSendText(context.Background(), json.RawMessage(`{"chatId":"fresh","text":"and again"}`)); code != "" {
		t.Fatalf("second send: %s", code)
	}
	second := client.sent[len(client.sent)-1]
	if second.IGThreadIGID == nil || *second.IGThreadIGID != "short-fresh" || len(second.RecipientIGIDs) != 0 {
		t.Fatalf("the second message did not address the conversation: %+v", second)
	}
}

// If the conversation turned out to exist after all, nothing about the call is special: it is addressed the ordinary
// way. Believing otherwise would address a live conversation by recipient on the strength of a stale picker.
func TestFirstMessageAddressesTheThreadWhenOneAlreadyExists(t *testing.T) {
	a, client := directoryFixture(t)

	started, code := startWithText(t, a, `{"personId":"456","text":"hello again"}`)
	if code != "" {
		t.Fatalf("start with text: %s", code)
	}
	sent := client.sent[0]
	if sent.IGThreadIGID == nil || *sent.IGThreadIGID != "short-direct" {
		t.Fatalf("an existing conversation was not addressed by thread: %+v", sent)
	}
	if len(sent.RecipientIGIDs) != 0 {
		t.Fatalf("an existing conversation was addressed by recipient: %v", sent.RecipientIGIDs)
	}
	if started.Chat == nil || started.Chat.ID != "direct" {
		t.Fatalf("the conversation was not reported: %+v", started.Chat)
	}
}

// Replying to a message request accepts it, which is a decision nobody has made. A first message from New chat must
// not become the way around that: the conversation is resolved in full and held to the same standing as a reply.
func TestAFirstMessageCannotSlipIntoAMessageRequestOrSpam(t *testing.T) {
	for _, folder := range []struct{ person, thread string }{{"999", "request"}, {"888", "spam"}} {
		a, client := directoryFixture(t)
		id := int64(999)
		if folder.person == "888" {
			id = 888
		}
		client.threads[id] = &instameow.ThreadIGIDs{ShortID: folder.thread, LongID: "long-" + folder.thread}

		if _, code := startWithText(t, a, `{"personId":"`+folder.person+`","text":"let me in"}`); code != "chat_not_writable" {
			t.Fatalf("a first message into %s was accepted as %q", folder.thread, code)
		}
		if len(client.sent) != 0 {
			t.Fatalf("a first message reached %s anyway", folder.thread)
		}
	}
}

// The thread found after a first message is filed in the inbox only when Instagram confirmed the send and the thread
// holds that message. A thread that appeared between the two lookups without it (a request arriving meanwhile, whose
// detail names no folder), one holding only somebody else's message with the same offline threading id, or a send
// answered without a message id, leaves the standing unknown, which refuses a reply.
func TestAFirstMessageFilesOnlyTheThreadThatHoldsIt(t *testing.T) {
	for _, unproven := range []struct {
		name string
		set  func(*fakeDirectoryClient)
	}{
		{"a thread without the message", func(c *fakeDirectoryClient) { c.notHoldingSent = true }},
		{"an unconfirmed send", func(c *fakeDirectoryClient) { c.unconfirmedSend = true }},
		{"another sender's message with the same offline id", func(c *fakeDirectoryClient) { c.othersOfflineID = true }},
	} {
		a, client := newcomerFixture(t)
		unproven.set(client)
		started, code := startWithText(t, a, `{"personId":"777","text":"hello there"}`)
		if code != "" || started.Chat == nil || started.Chat.ID != "fresh" {
			t.Fatalf("%s: the send or the lookup after it failed: %q %+v", unproven.name, code, started)
		}
		if started.Chat.Folder == "inbox" {
			t.Fatalf("%s: the thread was filed in the inbox", unproven.name)
		}
		if _, code := a.uiSendText(context.Background(), json.RawMessage(`{"chatId":"fresh","text":"and again"}`)); code != "chat_not_writable" {
			t.Fatalf("%s: a reply into it was taken: %q", unproven.name, code)
		}
		if len(client.sent) != 1 {
			t.Fatalf("%s: %d sends reached Instagram", unproven.name, len(client.sent))
		}
	}
}

// The thread can carry this account's message under another id than the send's answer named; the threading id the
// send chose, on a message this account sent, still shows the thread is the one the message made.
func TestAFirstMessageFoundByItsThreadingIDFilesTheThread(t *testing.T) {
	a, client := newcomerFixture(t)
	client.sentUnderOtherID = true
	started, code := startWithText(t, a, `{"personId":"777","text":"hello there"}`)
	if code != "" || started.Chat == nil || started.Chat.ID != "fresh" {
		t.Fatalf("the send or the lookup after it failed: %q %+v", code, started)
	}
	if started.Chat.Folder != "inbox" {
		t.Fatalf("the thread holding this account's message was not filed in the inbox: %q", started.Chat.Folder)
	}
}

// A message that was sent is never reported as a failure because the conversation could not be drawn afterwards.
// The alternative invites the one thing this must never do: sending it again.
func TestAMessageThatWentIsReportedEvenWhenTheConversationCannotBeFound(t *testing.T) {
	a, client := newcomerFixture(t)
	// The send succeeds, and the lookup that follows it does not.
	client.createdOnSend = map[int64]*instameow.ThreadIGIDs{}

	started, code := startWithText(t, a, `{"personId":"777","text":"hello there"}`)
	if code != "" {
		t.Fatalf("a sent message was reported as the failure %q", code)
	}
	if started.MessageID != "sent-1" {
		t.Fatalf("the message was not reported: %+v", started)
	}
	if started.Chat != nil {
		t.Fatalf("a conversation was reported that was never found: %+v", started.Chat)
	}
	if len(client.sent) != 1 {
		t.Fatalf("the message was sent %d times", len(client.sent))
	}
}

func TestAFirstMessageIsNeverSentTwiceOrSentAtAllWhenRefused(t *testing.T) {
	a, client := newcomerFixture(t)
	client.sendErr = errors.New("network is unreachable")

	if _, code := startWithText(t, a, `{"personId":"777","text":"hello"}`); code != "connection_failed" {
		t.Fatalf("expected connection_failed, got %q", code)
	}
	if len(client.sent) != 1 {
		t.Fatalf("a failed send was attempted %d times", len(client.sent))
	}

	// Everything refused before the send must not reach Instagram at all.
	a, client = newcomerFixture(t)
	for _, params := range []string{
		`{"personId":"777"}`, `{"personId":"777","text":""}`, `{"text":"hello"}`,
		`{"personId":"nonsense","text":"hello"}`, `{"personId":"0","text":"hello"}`,
	} {
		if _, code := startWithText(t, a, params); code != "invalid_params" {
			t.Fatalf("%s was accepted as %q", params, code)
		}
	}
	if _, code := startWithText(t, a, `{"personId":"1234","text":"hello"}`); code != "chat_is_self" {
		t.Fatalf("writing to this account was accepted as %q", code)
	}
	a.canMutate = false
	if _, code := startWithText(t, a, `{"personId":"777","text":"hello"}`); code != "read_only" {
		t.Fatalf("a read-only mode sent a first message: %q", code)
	}
	if len(client.sent) != 0 {
		t.Fatalf("a refused first message still reached Instagram: %d sends", len(client.sent))
	}
}

// Instagram answering with nothing recognizable is uncertain, not failure: the message may already have arrived.
func TestAnUnrecognizableAnswerToAFirstMessageIsUncertain(t *testing.T) {
	a, client := newcomerFixture(t)
	client.nilOnSend = true

	if _, code := startWithText(t, a, `{"personId":"777","text":"hello"}`); code != "send_uncertain" {
		t.Fatalf("expected send_uncertain, got %q", code)
	}
	if len(client.sent) != 1 {
		t.Fatalf("an uncertain send was attempted %d times", len(client.sent))
	}
}

// A message longer than Instagram takes is refused here rather than truncated or forwarded.
func TestAFirstMessageRespectsTheSameLengthLimitAsEveryOtherSend(t *testing.T) {
	a, client := newcomerFixture(t)
	oversize := make([]byte, maxSendLength+1)
	for i := range oversize {
		oversize[i] = 'a'
	}
	encoded, err := json.Marshal(map[string]string{"personId": "777", "text": string(oversize)})
	if err != nil {
		t.Fatal(err)
	}
	if _, code := startWithText(t, a, string(encoded)); code != "invalid_params" {
		t.Fatalf("an oversized first message was accepted as %q", code)
	}
	if len(client.sent) != 0 {
		t.Fatal("an oversized first message reached Instagram")
	}
}

func createGroup(t *testing.T, a *readOnlyAdapter, params string) (*uiCreatedGroup, string) {
	t.Helper()
	result, code := a.uiCreateGroup(context.Background(), json.RawMessage(params))
	if code != "" {
		return nil, code
	}
	created, ok := result.(*uiCreatedGroup)
	if !ok {
		t.Fatalf("expected a created group, got %T", result)
	}
	return created, ""
}

// A group fixture: Instagram answers the create with a thread id, and that thread reads back as a group.
func groupFixture(t *testing.T) (*readOnlyAdapter, *fakeDirectoryClient) {
	t.Helper()
	a, client := directoryFixture(t)
	viewer := person(1234, "The Account", "account")
	client.details["made"] = &slidetypes.ThreadInfo{
		ID: "made", ThreadFBID: "short-made", ThreadID: "long-made", IsGroup: true, ThreadTitle: "The Group",
		ViewerID: "123", Viewer: viewer,
		Users:                   []*slidetypes.User{viewer, person(456, "", "no_full_name"), person(777, "A Newcomer", "newcomer")},
		LastActivityTimestampMS: at(7000),
	}
	return a, client
}

// Unlike a first message to one person, nothing here is guessed: the field is named for the messaging FBIDs
// outright, and Instagram answers with the new thread's own id rather than leaving it to be looked up.
func TestCreatingAGroupAddressesMessagingIdsAndComesBackAsAConversation(t *testing.T) {
	a, client := groupFixture(t)

	created, code := createGroup(t, a, `{"personIds":["456","777"],"title":"The Group"}`)
	if code != "" {
		t.Fatalf("create group: %s", code)
	}
	if len(client.grouped) != 1 {
		t.Fatalf("expected one create, got %d", len(client.grouped))
	}
	if !slices.Equal(client.grouped[0].RecipientFBIDs, []string{"456", "777"}) {
		t.Fatalf("the members were not carried: %v", client.grouped[0].RecipientFBIDs)
	}
	if client.grouped[0].OfflineThreadingID == "" {
		t.Fatal("no threading id was sent")
	}
	if created.ThreadID != "made" || !created.Named {
		t.Fatalf("the group was not reported: %+v", created)
	}
	if len(client.renamed) != 1 || client.renamed[0] != "made=The Group" {
		t.Fatalf("the name was not applied: %v", client.renamed)
	}
	if created.Chat == nil || created.Chat.ID != "made" || !created.Chat.Group {
		t.Fatalf("the conversation was not read back as a group: %+v", created.Chat)
	}
	if created.Chat.shortID != "short-made" || created.Chat.longID != "long-made" {
		t.Fatalf("the group did not carry Instagram's own ids: %+v", created.Chat)
	}
	// Stored, so a send into it addresses the thread the ordinary way.
	if _, code := a.uiSendText(context.Background(), json.RawMessage(`{"chatId":"made","text":"hello all"}`)); code != "" {
		t.Fatalf("send into the new group: %s", code)
	}
}

// A group with no name asked for is not renamed at all, rather than named with nothing.
func TestAGroupWithNoNameIsNotRenamed(t *testing.T) {
	a, client := groupFixture(t)

	created, code := createGroup(t, a, `{"personIds":["456","777"]}`)
	if code != "" {
		t.Fatalf("create group: %s", code)
	}
	if len(client.renamed) != 0 {
		t.Fatalf("an unnamed group was renamed: %v", client.renamed)
	}
	if created.Named {
		t.Fatal("an unnamed group reported a name")
	}
}

// Instagram names a group in a second call, so it can refuse the name while keeping the group. A group that exists
// is never reported as a failure, and the app is told which of the two happened.
func TestAGroupThatCannotBeNamedIsStillAGroup(t *testing.T) {
	a, client := groupFixture(t)
	client.titleErr = errors.New("name refused")

	created, code := createGroup(t, a, `{"personIds":["456","777"],"title":"The Group"}`)
	if code != "" {
		t.Fatalf("a made group was reported as the failure %q", code)
	}
	if created.ThreadID != "made" || created.Chat == nil {
		t.Fatalf("the group was not reported: %+v", created)
	}
	if created.Named {
		t.Fatal("a refused name was reported as applied")
	}
}

// The group exists the moment Instagram answers, so a conversation that cannot be drawn afterwards is a drawing
// problem. Saying otherwise would invite making a second group.
func TestAGroupThatWasMadeIsReportedEvenWhenItCannotBeRead(t *testing.T) {
	a, client := directoryFixture(t)
	// No thread detail for "made", so reading it back fails.
	_ = client

	created, code := createGroup(t, a, `{"personIds":["456","777"]}`)
	if code != "" {
		t.Fatalf("a made group was reported as the failure %q", code)
	}
	if created.ThreadID != "made" {
		t.Fatalf("the group's id was not reported: %+v", created)
	}
	if created.Chat != nil {
		t.Fatalf("a conversation was reported that was never read: %+v", created.Chat)
	}
}

func TestAGroupIsNeverMadeTwiceOrMadeAtAllWhenRefused(t *testing.T) {
	a, client := groupFixture(t)
	client.groupErr = errors.New("network is unreachable")
	if _, code := createGroup(t, a, `{"personIds":["456","777"]}`); code != "connection_failed" {
		t.Fatalf("expected connection_failed, got %q", code)
	}
	if len(client.grouped) != 1 {
		t.Fatalf("a failed create was attempted %d times", len(client.grouped))
	}

	a, client = groupFixture(t)
	client.nilOnGroup = true
	if _, code := createGroup(t, a, `{"personIds":["456","777"]}`); code != "group_uncertain" {
		t.Fatalf("expected group_uncertain, got %q", code)
	}

	// Everything refused before the create must not reach Instagram at all.
	a, client = groupFixture(t)
	for _, params := range []string{
		`{}`, `{"personIds":[]}`, `{"personIds":["456"]}`, `{"personIds":["456","nonsense"]}`,
		`{"personIds":["456","456"]}`, `{"personIds":["456","0"]}`, `{"personIds":["456","-2"]}`,
	} {
		if _, code := createGroup(t, a, params); code != "invalid_params" {
			t.Fatalf("%s was accepted as %q", params, code)
		}
	}
	// This account is in every group it makes; naming it as a member is a different group than the one asked for.
	if _, code := createGroup(t, a, `{"personIds":["456","1234"]}`); code != "chat_is_self" {
		t.Fatalf("this account was accepted as a member: %q", code)
	}
	a.canMutate = false
	if _, code := createGroup(t, a, `{"personIds":["456","777"]}`); code != "read_only" {
		t.Fatalf("a read-only mode made a group: %q", code)
	}
	if len(client.grouped) != 0 {
		t.Fatalf("a refused create still reached Instagram: %d", len(client.grouped))
	}
}

func TestSearchReportsFailuresByCode(t *testing.T) {
	a, client := directoryFixture(t)
	client.searchErr = errors.New("network is unreachable")

	if _, code := searchUsers(t, a, `{"query":"person"}`); code != "connection_failed" {
		t.Fatalf("expected connection_failed, got %q", code)
	}

	client.searchErr = nil
	client.threadErr = context.Canceled
	if _, code := startDirect(t, a, `{"personId":"456"}`); code != "request_cancelled" {
		t.Fatalf("expected request_cancelled, got %q", code)
	}
}
