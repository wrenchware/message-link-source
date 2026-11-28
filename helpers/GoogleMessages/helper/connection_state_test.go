package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"regexp"
	"strings"
	"testing"
	"time"

	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/events"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

func TestConnectionEventsAreClassifiedAsUpstreamReadsThem(t *testing.T) {
	cases := []struct {
		event  any
		state  string
		reason string
		shown  bool
	}{
		{&gmproto.RevokePairData{}, pairingLost, "revoked", true},
		{&events.GaiaLoggedOut{}, "logged_out", "marker", true},
		{&events.HackySetActiveMayFail{}, "set_active_may_fail", "", true},
		{&gmproto.UserAlertEvent{AlertType: gmproto.AlertType_BROWSER_ACTIVE}, "active", "", true},
		{&gmproto.UserAlertEvent{AlertType: gmproto.AlertType_BROWSER_INACTIVE}, "inactive", "other_client", true},
		{&gmproto.UserAlertEvent{AlertType: gmproto.AlertType_BROWSER_INACTIVE_FROM_TIMEOUT}, "inactive", "timeout", true},
		{&gmproto.UserAlertEvent{AlertType: gmproto.AlertType_BROWSER_INACTIVE_FROM_INACTIVITY}, "inactive", "inactivity", true},
		{&gmproto.UserAlertEvent{AlertType: gmproto.AlertType_PUSH_THROTTLE_STARTED}, "push_throttled", "", true},
		{&gmproto.UserAlertEvent{AlertType: gmproto.AlertType_PUSH_THROTTLE_ENDED}, "push_resumed", "", true},
		{&gmproto.UserAlertEvent{AlertType: gmproto.AlertType_MOBILE_BATTERY_LOW}, "", "", false},
		{&events.PingFailed{Error: events.ErrRequestedEntityNotFound, ErrorCount: 1}, pairingLost, "not_found", true},
		{&events.PingFailed{Error: errors.New("synthetic"), ErrorCount: 1}, "", "", false},
		{&events.PingFailed{Error: errors.New("synthetic"), ErrorCount: 2}, "ping_failed", "", true},
		// Google refusing a ping as unauthenticated is the sign-in expiring, said the first time, in the library's shapes.
		{&events.PingFailed{Error: fmt.Errorf("failed to notify ditto activity: %w", refusal(401, &events.ErrInvalidCredentials)), ErrorCount: 1}, signedOut, "ping_unauthenticated", true},
		{&events.PingFailed{Error: fmt.Errorf("failed to notify ditto activity: %w", refusal(401, nil)), ErrorCount: 1}, signedOut, "ping_unauthenticated", true},
		{&events.PingFailed{Error: fmt.Errorf("failed to set active session: %w", refusal(401, nil))}, signedOut, "ping_unauthenticated", true},
		{&events.PingFailed{Error: fmt.Errorf("failed to notify ditto activity: %w", refusal(404, &events.ErrRequestedEntityNotFound)), ErrorCount: 1}, pairingLost, "not_found", true},
		{&events.PingFailed{Error: fmt.Errorf("failed to notify ditto activity: %w", refusal(403, nil)), ErrorCount: 1}, "", "", false},
		{&events.ListenFatalError{Error: events.ErrInvalidCredentials}, signedOut, "invalid_credentials", true},
		{&events.ListenFatalError{Error: fmt.Errorf("failed to refresh auth token: %w", refusal(401, &events.ErrInvalidCredentials))}, signedOut, "invalid_credentials", true},
		{&events.ListenFatalError{Error: events.HTTPError{Action: "polling", Resp: &http.Response{StatusCode: 401}}}, signedOut, "unauthenticated", true},
		{&events.ListenFatalError{Error: fmt.Errorf("failed to refresh auth token: %w", refusal(401, nil))}, signedOut, "unauthenticated", true},
		{&events.ListenFatalError{Error: errors.New("http 401 while polling")}, signedOut, "unauthenticated", true},
		// A single 403 or a "not found" mid-session is neither a refused sign-in nor, as upstream reads it, a removed pairing.
		{&events.ListenFatalError{Error: events.HTTPError{Action: "polling", Resp: &http.Response{StatusCode: 403}}}, "listen_failed", "", true},
		{&events.ListenFatalError{Error: fmt.Errorf("failed to refresh auth token: %w", refusal(404, &events.ErrRequestedEntityNotFound))}, "listen_failed", "", true},
		{&events.ListenFatalError{Error: errors.New("synthetic")}, "listen_failed", "", true},
		// The phone refusing this pairing again and again (upstream cdbe4a4, 5994925), wrapped as the library wraps it.
		{&events.ListenFatalError{Error: fmt.Errorf("%w: %w", libgm.ErrPairingPermissionDenied, events.ErrCallerNoPermission)}, pairingLost, "permission_denied", true},
		{&events.ListenFatalError{Error: fmt.Errorf("%w: %d data events in a row failed to decrypt", libgm.ErrPairingKeysRejected, 20)}, pairingLost, "keys_rejected", true},
		{&events.AccountChange{AccountChangeOrSomethingEvent: &gmproto.AccountChangeOrSomethingEvent{Account: "person@example.com"}, IsFake: true}, "account_mode", "fake", true},
		{&events.AccountChange{AccountChangeOrSomethingEvent: &gmproto.AccountChangeOrSomethingEvent{Account: "person@example.com", Enabled: true}}, "account_mode", "enabled", true},
		{&events.AccountChange{AccountChangeOrSomethingEvent: &gmproto.AccountChangeOrSomethingEvent{Account: "person@example.com"}}, "account_mode", "disabled", true},
		{&events.AccountChange{}, "account_mode", "disabled", true},
		{&events.ListenTemporaryError{Error: errors.New("synthetic")}, "listen_interrupted", "", true},
		{&events.ListenRecovered{}, "listen_recovered", "", true},
		{&events.PhoneNotResponding{}, "phone_not_responding", "", true},
		{&events.PhoneRespondingAgain{}, "phone_responding", "", true},
		{&gmproto.Conversation{}, "", "", false},
	}
	for i, c := range cases {
		state, shown := classifyConnectionEvent(c.event)
		if shown != c.shown || state.State != c.state || state.Reason != c.reason {
			t.Fatalf("case %d: got %+v %v", i, state, shown)
		}
		if line, _ := json.Marshal(state); strings.Contains(string(line), "@") {
			t.Fatalf("case %d: the account reached the state: %s", i, line)
		}
	}
}

