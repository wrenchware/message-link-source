package main

import (
	"errors"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/events"
)

// Why a saved session did not connect, as the code the app reads. Upstream's connector (pkg/connector/client.go Connect)
// reads the same two errors from Connect: "requested entity was not found" means the pairing is gone (it clears the
// pairing), "invalid credentials" means only the Google-account sign-in is (it clears the cookies and keeps the pairing).
// Until now a gone pairing came back here as remote_unavailable (HTTP 404), and a refused sign-in as a bare
// authentication_required the app did not treat as the expired sign-in it is: on 2026-09-24 a launch after six hours
// closed was refused that way, and the app asked the user to pair again instead of renewing the sign-in as it does when
// the same refusal comes mid-session. A refused sign-in is now also said as the signed_out state a refused poll reports,
// naming the step Google refused it at, so the app renews the sign-in from its browser profile as it does then, and its
// log says which request Google turned down. The helper's own refusal is left unset: this helper holds no client any
// more, and nothing waits on it.
func (a *adapter) connectFailed(step string, err error) string {
	if step == "refresh" {
		if code := refreshFailureCode(err); code == pairingLost {
			return code
		}
	}
	code := readErrorCode(err)
	if code == signInRefused || errors.Is(err, events.ErrInvalidCredentials) {
		a.out.write(response{Event: "connection", Result: connectionState{signedOut, "connect_" + step}})
		return signInRefused
	}
	return code
}

// A Google-account pairing whose configuration page names no account: Google answered, but not for a signed-in user.
// Upstream reads this as the sign-in being gone (GMLoggedOutNoEmailInConfig in pkg/connector/client.go Connect) and
// drops the cookies while keeping the pairing, before it asks for anything else; the helper says it as the signed_out a
// refused configuration page is, so the app renews the sign-in the same way. The caller drops the client.
func (a *adapter) configSignedOut() bool {
	if !a.client.AuthData.IsGoogleAccount() || a.client.Config.GetDeviceInfo().GetEmail() != "" {
		return false
	}
	a.out.write(response{Event: "connection", Result: connectionState{signedOut, "connect_config_no_email"}})
	return true
}

// Connect's own request (the auth-token refresh) answered "not found": Google no longer knows this computer's pairing.
// Everything else keeps its read code.
func refreshFailureCode(err error) string {
	if errors.Is(err, events.ErrRequestedEntityNotFound) {
		return pairingLost
	}
	return readErrorCode(err)
}
