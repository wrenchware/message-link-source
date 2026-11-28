package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/events"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"go.mau.fi/util/exhttp"
)

func refusal(status int, data *events.RequestError) error {
	httpErr := events.HTTPError{Resp: &http.Response{StatusCode: status}, Body: []byte("private response")}
	if data == nil {
		return httpErr
	}
	return events.RequestError{Data: data.Data, HTTP: &httpErr}
}

// A saved session Google refuses at connect says which: a gone pairing is pairing_lost, a refused sign-in is
// authentication_required plus the signed_out state that renews it, and anything else keeps its read code and says no state.
func TestConnectFailureTellsSignInFromPairing(t *testing.T) {
	for _, c := range []struct {
		step  string
		err   error
		code  string
		state string
	}{
		{"config", fmt.Errorf("failed to fetch config: %w", refusal(401, nil)), "authentication_required", `{"event":"connection","result":{"state":"signed_out","reason":"connect_config"}}`},
		{"refresh", fmt.Errorf("failed to refresh auth token: %w", refusal(401, &events.ErrInvalidCredentials)), "authentication_required", `{"event":"connection","result":{"state":"signed_out","reason":"connect_refresh"}}`},
		{"refresh", fmt.Errorf("failed to refresh auth token: %w", refusal(404, &events.ErrRequestedEntityNotFound)), "pairing_lost", ""},
		// "Not found" from the configuration page is not the pairing's answer: only the refresh speaks for the pairing.
		{"config", fmt.Errorf("failed to fetch config: %w", refusal(404, &events.ErrRequestedEntityNotFound)), "remote_unavailable", ""},
		{"config", fmt.Errorf("failed to fetch config: %w", refusal(404, nil)), "remote_unavailable", ""},
		{"refresh", fmt.Errorf("failed to refresh auth token: %w", refusal(403, nil)), "access_denied", ""},
		{"config", fmt.Errorf("failed to fetch config: %w", refusal(429, nil)), "rate_limited", ""},
		{"refresh", errors.New("private upstream message"), "history_failed", ""},
	} {
		var written bytes.Buffer
		a := &adapter{out: &output{encoder: json.NewEncoder(&written)}}
		if got := a.connectFailed(c.step, c.err); got != c.code {
			t.Fatalf("%s %v: code %s", c.step, c.err, got)
		}
		if got := strings.TrimSpace(written.String()); got != c.state {
			t.Fatalf("%s %v: events %s", c.step, c.err, got)
		}
		if strings.Contains(written.String(), "private") {
			t.Fatal("Google's answer reached the event")
		}
		// The helper holds no client after a failed connect; its own refusal stays unset so a later connect is not refused.
		if a.refused() != "" {
			t.Fatalf("%s: helper refusal %q", c.code, a.refused())
		}
	}
}

// A Google-account pairing whose configuration page names no account is a refused sign-in, said as upstream reads it
// and before any refresh is asked for; a page that names one, or a pairing made some other way, is not.
func TestConfigWithoutAccountIsARefusedSignIn(t *testing.T) {
	for _, c := range []struct {
		google  bool
		email   string
		refused bool
	}{
		{true, "", true},
		{true, "person@example.com", false},
		{false, "", false},
	} {
		var written bytes.Buffer
		a := &adapter{out: &output{encoder: json.NewEncoder(&written)}}
		auth := libgm.NewAuthData()
		if c.google {
			auth.DestRegID[0] = 1
		}
		a.client = libgm.NewClient(auth, nil, zerolog.Nop(), exhttp.SensibleClientSettings)
		a.client.Config = &gmproto.Config{DeviceInfo: &gmproto.Config_DeviceInfo{Email: c.email}}
		want := ""
		if c.refused {
			want = `{"event":"connection","result":{"state":"signed_out","reason":"connect_config_no_email"}}`
		}
		if got := a.configSignedOut(); got != c.refused || strings.TrimSpace(written.String()) != want {
			t.Fatalf("%+v: %v, events %s", c, got, written.String())
		}
		// As with every refusal at connect, the helper's own stays unset: the caller drops the client.
		if a.refused() != "" {
			t.Fatalf("%+v: helper refusal %q", c, a.refused())
		}
	}
}

// Renewing the sign-in meets the same refresh: a pairing Google no longer knows says so, and a refused sign-in stays the
// code the renewal dialog already explains. Renewal writes no event: nothing reads that connection's events.
func TestRenewalRefreshTellsLostPairing(t *testing.T) {
	for _, c := range []struct {
		err  error
		code string
	}{
		{fmt.Errorf("failed to refresh auth token: %w", refusal(404, &events.ErrRequestedEntityNotFound)), "pairing_lost"},
		{fmt.Errorf("failed to refresh auth token: %w", refusal(401, &events.ErrInvalidCredentials)), "authentication_required"},
		{fmt.Errorf("failed to refresh auth token: %w", refusal(500, nil)), "remote_unavailable"},
	} {
		if got := refreshFailureCode(c.err); got != c.code {
			t.Fatalf("%v: code %s", c.err, got)
		}
	}
}
