package main

import (
	"encoding/json"
	"slices"
	"sync"
	"testing"
	"time"

	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"google.golang.org/protobuf/proto"
)

// The conversation cache's clock, moved by hand.
type handClock struct{ at time.Time }

func (c *handClock) now() time.Time           { return c.at }
func (c *handClock) advance(by time.Duration) { c.at = c.at.Add(by) }

// A phone that answers through the library's own path for an answer (HandleRPCMsg): a conversation lookup with what it
// holds for that conversation, a list with everything it holds, and a send or a reaction as accepted. Nothing else is
// answered.
type answeringPhone struct {
	mu    sync.Mutex
	chats map[string]*gmproto.Conversation
}

func (p *answeringPhone) describes(c *gmproto.Conversation) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.chats == nil {
		p.chats = map[string]*gmproto.Conversation{}
	}
	p.chats[c.GetConversationID()] = c
}

func (p *answeringPhone) answer(t *testing.T, client *libgm.Client) func(relayPost) {
	return func(post relayPost) {
		p.mu.Lock()
		defer p.mu.Unlock()
		var answer proto.Message
		switch post.action {
		case gmproto.ActionType_GET_CONVERSATION:
			var asked gmproto.GetConversationRequest
			post.open(t, client, &asked)
			answer = &gmproto.GetConversationResponse{Conversation: p.chats[asked.GetConversationID()]}
		case gmproto.ActionType_LIST_CONVERSATIONS:
			list := &gmproto.ListConversationsResponse{}
			for _, c := range p.chats {
				list.Conversations = append(list.Conversations, c)
			}
			answer = list
		case gmproto.ActionType_SEND_MESSAGE:
			answer = &gmproto.SendMessageResponse{Status: gmproto.SendMessageResponse_SUCCESS}
		case gmproto.ActionType_SEND_REACTION:
			answer = &gmproto.SendReactionResponse{Success: true}
		default:
			return
		}
		plain, err := proto.Marshal(answer)
		if err != nil {
			t.Error(err)
			return
		}
		sealed, err := client.AuthData.RequestCrypto.Encrypt(plain)
		if err != nil {
			t.Error(err)
			return
		}
		data, err := proto.Marshal(&gmproto.RPCMessageData{SessionID: post.requestID, Action: post.action, EncryptedData: sealed})
		if err != nil {
			t.Error(err)
			return
		}
		client.HandleRPCMsg(&gmproto.IncomingRPCMessage{ResponseID: "answer-" + post.requestID, BugleRoute: gmproto.BugleRoute_DataEvent, MessageData: data})
	}
}

// The request as the phone reads it.
func (p relayPost) open(t *testing.T, client *libgm.Client, into proto.Message) {
	plain, err := client.AuthData.RequestCrypto.Decrypt(p.sealed)
	if err != nil || proto.Unmarshal(plain, into) != nil {
		t.Errorf("a %v request could not be read", p.action)
	}
}

func actionsOf(posts []relayPost) []gmproto.ActionType {
	actions := []gmproto.ActionType{}
	for _, p := range posts {
		actions = append(actions, p.action)
	}
	return actions
}

func testSIM(id string, number int32) *gmproto.SIMCard {
	return &gmproto.SIMCard{SIMParticipant: &gmproto.SIMParticipant{ID: id}, SIMData: &gmproto.SIMData{SIMPayload: &gmproto.SIMPayload{SIMNumber: number}}}
}

func rcsChat(id, sim string) *gmproto.Conversation {
	return &gmproto.Conversation{ConversationID: id, DefaultOutgoingID: sim, Type: gmproto.ConversationType_RCS, SendMode: gmproto.ConversationSendMode_SEND_MODE_AUTO}
}

// A paired helper on a phone with two SIMs, and the cache on a clock the test moves.
func sendingAdapter(t *testing.T, answering bool) (*adapter, *recordingRelay, *answeringPhone, *handClock) {
	relay := &recordingRelay{}
	a := relayAdapter(relay)
	clock := &handClock{at: time.Date(2026, 10, 14, 12, 0, 0, 0, time.UTC)}
	a.conversations.now = clock.now
	a.sims = map[string]*gmproto.SIMCard{"sim-a": testSIM("sim-a", 1), "sim-b": testSIM("sim-b", 2)}
	phone := &answeringPhone{}
	if answering {
		relay.answer = phone.answer(t, a.client)
	}
	return a, relay, phone, clock
}

func sendText(t *testing.T, a *adapter, method, chat string) (any, string) {
	params, _ := json.Marshal(map[string]string{"chatId": chat, "text": "hello"})
	return a.sendCall(shortCall(t), request{Method: method, Params: params})
}

