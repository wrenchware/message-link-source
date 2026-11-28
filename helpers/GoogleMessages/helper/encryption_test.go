package main

import (
	"encoding/json"
	"strings"
	"testing"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
)

// A message decoded the way libgm decodes one, with status field 8 appended as Google sends it.
func encryptionMessage(t *testing.T, route int64, third int64, field8 int64) *gmproto.Message {
	t.Helper()
	status := &gmproto.MessageStatus{Status: gmproto.MessageStatusType_OUTGOING_DELIVERED, ThirdCode: third}
	wire, err := proto.Marshal(&gmproto.Message{MessageID: "enc", ConversationID: "enc-c", Type: route, MessageStatus: status})
	if err != nil {
		t.Fatal(err)
	}
	var decoded gmproto.Message
	if err := proto.Unmarshal(wire, &decoded); err != nil {
		t.Fatal(err)
	}
	if field8 >= 0 {
		unknown := protowire.AppendTag(nil, 8, protowire.VarintType)
		unknown = protowire.AppendVarint(unknown, uint64(field8))
		decoded.MessageStatus.ProtoReflect().SetUnknown(unknown)
	}
	return &decoded
}

func TestMessageEncrypted(t *testing.T) {
	for _, c := range []struct {
		route, third, field8 int64
		want                 bool
	}{
		{4, 1, 1, true}, {4, 1, 2, true},
		{4, 0, -1, false}, // an RCS message before a switch to encryption
		{4, 1, -1, false}, {4, 0, 1, false},
		{4, 1, 3, false}, // an unknown protocol says nothing
		{1, 1, 1, false}, {2, 1, 2, false}, // SMS and MMS never
	} {
		if got := messageEncrypted(encryptionMessage(t, c.route, c.third, c.field8)); got != c.want {
			t.Errorf("route=%d third=%d f8=%d: %v, want %v", c.route, c.third, c.field8, got, c.want)
		}
	}
	if messageEncrypted(&gmproto.Message{Type: 4}) {
		t.Error("a message with no status claimed encryption")
	}
	encoded, err := json.Marshal(mapMessage(encryptionMessage(t, 4, 1, 2)))
	if err != nil || !strings.Contains(string(encoded), `"encrypted":true`) {
		t.Fatalf("encrypted missing from message JSON: %s %v", encoded, err)
	}
	plain, _ := json.Marshal(mapMessage(encryptionMessage(t, 4, 0, -1)))
	if strings.Contains(string(plain), `"encrypted"`) {
		t.Fatalf("an unmarked message said encrypted: %s", plain)
	}
}
