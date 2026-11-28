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
)

func TestDeleteChatAsksOnceForTheThreadAndReportsWhatGoogleSaid(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	remove := func(params string) (any, string) {
		return a.callContext(context.Background(), request{Method: "ui.deleteChat", Params: json.RawMessage(params)})
	}
	if _, code := remove(`{"chatId":"t.+15550100001"}`); code != "login_required" {
		t.Fatalf("deleting needs a signed-in client, got %q", code)
	}
	a.client = libgv.NewClient(map[string]string{"SID": "synthetic"})
	var asked []string
	answer := func(attempts, status int, failed bool, err error) {
		a.removeThread = func(ctx context.Context, _ *libgv.Client, id string) error {
			asked = append(asked, id)
			attempt := ctx.Value(sendAttemptKey{}).(*sendAttempt)
			attempt.attempts, attempt.status, attempt.failed = attempts, status, failed
			return err
		}
	}
	answer(1, 200, false, nil)
	for _, params := range []string{`{"chatId":""}`, `{}`, `{"chatId":"` + strings.Repeat("t", 201) + `"}`, `not json`} {
		if _, code := remove(params); code != "invalid_input" || len(asked) != 0 {
			t.Fatalf("a request that doesn't name one conversation must be refused before anything is sent: %q", code)
		}
	}
	a.lastSeen["t.+15550100001"], a.lastRead["t.+15550100001"] = 5, true
	result, code := remove(`{"chatId":"t.+15550100001"}`)
	if code != "" || result.(map[string]any)["outcome"] != "accepted" || len(asked) != 1 || asked[0] != "t.+15550100001" {
		t.Fatalf("expected one delete of the thread as named: %v %q %v", result, code, asked)
	}
	if _, watched := a.lastSeen["t.+15550100001"]; watched {
		t.Fatal("a deleted conversation must be forgotten by change detection, so a later message announces it afresh")
	}
	for _, c := range []struct {
		name     string
		attempts int
		status   int
		failed   bool
		err      error
		want     string
	}{
		{"refused", 1, 400, false, errors.New("bad request"), "delete_rejected"},
		{"not found", 1, 404, false, errors.New("not found"), "delete_rejected"},
		{"signed out", 1, 401, false, errors.New("unauthorized"), "auth_required"},
		{"server error", 1, 503, false, errors.New("server"), "delete_failed"},
		{"lost in transport", 1, 0, true, errors.New("reset"), "delete_failed"},
		{"unreadable answer", 1, 200, false, errors.New("decode"), "delete_failed"},
		{"never left", 0, 0, false, errors.New("marshal"), "delete_rejected"},
		{"cancelled first", 0, 0, false, context.Canceled, "request_cancelled"},
	} {
		asked = nil
		answer(c.attempts, c.status, c.failed, c.err)
		if _, code := remove(`{"chatId":"t.+15550100001"}`); code != c.want || len(asked) != 1 {
			t.Fatalf("%s: got %q after %d requests, want %q after one", c.name, code, len(asked), c.want)
		}
	}
	for _, line := range sink.lines {
		if strings.Contains(line, "555") {
			t.Fatalf("a conversation's number must never be logged: %v", line)
		}
	}
	if !mutationMethod("ui.deleteChat") {
		t.Fatal("deletes run in order with the account's other changes")
	}
}

// libgv repeats a request after a server error; for a delete the guard lets the first out and stops the rest.
func TestDeleteIsNeverRepeatedByTheLibrary(t *testing.T) {
	calls := 0
	base := roundTripFunc(func(req *http.Request) (*http.Response, error) {
		calls++
		return &http.Response{StatusCode: 503, Body: io.NopCloser(strings.NewReader("")), Request: req, Header: http.Header{}}, nil
	})
	a := newAdapter(&output{encoder: json.NewEncoder(&capture{})})
	defer a.stop()
	client := newSessionClient(map[string]string{"SID": "synthetic"}, "")
	client.HTTP.Transport = &sendGuard{base: base}
	a.client = client
	_, code := a.callContext(context.Background(), request{Method: "ui.deleteChat", Params: json.RawMessage(`{"chatId":"t.+15550100001"}`)})
	if code != "delete_failed" || calls != 1 {
		t.Fatalf("a delete answered with a server error is unknown and asked once: %q after %d", code, calls)
	}
}

type roundTripFunc func(*http.Request) (*http.Response, error)

func (f roundTripFunc) RoundTrip(req *http.Request) (*http.Response, error) { return f(req) }
