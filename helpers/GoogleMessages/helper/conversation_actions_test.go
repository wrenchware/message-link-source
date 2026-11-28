package main

import (
	"testing"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
)

func fieldBytes(t *testing.T, message []byte, field protowire.Number) []byte {
	t.Helper()
	for len(message) > 0 {
		number, wireType, n := protowire.ConsumeTag(message)
		if n < 0 {
			t.Fatal("invalid protobuf tag")
		}
		message = message[n:]
		if wireType == protowire.BytesType {
			value, valueLength := protowire.ConsumeBytes(message)
			if valueLength < 0 {
				t.Fatal("invalid protobuf bytes")
			}
			if number == field {
				return value
			}
			message = message[valueLength:]
			continue
		}
		valueLength := protowire.ConsumeFieldValue(number, wireType, message)
		if valueLength < 0 {
			t.Fatal("invalid protobuf value")
		}
		message = message[valueLength:]
	}
	return nil
}

func fieldString(t *testing.T, message []byte, field protowire.Number) string {
	t.Helper()
	return string(fieldBytes(t, message, field))
}

func TestConversationUpdatePayloads(t *testing.T) {
	me := &gmproto.Participant{IsMe: true, IsVisible: true, ID: &gmproto.SmallInfo{Number: "+15550000"}}
	other := &gmproto.Participant{IsVisible: true, ID: &gmproto.SmallInfo{Number: "+15551111"}}
	dm := &gmproto.Conversation{ConversationID: "c1", Participants: []*gmproto.Participant{me, other}}
	archive := conversationUpdate(dm, "archive")
	if archive.GetAction() != conversationActionArchive || archive.GetConversationID() != "c1" || archive.GetData() != nil {
		t.Fatal("archive payload")
	}
	archiveWire, err := proto.Marshal(archive)
	if err != nil {
		t.Fatal(err)
	}
	identity := fieldBytes(t, archiveWire, 6)
	if fieldString(t, identity, 1) != "c1" || fieldString(t, identity, 3) != "+15551111" {
		t.Fatal("direct archive identity")
	}
	repeatedIdentity := &gmproto.Participant{IsVisible: true, ID: &gmproto.SmallInfo{Number: "+15551111"}}
	dm.Participants = append(dm.Participants, repeatedIdentity)
	repeatedWire, err := proto.Marshal(conversationUpdate(dm, "archive"))
	if err != nil || fieldString(t, fieldBytes(t, repeatedWire, 6), 3) != "+15551111" {
		t.Fatal("direct archive identity must tolerate duplicate participant rows")
	}
	if r := conversationUpdate(dm, "unarchive"); r.GetAction() != conversationActionUnarchive || r.GetData() != nil {
		t.Fatal("unarchive payload")
	}
	if r := conversationUpdate(dm, "delete"); r.GetAction() != gmproto.ConversationActionStatus_DELETE ||
		r.GetDeleteData().GetPhone() != "+15551111" || r.GetDeleteData().GetConversationID() != "c1" {
		t.Fatal("direct delete must name the other participant")
	}
	group := &gmproto.Conversation{ConversationID: "g1", IsGroupChat: true,
		SomeKindOfGroupID: &gmproto.SomeKindOfGroupID{NestedID: &gmproto.SomeKindOfGroupID_NestedID{Id: "stable-group"}},
		Participants:      []*gmproto.Participant{me, other, {IsVisible: true, ID: &gmproto.SmallInfo{Number: "+15552222"}}}}
	groupArchiveWire, err := proto.Marshal(conversationUpdate(group, "archive"))
	if err != nil {
		t.Fatal(err)
	}
	groupIdentity := fieldBytes(t, groupArchiveWire, 6)
	if fieldString(t, groupIdentity, 1) != "g1" || fieldString(t, fieldBytes(t, groupIdentity, 2), 1) != "stable-group" || fieldString(t, groupIdentity, 3) != "" {
		t.Fatal("group archive identity")
	}
	if r := conversationUpdate(group, "delete"); r == nil || r.GetDeleteData().GetPhone() != "" {
		t.Fatal("group delete sends no number")
	}
	if conversationUpdate(&gmproto.Conversation{ConversationID: "c2", Participants: []*gmproto.Participant{me}}, "delete") != nil {
		t.Fatal("direct delete without a number must be refused")
	}
	if conversationUpdate(dm, "mute") != nil || conversationUpdate(&gmproto.Conversation{}, "archive") != nil {
		t.Fatal("unknown action or missing id must be refused")
	}
}

