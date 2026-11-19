package main

import (
	"fmt"
	"math/rand"
	"reflect"
	"testing"
)

// A page works out every reader's face once; it must put each face exactly where asking message by message does,
// shared times, unsent messages and the account's own reading included.
func TestPageFacesMatchMessageByMessage(t *testing.T) {
	random := rand.New(rand.NewSource(7))
	s := newStore()
	s.setSelf(1)
	for index := 0; index < 200; index++ {
		at := int64(1000 + random.Intn(150)*10)
		s.addEncrypted(9, fmt.Sprintf("m%03d", index), int64(1+random.Intn(3)), "synthetic", "", at)
	}
	s.mu.Lock()
	for id, held := range s.messages[9] {
		if id[len(id)-1] == '7' {
			held.unsent = true
		}
	}
	thread := s.threads[9]
	thread.readBy = map[int64]int64{1: 2400}
	for contact := int64(2); contact < 40; contact++ {
		thread.readBy[contact] = int64(900 + random.Intn(170)*10)
	}
	s.mu.Unlock()
	page := s.historyBefore(9, 0, 0)
	if len(page) == 0 {
		t.Fatal("nothing drawn")
	}
	faces := 0
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, drawn := range page {
		want := s.readMarkOf(9, s.messages[9][drawn.ID])
		if !reflect.DeepEqual(drawn.ReadMark, want) {
			t.Fatalf("message %s: page put %v, one by one puts %v", drawn.ID, drawn.ReadMark, want)
		}
		faces += len(want)
	}
	if faces == 0 {
		t.Fatal("the synthetic conversation drew no faces, so nothing was compared")
	}
}
