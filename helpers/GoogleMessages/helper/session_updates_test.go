package main

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"strconv"
	"sync"
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"go.mau.fi/util/exhttp"
)

// The session handed back by snapshot is the JSON the live session encoded to before, taken under the cookie lock while
// the library goes on writing the cookie map as its answers arrive.
func TestSnapshotIsTheSameSessionTakenUnderTheLock(t *testing.T) {
	auth := libgm.NewAuthData()
	auth.SetCookies(map[string]string{"SID": "s", "__Secure-1PSIDTS": "t"})
	auth.Browser = &gmproto.Device{UserID: 1, SourceID: "synthetic-browser"}
	auth.Mobile = &gmproto.Device{UserID: 2, SourceID: "synthetic-mobile"}
	auth.DestRegID[0] = 1
	a := &adapter{out: &output{encoder: json.NewEncoder(&bytes.Buffer{})}}
	a.client = libgm.NewClient(auth, nil, zerolog.Nop(), exhttp.SensibleClientSettings)
	var before, after bytes.Buffer
	if err := json.NewEncoder(&before).Encode(response{ID: 1, Result: auth}); err != nil {
		t.Fatal(err)
	}
	stop := make(chan struct{})
	var library sync.WaitGroup
	library.Add(1)
	go func() {
		// What AuthData.UpdateCookiesFromResponse does with every answer's Set-Cookie.
		defer library.Done()
		for i := 0; ; i++ {
			select {
			case <-stop:
				return
			default:
			}
			auth.CookiesLock.Lock()
			auth.Cookies["NID"+strconv.Itoa(i%8)] = "n"
			auth.CookiesLock.Unlock()
		}
	}()
	for range 200 {
		result, code := a.callContext(context.Background(), request{Method: "snapshot"})
		if code != "" {
			t.Fatalf("snapshot answered %q", code)
		}
		// As the dispatcher writes the answer, while the library writes on.
		if err := json.NewEncoder(io.Discard).Encode(response{ID: 1, Result: result}); err != nil {
			t.Fatal(err)
		}
	}
	close(stop)
	library.Wait()
	auth.CookiesLock.Lock()
	for i := range 8 {
		delete(auth.Cookies, "NID"+strconv.Itoa(i))
	}
	auth.CookiesLock.Unlock()
	result, code := a.callContext(context.Background(), request{Method: "snapshot"})
	if code != "" {
		t.Fatalf("snapshot answered %q", code)
	}
	if err := json.NewEncoder(&after).Encode(response{ID: 1, Result: result}); err != nil {
		t.Fatal(err)
	}
	if before.String() != after.String() {
		t.Fatalf("the session changed shape:\n%s\n%s", before.String(), after.String())
	}
}

// A renewal whose configuration page names no account was not taken by Google at all; another account is the wrong one.
func TestRenewalTellsARefusedSignInFromTheWrongAccount(t *testing.T) {
	for _, c := range []struct {
		expected, email, code string
	}{
		{"person@example.com", "", signInRefused},
		{"", "", signInRefused},
		{"person@example.com", "other@example.com", "wrong_account"},
		{"", "person@example.com", "wrong_account"},
		{"person@example.com", "person@example.com", ""},
	} {
		if got := renewedAccountCode(c.expected, c.email); got != c.code {
			t.Fatalf("%q against %q: %q, want %q", c.email, c.expected, got, c.code)
		}
	}
}