// The requests a send posted, which must be these actions, and the SIM its message went from.
func expectSent(t *testing.T, a *adapter, relay *recordingRelay, step string, sim string, number int32, actions ...gmproto.ActionType) {
	t.Helper()
	posts := relay.takePosts()
	if got := actionsOf(posts); !slices.Equal(got, actions) {
		t.Fatalf("%s asked the phone for %v", step, got)
	}
	var sent gmproto.SendMessageRequest
	posts[len(posts)-1].open(t, a.client, &sent)
	if sent.GetConversationID() != "chat" || sent.GetMessagePayload().GetParticipantID() != sim || sent.GetSIMPayload().GetSIMNumber() != number {
		t.Fatalf("%s went from %q, SIM %d", step, sent.GetMessagePayload().GetParticipantID(), sent.GetSIMPayload().GetSIMNumber())
	}
}

func expectAnswer(t *testing.T, step string, result any, code, outcome, conversation string) {
	t.Helper()
	answer, _ := result.(map[string]any)
	if code != "" || answer["outcome"] != outcome || answer["conversation"] != conversation {
		t.Fatalf("%s answered %v %q", step, result, code)
	}
}

// The owner's minute (2026-10-14): a send checks the phone's description of the conversation from the last minute
// rather than asking the phone first, and asks it again once the description is a minute old.
func TestSendTrustsTheConversationForAMinute(t *testing.T) {
	a, relay, phone, clock := sendingAdapter(t, true)
	phone.describes(rcsChat("chat", "sim-a"))
	result, code := sendText(t, a, "ui.sendText", "chat")
	expectSent(t, a, relay, "a send with nothing held", "sim-a", 1, gmproto.ActionType_GET_CONVERSATION, gmproto.ActionType_SEND_MESSAGE)
	expectAnswer(t, "a send with nothing held", result, code, "pending", "fetched")

	// The phone moves the conversation to its other SIM without saying so. Within the minute the send goes from the SIM
	// the phone last described, with nothing asked first.
	phone.describes(rcsChat("chat", "sim-b"))
	clock.advance(59 * time.Second)
	result, code = sendText(t, a, "ui.sendText", "chat")
	expectSent(t, a, relay, "a send 59 s after the lookup", "sim-a", 1, gmproto.ActionType_SEND_MESSAGE)
	expectAnswer(t, "a send 59 s after the lookup", result, code, "pending", "cached")

	// A minute on, the phone is asked again, and its answer is what the send goes from and what is kept.
	clock.advance(time.Second)
	result, code = sendText(t, a, "ui.sendText", "chat")
	expectSent(t, a, relay, "a send a minute after the lookup", "sim-b", 2, gmproto.ActionType_GET_CONVERSATION, gmproto.ActionType_SEND_MESSAGE)
	expectAnswer(t, "a send a minute after the lookup", result, code, "pending", "fetched")
	clock.advance(30 * time.Second)
	result, code = sendText(t, a, "ui.sendText", "chat")
	expectSent(t, a, relay, "a send after the second lookup", "sim-b", 2, gmproto.ActionType_SEND_MESSAGE)
	expectAnswer(t, "a send after the second lookup", result, code, "pending", "cached")
}

// A send the app checked, queued behind a slow upload while its conversation left the list (audit, 2026-11-01), goes nowhere, and is
// said to be certainly not sent. The word has to pass the queue to arrive in time.
func TestHeldConversationTakesNoSend(t *testing.T) {
	a, relay, phone, _ := sendingAdapter(t, true)
	phone.describes(rcsChat("chat", "sim-a"))
	hold := func(held bool) {
		params, _ := json.Marshal(map[string]any{"chatId": "chat", "held": held})
		if _, code := a.serveCall(shortCall(t), request{Method: "ui.holdSends", Params: params}); code != "" {
			t.Fatalf("holding sends answered %q", code)
		}
	}
	if mutationMethod("ui.holdSends") {
		t.Fatal("the word would wait behind the very sends it stops")
	}
	hold(true)
	result, code := sendText(t, a, "ui.sendText", "chat")
	if answer, _ := result.(map[string]any); code != "" || answer["outcome"] != "rejected" {
		t.Fatalf("a held conversation's send answered %v %q", result, code)
	}
	if slices.Contains(relay.take(), gmproto.ActionType_SEND_MESSAGE) {
		t.Fatal("a held conversation's send reached the phone")
	}
	hold(false)
	result, code = sendText(t, a, "ui.sendText", "chat")
	expectAnswer(t, "a send once let back in", result, code, "pending", "cached")
}

