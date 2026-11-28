package main

import (
	"strings"
	"testing"
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
	if line := s.refusal(10); !strings.Contains(line, "reason=absent") || strings.Contains(line, "kind=") {
		t.Fatalf("absent: %s", line)
	}
	gone := s.get(11)
	gone.deleted = true
	gone.deletedAt = 5
	if line := s.refusal(11); !strings.Contains(line, "reason=deleted") || !strings.Contains(line, "cutoff=true") {
		t.Fatalf("deleted: %s", line)
	}
	// A conversation held under an encrypted address another one was paired with, nobody in it known.
	orphan := s.get(12)
	orphan.unresolved = true
	orphan.participants[77] = struct{}{}
	s.hybrid[12] = 13
	if line := s.refusal(12); !strings.Contains(line, "reason=nameless") || !strings.Contains(line, "paired=true pairs=false") ||
		!strings.Contains(line, "participants=1 known=0") {
		t.Fatalf("nameless: %s", line)
	}
	if line := s.refusal(13); !strings.Contains(line, "pairs=true") {
		t.Fatalf("pairs: %s", line)
	}
	// Folder names Facebook sends are not written as they are.
	orphan.folder = "e2ee_cutover_pending"
	if line := s.refusal(12); !strings.Contains(line, "folder=other") {
		t.Fatalf("folder: %s", line)
	}
}
