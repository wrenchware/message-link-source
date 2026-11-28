package main

import (
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"strings"
	"testing"
)

func TestReactionOwnershipAndActions(t *testing.T) {
	c := &gmproto.Conversation{ConversationID: "chat", DefaultOutgoingID: "self", Type: gmproto.ConversationType_RCS}
	m := &gmproto.Message{ConversationID: "chat", MessageID: "message"}
	sim := &gmproto.SIMCard{SIMParticipant: &gmproto.SIMParticipant{ID: "self"}, SIMData: &gmproto.SIMData{SIMPayload: &gmproto.SIMPayload{}}}
	r := reactionRequest(c, m, sim, "👍", false)
	if r == nil || r.Action != gmproto.SendReactionRequest_ADD || r.MessageID != "message" {
		t.Fatal("add")
	}
	m.Reactions = []*gmproto.ReactionEntry{{Data: gmproto.MakeReactionData("👍"), ParticipantIDs: []string{"other"}}}
	if reactionRequest(c, m, sim, "👍", true) != nil {
		t.Fatal("removed another participant reaction")
	}
	m.Reactions[0].ParticipantIDs = append(m.Reactions[0].ParticipantIDs, "self")
	r = reactionRequest(c, m, sim, "❤️", false)
	if r == nil || r.Action != gmproto.SendReactionRequest_SWITCH {
		t.Fatal("switch")
	}
	r = reactionRequest(c, m, sim, "👍", true)
	if r == nil || r.Action != gmproto.SendReactionRequest_REMOVE {
		t.Fatal("remove")
	}
	mapped := mapMessage(m)
	if len(mapped.Reactions) != 2 || mapped.Reactions[0].Key != "👍" {
		t.Fatal("reaction mapping")
	}
	// Any one emoji, as the phone's smile+ picker sends it; never words, which would reach the other person as a text.
	for _, emoji := range []string{"🖕", "🖕🏽", "👩‍👩‍👧‍👦", "🏳️‍🌈", "1️⃣"} {
		if r := reactionRequest(c, m, sim, emoji, false); r == nil || r.ReactionData.GetUnicode() != emoji {
			t.Fatalf("any emoji %q", emoji)
		}
	}
	for _, text := range []string{"", "a", "ok", "1", "#", "lol 😂", "👍 ", "\n", strings.Repeat("😀", 40)} {
		if reactionRequest(c, m, sim, text, false) != nil {
			t.Fatalf("words as a reaction %q", text)
		}
	}
	c.ConversationID = "other-chat"
	if reactionRequest(c, m, sim, "👍", false) != nil {
		t.Fatal("cross chat")
	}
	c.ConversationID = "chat"
	c.ReadOnly = true
	if reactionRequest(c, m, sim, "👍", false) != nil {
		t.Fatal("read-only")
	}
}
