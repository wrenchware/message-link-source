package main

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

type authUserCapture struct{ values []string }

func (c *authUserCapture) RoundTrip(req *http.Request) (*http.Response, error) {
	c.values = append(c.values, req.Header.Get("X-Goog-AuthUser"))
	return &http.Response{StatusCode: http.StatusUnauthorized, Body: io.NopCloser(strings.NewReader("")), Request: req}, nil
}

func refused() error {
	return &libgv.ResponseError{Resp: &http.Response{StatusCode: http.StatusUnauthorized}}
}

func TestNewSessionClientPreservesDefaultAndExplicitAuthUser(t *testing.T) {
	for _, test := range []struct{ supplied, want string }{{"", "0"}, {"1", "1"}} {
		client := newSessionClient(map[string]string{"SID": "synthetic", "SAPISID": "synthetic"}, test.supplied)
		guard, ok := client.HTTP.Transport.(*sendGuard)
		if !ok {
			t.Fatal("session clients must retain the exactly-once send guard")
		}
		capture := &authUserCapture{}
		guard.base = capture
		_, err := client.GetAccount(context.Background())
		if !libgv.IsAuthError(err) || len(capture.values) != 1 || capture.values[0] != test.want {
			t.Fatalf("supplied %q sent account selector %v with error %v, want %q", test.supplied, capture.values, err, test.want)
		}
	}
}

// RotateCookies never renewed the sign-in (it changed only SIDCC and its siblings) and was retired in 0.1.309: a refused
// saved session is tried once more without its short-lived cookies and then left to the app's browser profile.
func TestRefusedSignInTriesOnlyWithoutShortLivedCookies(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	var tried []string
	a.getAccount = func(_ context.Context, client *libgv.Client) (*gvproto.RespGetAccount, error) {
		tried = append(tried, client.GetCookies()["__Secure-1PSIDTS"])
		return nil, refused()
	}
	_, _, _, err := a.login(context.Background(), map[string]string{"SID": "s", "__Secure-1PSID": "p", "__Secure-1PSIDTS": "stale"})
	if !libgv.IsAuthError(err) || len(tried) != 2 || tried[0] != "stale" || tried[1] != "" {
		t.Fatalf("expected the saved session, then the same without short-lived cookies, and nothing else: %v %v", err, tried)
	}
	if lines := strings.Join(sink.lines, "\n"); strings.Contains(lines, "rotate") || strings.Contains(lines, "stale") {
		t.Fatalf("no rotation may be attempted or any value reported: %v", sink.lines)
	}
}

func TestReplacedClientKeepsTheAccountAndHandsTheAppTheRenewedSession(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	old := libgv.NewClient(map[string]string{"SID": "s", "__Secure-1PSIDTS": "old"})
	old.AuthUser = "1"
	a.client = old
	a.replaceClient(map[string]string{"SID": "s", "__Secure-1PSIDTS": "fresh"})
	current := a.current()
	if current == old || current.AuthUser != "1" || current.GetCookies()["__Secure-1PSIDTS"] != "fresh" {
		t.Fatal("expected a new client with the renewed cookies for the same account")
	}
	if _, ok := current.HTTP.Transport.(*sendGuard); !ok {
		t.Fatal("the replaced client must keep sends to a single attempt")
	}
	saved := false
	for _, line := range sink.lines {
		if strings.Contains(line, `"event":"cookies"`) && strings.Contains(line, "fresh") {
			saved = true
		}
		if strings.Contains(line, `"diagnostic"`) && strings.Contains(line, "fresh") {
			t.Fatalf("diagnostics must not carry cookie values: %v", line)
		}
	}
	if !saved {
		t.Fatalf("the app must be handed the renewed session to save: %v", sink.lines)
	}
}

func TestRuntimeReadRecoversOnceWithoutShortLivedCookies(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	old := newSessionClient(map[string]string{"SID": "synthetic", "__Secure-1PSID": "synthetic", "__Secure-1PSIDTS": "stale", "SIDCC": "stale"}, "")
	if generation := a.installClient(old); generation != 1 {
		t.Fatalf("first client generation = %d, want 1", generation)
	}
	accounts := 0
	a.getAccount = func(_ context.Context, client *libgv.Client) (*gvproto.RespGetAccount, error) {
		accounts++
		if client.GetCookies()["__Secure-1PSIDTS"] != "" || client.GetCookies()["SIDCC"] != "" {
			return nil, refused()
		}
		return &gvproto.RespGetAccount{}, nil
	}
	reads := 0
	value, err := runtimeRead(a, context.Background(), old, func(_ context.Context, client *libgv.Client) (string, error) {
		reads++
		if client == old {
			return "", refused()
		}
		return "recovered", nil
	})
	if err != nil || value != "recovered" || reads != 2 || accounts != 1 {
		t.Fatalf("read recovery = %q, %v; reads=%d accounts=%d", value, err, reads, accounts)
	}
	if current := a.current(); current == old || current.GetCookies()["__Secure-1PSIDTS"] != "" || current.GetCookies()["SIDCC"] != "" {
		t.Fatal("only the validated, short-lived-cookie-free client may become current")
	}
	lines := strings.Join(sink.lines, "\n")
	if !strings.Contains(lines, `"generation":1`) || !strings.Contains(lines, `"recovery":"without_short_lived"`) {
		t.Fatalf("recovery must replace with a generated cookie event: %v", sink.lines)
	}
	if strings.Contains(strings.Join(diagnosticLines(sink.lines), "\n"), "stale") {
		t.Fatalf("recovery diagnostics must not include cookie values: %v", sink.lines)
	}
}

