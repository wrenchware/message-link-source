package main

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/util/exhttp"
)

func watchAdapter(t *testing.T, cookies map[string]string) (*adapter, *bytes.Buffer) {
	t.Helper()
	var written bytes.Buffer
	a := &adapter{out: &output{encoder: json.NewEncoder(&written)}}
	auth := libgm.NewAuthData()
	auth.SetCookies(cookies)
	a.client = libgm.NewClient(auth, nil, zerolog.Nop(), exhttp.SensibleClientSettings)
	return a, &written
}

// The lines the app receives, but the session: that one carries the values, and goes only to the app to save.
func outcomeLines(written *bytes.Buffer) []string {
	var lines []string
	for _, line := range strings.Split(strings.TrimSpace(written.String()), "\n") {
		if line != "" && !strings.Contains(line, `"event":"session"`) {
			lines = append(lines, line)
		}
	}
	return lines
}

// Each look names the cookies the library merged from its own answers since the last one: added, changed, removed (an
// emptied cookie is a removed one), never a value, and never a name outside the plain alphabet. The session goes to the
// app to save only when something changed, and nothing at all is said before the first look.
func TestLibraryMergedCookiesAreNamed(t *testing.T) {
	a, written := watchAdapter(t, map[string]string{"__Secure-1PSID": "p", "__Secure-1PSIDTS": "old", "SIDCC": "c", "NID": "n", "OTZ": "o", "odd name": "x"})
	auth := a.client.AuthData
	a.watchCookies()
	if written.Len() != 0 {
		t.Fatalf("said something before the first look: %s", written.String())
	}
	a.lookAtCookies(auth)
	auth.CookiesLock.Lock()
	auth.Cookies["__Secure-1PSIDTS"] = "merged-sidts-value"
	auth.Cookies["AEC"] = "added-value"
	auth.Cookies["OTZ"] = ""
	delete(auth.Cookies, "NID")
	auth.Cookies["odd name"] = "changed-value"
	auth.Cookies["other odd"] = "added-value"
	auth.CookiesLock.Unlock()
	a.watchCookies()
	lines := outcomeLines(written)
	want := `{"event":"cookies","result":{"merged":{"added":["AEC"],"changed":["__Secure-1PSIDTS"],"removed":["NID","OTZ"]}}}`
	if strings.Join(lines, "\n") != want || !strings.Contains(written.String(), `"event":"session"`) {
		t.Fatalf("events:\n%s", written.String())
	}
	if all := strings.Join(lines, "\n"); strings.Contains(all, "value") || strings.Contains(all, "odd") {
		t.Fatalf("a value or an odd name reached an event: %s", all)
	}
	written.Reset()
	a.watchCookies()
	if written.Len() != 0 {
		t.Fatalf("an unchanged look said something: %s", written.String())
	}
}
