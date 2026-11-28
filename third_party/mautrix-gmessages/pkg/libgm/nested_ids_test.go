package libgm

// Added to the pinned libgm (see third_party/mautrix-gmessages/NATIVE_MESSAGES_PATCH.md).

import (
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/util/exhttp"
	"google.golang.org/protobuf/proto"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// For some accounts the phone leaves Conversation.conversationID and Message.messageID empty and sends the IDs only
// in someKindOfGroupID and anotherMessageID (upstream #166). Without the fallback every list answer maps to no
// conversation and every pushed message to an empty chat ID.
func decodeAsPhone(t *testing.T, action gmproto.ActionType, payload proto.Message) proto.Message {
	t.Helper()
	client := NewClient(NewAuthData(), nil, zerolog.Nop(), exhttp.ClientSettings{})
	plain, err := proto.Marshal(payload)
	if err != nil {
		t.Fatal(err)
	}
	encrypted, err := client.AuthData.RequestCrypto.Encrypt(plain)
	if err != nil {
		t.Fatal(err)
	}
	data, err := proto.Marshal(&gmproto.RPCMessageData{Action: action, EncryptedData: encrypted})
	if err != nil {
		t.Fatal(err)
	}
	msg, err := client.decryptInternalMessage(&gmproto.IncomingRPCMessage{BugleRoute: gmproto.BugleRoute_DataEvent, MessageData: data})
	if err != nil {
		t.Fatal(err)
	}
	return msg.DecryptedMessage
}

func nestedConversation(id, latest string) *gmproto.Conversation {
	return &gmproto.Conversation{
		SomeKindOfGroupID:      &gmproto.SomeKindOfGroupID{ConversationID: id},
		LatestMessageAnotherID: &gmproto.AnotherMessageID{MessageID: latest},
	}
}

func nestedMessage(id, chat string) *gmproto.Message {
	return &gmproto.Message{
		AnotherMessageID:  &gmproto.AnotherMessageID{MessageID: id},
		SomeKindOfGroupID: &gmproto.SomeKindOfGroupID{ConversationID: chat},
		MessageStatus:     &gmproto.MessageStatus{Status: gmproto.MessageStatusType_INCOMING_COMPLETE},
	}
}

func TestNestedIDsFillConversationList(t *testing.T) {
	got := decodeAsPhone(t, gmproto.ActionType_LIST_CONVERSATIONS, &gmproto.ListConversationsResponse{
		Conversations: []*gmproto.Conversation{nestedConversation("1", "m1"), nestedConversation("2", "m2")},
	}).(*gmproto.ListConversationsResponse)
	for i, want := range []string{"1", "2"} {
		c := got.GetConversations()[i]
		if c.GetConversationID() != want || c.GetLatestMessageID() != "m"+want {
			t.Fatalf("conversation %d: id=%q latest=%q, want %q and %q", i, c.GetConversationID(), c.GetLatestMessageID(), want, "m"+want)
		}
	}
}

func TestNestedIDsFillPushedMessagesAndChats(t *testing.T) {
	got := decodeAsPhone(t, gmproto.ActionType_GET_UPDATES, &gmproto.UpdateEvents{
		Event: &gmproto.UpdateEvents_MessageEvent{MessageEvent: &gmproto.MessageEvent{Data: []*gmproto.Message{nestedMessage("m1", "1")}}},
	}).(*gmproto.UpdateEvents)
	if m := got.GetMessageEvent().GetData()[0]; m.GetMessageID() != "m1" || m.GetConversationID() != "1" {
		t.Fatalf("message: id=%q chat=%q", m.GetMessageID(), m.GetConversationID())
	}
	got = decodeAsPhone(t, gmproto.ActionType_GET_UPDATES, &gmproto.UpdateEvents{
		Event: &gmproto.UpdateEvents_ConversationEvent{ConversationEvent: &gmproto.ConversationEvent{Data: []*gmproto.Conversation{nestedConversation("1", "m1")}}},
	}).(*gmproto.UpdateEvents)
	if c := got.GetConversationEvent().GetData()[0]; c.GetConversationID() != "1" {
		t.Fatalf("chat: id=%q", c.GetConversationID())
	}
}

func TestNestedIDsFillReads(t *testing.T) {
	page := decodeAsPhone(t, gmproto.ActionType_LIST_MESSAGES, &gmproto.ListMessagesResponse{
		Messages: []*gmproto.Message{nestedMessage("m1", "1")},
	}).(*gmproto.ListMessagesResponse)
	if m := page.GetMessages()[0]; m.GetMessageID() != "m1" || m.GetConversationID() != "1" {
		t.Fatalf("page: id=%q chat=%q", m.GetMessageID(), m.GetConversationID())
	}
	one := decodeAsPhone(t, gmproto.ActionType_GET_CONVERSATION, &gmproto.GetConversationResponse{Conversation: nestedConversation("1", "m1")}).(*gmproto.GetConversationResponse)
	if one.GetConversation().GetConversationID() != "1" {
		t.Fatalf("get: id=%q", one.GetConversation().GetConversationID())
	}
	made := decodeAsPhone(t, gmproto.ActionType_GET_OR_CREATE_CONVERSATION, &gmproto.GetOrCreateConversationResponse{Conversation: nestedConversation("1", "m1")}).(*gmproto.GetOrCreateConversationResponse)
	if made.GetConversation().GetConversationID() != "1" {
		t.Fatalf("get or create: id=%q", made.GetConversation().GetConversationID())
	}
}

// When the top-level IDs are set they win, as before.
func TestTopLevelIDsKept(t *testing.T) {
	c := nestedConversation("nested", "nested-m")
	c.ConversationID, c.LatestMessageID = "top", "top-m"
	got := decodeAsPhone(t, gmproto.ActionType_LIST_CONVERSATIONS, &gmproto.ListConversationsResponse{Conversations: []*gmproto.Conversation{c}}).(*gmproto.ListConversationsResponse)
	if g := got.GetConversations()[0]; g.GetConversationID() != "top" || g.GetLatestMessageID() != "top-m" {
		t.Fatalf("id=%q latest=%q", g.GetConversationID(), g.GetLatestMessageID())
	}
}