// A lost pairing ends the requests still waiting, refuses new ones with one code, and still answers the handshake.
// The event the app receives carries the fixed codes only.
func TestLostPairingEndsWaitingRequestsAndRefusesNewOnes(t *testing.T) {
	var written bytes.Buffer
	a := &adapter{out: &output{encoder: json.NewEncoder(&written)}}
	waiting := make(chan string, 1)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	started := make(chan struct{})
	go func() {
		// Stands in for a read the phone never answers: it returns only when its context ends.
		inner, stop := context.WithCancel(ctx)
		defer stop()
		release := a.track(stop)
		close(started)
		<-inner.Done()
		release()
		waiting <- "ended"
	}()
	<-started
	a.noteConnection(connectionState{pairingLost, "revoked"})
	select {
	case <-waiting:
	case <-time.After(2 * time.Second):
		t.Fatal("a waiting request outlived the lost pairing")
	}
	if _, code := a.serveCall(ctx, request{Method: "ui.chatList"}); code != pairingLost {
		t.Fatalf("new request after a lost pairing answered %q", code)
	}
	if _, code := a.serveCall(ctx, request{Method: "hello"}); code != "" {
		t.Fatalf("the handshake was refused: %q", code)
	}
	line := strings.TrimSpace(written.String())
	if line != `{"event":"connection","result":{"state":"pairing_lost","reason":"revoked"}}` {
		t.Fatalf("event line %s", line)
	}
}

