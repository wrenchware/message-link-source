package main

import (
	"bytes"
	"encoding/json"
	"testing"

	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

func TestReplayedConfirmationIsForwardedWithoutNewMessageAlert(t *testing.T) {
	for _, old := range []bool{false, true} {
		var wire bytes.Buffer
		a := adapter{out: &output{encoder: json.NewEncoder(&wire)}}
		a.publishMessage(&libgm.WrappedMessage{IsOld: old, Message: &gmproto.Message{
			MessageID: "confirmed", ConversationID: "chat", TmpID: "txn",
			MessageStatus: &gmproto.MessageStatus{Status: gmproto.MessageStatusType_OUTGOING_COMPLETE},
		}})
		var got struct {
			Event  string      `json:"event"`
			Result viewMessage `json:"result"`
		}
		if err := json.Unmarshal(wire.Bytes(), &got); err != nil {
			t.Fatal(err)
		}
		expected := "message"
		if old {
			expected = "messageUpdate"
		}
		if got.Event != expected || got.Result.TmpID != "txn" || got.Result.ID != "confirmed" || got.Result.Status != "OUTGOING_COMPLETE" {
			t.Fatal("confirmation lost or replay treated as a new message")
		}
	}
}
