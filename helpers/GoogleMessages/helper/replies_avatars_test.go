package main

import (
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"testing"
)

func TestReplyRequiresAvailableRCSMessageAndMatchingSIM(t *testing.T) {
	c := &gmproto.Conversation{ConversationID: "chat", DefaultOutgoingID: "self", Type: gmproto.ConversationType_RCS}
	sim := &gmproto.SIMCard{SIMParticipant: &gmproto.SIMParticipant{ID: "self"}, SIMData: &gmproto.SIMData{SIMPayload: &gmproto.SIMPayload{}}}
	m := &gmproto.Message{MessageID: "target", ConversationID: "chat", MessageStatus: &gmproto.MessageStatus{Status: gmproto.MessageStatusType_INCOMING_COMPLETE}, ReplyMessage: &gmproto.ReplyMessage{MessageID: "original"}}
	if !replyAllowed(c, m, sim) {
		t.Fatal("RCS reply disabled")
	}
	if mapMessage(m).ReplyTo != "original" {
		t.Fatal("incoming quote lost")
	}
	c.Type = gmproto.ConversationType_SMS
	if replyAllowed(c, m, sim) {
		t.Fatal("SMS native quote advertised")
	}
	c.Type = gmproto.ConversationType_RCS
	m.ConversationID = "other"
	if replyAllowed(c, m, sim) {
		t.Fatal("cross-chat quote")
	}
	m.ConversationID = "chat"
	m.MessageStatus.Status = gmproto.MessageStatusType_OUTGOING_SENDING
	if replyAllowed(c, m, sim) {
		t.Fatal("pending target")
	}
	m.MessageStatus.Status = gmproto.MessageStatusType_OUTGOING_DELETED
	if replyAllowed(c, m, sim) {
		t.Fatal("deleted target")
	}
	m.MessageStatus.Status = gmproto.MessageStatusType_OUTGOING_COMPLETE
	sim.SIMParticipant.ID = "other"
	if replyAllowed(c, m, sim) {
		t.Fatal("wrong SIM")
	}
}

func TestAvatarOriginsAndDocumentTypes(t *testing.T) {
	if !permittedAvatarURL("https://lh3.googleusercontent.com/photo") {
		t.Fatal("Google photo rejected")
	}
	for _, u := range []string{"http://lh3.googleusercontent.com/photo", "https://googleusercontent.com.evil.test/photo", "https://user@lh3.googleusercontent.com/photo", "https://127.0.0.1/photo", "file:///photo"} {
		if permittedAvatarURL(u) {
			t.Fatal("untrusted avatar accepted")
		}
	}
	for _, mime := range []string{"application/pdf", "application/zip", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "text/csv"} {
		if !permittedUpload(mime) {
			t.Fatal("supported document rejected")
		}
	}
}

func TestThumbnailPictureUsesOnlyTheAskedIdentifier(t *testing.T) {
	picture := func(id string, data []byte) *gmproto.GetThumbnailResponse_Thumbnail {
		return &gmproto.GetThumbnailResponse_Thumbnail{Identifier: id, Data: &gmproto.ThumbnailData{ImageBuffer: data}}
	}
	data, shape := thumbnailPicture(&gmproto.GetThumbnailResponse{Thumbnail: []*gmproto.GetThumbnailResponse_Thumbnail{picture("someone else", []byte{9}), picture("asked", []byte{1, 2})}}, "asked")
	if string(data) != string([]byte{1, 2}) || shape != "entries=2 matched=1 pictures=1 tiles=0 bytes=2 pic1_bytes=2 pic1_declared=0x0 pic1_img=unreadable int=0 dims=0 hash=0" {
		t.Fatalf("picked %v with shape %q", data, shape)
	}
	if data, _ := thumbnailPicture(&gmproto.GetThumbnailResponse{Thumbnail: []*gmproto.GetThumbnailResponse_Thumbnail{picture("someone else", []byte{9})}}, "asked"); len(data) != 0 {
		t.Fatal("another person's face was used")
	}
	if data, shape := thumbnailPicture(&gmproto.GetThumbnailResponse{Thumbnail: []*gmproto.GetThumbnailResponse_Thumbnail{{Identifier: "asked"}}}, "asked"); len(data) != 0 || shape != "entries=1 matched=1 pictures=0 tiles=0 bytes=0 int=0 dims=0 hash=0" {
		t.Fatalf("empty answer read as %v %q", data, shape)
	}
	if _, shape := thumbnailPicture(nil, "asked"); shape != "entries=0 matched=0 pictures=0 tiles=0 bytes=0" {
		t.Fatalf("no answer read as %q", shape)
	}
}

func TestDirectPartnerUsesTheCounterpartRule(t *testing.T) {
	person := func(id, contact string, me, visible bool) *gmproto.Participant {
		return &gmproto.Participant{ID: &gmproto.SmallInfo{ParticipantID: id}, IsMe: me, IsVisible: visible, ContactID: contact}
	}
	id := func(p *gmproto.Participant) string { return p.GetID().GetParticipantID() }
	if p, others, _ := directPartner(&gmproto.Conversation{Participants: []*gmproto.Participant{person("me", "", true, true), person("them", "", false, true)}}); p == nil || id(p) != "them" || others != 1 {
		t.Fatalf("got %v %d", p, others)
	}
	// The same person twice, under a second RCS identity the phone does not display: the displayed one.
	if p, others, candidates := directPartner(&gmproto.Conversation{Participants: []*gmproto.Participant{person("me", "", true, true), person("them", "c1", false, true), person("them-rcs", "", false, false)}}); p == nil || id(p) != "them" || others != 2 || candidates != 1 {
		t.Fatalf("got %v %d %d", p, others, candidates)
	}
	// Google's own list of everyone else wins over visibility.
	if p, _, _ := directPartner(&gmproto.Conversation{Participants: []*gmproto.Participant{person("them", "", false, false), person("other", "", false, true)}, OtherParticipants: []string{"them"}}); p == nil || id(p) != "them" {
		t.Fatalf("got %v", p)
	}
	// Two left: the one linked to a contact, and only when exactly one is.
	if p, _, _ := directPartner(&gmproto.Conversation{Participants: []*gmproto.Participant{person("a", "", false, true), person("b", "c2", false, true)}}); p == nil || id(p) != "b" {
		t.Fatalf("got %v", p)
	}
	if p, _, _ := directPartner(&gmproto.Conversation{Participants: []*gmproto.Participant{person("a", "c1", false, true), person("b", "c2", false, true)}}); p != nil {
		t.Fatalf("guessed %v", id(p))
	}
}

func TestKnownContactsFollowMappedConversations(t *testing.T) {
	c := &gmproto.Conversation{ConversationID: "chat", Participants: []*gmproto.Participant{{ID: &gmproto.SmallInfo{ParticipantID: "p1"}, ContactID: "c1"}, {ID: &gmproto.SmallInfo{ParticipantID: "p2"}}}}
	mapChat(c)
	if contact, ok := knownContacts.of("p1"); !ok || contact != "c1" {
		t.Fatalf("p1: %q %v", contact, ok)
	}
	if contact, ok := knownContacts.of("p2"); !ok || contact != "" {
		t.Fatalf("p2: %q %v", contact, ok)
	}
	if _, ok := knownContacts.of("unseen"); ok {
		t.Fatal("an unseen participant was known")
	}
	if prefixed("contact_", "entries=1 bytes=2") != "contact_entries=1 contact_bytes=2" {
		t.Fatal(prefixed("contact_", "entries=1 bytes=2"))
	}
}
