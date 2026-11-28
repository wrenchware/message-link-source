package main

import (
	"strconv"
	"testing"
)

// A window around a moment comes from what is known, oldest first, with as many on each side as asked for, never
// splitting one moment at either end, and says whether there is more beyond each.
func TestHistoryAroundTakesBothSidesWithoutSplittingAMoment(t *testing.T) {
	s := newStore()
	// Messages at 1000, 2000, ... 20000, with two more sharing 5000 and two more sharing 16000.
	for i := 1; i <= 20; i++ {
		s.addEncrypted(7, "m"+strconv.Itoa(i), 42, "text", "", int64(i)*1000)
	}
	s.addEncrypted(7, "m5b", 42, "text", "", 5000)
	s.addEncrypted(7, "m16b", 42, "text", "", 16000)
	ats := func(page []viewMessage) []int64 {
		out := make([]int64, len(page))
		for i, message := range page {
			out[i] = message.At.UnixMilli()
		}
		return out
	}

	page, moreOlder, moreNewer := s.historyAround(7, 10000, 3, 3)
	if got := ats(page); len(got) != 6 || got[0] != 7000 || got[2] != 9000 || got[3] != 10000 || got[5] != 12000 || !moreOlder || !moreNewer {
		t.Fatalf("around 10000: %v older=%v newer=%v", got, moreOlder, moreNewer)
	}
	// The older end lands inside the moment at 5000: both of its messages come.
	page, _, _ = s.historyAround(7, 7000, 2, 0)
	if got := ats(page); len(got) != 3 || got[0] != 5000 || got[1] != 5000 || got[2] != 6000 {
		t.Fatalf("older end must keep the moment whole: %v", got)
	}
	// The newer end lands inside the moment at 16000: both come.
	page, _, _ = s.historyAround(7, 15000, 0, 2)
	if got := ats(page); len(got) != 3 || got[0] != 15000 || got[1] != 16000 || got[2] != 16000 {
		t.Fatalf("newer end must keep the moment whole: %v", got)
	}
	// Paging newer from just past the newest message there is: nothing, and nothing more.
	page, moreOlder, moreNewer = s.historyAround(7, 20001, 0, 50)
	if len(page) != 0 || !moreOlder || moreNewer {
		t.Fatalf("past the end: %d older=%v newer=%v", len(page), moreOlder, moreNewer)
	}
	// Reaching both ends says so.
	page, moreOlder, moreNewer = s.historyAround(7, 10000, 200, 200)
	if len(page) != 22 || moreOlder || moreNewer {
		t.Fatalf("whole conversation: %d older=%v newer=%v", len(page), moreOlder, moreNewer)
	}
	// Another conversation, or one with nothing known, is empty.
	if page, moreOlder, moreNewer = s.historyAround(8, 10000, 5, 5); len(page) != 0 || moreOlder || moreNewer {
		t.Fatal("an unknown conversation must answer nothing")
	}
}
