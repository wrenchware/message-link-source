package main

import (
	"context"
	"encoding/json"
	"errors"
	"maps"
	"strings"
	"testing"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

func replaceRequest(t *testing.T, cookies map[string]string) request {
	t.Helper()
	params, err := json.Marshal(map[string]any{"cookies": cookies})
	if err != nil {
		t.Fatal(err)
	}
	return request{ID: 1, Method: "cookies.replace", Params: params}
}

func freshCookies() map[string]string {
	return map[string]string{"SID": "new-sid", "HSID": "h", "SSID": "s", "OSID": "o", "APISID": "a", "SAPISID": "sa", "__Secure-1PSIDTS": "new-sidts"}
}

func pairedAdapter(t *testing.T) *adapter {
	t.Helper()
	a, _ := watchAdapter(t, map[string]string{"SID": "old-sid", "__Secure-1PSIDTS": "old-sidts", "SIDCC": "c"})
	a.client.AuthData.Mobile = &gmproto.Device{SourceID: "person@example.com"}
	return a
}

// Cookies the configuration page names the paired account for go into the running session as that answer left them
// (a new OSID, say), the session goes to the app to save, and the cookie watch starts afresh from them, so the next look
// does not report the app's own replacement as something the library merged. Nothing is stopped or reconnected.
func TestReplacedCookiesAreUsedAndSaved(t *testing.T) {
	a, written := watchAdapter(t, map[string]string{"SID": "old-sid", "__Secure-1PSIDTS": "old-sidts", "SIDCC": "c"})
	a.client.AuthData.Mobile = &gmproto.Device{SourceID: "person@example.com"}
	a.lookAtCookies(a.client.AuthData)
	var asked map[string]string
	a.checkCookies = func(_ context.Context, cookies map[string]string) (string, map[string]string, error) {
		asked = cookies
		after := maps.Clone(cookies)
		after["OSID"] = "osid-from-the-answer"
		return "person@example.com", after, nil
	}
	result, code := a.serveCall(context.Background(), replaceRequest(t, freshCookies()))
	if code != "" || result == nil {
		t.Fatalf("replace answered %v %q", result, code)
	}
	if asked["SID"] != "new-sid" {
		t.Fatalf("checked %v", asked)
	}
	cookies := a.client.AuthData.Cookies
	if cookies["SID"] != "new-sid" || cookies["__Secure-1PSIDTS"] != "new-sidts" || cookies["OSID"] != "osid-from-the-answer" || cookies["SIDCC"] != "" {
		t.Fatalf("session holds %v", cookies)
	}
	if !strings.Contains(written.String(), `"event":"session"`) {
		t.Fatalf("session not handed over: %s", written.String())
	}
	written.Reset()
	a.watchCookies()
	if written.Len() != 0 {
		t.Fatalf("the watch reported the replacement as a merge: %s", written.String())
	}
}

// Nothing is swapped unless Google speaks for the paired account: cookies it did not take, another account's, an
// incomplete set, or a check that could not be made all leave the cookies in use as they were. None of them is a refusal
// of the running helper's own sign-in, so none latches one or says signed_out.
func TestCookiesNotReplacedUnlessGoogleNamesThePairedAccount(t *testing.T) {
	cases := []struct {
		name    string
		cookies map[string]string
		email   string
		err     error
		want    string
	}{
		{"refused", freshCookies(), "", nil, signInRefused},
		{"other account", freshCookies(), "someone@example.com", nil, "wrong_account"},
		{"incomplete", map[string]string{"SID": "new-sid"}, "person@example.com", nil, "missing_cookies"},
		{"unreachable", freshCookies(), "", errors.New("offline"), "history_failed"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			a, written := watchAdapter(t, map[string]string{"SID": "old-sid"})
			a.client.AuthData.Mobile = &gmproto.Device{SourceID: "person@example.com"}
			a.checkCookies = func(_ context.Context, cookies map[string]string) (string, map[string]string, error) {
				return c.email, cookies, c.err
			}
			_, code := a.serveCall(context.Background(), replaceRequest(t, c.cookies))
			if code != c.want {
				t.Fatalf("code %q, want %q", code, c.want)
			}
			if a.client.AuthData.Cookies["SID"] != "old-sid" || written.Len() != 0 || a.refused() != "" {
				t.Fatalf("changed the helper: %v %q %s", a.client.AuthData.Cookies, a.refused(), written.String())
			}
		})
	}
	a := &adapter{}
	if _, code := a.callContext(context.Background(), replaceRequest(t, freshCookies())); code != "pairing_required" {
		t.Fatalf("no client answered %q", code)
	}
}

// A helper refused before the call answers with the refusal without asking Google; one refused while the check ran
// (the look that fetched the cookies may have cost the ones in use) takes nothing either; and a call the app gave up on
// changes nothing. In each case the renewal after the refusal is what takes over.
func TestRefusedOrAbandonedReplacementChangesNothing(t *testing.T) {
	a := pairedAdapter(t)
	a.checkCookies = func(context.Context, map[string]string) (string, map[string]string, error) {
		t.Fatal("asked Google for a refused helper")
		return "", nil, nil
	}
	a.refusal = signInRefused
	if _, code := a.serveCall(context.Background(), replaceRequest(t, freshCookies())); code != signInRefused {
		t.Fatalf("refused helper answered %q", code)
	}

	during, _ := watchAdapter(t, map[string]string{"SID": "old-sid"})
	during.client.AuthData.Mobile = &gmproto.Device{SourceID: "person@example.com"}
	during.checkCookies = func(_ context.Context, cookies map[string]string) (string, map[string]string, error) {
		during.signOut("ack_unauthenticated")
		return "person@example.com", cookies, nil
	}
	if result, code := during.serveCall(context.Background(), replaceRequest(t, freshCookies())); code != signInRefused || result != nil {
		t.Fatalf("refused during the check answered %v %q", result, code)
	}
	if during.client.AuthData.Cookies["SID"] != "old-sid" {
		t.Fatal("took cookies after a refusal")
	}

	abandoned, _ := watchAdapter(t, map[string]string{"SID": "old-sid"})
	abandoned.client.AuthData.Mobile = &gmproto.Device{SourceID: "person@example.com"}
	ctx, cancel := context.WithCancel(context.Background())
	abandoned.checkCookies = func(_ context.Context, cookies map[string]string) (string, map[string]string, error) {
		cancel()
		return "person@example.com", cookies, nil
	}
	if _, code := abandoned.callContext(ctx, replaceRequest(t, freshCookies())); code != "request_cancelled" {
		t.Fatalf("abandoned call answered %q", code)
	}
	if abandoned.client.AuthData.Cookies["SID"] != "old-sid" {
		t.Fatal("took cookies for a call the app gave up on")
	}
}
