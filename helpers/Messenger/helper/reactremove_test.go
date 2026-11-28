package main

import (
	"context"
	"encoding/json"
	"io"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	waTypes "go.mau.fi/whatsmeow/types"
)

// A removal names the reaction it takes away. Asked for after a different reaction of this account replaced it (an
// older view of the message), it changes nothing and says so: Facebook's removal clears whatever the account has, and
// it used to clear the newer reaction. A removal of the reaction the account has now still goes ahead.
func TestStaleReactionRemovalLeavesTheNewerReaction(t *testing.T) {
	for _, encrypted := range []bool{false, true} {
		a := newAdapter(&output{encoder: json.NewEncoder(io.Discard)})
		a.chats.setSelf(1)
		if encrypted {
			a.chats.encryptedIdentity(7, 555, waTypes.MessengerServer)
			a.chats.addEncrypted(7, "m", 42, "synthetic", "", 1000)
		} else {
			a.chats.apply(&table.LSTable{
				LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 7, ThreadType: table.ONE_TO_ONE}},
				LSInsertMessage:          []*table.LSInsertMessage{{ThreadKey: 7, MessageId: "m", SenderId: 42, TimestampMs: 1000, Text: "synthetic"}},
			})
		}
		a.chats.react(7, "m", 1, "❤️")
		result, code := a.reactTo(context.Background(), 7, "m", "👍", true)
		answer, _ := result.(map[string]any)
		if code != "" || answer["outcome"] != "accepted" || answer["changed"] != false {
			t.Fatalf("encrypted=%v: a stale removal answered %v %q", encrypted, result, code)
		}
		if held := a.chats.reactionBy("m", 1); held != "❤️" {
			t.Fatalf("encrypted=%v: a stale removal left %q", encrypted, held)
		}
		// Nothing connected here, so the removal of the current reaction stops at the connection: it was not skipped.
		want := "not_connected"
		if encrypted {
			want = "e2ee_off"
		}
		if _, code := a.reactTo(context.Background(), 7, "m", "❤️", true); code != want {
			t.Fatalf("encrypted=%v: the removal of the current reaction answered %q", encrypted, code)
		}
		if _, code := a.reactTo(context.Background(), 7, "m", "", true); code != "invalid_input" {
			t.Fatalf("encrypted=%v: a removal naming no reaction answered %q", encrypted, code)
		}
		a.stop()
	}
}