// Why a pairing failed reaches the app as a fixed code carrying Google's own numbers, never the error text.
func TestPairingFailuresKeepGooglesReason(t *testing.T) {
	cases := []struct {
		code string
		want string
	}{
		{pairStartCode(libgm.ErrNoDevicesFound), "pair_no_devices"},
		{pairStartCode(fmt.Errorf("%w (%w)", libgm.ErrPairingInitTimeout, libgm.ErrHadMultipleDevices)), "pair_multiple_devices"},
		{pairStartCode(libgm.ErrPairingInitTimeout), "pair_phone_not_responding"},
		{pairStartCode(events.ErrCallerNoPermission), "pair_no_permission"},
		{pairStartCode(events.RequestError{Data: &gmproto.ErrorResponse{Type: 5, Message: "Synthetic words"}}), "pair_start_google_5"},
		{pairStartCode(errors.New("synthetic")), "pair_start_failed"},
		{pairFinishCode(libgm.ErrIncorrectEmoji), "pair_wrong_emoji"},
		{pairFinishCode(libgm.ErrPairingCancelled), "pair_cancelled"},
		{pairFinishCode(fmt.Errorf("%w (user chose 'this is not me' option)", libgm.ErrPairingCancelled)), "pair_cancelled"},
		// The library's own format strings (FinishGaiaPairing): a timeout carries both numbers, anything else the code's name.
		{pairFinishCode(fmt.Errorf("%w (code: %d/%d)", libgm.ErrPairingTimeout, int32(1), gmproto.GaiaPairingErrorCode_VERIFICATION_TIMED_OUT)), "pair_refused_1_6"},
		{pairFinishCode(libgm.ErrPairingTimeout), "pair_timeout"},
		{pairFinishCode(fmt.Errorf("unknown error pairing: %d/%s", int32(1), gmproto.GaiaPairingErrorCode_CLIENT_ATTESTATION_MISSING.String())), "pair_attestation_32"},
		{pairFinishCode(fmt.Errorf("unknown error pairing: %d/%s", int32(1), gmproto.GaiaPairingErrorCode_CLIENT_ATTESTATION_MISMATCH.String())), "pair_attestation_33"},
		{pairFinishCode(fmt.Errorf("unknown error pairing: %d/%s", int32(2), gmproto.GaiaPairingErrorCode_CLIENT_ATTESTATION_REVISION_MISMATCH.String())), "pair_attestation_35"},
		{pairFinishCode(fmt.Errorf("unknown error pairing: %d/%s", int32(1), gmproto.GaiaPairingErrorCode_INVALID_USER.String())), "pair_failed_code_4"},
		{pairFinishCode(fmt.Errorf("unknown error pairing: %d/%s", int32(1), gmproto.GaiaPairingErrorCode(36).String())), "pair_failed_code_36"},
		{pairFinishCode(fmt.Errorf("unknown error pairing: %d/%s", int32(1), gmproto.GaiaPairingErrorCode(123456).String())), "pair_finish_failed"},
		{pairFinishCode(errors.New("unknown error pairing: 1/NOT_A_CODE")), "pair_finish_failed"},
		{pairFinishCode(events.RequestError{Data: &gmproto.ErrorResponse{Type: 9}}), "pair_finish_google_9"},
		{pairFinishCode(fmt.Errorf("failed to send client finish: %w", errors.New("synthetic"))), "pair_finish_failed"},
		{pairFinishCode(errors.New("synthetic")), "pair_finish_failed"},
	}
	shape := regexp.MustCompile(`^pair_[a-z_]+?(_[0-9]{1,4}){0,2}$`)
	for i, c := range cases {
		if c.code != c.want {
			t.Fatalf("case %d: %q, want %q", i, c.code, c.want)
		}
		if !shape.MatchString(c.code) {
			t.Fatalf("case %d: %q is not a fixed code", i, c.code)
		}
	}
}

// The library's warnings reach the app as level and message only, and a message carrying anything but words is
// replaced rather than passed on.
func TestLibraryLogPassesOnlyWords(t *testing.T) {
	var written bytes.Buffer
	w := libraryWriter{out: &output{encoder: json.NewEncoder(&written)}}
	acks := events.RequestError{HTTP: &events.HTTPError{Resp: &http.Response{StatusCode: 401}}, Data: &gmproto.ErrorResponse{Type: 16, Message: "Request had invalid authentication credentials for 5550100"}}
	ackLine, _ := json.Marshal(map[string]any{"level": "error", "error": acks.Error(), "message_ids": []string{"synthetic-id"}, "message": "Failed to send acks, re-queued for retry"})
	for _, line := range []string{
		`{"level":"warn","message":"Failed to refresh auth token","token":"synthetic-secret"}`,
		`{"level":"error","message":"Got 3 messages from 5550100"}`,
		`{"level":"warn","message":"person@example.com"}`,
		string(ackLine),
		`{"level":"error","message":"Error making listen request","error":"dial tcp 10.0.0.1:443: http 999"}`,
		`not json`,
	} {
		w.Write([]byte(line))
	}
	got := strings.TrimSpace(written.String())
	want := `{"event":"library","result":{"level":"warn","message":"Failed to refresh auth token"}}` + "\n" +
		`{"event":"library","result":{"level":"error","message":"(other)"}}` + "\n" +
		`{"event":"library","result":{"level":"warn","message":"(other)"}}` + "\n" +
		`{"event":"library","result":{"level":"error","message":"Failed to send acks, re-queued for retry","status":401}}` + "\n" +
		`{"event":"library","result":{"level":"error","message":"Error making listen request"}}`
	if got != want || strings.Contains(got, "synthetic-secret") || strings.Contains(got, "5550100") || strings.Contains(got, "synthetic-id") {
		t.Fatalf("library lines:\n%s", got)
	}
}

