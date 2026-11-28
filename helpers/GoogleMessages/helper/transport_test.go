package main

import (
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"testing"
)

func TestExpectedTransportRespectsFallbackAndUnknownModes(t *testing.T) {
	c := &gmproto.Conversation{Type: gmproto.ConversationType_RCS, DefaultOutgoingID: "self"}
	if mapChat(c).ExpectedTransport != "RCS" {
		t.Fatal("RCS hint missing")
	}
	for _, mode := range []gmproto.ConversationSendMode{gmproto.ConversationSendMode_SEND_MODE_XMS, gmproto.ConversationSendMode_SEND_MODE_XMS_LATCH} {
		c.SendMode = mode
		if expectedTransport(c) != "SMS" {
			t.Fatal("Fallback incorrectly labeled RCS")
		}
	}
	c.IsGroupChat = true
	if expectedTransport(c) != "SMS/MMS" {
		t.Fatal("Group preference must remain unknown")
	}
	c.SendMode = gmproto.ConversationSendMode(99)
	if expectedTransport(c) != "" {
		t.Fatal("Unknown send mode inferred")
	}
	c.SendMode = gmproto.ConversationSendMode_SEND_MODE_AUTO
	c.ReadOnly = true
	if expectedTransport(c) != "" {
		t.Fatal("Read-only hint offered")
	}
	c.ReadOnly = false
	c.DefaultOutgoingID = ""
	if expectedTransport(c) != "" || expectedTransport(nil) != "" {
		t.Fatal("Missing routing inferred")
	}
}

func TestMessageTransportMapsOnlyRecognizedTypes(t *testing.T) {
	for value, want := range map[int64]string{1: "SMS", 2: "MMS", 3: "MMS", 4: "RCS", 0: "", 5: ""} {
		if got := mapMessage(&gmproto.Message{Type: value}).Transport; got != want {
			t.Fatalf("type %d mapped to %q, want %q", value, got, want)
		}
	}
}
