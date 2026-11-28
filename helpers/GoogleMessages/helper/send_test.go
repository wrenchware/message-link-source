package main

import (
	"context"
	"errors"
	"fmt"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/events"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"net/http"
	"net/url"
	"testing"
)

func TestSubmitNeverRetriesAndKeepsUncertainIdentity(t *testing.T) {
	refused := func(status int) error {
		return events.RequestError{HTTP: &events.HTTPError{Resp: &http.Response{StatusCode: status}}}
	}
	for _, tc := range []struct {
		status  gmproto.SendMessageResponse_Status
		err     error
		outcome string
		code    string
		class   string
	}{
		{gmproto.SendMessageResponse_SUCCESS, nil, "pending", "", ""},
		{gmproto.SendMessageResponse_FAILURE_4, nil, "rejected", "", ""},
		// A refusal with no reason (a photo, 2026-11-19): its status is named for the app's log.
		{gmproto.SendMessageResponse_FAILURE_2, nil, "rejected", "", ""},
		{gmproto.SendMessageResponse_UNKNOWN, nil, "pending", "", ""},
		// The phone not answering may still send later; its echo resolves the transaction.
		{gmproto.SendMessageResponse_UNKNOWN, fmt.Errorf("%w in 30s", libgm.ErrPhoneNotResponding), "pending", "", "phone_unavailable"},
		{gmproto.SendMessageResponse_UNKNOWN, context.DeadlineExceeded, "pending", "", "phone_unavailable"},
		// The same wait as the library words it since upstream d07c0a5 (session_handler.go, sendMessageWithParams).
		{gmproto.SendMessageResponse_UNKNOWN, fmt.Errorf("%w: %w", libgm.ErrNoServerResponse, context.DeadlineExceeded), "pending", "", "phone_unavailable"},
		// The deadline during the post itself: it may or may not have reached the relay.
		{gmproto.SendMessageResponse_UNKNOWN, &url.Error{Op: "Post", URL: "relay", Err: context.DeadlineExceeded}, "unknown", "", "phone_unavailable"},
		// The post written and then no answer (sendAsyncMessageWithID since d07c0a5), a deadline among its causes.
		{gmproto.SendMessageResponse_UNKNOWN, fmt.Errorf("%w: %w", libgm.ErrNoServerResponse, &url.Error{Op: "Post", URL: "relay", Err: context.DeadlineExceeded}), "unknown", "", "phone_unavailable"},
		// Shutting down while waiting for the phone: unknown, as before.
		{gmproto.SendMessageResponse_UNKNOWN, fmt.Errorf("%w: %w", libgm.ErrNoServerResponse, context.Canceled), "unknown", "", "history_failed"},
		// Anything else may or may not have been passed on: reported neither as sent nor as not sent.
		{gmproto.SendMessageResponse_UNKNOWN, errors.New("synthetic private upstream error"), "unknown", "", "history_failed"},
		{gmproto.SendMessageResponse_UNKNOWN, libgm.ErrConnectionClosed, "unknown", "", "connection_closed"},
		{gmproto.SendMessageResponse_UNKNOWN, events.HTTPError{Resp: &http.Response{StatusCode: 502}}, "unknown", "", "remote_unavailable"},
		// The relay refusing it means the phone never saw it.
		{gmproto.SendMessageResponse_UNKNOWN, refused(400), "", "send_rejected", ""},
		{gmproto.SendMessageResponse_UNKNOWN, refused(401), "", "authentication_required", ""},
		{gmproto.SendMessageResponse_UNKNOWN, events.HTTPError{Resp: &http.Response{StatusCode: 429}}, "", "rate_limited", ""},
	} {
		calls := 0
		result, code := submitText(context.Background(), &gmproto.SendMessageRequest{TmpID: "txn"}, func(context.Context, *gmproto.SendMessageRequest) (*gmproto.SendMessageResponse, error) {
			calls++
			return &gmproto.SendMessageResponse{Status: tc.status}, tc.err
		})
		if calls != 1 || code != tc.code {
			t.Fatalf("unsafe code or retry for %v: %q", tc.err, code)
		}
		if tc.code != "" {
			if result != nil {
				t.Fatal("a refused send also answered")
			}
			continue
		}
		// Only a refusal (its status; the phone naming it adds its code and reason), or an error's class, carries more than the
		// outcome and the transaction.
		want := 2
		if tc.outcome == "rejected" {
			want = 3
		}
		if tc.status == gmproto.SendMessageResponse_FAILURE_4 {
			want = 5
		}
		if tc.class != "" {
			want = 3
		}
		if tc.outcome == "rejected" && result["status"] != tc.status.String() {
			t.Fatalf("refusal status not named: %v", result)
		}
		if result["outcome"] != tc.outcome || result["pendingId"] != "txn" || len(result) != want || (tc.class != "" && result["error"] != tc.class) {
			t.Fatalf("unsafe outcome for %v: %v", tc.err, result)
		}
	}
}