// Without a description from the last minute the send waits for the phone's answer, and a phone that does not answer
// ends it as before: nothing sent, the same code.
func TestSendWithoutARecentDescriptionAsksThePhoneFirst(t *testing.T) {
	a, relay, _, clock := sendingAdapter(t, false)
	if _, code := sendText(t, a, "ui.sendText", "chat"); code != "phone_unavailable" || !slices.Equal(relay.take(), []gmproto.ActionType{gmproto.ActionType_GET_CONVERSATION}) {
		t.Fatalf("a send with nothing held answered %q", code)
	}
	a.handleEvent(rcsChat("chat", "sim-a"))
	clock.advance(conversationTrust)
	if _, code := sendText(t, a, "ui.sendText", "chat"); code != "phone_unavailable" || !slices.Equal(relay.take(), []gmproto.ActionType{gmproto.ActionType_GET_CONVERSATION}) {
		t.Fatalf("a send a minute after the description answered %q", code)
	}
}

// The held description is checked exactly as the phone's answer was: read-only and a SIM the phone has not described
// are refused without anything posted, and the send check answers from it.
func TestSendChecksTheHeldConversationAsItWouldTheAnswer(t *testing.T) {
	a, relay, _, _ := sendingAdapter(t, true)
	readOnly := rcsChat("chat", "sim-a")
	readOnly.ReadOnly = true
	a.handleEvent(readOnly)
	result, code := sendText(t, a, "ui.sendText", "chat")
	if got := relay.take(); len(got) != 0 {
		t.Fatalf("a read-only conversation asked the phone for %v", got)
	}
	expectAnswer(t, "a read-only conversation", result, code, "rejected", "cached")
	if result.(map[string]any)["reason"] != "This conversation is read-only." {
		t.Fatalf("a read-only conversation was refused as %v", result)
	}
	a.handleEvent(rcsChat("chat", "sim-unknown"))
	result, code = sendText(t, a, "ui.sendText", "chat")
	if got := relay.take(); len(got) != 0 {
		t.Fatalf("an unknown SIM asked the phone for %v", got)
	}
	expectAnswer(t, "an unknown SIM", result, code, "rejected", "cached")
	a.handleEvent(rcsChat("chat", "sim-a"))
	result, code = sendText(t, a, "ui.sendPermission", "chat")
	if got := relay.take(); len(got) != 0 || code != "" || result != (sendPermission{true, "", 65536}) {
		t.Fatalf("the send check asked the phone for %v and answered %v %q", got, result, code)
	}
}

// The event the phone sends when a conversation changes, and a list it answers, replace the held description and start
// its minute again.
func TestConversationEventsAndListsRefreshTheHeldDescription(t *testing.T) {
	a, relay, phone, clock := sendingAdapter(t, true)
	a.handleEvent(rcsChat("chat", "sim-a"))
	clock.advance(50 * time.Second)
	a.handleEvent(rcsChat("chat", "sim-b"))
	clock.advance(50 * time.Second)
	result, code := sendText(t, a, "ui.sendText", "chat")
	expectSent(t, a, relay, "a send 50 s after an event", "sim-b", 2, gmproto.ActionType_SEND_MESSAGE)
	expectAnswer(t, "a send 50 s after an event", result, code, "pending", "cached")

	phone.describes(rcsChat("chat", "sim-a"))
	clock.advance(50 * time.Second)
	if _, err := a.listConversations(shortCall(t), 25, gmproto.ListConversationsRequest_INBOX); err != nil {
		t.Fatal(err)
	}
	if got := relay.take(); !slices.Equal(got, []gmproto.ActionType{gmproto.ActionType_LIST_CONVERSATIONS}) {
		t.Fatalf("a list asked the phone for %v", got)
	}
	clock.advance(50 * time.Second)
	result, code = sendText(t, a, "ui.sendText", "chat")
	expectSent(t, a, relay, "a send 50 s after a list", "sim-a", 1, gmproto.ActionType_SEND_MESSAGE)
	expectAnswer(t, "a send 50 s after a list", result, code, "pending", "cached")
}