// Google's status reaches the app in every shape the library logs a refusal, written here by the library's own logger;
// a refused acknowledgement also says the sign-in expired, and nothing else does.
func TestLibraryLogReadsGooglesStatus(t *testing.T) {
	var written bytes.Buffer
	var refused []string
	log := libraryLogger(&output{encoder: json.NewEncoder(&written)}, func(message string, status int) {
		refused = append(refused, fmt.Sprintf("%s %d", message, status))
	})
	unauthorized := &http.Response{StatusCode: 401}
	invalid := events.RequestError{HTTP: &events.HTTPError{Resp: unauthorized, Body: []byte("private response")}, Data: &gmproto.ErrorResponse{Type: 16, Message: "Request had invalid authentication credentials."}}
	log.Err(invalid).Strs("message_ids", []string{"synthetic-id"}).Msg("Failed to send acks, re-queued for retry")
	log.Err(events.HTTPError{Resp: unauthorized}).Strs("message_ids", []string{"synthetic-id"}).Msg("Failed to send acks, dropping as retry queue is full")
	log.Err(events.HTTPError{Resp: &http.Response{StatusCode: 403}}).Msg("Failed to send acks, re-queued for retry")
	log.Err(invalid).Uint64("ping_id", 7).Msg("Error sending ping")
	log.Error().Int("status_code", 401).Bytes("resp_body", []byte("private response")).Msg("Error making listen request")
	log.Err(fmt.Errorf("failed to refresh auth token: %w", events.HTTPError{Resp: unauthorized})).Msg("Error refreshing auth token")
	log.Warn().Err(events.HTTPError{Action: "polling", Resp: &http.Response{StatusCode: 502}}).Msg("Error in long polling")
	log.Info().Int("status_code", 401).Msg("Failed to send acks, re-queued for retry")
	got := strings.TrimSpace(written.String())
	want := `{"event":"library","result":{"level":"error","message":"Failed to send acks, re-queued for retry","status":401}}` + "\n" +
		`{"event":"library","result":{"level":"error","message":"Failed to send acks, dropping as retry queue is full","status":401}}` + "\n" +
		`{"event":"library","result":{"level":"error","message":"Failed to send acks, re-queued for retry","status":403}}` + "\n" +
		`{"event":"library","result":{"level":"error","message":"Error sending ping","status":401}}` + "\n" +
		`{"event":"library","result":{"level":"error","message":"Error making listen request","status":401}}` + "\n" +
		`{"event":"library","result":{"level":"error","message":"Error refreshing auth token","status":401}}` + "\n" +
		`{"event":"library","result":{"level":"warn","message":"Error in long polling","status":502}}`
	if got != want || strings.Contains(got, "private") || strings.Contains(got, "synthetic-id") {
		t.Fatalf("library lines:\n%s", got)
	}
	if strings.Join(refused, "\n") != "Failed to send acks, re-queued for retry 401\nFailed to send acks, dropping as retry queue is full 401" {
		t.Fatalf("refusals %q", refused)
	}
}

