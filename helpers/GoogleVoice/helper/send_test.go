package main

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

type fakeRoundTripper struct {
	calls  int
	status int
	err    error
}

func (f *fakeRoundTripper) RoundTrip(req *http.Request) (*http.Response, error) {
	f.calls++
	if f.err != nil {
		return nil, f.err
	}
	return &http.Response{StatusCode: f.status, Body: io.NopCloser(strings.NewReader("")), Request: req}, nil
}

func TestSendGuardLetsOneRequestOutAndStopsTheRetry(t *testing.T) {
	for _, c := range []struct {
		name   string
		base   *fakeRoundTripper
		stops  bool
		status int
	}{
		{"accepted", &fakeRoundTripper{status: 200}, false, 200},
		{"server error", &fakeRoundTripper{status: 503}, true, 503},
		{"network error", &fakeRoundTripper{err: errors.New("reset")}, true, 0},
		{"refused", &fakeRoundTripper{status: 400}, false, 400},
	} {
		ctx, stop := context.WithCancel(context.Background())
		attempt := &sendAttempt{stop: stop}
		guard := &sendGuard{base: c.base}
		req, _ := http.NewRequestWithContext(context.WithValue(ctx, sendAttemptKey{}, attempt), http.MethodPost, "https://clients6.google.com/x", nil)
		_, _ = guard.RoundTrip(req)
		if (ctx.Err() != nil) != c.stops {
			t.Fatalf("%s: the retry loop must be stopped only for answers libgv would retry", c.name)
		}
		if attempt.status != c.status {
			t.Fatalf("%s: status %d recorded, want %d", c.name, attempt.status, c.status)
		}
		if _, err := guard.RoundTrip(req); !errors.Is(err, errSendNotRepeated) || c.base.calls != 1 {
			t.Fatalf("%s: a second request for the same send must never leave: %v %d", c.name, err, c.base.calls)
		}
		stop()
	}
	plain := &fakeRoundTripper{status: 500}
	req, _ := http.NewRequest(http.MethodPost, "https://clients6.google.com/x", nil)
	_, _ = (&sendGuard{base: plain}).RoundTrip(req)
	_, _ = (&sendGuard{base: plain}).RoundTrip(req)
	if plain.calls != 2 {
		t.Fatal("requests that are not sends keep libgv's ordinary retries")
	}
}

func TestSendOutcomeIsCertainOnlyWhenNothingCouldHaveBeenSent(t *testing.T) {
	cases := []struct {
		err      error
		attempts int
		status   int
		failed   bool
		want     string
	}{
		{errors.New("marshal"), 0, 0, false, "send_rejected"},
		{context.Canceled, 0, 0, false, "request_cancelled"},
		{errors.New("unauthorized"), 1, 401, false, "auth_required"},
		{errors.New("bad request"), 1, 400, false, "send_rejected"},
		{errors.New("server"), 1, 503, false, "send_failed"},
		{errors.New("reset"), 1, 0, true, "send_failed"},
		{errors.New("unreadable answer"), 1, 200, false, "send_failed"},
		{nil, 1, 200, false, "send_failed"},
	}
	for _, c := range cases {
		if got := sendOutcome(c.err, c.attempts, c.status, c.failed); got != c.want {
			t.Fatalf("%v/%d/%d/%v: got %q want %q", c.err, c.attempts, c.status, c.failed, got, c.want)
		}
	}
}

func TestSendTextSendsOnceUnsignedAndNeverLogsTheText(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	send := func(params string) (any, string) {
		return a.callContext(context.Background(), request{Method: "ui.sendText", Params: json.RawMessage(params)})
	}
	if _, code := send(`{"chatId":"t1","text":"synthetic"}`); code != "login_required" {
		t.Fatalf("sending needs a signed-in client, got %q", code)
	}
	a.client = libgv.NewClient(map[string]string{"SID": "synthetic"})
	var sent []*gvproto.ReqSendSMS
	a.sendSMS = func(ctx context.Context, _ *libgv.Client, req *gvproto.ReqSendSMS) (*gvproto.RespSendSMS, error) {
		sent = append(sent, req)
		attempt := ctx.Value(sendAttemptKey{}).(*sendAttempt)
		attempt.attempts, attempt.status = 1, 200
		return &gvproto.RespSendSMS{ThreadItemID: "m9", TimestampMS: 5000}, nil
	}
	for _, params := range []string{`{"chatId":"t1","text":"   "}`, `{"chatId":"","text":"synthetic"}`, `{"chatId":"t1","text":"` + strings.Repeat("a", maxTextBytes+1) + `"}`} {
		if _, code := send(params); code != "invalid_input" || len(sent) != 0 {
			t.Fatalf("an empty, unaddressed or oversized text must be refused before anything is sent: %q", code)
		}
	}
	result, code := send(`{"chatId":"t1","text":"synthetic"}`)
	if code != "" || result.(map[string]any)["id"] != "m9" || len(sent) != 1 || sent[0].GetThreadID() != "t1" || sent[0].GetText() != "synthetic" ||
		sent[0].GetTransactionID().GetID() == 0 || sent[0].GetTrackingData() != nil {
		t.Fatalf("expected one unsigned send with a transaction id: %v %q %+v", result, code, sent)
	}
	a.sendSMS = func(ctx context.Context, _ *libgv.Client, _ *gvproto.ReqSendSMS) (*gvproto.RespSendSMS, error) {
		attempt := ctx.Value(sendAttemptKey{}).(*sendAttempt)
		attempt.attempts, attempt.failed = 1, true
		return nil, errors.New("reset")
	}
	if _, code := send(`{"chatId":"t1","text":"synthetic"}`); code != "send_failed" {
		t.Fatalf("a send lost in transport is unknown, not refused: %q", code)
	}
	for _, line := range sink.lines {
		if strings.Contains(line, "synthetic") {
			t.Fatalf("the text must never be logged: %v", line)
		}
	}
	if !mutationMethod("ui.sendText") {
		t.Fatal("sends run in order with other account changes")
	}
	if login := libgv.NewClient(nil); login.HTTP.Transport != nil {
		t.Fatal("expected libgv's client to start with the default transport")
	}
}

