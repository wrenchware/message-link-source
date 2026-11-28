package main

import (
	"context"
	"encoding/json"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// Reauthenticate an existing pairing using user-provided cookies, as upstream
// connector/login.go SubmitCookies does. Never create or replace a pairing.
func (a *adapter) reauthenticate(ctx context.Context, r request) (any, string) {
	if a.client != nil {
		return nil, "session_already_started"
	}
	var p struct {
		Auth    libgm.AuthData    `json:"auth"`
		Cookies map[string]string `json:"cookies"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.Auth.Mobile == nil || p.Auth.Browser == nil {
		return nil, "invalid_session"
	}
	for _, name := range []string{"SID", "HSID", "SSID", "OSID", "APISID", "SAPISID"} {
		if p.Cookies[name] == "" {
			return nil, "missing_cookies"
		}
	}
	p.Auth.SetCookies(p.Cookies)
	a.init(&p.Auth)
	defer a.client.Disconnect()
	if err := a.client.FetchConfig(ctx); err != nil {
		return nil, readErrorCode(err)
	}
	if code := renewedAccountCode(p.Auth.Mobile.GetSourceID(), a.client.Config.GetDeviceInfo().GetEmail()); code != "" {
		return nil, code
	}
	if err := a.client.Connect(context.Background()); err != nil {
		// A pairing Google no longer knows cannot be renewed; the app then offers pairing as a new device.
		return nil, refreshFailureCode(err)
	}
	if _, err := a.listConversations(ctx, 1, gmproto.ListConversationsRequest_INBOX); err != nil {
		return nil, readErrorCode(err)
	}
	a.client.Disconnect()
	return sessionAnswer(a.client.AuthData)
}

// Whether the configuration page speaks for the paired account. One that names no account means Google did not take the
// sign-in at all, as at connect (configSignedOut), which is the refusal the renewal dialog already explains; another
// account is the wrong one.
func renewedAccountCode(expected, email string) string {
	switch {
	case email == "":
		return signInRefused
	case expected == "" || email != expected:
		return "wrong_account"
	}
	return ""
}
