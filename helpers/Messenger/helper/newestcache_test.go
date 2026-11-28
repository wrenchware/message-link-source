package main

import (
	"fmt"
	"math/rand"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func scannedNewest(s *store, key int64) int64 {
	var latest int64
	for _, current := range s.messages[key] {
		if !current.unsent && current.timestamp > latest {
			latest = current.timestamp
		}
	}
	return latest
}

// The newest time per conversation is kept rather than rescanned for every message added (a restore of thousands was
// quadratic). It must answer what a scan would after any mix of arrivals, replacements, unsends and removals.
func TestNewestMessageTimeStaysTrue(t *testing.T) {
	random := rand.New(rand.NewSource(11))
	s := newStore()
	s.setSelf(1)
	s.get(9).kind = table.ONE_TO_ONE
	ids := []string{}
	for step := 0; step < 3000; step++ {
		at := int64(1000 + random.Intn(400))
		switch choice := random.Intn(10); {
		case choice < 4 || len(ids) == 0:
			id := fmt.Sprintf("m%d", step)
			ids = append(ids, id)
			s.addEncrypted(9, id, 2, "synthetic", "", at, false)
		case choice < 6:
			id := ids[random.Intn(len(ids))]
			s.apply(&table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 9, MessageId: id, SenderId: 2, TimestampMs: at, Text: "again", IsUnsent: random.Intn(3) == 0}}})
		case choice < 8:
			s.forget(9, ids[random.Intn(len(ids))])
		default:
			s.removeEncrypted(9, ids[random.Intn(len(ids))])
		}
		s.mu.Lock()
		cached, scanned := s.latestMessageTimestampLocked(9), scannedNewest(s, 9)
		s.mu.Unlock()
		if cached != scanned {
			t.Fatalf("step %d: kept newest %d, the conversation's is %d", step, cached, scanned)
		}
	}
}
