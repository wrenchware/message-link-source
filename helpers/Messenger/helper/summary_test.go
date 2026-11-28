package main

import (
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func TestSummarizeCountsRowsWithoutContent(t *testing.T) {
	tbl := &table.LSTable{
		LSDeleteThenInsertThread:  []*table.LSDeleteThenInsertThread{{ThreadKey: 1, ThreadName: "private"}, {ThreadKey: 2}},
		LSUpsertMessage:           []*table.LSUpsertMessage{{}, {}, {}},
		LSDeleteThenInsertContact: []*table.LSDeleteThenInsertContact{{}},
	}
	got := summarize(tbl)
	if got.Threads != 2 || got.Messages != 3 || got.Contacts != 1 {
		t.Fatalf("unexpected counts: %+v", got)
	}
}

func TestSummarizeHandlesMissingTable(t *testing.T) {
	if got := summarize(nil); got != (summary{}) {
		t.Fatalf("expected zero counts, got %+v", got)
	}
}

func TestSummaryAddAccumulates(t *testing.T) {
	total := summary{Threads: 1, Messages: 2, Contacts: 3}.add(summary{Threads: 10, Messages: 20, Contacts: 30})
	if total != (summary{Threads: 11, Messages: 22, Contacts: 33}) {
		t.Fatalf("unexpected total: %+v", total)
	}
}
