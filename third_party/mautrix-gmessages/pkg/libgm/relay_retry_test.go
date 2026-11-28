package libgm

// Added to the pinned libgm (see third_party/mautrix-gmessages/NATIVE_MESSAGES_PATCH.md).

import (
	"context"
	"io"
	"net/http"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/util/exhttp"
	"go.mau.fi/util/pblite"
	"google.golang.org/protobuf/proto"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// A relay that answers every post with 503 and records which action each post carried.
type failingRelay struct {
	mu      sync.Mutex
	actions []gmproto.ActionType
}

func (r *failingRelay) RoundTrip(req *http.Request) (*http.Response, error) {
	body, _ := io.ReadAll(req.Body)
	action := gmproto.ActionType_UNSPECIFIED
	var message gmproto.OutgoingRPCMessage
	var data gmproto.OutgoingRPCData
	if pblite.Unmarshal(body, &message) == nil && proto.Unmarshal(message.GetData().GetMessageData(), &data) == nil {
		action = data.GetAction()
	}
	r.mu.Lock()
	r.actions = append(r.actions, action)
	r.mu.Unlock()
	return &http.Response{
		StatusCode: http.StatusServiceUnavailable,
		Header:     http.Header{"Content-Type": {ContentTypePBLite}},
		Body:       io.NopCloser(strings.NewReader("")),
		Request:    req,
	}, nil
}

// How many times each action was posted since the last call.
func (r *failingRelay) take() map[gmproto.ActionType]int {
	r.mu.Lock()
	defer r.mu.Unlock()
	counts := map[gmproto.ActionType]int{}
	for _, action := range r.actions {
		counts[action]++
	}
	r.actions = nil
	return counts
}

func failingRelayClient(t *testing.T) (*Client, *failingRelay) {
	t.Helper()
	previous := serverErrorRetryInterval
	serverErrorRetryInterval = time.Millisecond
	t.Cleanup(func() { serverErrorRetryInterval = previous })
	relay := &failingRelay{}
	settings := exhttp.ClientSettings{TransportOverride: func(exhttp.ClientSettings) http.RoundTripper { return relay }}
	return NewClient(NewAuthData(), nil, zerolog.Nop(), settings), relay
}

// A 5xx does not say whether the relay passed the request to the phone before failing, so posting a send, reaction,
// delete or new conversation again can do it twice. Each of those is posted once; reads keep upstream's three attempts.
func TestRelayRepeatsOnlyReadsAfterServerError(t *testing.T) {
	client, relay := failingRelayClient(t)
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	once := []struct {
		action gmproto.ActionType
		call   func() error
	}{
		{gmproto.ActionType_SEND_MESSAGE, func() error {
			_, err := client.SendMessage(ctx, &gmproto.SendMessageRequest{ConversationID: "synthetic"})
			return err
		}},
		{gmproto.ActionType_SEND_REACTION, func() error {
			_, err := client.SendReaction(ctx, &gmproto.SendReactionRequest{MessageID: "synthetic"})
			return err
		}},
		{gmproto.ActionType_DELETE_MESSAGE, func() error { _, err := client.DeleteMessage(ctx, "synthetic"); return err }},
		{gmproto.ActionType_UPDATE_CONVERSATION, func() error {
			_, err := client.UpdateConversation(ctx, &gmproto.UpdateConversationRequest{ConversationID: "synthetic"})
			return err
		}},
		{gmproto.ActionType_GET_OR_CREATE_CONVERSATION, func() error {
			_, err := client.GetOrCreateConversation(ctx, &gmproto.GetOrCreateConversationRequest{})
			return err
		}},
		{gmproto.ActionType_MESSAGE_READ, func() error { return client.MarkRead(ctx, "synthetic", "synthetic") }},
		{gmproto.ActionType_TYPING_UPDATES, func() error { return client.SetTyping(ctx, "synthetic", true, nil) }},
		{gmproto.ActionType_SETTINGS_UPDATE, func() error { return client.UpdateSettings(ctx, &gmproto.SettingsUpdateRequest{}) }},
	}
	for _, test := range once {
		if err := test.call(); err == nil {
			t.Fatalf("%s: a 503 must fail the call", test.action)
		}
		if got := relay.take(); len(got) != 1 || got[test.action] != 1 {
			t.Fatalf("%s must be posted exactly once after a 503, got %v", test.action, got)
		}
	}
	reads := []struct {
		action gmproto.ActionType
		call   func() error
	}{
		{gmproto.ActionType_LIST_CONVERSATIONS, func() error {
			_, err := client.ListConversations(ctx, &gmproto.ListConversationsRequest{Count: 1})
			return err
		}},
		{gmproto.ActionType_LIST_MESSAGES, func() error { _, err := client.FetchMessages(ctx, "synthetic", 1, nil); return err }},
		{gmproto.ActionType_GET_CONVERSATION, func() error { _, err := client.GetConversation(ctx, "synthetic"); return err }},
	}
	for _, test := range reads {
		if err := test.call(); err == nil {
			t.Fatalf("%s: a 503 must fail the call", test.action)
		}
		if got := relay.take(); len(got) != 1 || got[test.action] != ServerErrorMaxAttempts {
			t.Fatalf("%s must keep its %d attempts after a 503, got %v", test.action, ServerErrorMaxAttempts, got)
		}
	}
}

// The list names what may repeat. Nothing that changes the account, reaches a conversation or pairs a device may be on it.
func TestRepeatableRelayActionsHoldNoMutation(t *testing.T) {
	for _, action := range []gmproto.ActionType{
		gmproto.ActionType_UNSPECIFIED,
		gmproto.ActionType_SEND_MESSAGE, gmproto.ActionType_SEND_REPLY, gmproto.ActionType_RESEND_MESSAGE,
		gmproto.ActionType_SEND_REACTION, gmproto.ActionType_DELETE_MESSAGE, gmproto.ActionType_MESSAGE_READ,
		gmproto.ActionType_TYPING_UPDATES, gmproto.ActionType_SETTINGS_UPDATE, gmproto.ActionType_UPDATE_CONVERSATION,
		gmproto.ActionType_GET_OR_CREATE_CONVERSATION, gmproto.ActionType_LEAVE_RCS_GROUP,
		gmproto.ActionType_ADD_PARTICIPANT_TO_RCS_GROUP, gmproto.ActionType_REMOVE_PARTICIPANT_FROM_RCS_GROUP,
		gmproto.ActionType_UPDATE_GROUP_ICON, gmproto.ActionType_CHANGE_PARTICIPANT_COLOR,
		gmproto.ActionType_INSTALL_STICKER_SET, gmproto.ActionType_UPDATE_RECENT_STICKERS,
		gmproto.ActionType_CREATE_GAIA_PAIRING, gmproto.ActionType_CREATE_GAIA_PAIRING_CLIENT_INIT,
		gmproto.ActionType_CREATE_GAIA_PAIRING_CLIENT_FINISHED, gmproto.ActionType_UNPAIR_GAIA_PAIRING,
		gmproto.ActionType_CANCEL_GAIA_PAIRING, gmproto.ActionType_LINK_RCS_IDENTITY, gmproto.ActionType_UNLINK_RCS_IDENTITY,
	} {
		if attempts := relayAttempts(action); attempts != 1 {
			t.Fatalf("%s changes something and must be posted once, not %d times", action, attempts)
		}
	}
}
