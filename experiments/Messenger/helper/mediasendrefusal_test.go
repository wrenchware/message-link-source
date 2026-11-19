package main

import (
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// A failure row that names no send can be the mark-read task sent with the photo: it must not turn a photo Facebook
// confirmed into a refusal, which the app offers to send again.
func TestMediaSendRefusalNeverRefusesAConfirmedSend(t *testing.T) {
	unnamed := &table.LSTable{LSHandleFailedTask: []*table.LSHandleFailedTask{{}}}
	named := &table.LSTable{LSMarkOptimisticMessageFailed: []*table.LSMarkOptimisticMessageFailed{{OTID: "42"}}}
	other := &table.LSTable{LSHandleFailedTask: []*table.LSHandleFailedTask{{OTID: "7"}}}
	for _, tc := range []struct {
		tbl   *table.LSTable
		known bool
		want  string
	}{
		{unnamed, true, ""},
		{unnamed, false, "rejected"},
		{named, false, "rejected"},
		{named, true, "unknown"},
		{other, true, ""},
		{other, false, ""},
		{&table.LSTable{}, true, ""},
	} {
		if got := mediaSendRefusal(tc.tbl, "42", tc.known); got != tc.want {
			t.Fatalf("mediaSendRefusal(known=%v) = %q, want %q", tc.known, got, tc.want)
		}
	}
}