// A reaction trusts the description for the same minute; the message's options still ask the phone each time, and their
// answer is what a reaction then trusts.
func TestReactionTrustsTheConversationForAMinute(t *testing.T) {
	a, relay, phone, clock := sendingAdapter(t, true)
	incoming := &gmproto.MessageStatus{Status: gmproto.MessageStatusType_INCOMING_COMPLETE}
	a.viewMessage(&gmproto.Message{ConversationID: "chat", MessageID: "message", MessageStatus: incoming})
	a.viewMessage(&gmproto.Message{ConversationID: "other", MessageID: "other-message", MessageStatus: incoming})
	call := func(method, chat, message string) (map[string]string, string, []relayPost) {
		params, _ := json.Marshal(map[string]any{"chatId": chat, "messageId": message, "key": "👍"})
		result, code := a.actionCall(shortCall(t), request{Method: method, Params: params})
		answer, _ := result.(map[string]string)
		return answer, code, relay.takePosts()
	}
	reactedFrom := func(step string, posts []relayPost, number int32, actions ...gmproto.ActionType) {
		t.Helper()
		if got := actionsOf(posts); !slices.Equal(got, actions) {
			t.Fatalf("%s asked the phone for %v", step, got)
		}
		var sent gmproto.SendReactionRequest
		posts[len(posts)-1].open(t, a.client, &sent)
		if sent.GetSIMPayload().GetSIMNumber() != number || sent.GetReactionData().GetUnicode() != "👍" {
			t.Fatalf("%s went from SIM %d", step, sent.GetSIMPayload().GetSIMNumber())
		}
	}

	a.handleEvent(rcsChat("chat", "sim-a"))
	phone.describes(rcsChat("chat", "sim-b"))
	clock.advance(59 * time.Second)
	answer, code, posts := call("ui.react", "chat", "message")
	reactedFrom("a reaction 59 s after an event", posts, 1, gmproto.ActionType_SEND_REACTION)
	if code != "" || answer["outcome"] != "accepted" || answer["conversation"] != "cached" {
		t.Fatalf("a reaction 59 s after an event answered %v %q", answer, code)
	}

	clock.advance(time.Second)
	answer, code, posts = call("ui.react", "chat", "message")
	reactedFrom("a reaction a minute after an event", posts, 2, gmproto.ActionType_GET_CONVERSATION, gmproto.ActionType_SEND_REACTION)
	if code != "" || answer["outcome"] != "accepted" || answer["conversation"] != "fetched" {
		t.Fatalf("a reaction a minute after an event answered %v %q", answer, code)
	}

	phone.describes(rcsChat("other", "sim-a"))
	answer, code, posts = call("ui.react", "other", "other-message")
	if got := actionsOf(posts); code != "" || answer["outcome"] != "accepted" || answer["conversation"] != "fetched" ||
		!slices.Equal(got, []gmproto.ActionType{gmproto.ActionType_GET_CONVERSATION, gmproto.ActionType_SEND_REACTION}) {
		t.Fatalf("a reaction with nothing held asked the phone for %v and answered %v %q", got, answer, code)
	}

	phone.describes(rcsChat("chat", "sim-a"))
	clock.advance(10 * time.Second)
	if _, code, posts = call("ui.actions", "chat", "message"); code != "" || !slices.Equal(actionsOf(posts), []gmproto.ActionType{gmproto.ActionType_GET_CONVERSATION}) {
		t.Fatalf("a message's options asked the phone for %v and answered %q", actionsOf(posts), code)
	}
	_, _, posts = call("ui.react", "chat", "message")
	reactedFrom("a reaction after the message's options", posts, 1, gmproto.ActionType_SEND_REACTION)
}

func TestHeldConversationIsTrustedForUnderAMinute(t *testing.T) {
	if conversationTrust != time.Minute {
		t.Fatalf("trust is meant to last a minute, not %v", conversationTrust)
	}
	clock := &handClock{at: time.Date(2026, 10, 14, 12, 0, 0, 0, time.UTC)}
	cache := conversationCache{now: clock.now}
	cache.note(rcsChat("chat", "sim-a"))
	clock.advance(conversationTrust - time.Nanosecond)
	if cache.recent("chat") == nil {
		t.Fatal("a description just under a minute old was not trusted")
	}
	clock.advance(time.Nanosecond)
	if cache.recent("chat") != nil {
		t.Fatal("a description a minute old was trusted")
	}
	if cache.get("chat") == nil {
		t.Fatal("the typing ping lost a description a minute old")
	}
	cache.note(rcsChat("chat", "sim-b"))
	if cache.recent("chat").GetDefaultOutgoingID() != "sim-b" {
		t.Fatal("a new description did not replace the old one")
	}
	clock.advance(-time.Second)
	if cache.recent("chat") != nil || cache.recent("unknown") != nil {
		t.Fatal("a description from the future, or none, was trusted")
	}
}