func TestRuntimeReadGivesUpWhenGoogleRefusesTheSessionItself(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	old := newSessionClient(map[string]string{"SID": "synthetic", "__Secure-1PSIDTS": "stale"}, "")
	a.installClient(old)
	accounts := 0
	a.getAccount = func(context.Context, *libgv.Client) (*gvproto.RespGetAccount, error) {
		accounts++
		return nil, refused()
	}
	reads := 0
	_, err := runtimeRead(a, context.Background(), old, func(context.Context, *libgv.Client) (bool, error) {
		reads++
		return false, refused()
	})
	if !libgv.IsAuthError(err) || reads != 1 || accounts != 1 || a.current() != old {
		t.Fatalf("a refused recovery must leave the client and report the refusal: %v reads=%d accounts=%d", err, reads, accounts)
	}
}

func TestStaleClientEventsAreIgnoredAndCurrentCookieEventHasGeneration(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	old := newSessionClient(map[string]string{"SID": "old"}, "")
	a.installClient(old)
	a.replaceClient(map[string]string{"SID": "current"})
	sink.lines = nil
	a.handleEvent(1, context.Background(), &libgv.CookieChanged{Cookies: map[string]string{"SID": "stale"}})
	a.handleEvent(2, context.Background(), &libgv.CookieChanged{Cookies: map[string]string{"SID": "current"}})
	if len(sink.lines) != 1 || !strings.Contains(sink.lines[0], `"generation":2`) || strings.Contains(sink.lines[0], "stale") {
		t.Fatalf("only current cookie event with generation belongs on the pipe: %v", sink.lines)
	}
}

// A photo Google will not serve says nothing about the account, as a voicemail's recording and a contact's picture do
// not: the session is neither recovered nor replaced on its account, and the photo is unavailable, not a sign-in error.
func TestRefusedPhotoLeavesTheSessionAlone(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	old := newSessionClient(map[string]string{"SID": "synthetic", "__Secure-1PSID": "synthetic", "__Secure-1PSIDTS": "stale"}, "")
	a.installClient(old)
	checks := 0
	a.getAccount = func(_ context.Context, client *libgv.Client) (*gvproto.RespGetAccount, error) {
		checks++
		return &gvproto.RespGetAccount{}, nil
	}
	calls := 0
	a.downloadAttachment = func(_ context.Context, client *libgv.Client, _ string) ([]byte, string, error) {
		calls++
		return nil, "", refused()
	}
	result, code := a.callContext(context.Background(), request{Method: "ui.mediaRead", Params: json.RawMessage(`{"id":"attachment","offset":0}`)})
	if code != "media_unavailable" || result != nil || calls != 1 || checks != 0 || a.current() != old || old.GetCookies()["__Secure-1PSIDTS"] != "stale" {
		t.Fatalf("a refused photo = %v %q calls=%d checks=%d replaced=%v", result, code, calls, checks, a.current() != old)
	}
}

func TestStaleRealtimeEventsHaveNoSideEffects(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	a.installClient(newSessionClient(map[string]string{"SID": "old"}, ""))
	a.replaceClient(map[string]string{"SID": "current"})
	sink.lines = nil
	a.handleEvent(1, context.Background(), &libgv.RealtimeConnected{})
	a.handleEvent(1, context.Background(), &libgv.RealtimeEvent{})
	if len(sink.lines) != 0 || len(a.refresh) != 0 {
		t.Fatalf("stale realtime events must neither report connected nor wake refresh: %v", sink.lines)
	}
}

// Without the nine-minute rotation restarting realtime, failures far apart must not add up to a final disconnect.
func TestRealtimeFailuresCountOnlyInARow(t *testing.T) {
	count := 0
	for range maxTransientRetries + 3 {
		count = realtimeFailures(count, 3*time.Hour)
	}
	if count != 1 {
		t.Fatalf("a channel that ran for hours before each failure is one failure in a row, got %d", count)
	}
	count = 0
	for range maxTransientRetries + 1 {
		count = realtimeFailures(count, 2*time.Second)
	}
	if count <= maxTransientRetries || realtimeFailures(count, realtimeStable) != 1 {
		t.Fatalf("failures in quick succession must still reach the final limit, and a stable run must start again: %d", count)
	}
}

func diagnosticLines(lines []string) []string {
	var diagnostics []string
	for _, line := range lines {
		if strings.Contains(line, `"event":"diagnostic"`) {
			diagnostics = append(diagnostics, line)
		}
	}
	return diagnostics
}
