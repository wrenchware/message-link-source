package main

import (
	"testing"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

func TestAvatarColorPrefersOtherParticipantAndValidatesHex(t *testing.T) {
	for in, want := range map[string]string{"#1A73E8": "#1A73E8", "1a73e8": "#1a73e8", "#FF1A73E8": "#FF1A73E8", "blue": "", "#12345": "", "": ""} {
		if got := validHexColor(in); got != want {
			t.Fatalf("%q mapped to %q, want %q", in, got, want)
		}
	}
	direct := &gmproto.Conversation{AvatarHexColor: "#111111", Participants: []*gmproto.Participant{
		{IsMe: true, AvatarHexColor: "#222222"}, {AvatarHexColor: "#333333"}}}
	if avatarColor(direct) != "#333333" {
		t.Fatal("direct chat should use the other participant's color")
	}
	direct.IsGroupChat = true
	if avatarColor(direct) != "#111111" {
		t.Fatal("group should use the conversation color")
	}
}
