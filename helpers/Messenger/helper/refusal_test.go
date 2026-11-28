package main

import (
	"bytes"
	"encoding/json"
	"slices"
	"strconv"
	"strings"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// The tag must be the server's own (HistoryParts.Tag), or the line cannot be matched with the server's log.
func TestServerTagMatchesTheServers(t *testing.T) {
	if got := serverTag(4242); got != "c3c448" {
		t.Fatalf("serverTag(4242) = %s", got)
	}
}

func TestRefusalSaysWhichReason(t *testing.T) {
	s := newStore()
	s.self = 1
	if line, code := s.refusal(10); !strings.Contains(line, "reason=absent") || strings.Contains(line, "kind=") || code != "unknown_chat" {
		t.Fatalf("absent: %s %s", line, code)
	}
	gone := s.get(11)
	gone.deleted = true
	gone.deletedAt = 5
	// A deleted conversation is said to be one, so the app forgets it rather than keeping it as unreadable.
	if line, code := s.refusal(11); !strings.Contains(line, "reason=deleted") || !strings.Contains(line, "cutoff=true") || code != "chat_deleted" {
		t.Fatalf("deleted: %s %s", line, code)
	}
	// A conversation held under an encrypted address another one was paired with, nobody in it known.
	orphan := s.get(12)
	orphan.unresolved = true
	orphan.participants[77] = struct{}{}
	orphan.participants[1] = struct{}{}
	s.hybrid[12] = 13
	s.messages[12] = map[string]*message{"a": {sender: 1}, "b": {sender: 77}, "c": {sender: 77}}
	if line, code := s.refusal(12); !strings.Contains(line, "reason=nameless") || !strings.Contains(line, "paired=true pairs=false") ||
		!strings.Contains(line, "participants=2 others=1 known=0 own=1 senders=1") || code != "unknown_chat" {
		t.Fatalf("nameless: %s", line)
	}
	if line, _ := s.refusal(13); !strings.Contains(line, "pairs=true") {
		t.Fatalf("pairs: %s", line)
	}
	// Folder names Facebook sends are not written as they are.
	orphan.folder = "e2ee_cutover_pending"
	if line, _ := s.refusal(12); !strings.Contains(line, "folder=other") {
		t.Fatalf("folder: %s", line)
	}
}

// Announcements leave out conversations held as deleted, all but their deletion; one about nothing else is not sent.
func TestAnnouncementsLeaveOutDeletedConversations(t *testing.T) {
	var wire bytes.Buffer
	o := &output{encoder: json.NewEncoder(&wire)}
	a := newAdapter(o)
	defer a.stop()
	o.deleted = a.deletedChat
	a.chats.get(7).deleted = true
	a.chats.get(8)
	both := &changes{Chats: []string{"7", "8"}, Messages: map[string][]string{"7": {"a"}, "8": {"b"}},
		Receipts: map[string][]string{"7": {"c"}}, ChatsDeleted: []string{"7"}}
	o.write(response{Event: "changed", Result: both})
	if len(both.Chats) != 2 || len(both.Messages) != 2 {
		t.Fatal("the announced changes were altered")
	}
	o.write(response{Event: "changed", Result: &changes{Chats: []string{"7"}, Messages: map[string][]string{"7": {"d"}}}})
	lines := strings.Split(strings.TrimSpace(wire.String()), "\n")
	if len(lines) != 1 {
		t.Fatalf("written %d: %s", len(lines), wire.String())
	}
	var got struct {
		Result changes `json:"result"`
	}
	if err := json.Unmarshal([]byte(lines[0]), &got); err != nil {
		t.Fatal(err)
	}
	if !slices.Equal(got.Result.Chats, []string{"8"}) || len(got.Result.Messages) != 1 || got.Result.Messages["8"] == nil ||
		len(got.Result.Receipts) != 0 || !slices.Equal(got.Result.ChatsDeleted, []string{"7"}) {
		t.Fatalf("written %s", lines[0])
	}
}

// An encrypted one-to-one kept with nobody in it but this account takes its other person from its messages, once there
// is exactly one, so the name lookup can find them; anything else is left as it is.
func TestEncryptedPeerFromMessages(t *testing.T) {
	s := newStore()
	s.self = 1
	lone := func(key int64, senders ...int64) *thread {
		current := s.get(key)
		current.kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
		current.unresolved = true
		current.participants[1] = struct{}{}
		held := map[string]*message{}
		for i, sender := range senders {
			held[strconv.Itoa(i)] = &message{sender: sender}
		}
		s.messages[key] = held
		return current
	}
	found := lone(20, 1, 55, 55, 0)
	withSelfOnly := lone(21, 1, 1)
	twoOthers := lone(22, 55, 66)
	gone := lone(23, 55)
	gone.deleted = true
	named := lone(24, 55)
	named.name = "Group"
	group := lone(25, 55)
	group.kind = table.ENCRYPTED_OVER_WA_GROUP
	adopted := s.adoptKeyPeople()
	if len(adopted) != 1 || adopted[0] != 20 {
		t.Fatalf("adopted %v", adopted)
	}
	if _, ok := found.participants[55]; !ok || len(found.participants) != 2 {
		t.Fatalf("peer not taken: %v", found.participants)
	}
	for _, left := range []*thread{withSelfOnly, twoOthers, gone, named, group} {
		if len(left.participants) != 1 {
			t.Fatalf("chat %d changed: %v", left.key, left.participants)
		}
	}
	// Named once the lookup brings the person's name.
	if s.visible(found) {
		t.Fatal("visible before the name")
	}
	s.contacts[55] = "Someone"
	if !s.visible(found) {
		t.Fatal("hidden after the name")
	}
}
