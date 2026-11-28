package main

import (
	"context"
	"encoding/json"
	"strings"
	"testing"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

const replaceNumber = "+15550009"

func renewedSignIn(timestamp string) map[string]string {
	return map[string]string{"SID": "s", "HSID": "h", "SSID": "ss", "APISID": "a", "SAPISID": "sa", "__Secure-1PSID": "p",
		"__Secure-1PSIDTS": timestamp, "__Secure-3PSIDTS": timestamp, "OSID": "o"}
}

// An adapter signed in as replaceNumber whose account read accepts only the cookies accept says it does.
func replaceAdapter(t *testing.T, accept func(map[string]string) (string, bool)) (*adapter, *libgv.Client, *capture, *int) {
	t.Helper()
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	t.Cleanup(a.stop)
	old := newSessionClient(renewedSignIn("old"), "1")
	a.installClient(old)
	a.number = replaceNumber
	reads := 0
	a.getAccount = func(_ context.Context, client *libgv.Client) (*gvproto.RespGetAccount, error) {
		reads++
		number, ok := accept(client.GetCookies())
		if !ok {
			return nil, refused()
		}
		return &gvproto.RespGetAccount{Account: &gvproto.Account{PrimaryDestinationID: number}}, nil
	}
	return a, old, sink, &reads
}

func replaceCall(a *adapter, ctx context.Context, cookies map[string]string) (any, string) {
	params, _ := json.Marshal(map[string]any{"cookies": cookies})
	return a.callContext(ctx, request{Method: "cookies.replace", Params: params})
}

func TestReplaceCookiesTakesAValidatedSignInInPlace(t *testing.T) {
	a, old, sink, reads := replaceAdapter(t, func(cookies map[string]string) (string, bool) {
		return replaceNumber, cookies["__Secure-1PSIDTS"] == "fresh"
	})
	result, code := replaceCall(a, context.Background(), renewedSignIn("fresh"))
	current := a.current()
	if code != "" || result == nil || *reads != 1 || current == old || current.AuthUser != "1" || current.GetCookies()["__Secure-1PSIDTS"] != "fresh" {
		t.Fatalf("a validated renewal must become current for the same account selector: %v %q reads=%d", result, code, *reads)
	}
	if _, ok := current.HTTP.Transport.(*sendGuard); !ok {
		t.Fatal("the renewed client must keep sends to a single attempt")
	}
	lines := strings.Join(sink.lines, "\n")
	if !strings.Contains(lines, `"event":"cookies"`) || !strings.Contains(lines, `"generation":1`) || !strings.Contains(lines, `"replace":"adopted"`) {
		t.Fatalf("the app must be handed the renewed sign-in to save, with its generation: %v", sink.lines)
	}
	if strings.Contains(strings.Join(diagnosticLines(sink.lines), "\n"), "fresh") {
		t.Fatalf("diagnostics must not carry cookie values: %v", sink.lines)
	}
	// A read refused on the old client after the hand-over retries on the new one without another recovery.
	value, err := runtimeRead(a, context.Background(), old, func(_ context.Context, client *libgv.Client) (string, error) {
		if client == old {
			return "", refused()
		}
		return "read", nil
	})
	if err != nil || value != "read" || *reads != 1 {
		t.Fatalf("a read refused on the replaced client must use the renewed one: %q %v reads=%d", value, err, *reads)
	}
}

func TestReplaceCookiesKeepsTheCurrentSignInUnlessGoogleAcceptsTheSameAccount(t *testing.T) {
	withoutTimestamp := renewedSignIn("fresh")
	delete(withoutTimestamp, "__Secure-1PSIDTS")
	for _, test := range []struct {
		name    string
		cookies map[string]string
		accept  func(map[string]string) (string, bool)
		want    string
		reads   int
	}{
		{"refused", renewedSignIn("fresh"), func(map[string]string) (string, bool) { return "", false }, "auth_required", 1},
		{"another account", renewedSignIn("fresh"), func(map[string]string) (string, bool) { return "+15550010", true }, "wrong_account", 1},
		{"no timestamp", withoutTimestamp, func(map[string]string) (string, bool) { return replaceNumber, true }, "invalid_input", 0},
		{"no session", map[string]string{"__Secure-1PSIDTS": "fresh"}, func(map[string]string) (string, bool) { return replaceNumber, true }, "invalid_input", 0},
	} {
		a, old, sink, reads := replaceAdapter(t, test.accept)
		_, code := replaceCall(a, context.Background(), test.cookies)
		if code != test.want || *reads != test.reads || a.current() != old {
			t.Fatalf("%s: code %q reads %d, want %q %d and the current client kept", test.name, code, *reads, test.want, test.reads)
		}
		if lines := strings.Join(sink.lines, "\n"); strings.Contains(lines, `"event":"cookies"`) || strings.Contains(lines, "fresh") {
			t.Fatalf("%s: nothing may be handed to the app or reported by value: %v", test.name, sink.lines)
		}
	}
}

func TestReplaceCookiesTheAppStoppedWaitingForChangesNothing(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	a, old, sink, _ := replaceAdapter(t, func(map[string]string) (string, bool) {
		cancel()
		return replaceNumber, true
	})
	_, code := replaceCall(a, ctx, renewedSignIn("fresh"))
	if code != "request_cancelled" || a.current() != old || strings.Contains(strings.Join(sink.lines, "\n"), `"event":"cookies"`) {
		t.Fatalf("an abandoned hand-over must leave the current sign-in: %q %v", code, sink.lines)
	}
}

func TestReplaceCookiesNeedsASignedInHelper(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	if _, code := replaceCall(a, context.Background(), renewedSignIn("fresh")); code != "login_required" {
		t.Fatalf("a helper with no sign-in has nothing to replace: %q", code)
	}
}
