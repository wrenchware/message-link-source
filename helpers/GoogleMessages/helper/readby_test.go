package main

import (
	"encoding/json"
	"slices"
	"strings"
	"testing"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

func statused(status gmproto.MessageStatusType, text string) *gmproto.Message {
	return &gmproto.Message{MessageID: "m", ConversationID: "c",
		MessageStatus: &gmproto.MessageStatus{Status: status, StatusText: text}}
}

// A group's readers come from Google's own words, read exactly as upstream reads them: a displayed message whose
// status text begins "Read by ", split on ", ".
func TestGroupReadersFollowUpstreamsReadingOfStatusText(t *testing.T) {
	got := mapMessage(statused(gmproto.MessageStatusType_OUTGOING_DISPLAYED, "Read by Ann, Bob Smith,  Cat , ")).ReadBy
	if !slices.Equal(got, []string{"Ann", "Bob Smith", "Cat"}) {
		t.Fatalf("readers not parsed as upstream does: %q", got)
	}
	for _, test := range []struct {
		status gmproto.MessageStatusType
		text   string
	}{
		// Upstream reads the names only from a message the phone reports as displayed.
		{gmproto.MessageStatusType_OUTGOING_DELIVERED, "Read by Ann"},
		{gmproto.MessageStatusType_OUTGOING_COMPLETE, "Read by Ann"},
		{gmproto.MessageStatusType_INCOMING_COMPLETE, "Read by Ann"},
		// And only from text that says so; a direct message's own wording is not a list of names.
		{gmproto.MessageStatusType_OUTGOING_DISPLAYED, "Read"},
		{gmproto.MessageStatusType_OUTGOING_DISPLAYED, "read by Ann"},
		{gmproto.MessageStatusType_OUTGOING_DISPLAYED, ""},
		{gmproto.MessageStatusType_OUTGOING_DISPLAYED, "Read by "},
	} {
		if got := mapMessage(statused(test.status, test.text)).ReadBy; got != nil {
			t.Fatalf("%s %q produced readers %q", test.status, test.text, got)
		}
	}
	// No status at all is no readers, not a crash.
	if got := mapMessage(&gmproto.Message{MessageID: "m"}).ReadBy; got != nil {
		t.Fatalf("a message with no status produced readers %q", got)
	}
	// Nothing on the pipe for a message nobody is named as reading.
	raw, _ := json.Marshal(mapMessage(statused(gmproto.MessageStatusType_OUTGOING_DELIVERED, "")))
	if strings.Contains(string(raw), "readBy") {
		t.Fatalf("an empty reader list was sent: %s", raw)
	}
}

// The app matches a reader by first name where it has to, so a participant carries it alongside the name shown.
func TestParticipantsCarryTheirFirstName(t *testing.T) {
	c := &gmproto.Conversation{ConversationID: "c", IsGroupChat: true, Participants: []*gmproto.Participant{
		{ID: &gmproto.SmallInfo{Number: "+15550100", ParticipantID: "p1"}, FirstName: "Ann", FullName: "Ann Lee"},
		{ID: &gmproto.SmallInfo{Number: "+15550101", ParticipantID: "p2"}, FormattedNumber: "(555) 010-1"},
	}}
	people := mapChat(c).Participants
	if people[0].First != "Ann" || people[0].Name != "Ann Lee" || people[1].First != "" {
		t.Fatalf("first names wrong: %+v", people)
	}
}
