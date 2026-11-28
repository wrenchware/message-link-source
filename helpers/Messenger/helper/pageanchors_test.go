package main

import "testing"

// A page answered from the local store has to be asked of Facebook again, and Facebook answers about twenty messages
// at a time — so a page takes several asks, each naming a message further back through it. Asking only once left the
// older part of the page without its pictures.
func TestPageIsWalkedInSteps(t *testing.T) {
	s := newStore()
	for index := 0; index < 120; index++ {
		s.addEncrypted(9, id(index), 1, "text", "", int64(1000+index))
	}
	// The page reaches back to the 50th newest message, 1070.
	anchors := s.pageAnchors(9, 0, 1070, 20)
	if len(anchors) != 3 {
		t.Fatalf("a page of 50 answered 20 at a time needs 3 asks, got %d", len(anchors))
	}
	if anchors[0].at != 1119 || anchors[1].at != 1099 || anchors[2].at != 1079 {
		t.Errorf("anchors walk back through the page: %+v", anchors)
	}
	for _, anchor := range anchors {
		if anchor.at < 1070 {
			t.Errorf("anchor %d is past the end of the page", anchor.at)
		}
	}
}

// The reason the walk is bounded by a time rather than a count: a photo with no caption has no text and no attachment
// until Facebook resends it, so it is filtered out of the page — and the page therefore spans more messages than it
// shows. Counting the messages in it would stop short of exactly the ones that are missing.
func TestWalkReachesPastTheMessagesThePageShows(t *testing.T) {
	s := newStore()
	// Sixty messages, every third one a photo whose attachment has not come back yet.
	for index := 0; index < 60; index++ {
		text := "text"
		if index%3 == 0 {
			text = ""
		}
		s.addEncrypted(9, id(index), 1, text, "", int64(1000+index))
	}
	page := s.historyBefore(9, 0, 50)
	if len(page) != 40 {
		t.Fatalf("the photo rows are not in the page yet, so it shows 40 of the 60: got %d", len(page))
	}
	oldest := s.oldestDisplayed(9, 0, 50)
	if oldest != 1001 {
		t.Fatalf("the page reaches back to the oldest message it shows, got %d", oldest)
	}
	anchors := s.pageAnchors(9, 0, oldest, 20)
	// 59 messages from 1001 to 1059 inclusive, twenty at a time.
	if len(anchors) != 3 {
		t.Fatalf("the walk covers the whole span the page reaches over, got %d asks", len(anchors))
	}
	if anchors[len(anchors)-1].at > 1019 {
		t.Errorf("the last ask should reach the far end of the page, got %d", anchors[len(anchors)-1].at)
	}
}

func TestPageAnchorsRespectTheCursor(t *testing.T) {
	s := newStore()
	for index := 0; index < 60; index++ {
		s.addEncrypted(9, id(index), 1, "text", "", int64(1000+index))
	}
	anchors := s.pageAnchors(9, 1030, 1000, 20)
	if len(anchors) == 0 {
		t.Fatal("a page below the cursor should still be asked for")
	}
	if anchors[0].at != 1029 {
		t.Errorf("the first anchor is the newest message below the cursor, got %d", anchors[0].at)
	}
	for _, anchor := range anchors {
		if anchor.at >= 1030 {
			t.Errorf("anchor %d is newer than the cursor", anchor.at)
		}
	}
}

func TestShortPageIsAskedForOnce(t *testing.T) {
	s := newStore()
	for index := 0; index < 5; index++ {
		s.addEncrypted(9, id(index), 1, "text", "", int64(1000+index))
	}
	if anchors := s.pageAnchors(9, 0, 1000, 20); len(anchors) != 1 {
		t.Errorf("five messages are one ask, got %d", len(anchors))
	}
	if anchors := s.pageAnchors(10, 0, 0, 20); len(anchors) != 0 {
		t.Errorf("a conversation with nothing in it asks for nothing, got %d", len(anchors))
	}
}

func id(index int) string {
	return string(rune('a'+index/26)) + string(rune('a'+index%26))
}
