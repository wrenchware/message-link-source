package main

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"go.mau.fi/util/exhttp"
	"go.mau.fi/util/pblite"
	"google.golang.org/protobuf/proto"
)

// Google's relay as far as this helper can see it: each request to the phone is accepted and its action recorded. The
// phone never answers unless a test gives it answers, so a call that waits for its answer ends with its context.
type recordingRelay struct {
	mu      sync.Mutex
	actions []gmproto.ActionType
	posts   []relayPost
	// The phone answering a request, when set (phoneAnswers in send_cache_test.go).
	answer func(relayPost)
}

// One request to the phone: its action, the id its answer carries, and its payload as the library encrypted it.
type relayPost struct {
	action    gmproto.ActionType
	requestID string
	sealed    []byte
}

func (r *recordingRelay) RoundTrip(req *http.Request) (*http.Response, error) {
	body, _ := io.ReadAll(req.Body)
	post := relayPost{action: gmproto.ActionType_UNSPECIFIED}
	var message gmproto.OutgoingRPCMessage
	var data gmproto.OutgoingRPCData
	if pblite.Unmarshal(body, &message) == nil && proto.Unmarshal(message.GetData().GetMessageData(), &data) == nil {
		post = relayPost{data.GetAction(), data.GetRequestID(), data.GetEncryptedProtoData()}
	}
	r.mu.Lock()
	r.actions = append(r.actions, post.action)
	r.posts = append(r.posts, post)
	answer := r.answer
	r.mu.Unlock()
	if answer != nil {
		answer(post)
	}
	return &http.Response{StatusCode: http.StatusOK, Header: http.Header{"Content-Type": {libgm.ContentTypePBLite}},
		Body: io.NopCloser(strings.NewReader("[]")), Request: req}, nil
}

// The actions posted since the last call, in order.
func (r *recordingRelay) take() []gmproto.ActionType {
	r.mu.Lock()
	defer r.mu.Unlock()
	taken := r.actions
	r.actions, r.posts = nil, nil
	return taken
}

// The requests posted since the last call, in order.
func (r *recordingRelay) takePosts() []relayPost {
	r.mu.Lock()
	defer r.mu.Unlock()
	taken := r.posts
	r.actions, r.posts = nil, nil
	return taken
}

func relayAdapter(relay http.RoundTripper) *adapter {
	auth := libgm.NewAuthData()
	auth.Browser = &gmproto.Device{}
	settings := exhttp.ClientSettings{TransportOverride: func(exhttp.ClientSettings) http.RoundTripper { return relay }}
	return &adapter{client: libgm.NewClient(auth, nil, zerolog.Nop(), settings), out: &output{encoder: json.NewEncoder(io.Discard)}}
}

func shortCall(t *testing.T) context.Context {
	ctx, cancel := context.WithTimeout(context.Background(), 300*time.Millisecond)
	t.Cleanup(cancel)
	return ctx
}

// Marking read asked the phone for the conversation first, only to see that it exists: a phone round trip before
// every read marker. The marker is the one request now.
func TestMarkReadIsOneRequestToThePhone(t *testing.T) {
	relay := &recordingRelay{}
	a := relayAdapter(relay)
	params, _ := json.Marshal(map[string]string{"chatId": "chat", "messageId": "message"})
	_, code := a.actionCall(shortCall(t), request{Method: "ui.markRead", Params: params})
	if got := relay.take(); len(got) != 1 || got[0] != gmproto.ActionType_MESSAGE_READ {
		t.Fatalf("marking read asked the phone for %v", got)
	}
	// This phone never answers; one that does not know the conversation fails the same way.
	if code != "read_failed" {
		t.Fatalf("an unanswered read marker came back %q", code)
	}
	params, _ = json.Marshal(map[string]string{"chatId": "chat"})
	if _, code = a.actionCall(shortCall(t), request{Method: "ui.markRead", Params: params}); code != "invalid_input" || len(relay.take()) != 0 {
		t.Fatalf("a read marker without a message reached the phone: %q", code)
	}
}

// Typing pinged the phone for the conversation before each ping. A conversation the phone has described, here by the
// event it sends when one changes, is taken as described; one it has not is asked for as before.
func TestTypingUsesThePhonesLastDescription(t *testing.T) {
	relay := &recordingRelay{}
	a := relayAdapter(relay)
	a.sims = map[string]*gmproto.SIMCard{"sim": {SIMParticipant: &gmproto.SIMParticipant{ID: "sim"}, SIMData: &gmproto.SIMData{SIMPayload: &gmproto.SIMPayload{}}}}
	a.handleEvent(&gmproto.Conversation{ConversationID: "chat", DefaultOutgoingID: "sim", Type: gmproto.ConversationType_RCS, SendMode: gmproto.ConversationSendMode_SEND_MODE_AUTO})
	typing := func(chat string) (any, string) {
		params, _ := json.Marshal(map[string]any{"chatId": chat, "expiresAt": time.Now().Add(3 * time.Second)})
		return a.typingCall(shortCall(t), request{Method: "ui.typing", Params: params})
	}
	result, code := typing("chat")
	if got := relay.take(); len(got) != 1 || got[0] != gmproto.ActionType_TYPING_UPDATES {
		t.Fatalf("a typing ping for a described conversation asked the phone for %v", got)
	}
	if code != "" || result.(map[string]bool)["submitted"] != true {
		t.Fatalf("typing ping answered %v %q", result, code)
	}
	result, code = typing("other")
	if got := relay.take(); len(got) != 1 || got[0] != gmproto.ActionType_GET_CONVERSATION {
		t.Fatalf("a typing ping for an undescribed conversation asked the phone for %v", got)
	}
	if code != "" || result.(map[string]bool)["skipped"] != true {
		t.Fatalf("a conversation the phone never described answered %v %q", result, code)
	}
}

func TestConversationCacheIsBounded(t *testing.T) {
	var cache conversationCache
	for i := range conversationCacheLimit {
		cache.note(&gmproto.Conversation{ConversationID: strconv.Itoa(i)})
	}
	cache.note(&gmproto.Conversation{ConversationID: "0", Name: "renamed"})
	if len(cache.chats) != conversationCacheLimit || cache.get("0").GetName() != "renamed" {
		t.Fatal("a conversation already held is updated in place")
	}
	cache.note(&gmproto.Conversation{ConversationID: "new"})
	if len(cache.chats) != 1 || cache.get("new") == nil {
		t.Fatalf("the cache holds %d conversations past its limit", len(cache.chats))
	}
	cache.note(&gmproto.Conversation{})
	if cache.get("") != nil {
		t.Fatal("a conversation without an id was held")
	}
}
