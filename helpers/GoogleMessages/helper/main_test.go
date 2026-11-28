package main

import (
	"bytes"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"strings"
	"testing"
	"time"
)

func TestTyping(t *testing.T) {
	now := time.Unix(100, 0).UTC()
	evt := &gmproto.TypingData{ConversationID: "synthetic-chat", User: &gmproto.User{Number: "synthetic-user"}, Type: gmproto.TypingTypes_STARTED_TYPING}
	r := mapTyping(evt, now)
	if r == nil || !r.Active || !r.ExpiresAt.Equal(now.Add(15*time.Second)) {
		t.Fatal("start mapping")
	}
	evt.Type = gmproto.TypingTypes_STOPPED_TYPING
	if r = mapTyping(evt, now); r.Active || !r.ExpiresAt.Equal(now) {
		t.Fatal("stop mapping")
	}
	evt.Type = gmproto.TypingTypes(99)
	if mapTyping(evt, now) != nil {
		t.Fatal("unknown typing must not be fabricated")
	}
	evt.User = nil
	if mapTyping(evt, now) != nil {
		t.Fatal("missing user")
	}
}
func TestUnauthenticatedProtocol(t *testing.T) {
	var b bytes.Buffer
	serve(strings.NewReader("{\"id\":1,\"method\":\"hello\"}\n{\"id\":2,\"method\":\"chats\"}\n{\"id\":3,\"method\":\"send\"}\n{\"id\":4,\"method\":\"hello\"}\n"), &b)
	s := b.String()
	if !strings.Contains(s, "pairing_required") || !strings.Contains(s, "unsupported_method") || !strings.Contains(s, "v0.2611.1-0.20261126200815-6d8ba56a9462") {
		t.Fatal("protocol guards")
	}
	// The app refuses a helper that says any other number (GoogleMessagesBackend).
	if !strings.Contains(s, `"protocol":7,`) {
		t.Fatal("protocol number")
	}
}

func TestViewMapping(t *testing.T) {
	c := &gmproto.Conversation{ConversationID: "native-123", Participants: []*gmproto.Participant{
		{ID: &gmproto.SmallInfo{Number: "synthetic-self"}, FullName: "Self", IsMe: true},
		{ID: &gmproto.SmallInfo{Number: "synthetic-other"}, FullName: "Other", FormattedNumber: "(555) 010-0200"},
	}}
	mapped := mapChat(c)
	if mapped.ID != "native-123" || mapped.Title != "Other" || !mapped.Participants[0].Self || mapped.Participants[1].ID != "synthetic-other" || mapped.Participants[1].Phone != "(555) 010-0200" {
		t.Fatal("chat identity mapping")
	}
	m := &gmproto.Message{MessageID: "message-1", ConversationID: "native-123", Timestamp: 1234567890000000, MessageInfo: []*gmproto.MessageInfo{
		{Data: &gmproto.MessageInfo_MessageContent{MessageContent: &gmproto.MessageContent{Content: "synthetic text"}}},
		{Data: &gmproto.MessageInfo_MediaContent{MediaContent: &gmproto.MediaContent{}}},
	}}
	msg := mapMessage(m)
	if msg.ChatID != "native-123" || msg.At.UnixMicro() != m.Timestamp || !strings.Contains(msg.Text, "synthetic text") || len(msg.Media) != 1 {
		t.Fatal("message mapping")
	}
}

// A direct conversation that repeats its counterpart must still yield exactly one header number.
func TestDirectCounterpartNumber(t *testing.T) {
	self := &gmproto.Participant{ID: &gmproto.SmallInfo{Number: "synthetic-self", ParticipantID: "p0"}, FullName: "Self", IsMe: true, IsVisible: true}
	shown := &gmproto.Participant{ID: &gmproto.SmallInfo{Number: "synthetic-other", ParticipantID: "p1"}, FullName: "Other", FormattedNumber: "(555) 010-0200", IsVisible: true}
	hidden := &gmproto.Participant{ID: &gmproto.SmallInfo{Number: "synthetic-other-rcs", ParticipantID: "p2"}, FullName: "Other", FormattedNumber: "(555) 010-0999"}

	numbered := func(c *gmproto.Conversation) []string {
		out := []string{}
		for _, p := range mapChat(c).Participants {
			if p.Phone != "" {
				out = append(out, p.Phone)
			}
		}
		return out
	}

	// Google's own counterpart list decides, even when the extra row is marked visible.
	listed := &gmproto.Conversation{ConversationID: "c1", OtherParticipants: []string{"p1"},
		Participants: []*gmproto.Participant{self, shown, {ID: &gmproto.SmallInfo{Number: "synthetic-other-rcs", ParticipantID: "p2"}, FullName: "Other", FormattedNumber: "(555) 010-0999", IsVisible: true}}}
	if got := numbered(listed); len(got) != 1 || got[0] != "(555) 010-0200" {
		t.Fatalf("listed counterpart: %v", got)
	}

	// Without that list, visibility separates the shown row from the repeated one.
	unlisted := &gmproto.Conversation{ConversationID: "c2", Participants: []*gmproto.Participant{self, shown, hidden}}
	if got := numbered(unlisted); len(got) != 1 || got[0] != "(555) 010-0200" {
		t.Fatalf("visible counterpart: %v", got)
	}

	// A list that names nobody present, and rows that are all invisible, must not lose the number.
	stale := &gmproto.Conversation{ConversationID: "c3", OtherParticipants: []string{"p9"},
		Participants: []*gmproto.Participant{{ID: &gmproto.SmallInfo{Number: "synthetic-self", ParticipantID: "p0"}, IsMe: true},
			{ID: &gmproto.SmallInfo{Number: "synthetic-other", ParticipantID: "p1"}, FullName: "Other", FormattedNumber: "(555) 010-0200"}}}
	if got := numbered(stale); len(got) != 1 || got[0] != "(555) 010-0200" {
		t.Fatalf("stale counterpart list: %v", got)
	}

	// Every group member is a counterpart; self never carries a header number.
	group := &gmproto.Conversation{ConversationID: "c4", IsGroupChat: true, OtherParticipants: []string{"p1", "p2"},
		Participants: []*gmproto.Participant{self, shown, {ID: &gmproto.SmallInfo{Number: "synthetic-third", ParticipantID: "p2"}, FullName: "Third", FormattedNumber: "(555) 010-0300"}}}
	if got := numbered(group); len(got) != 2 {
		t.Fatalf("group counterparts: %v", got)
	}
}
