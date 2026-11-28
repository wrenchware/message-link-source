package main

import (
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"testing"
)

func TestTypingRequiresRCSAndMatchingSIM(t *testing.T) {
	c := &gmproto.Conversation{ConversationID: "chat", DefaultOutgoingID: "sim", Type: gmproto.ConversationType_RCS}
	sim := &gmproto.SIMCard{SIMParticipant: &gmproto.SIMParticipant{ID: "sim"}, SIMData: &gmproto.SIMData{SIMPayload: &gmproto.SIMPayload{}}}
	if !canType(c, sim) {
		t.Fatal("RCS typing blocked")
	}
	c.ReadOnly = true
	if canType(c, sim) {
		t.Fatal("read-only typing")
	}
	c.ReadOnly = false
	c.SendMode = gmproto.ConversationSendMode_SEND_MODE_XMS
	if canType(c, sim) {
		t.Fatal("SMS typing")
	}
	c.SendMode = gmproto.ConversationSendMode_SEND_MODE_AUTO
	sim.SIMParticipant.ID = "other"
	if canType(c, sim) || canType(c, nil) || canType(nil, sim) {
		t.Fatal("incorrect routing")
	}
}