// The helper's own logger says a refused acknowledgement as the signed_out the app renews on, once however often the
// library retries it.
func TestRefusedAcknowledgementSignsOutOnce(t *testing.T) {
	var written bytes.Buffer
	a := &adapter{out: &output{encoder: json.NewEncoder(&written)}}
	a.init(libgm.NewAuthData())
	refused := events.RequestError{HTTP: &events.HTTPError{Resp: &http.Response{StatusCode: 401}}, Data: &gmproto.ErrorResponse{Type: 16, Message: "Request had invalid authentication credentials."}}
	for range 3 {
		a.client.Logger.Err(refused).Strs("message_ids", []string{"synthetic-id"}).Msg("Failed to send acks, re-queued for retry")
	}
	if a.refused() != signInRefused || strings.Count(written.String(), `{"event":"connection","result":{"state":"signed_out","reason":"ack_unauthenticated"}}`) != 1 {
		t.Fatalf("refusal %q, events %s", a.refused(), written.String())
	}
}

// Each refusal is said once; a removed pairing is still said after a refused sign-in, and nothing after it.
func TestRefusalIsSaidOnce(t *testing.T) {
	var written bytes.Buffer
	a := &adapter{out: &output{encoder: json.NewEncoder(&written)}}
	a.signOut("ping_unauthenticated")
	a.signOut("ack_unauthenticated")
	a.noteConnection(connectionState{pairingLost, "not_found"})
	a.noteConnection(connectionState{pairingLost, "revoked"})
	a.signOut("request_unauthenticated")
	got := strings.TrimSpace(written.String())
	want := `{"event":"connection","result":{"state":"signed_out","reason":"ping_unauthenticated"}}` + "\n" +
		`{"event":"connection","result":{"state":"pairing_lost","reason":"not_found"}}`
	if got != want || a.refused() != pairingLost {
		t.Fatalf("refusal %q, events:\n%s", a.refused(), got)
	}
}

// One of the app's own requests that Google answers 401 says the sign-in expired, once; a lifecycle call's refusal and
// any other failure do not.
func TestRequestRefusedAsUnauthenticatedSignsOut(t *testing.T) {
	var written bytes.Buffer
	a := &adapter{out: &output{encoder: json.NewEncoder(&written)}}
	answered := 0
	a.answer = func(_ context.Context, r request) (any, string) {
		answered++
		if r.Method == "ui.messages" {
			return nil, "access_denied"
		}
		return nil, signInRefused
	}
	ctx := context.Background()
	for _, method := range []string{"reauthenticate", "connect", "ui.encrypted", "ui.messages"} {
		if _, code := a.serveCall(ctx, request{Method: method}); code == "" || a.refused() != "" || written.Len() != 0 {
			t.Fatalf("%s: code %q, refusal %q, events %s", method, code, a.refused(), written.String())
		}
	}
	if _, code := a.serveCall(ctx, request{Method: "ui.chatList"}); code != signInRefused || a.refused() != signInRefused {
		t.Fatalf("a request refused as unauthenticated: code %q, refusal %q", code, a.refused())
	}
	before := answered
	if _, code := a.serveCall(ctx, request{Method: "ui.sendText"}); code != signInRefused || answered != before {
		t.Fatalf("after the refusal: code %q, asked %d more", code, answered-before)
	}
	if got := strings.TrimSpace(written.String()); got != `{"event":"connection","result":{"state":"signed_out","reason":"request_unauthenticated"}}` {
		t.Fatalf("events %s", got)
	}
}

// An expired sign-in refuses requests as "sign in again", not as a removed pairing, and a removed pairing that follows
// outranks it; the reverse order keeps the removal.
func TestRefusedSignInIsNotALostPairing(t *testing.T) {
	var written bytes.Buffer
	a := &adapter{out: &output{encoder: json.NewEncoder(&written)}}
	ctx := context.Background()
	a.noteConnection(connectionState{signedOut, "unauthenticated"})
	if _, code := a.serveCall(ctx, request{Method: "ui.chatList"}); code != signInRefused {
		t.Fatalf("after a refused sign-in: %q", code)
	}
	a.noteConnection(connectionState{pairingLost, "revoked"})
	a.noteConnection(connectionState{signedOut, "unauthenticated"})
	if _, code := a.serveCall(ctx, request{Method: "ui.chatList"}); code != pairingLost {
		t.Fatalf("a removal was downgraded to %q", code)
	}
	if !strings.Contains(written.String(), `{"state":"signed_out","reason":"unauthenticated"}`) {
		t.Fatalf("events %s", written.String())
	}
}