func TestChatFolderFlags(t *testing.T) {
	for status, want := range map[gmproto.ConversationStatus][3]bool{
		gmproto.ConversationStatus_ACTIVE: {false, false, false}, gmproto.ConversationStatus_ARCHIVED: {true, false, false},
		gmproto.ConversationStatus_KEEP_ARCHIVED: {true, false, false}, gmproto.ConversationStatus_DELETED: {false, true, false},
		gmproto.ConversationStatus_SPAM_FOLDER: {false, false, true}, gmproto.ConversationStatus_BLOCKED_FOLDER: {false, false, true},
	} {
		v := mapChat(&gmproto.Conversation{ConversationID: "c", Status: status})
		if v.Archived != want[0] || v.Hidden != want[1] || v.Spam != want[2] {
			t.Fatalf("status %v mapped to archived=%v hidden=%v spam=%v", status, v.Archived, v.Hidden, v.Spam)
		}
	}
}

func TestBlockAndReportIsForOnePerson(t *testing.T) {
	me := &gmproto.Participant{IsMe: true, ID: &gmproto.SmallInfo{ParticipantID: "me"}}
	them := &gmproto.Participant{ID: &gmproto.SmallInfo{ParticipantID: "them", Number: "+15550100"}}
	person := &gmproto.Conversation{ConversationID: "c", Participants: []*gmproto.Participant{me, them}}
	block := conversationUpdate(person, "blockAndReport")
	if block == nil || block.GetAction() != gmproto.ConversationActionStatus_BLOCK_AND_REPORT {
		t.Fatalf("one person's conversation not blocked and reported: %v", block)
	}
	if len(block.ProtoReflect().GetUnknown()) == 0 {
		t.Fatal("the block carries no conversation identity")
	}
	wire, err := proto.Marshal(block)
	if err != nil {
		t.Fatal(err)
	}
	fields := map[protowire.Number]uint64{}
	for raw := wire; len(raw) > 0; {
		number, kind, n := protowire.ConsumeTag(raw)
		if n < 0 {
			t.Fatal("unreadable block request")
		}
		raw = raw[n:]
		size := protowire.ConsumeFieldValue(number, kind, raw)
		if size < 0 {
			t.Fatal("unreadable block request")
		}
		if kind == protowire.VarintType {
			fields[number], _ = protowire.ConsumeVarint(raw)
		} else if number == 5 {
			body, _ := protowire.ConsumeBytes(raw)
			// Field 2 of field 5 is where the block was asked for: 1, the conversation's details.
			if len(body) != 2 || body[0] != 0x10 || body[1] != 1 {
				t.Fatalf("block source = %x", body)
			}
			fields[number] = 1
		}
		raw = raw[size:]
	}
	if fields[2] != 8 || fields[5] != 1 || fields[8] != 3 || fields[9] != 3 {
		t.Fatalf("block request is not the website's: %v", fields)
	}
	if un := conversationUpdate(person, "unblock"); un == nil || un.GetAction() != gmproto.ConversationActionStatus_UNBLOCK {
		t.Fatalf("not unblocked: %v", un)
	} else if un.GetAction5() != nil {
		t.Fatal("the website's unblock names no source")
	}
	group := &gmproto.Conversation{ConversationID: "g", IsGroupChat: true, Participants: []*gmproto.Participant{me, them}}
	if conversationUpdate(group, "blockAndReport") != nil {
		t.Fatal("a group must not be blocked")
	}
	if conversationUpdate(&gmproto.Conversation{ConversationID: "n"}, "blockAndReport") != nil {
		t.Fatal("a conversation with no number must not be blocked")
	}
}

func TestRefusalReasonIsNumbersOnly(t *testing.T) {
	refusal := func(field protowire.Number, code uint64) []byte {
		body := protowire.AppendVarint(protowire.AppendTag(nil, 1, protowire.VarintType), code)
		return protowire.AppendBytes(protowire.AppendTag(nil, field, protowire.BytesType), body)
	}
	if got := refusalReason(nil); got != "refused" {
		t.Fatalf("plain refusal = %q", got)
	}
	if got := refusalReason(refusal(4, 1)); got != "refused_f4e1" {
		t.Fatalf("location refusal = %q", got)
	}
	// Field 3 is the website's undo record, which names the conversation: it never reaches the log.
	undo := protowire.AppendString(protowire.AppendTag(nil, 3, protowire.BytesType), "+15550100")
	if got := refusalReason(append(undo, refusal(5, 3)...)); got != "refused_f5e3" {
		t.Fatalf("mixed refusal = %q", got)
	}
}