func TestNotDefaultSMSRefusalIsNamed(t *testing.T) {
	send := func(status gmproto.SendMessageResponse_Status) map[string]any {
		result, _ := submitText(context.Background(), &gmproto.SendMessageRequest{TmpID: "txn"}, func(context.Context, *gmproto.SendMessageRequest) (*gmproto.SendMessageResponse, error) {
			return &gmproto.SendMessageResponse{Status: status}, nil
		})
		return result
	}
	named := send(gmproto.SendMessageResponse_FAILURE_4)
	if named["outcome"] != "rejected" || named["code"] != "not_default_sms_app" || named["reason"] == "" {
		t.Fatal("the default-SMS refusal was not named")
	}
	for _, status := range []gmproto.SendMessageResponse_Status{gmproto.SendMessageResponse_FAILURE_2, gmproto.SendMessageResponse_FAILURE_3, gmproto.SendMessageResponse_SUCCESS} {
		if _, ok := send(status)["code"]; ok {
			t.Fatalf("status %v was named as the default-SMS refusal", status)
		}
	}
}

func TestTextRequestRoutingAndContent(t *testing.T) {
	c := &gmproto.Conversation{ConversationID: "chat", DefaultOutgoingID: "sim-participant"}
	sim := &gmproto.SIMCard{SIMParticipant: &gmproto.SIMParticipant{ID: "sim-participant"}, SIMData: &gmproto.SIMData{SIMPayload: &gmproto.SIMPayload{}}}
	body := "A photo?\nNo, just text 😊"
	r, reason := textRequest(c, sim, body, "txn")
	if reason != "" || r == nil {
		t.Fatal("valid text rejected")
	}
	if r.ConversationID != "chat" || r.TmpID != "txn" || r.MessagePayload.TmpID != "txn" || r.MessagePayload.TmpID2 != "txn" || r.MessagePayload.ParticipantID != "sim-participant" {
		t.Fatal("identity mismatch")
	}
	if r.MessagePayload.MessageInfo[0].GetMessageContent().Content != body || r.ForceRCS || r.SIMPayload != sim.SIMData.SIMPayload {
		t.Fatal("text or routing changed")
	}
	for _, body := range []string{"", " \n\t"} {
		if r, _ := textRequest(c, sim, body, "txn"); r != nil {
			t.Fatal("empty text accepted")
		}
	}
	c.ReadOnly = true
	if r, _ := textRequest(c, sim, "text", "txn"); r != nil {
		t.Fatal("read-only accepted")
	}
	c.ReadOnly = false
	if r, _ := textRequest(c, nil, "text", "txn"); r != nil {
		t.Fatal("missing SIM accepted")
	}
	sim.SIMParticipant.ID = "other-sim"
	if r, _ := textRequest(c, sim, "text", "txn"); r != nil {
		t.Fatal("wrong SIM accepted")
	}
}