func TestFirstMessageToANewNumberGoesToTheNumber(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	a.client = libgv.NewClient(map[string]string{"SID": "synthetic"})
	send := func(chat string) (any, string) {
		return a.callContext(context.Background(), request{Method: "ui.sendText", Params: json.RawMessage(`{"chatId":"` + chat + `","text":"synthetic"}`)})
	}
	var sent []*gvproto.ReqSendSMS
	refuseThreads := true
	a.sendSMS = func(ctx context.Context, _ *libgv.Client, req *gvproto.ReqSendSMS) (*gvproto.RespSendSMS, error) {
		sent = append(sent, req)
		attempt := ctx.Value(sendAttemptKey{}).(*sendAttempt)
		attempt.attempts = 1
		if refuseThreads && req.GetThreadID() != "" {
			attempt.status = 400
			return nil, &libgv.ResponseError{Resp: &http.Response{StatusCode: 400}, Body: []byte(`[3,"INVALID_ARGUMENT"]`)}
		}
		attempt.status = 200
		return &gvproto.RespSendSMS{ThreadItemID: "m1", TimestampMS: 1}, nil
	}

	// Known unstarted (its thread answered not found): one request, to the number, with no thread id.
	a.setUnstarted("t.+15550100", true)
	if _, code := send("t.+15550100"); code != "" || len(sent) != 1 || sent[0].GetThreadID() != "" ||
		len(sent[0].GetRecipients()) != 1 || sent[0].GetRecipients()[0] != "+15550100" || sent[0].GetText() != "synthetic" {
		t.Fatalf("an unstarted conversation's first message goes to its number: %q %+v", code, sent)
	}
	if a.isUnstarted("t.+15550100") {
		t.Fatal("once sent, the conversation exists")
	}

	// Not known: the thread id first, and on Google's refusal once more to the number.
	sent = nil
	if _, code := send("t.+15550101"); code != "" || len(sent) != 2 || sent[0].GetThreadID() != "t.+15550101" || sent[1].GetThreadID() != "" ||
		sent[1].GetRecipients()[0] != "+15550101" || sent[0].GetTransactionID().GetID() == sent[1].GetTransactionID().GetID() {
		t.Fatalf("a refused first try goes once more to the number: %q %+v", code, sent)
	}

	// Anything but a one-to-one text conversation is never tried a second way.
	sent = nil
	if _, code := send("g.group"); code != "send_rejected" || len(sent) != 1 {
		t.Fatalf("a refused group send is not tried again: %q %d", code, len(sent))
	}

	// Refused both ways: the second refusal stands, and Google's word for it is logged.
	sent = nil
	a.sendSMS = func(ctx context.Context, _ *libgv.Client, req *gvproto.ReqSendSMS) (*gvproto.RespSendSMS, error) {
		sent = append(sent, req)
		attempt := ctx.Value(sendAttemptKey{}).(*sendAttempt)
		attempt.attempts, attempt.status = 1, 400
		return nil, &libgv.ResponseError{Resp: &http.Response{StatusCode: 400}, Body: []byte(`{"error":{"code":400,"status":"FAILED_PRECONDITION"}}`)}
	}
	if _, code := send("t.+15550102"); code != "send_rejected" || len(sent) != 2 {
		t.Fatalf("refused both ways is refused: %q %d", code, len(sent))
	}
	joined := strings.Join(sink.lines, "\n")
	if !strings.Contains(joined, `"reason":"FAILED_PRECONDITION"`) || !strings.Contains(joined, `"shape":"recipients"`) || strings.Contains(joined, "synthetic") ||
		strings.Contains(joined, "15550102") {
		t.Fatalf("the refusal is named by Google's word alone: %s", joined)
	}
	if refusalReason(errors.New("plain")) != "none" || refusalReason(&libgv.ResponseError{Resp: &http.Response{StatusCode: 400}, Body: []byte("x")}) != "unnamed" {
		t.Fatal("a refusal without Google's word says so")
	}
	tooMany := &libgv.ResponseError{Resp: &http.Response{StatusCode: 400}, VoiceError: &gvproto.VoiceError{Code: gvproto.VoiceError_TOO_MANY_MESSAGES}}
	if reason := refusalReason(tooMany); reason != "voice_too_many_messages" {
		t.Fatalf("a voice_error refusal is named by its code: %s", reason)
	}
}
