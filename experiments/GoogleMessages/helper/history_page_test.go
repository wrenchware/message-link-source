package main

import (
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"testing"
)

func TestPageCountBoundsAndDefault(t *testing.T) {
	for in, want := range map[int]int64{0: 20, -5: 20, 1: 1, 50: 50, 100: 100, 1000: 100} {
		if got := pageCount(in); got != want {
			t.Fatalf("count %d mapped to %d, want %d", in, got, want)
		}
	}
}

func TestPageCursorFallsBackToOldestMessage(t *testing.T) {
	r := &gmproto.ListMessagesResponse{Messages: []*gmproto.Message{{MessageID: "new", Timestamp: 5_000_000}, {MessageID: "old", Timestamp: 2_000_000}}}
	if c := pageCursor(r); c.GetLastItemID() != "old" || c.GetLastItemTimestamp() != 2000 {
		t.Fatal("fallback cursor must use the oldest message in milliseconds")
	}
	explicit := &gmproto.Cursor{LastItemID: "x", LastItemTimestamp: 1}
	r.Cursor = explicit
	if pageCursor(r) != explicit {
		t.Fatal("returned cursor must be preferred")
	}
	if pageCursor(&gmproto.ListMessagesResponse{}) != nil {
		t.Fatal("empty page must end pagination")
	}
}

func TestListFolderAllowsOnlyTheThreeFolders(t *testing.T) {
	if f, ok := listFolder(""); !ok || f != gmproto.ListConversationsRequest_INBOX {
		t.Fatal("default folder must be inbox")
	}
	if f, ok := listFolder("archive"); !ok || f != gmproto.ListConversationsRequest_ARCHIVE {
		t.Fatal("archive folder missing")
	}
	if f, ok := listFolder("spam"); !ok || f != gmproto.ListConversationsRequest_SPAM_BLOCKED {
		t.Fatal("spam folder missing")
	}
	if _, ok := listFolder("trash"); ok {
		t.Fatal("an unknown folder must not be listed")
	}
}
