package main

import (
	"context"
	"errors"
	"net/http"
	"strings"
	"time"

	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/events"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// What the library says about the pairing and the phone, passed on to the app as fixed codes; never the library's
// error text. Upstream (pkg/connector/handlegmessages.go) reads the same events: a pairing the phone revoked, a ping
// Google answers "not found" mean the saved pairing is dead, and only pairing again brings the account back. A poll
// Google rejects as unauthenticated, or its "invalid credentials", is not that: upstream drops only the Google-account
// cookies there and keeps the pairing (invalidateSession(…, false, true)), because it is the sign-in that expired. The
// helper said pairing_lost for it until 0.1.285, and on 2026-09-23 that told the user to pair again while the phone
// still listed this computer, and hid the renewal that fixes it. The phone's own silence, and its saying this computer
// is inactive, are only states.
//
// Google's logout marker is reported apart, as logged_out, and is not treated as proof here: a maintainer reports that
// Google pushes it when the phone's registration rotates, roughly daily (mautrix/gmessages#73), and before upstream's
// c1b622f a failed pairing's marker could be replayed to a later session (#52). The app reconnects on it, and only a
// second marker, or one of the signals above, makes it say the pairing is gone.
//
// Before this the helper dropped every one of them. A pairing removed on the phone reached the app as requests that
// never answered, and it retried each for a minute at a time, for as long as it ran.
type connectionState struct {
	State  string `json:"state"`
	Reason string `json:"reason,omitempty"`
}

const pairingLost = "pairing_lost"

// The Google-account sign-in this computer uses stopped being accepted; the pairing stands. Requests are refused with
// the code the app already reads as "sign in again".
const signedOut, signInRefused = "signed_out", "authentication_required"

// The state an event reports, or false for events that say nothing about the connection. A first failed ping is
// ignored, as upstream ignores it, unless Google refused it as unauthenticated; a second is worth saying.
func classifyConnectionEvent(evt any) (connectionState, bool) {
	switch e := evt.(type) {
	case *gmproto.RevokePairData:
		return connectionState{pairingLost, "revoked"}, true
	case *events.GaiaLoggedOut:
		return connectionState{"logged_out", "marker"}, true
	case *events.HackySetActiveMayFail:
		return connectionState{"set_active_may_fail", ""}, true
	case *gmproto.UserAlertEvent:
		// Upstream keeps these as the connection's state (handleUserAlert); the rest of the alerts say nothing about it.
		switch e.GetAlertType() {
		case gmproto.AlertType_BROWSER_ACTIVE:
			return connectionState{"active", ""}, true
		case gmproto.AlertType_BROWSER_INACTIVE:
			return connectionState{"inactive", "other_client"}, true
		case gmproto.AlertType_BROWSER_INACTIVE_FROM_TIMEOUT:
			return connectionState{"inactive", "timeout"}, true
		case gmproto.AlertType_BROWSER_INACTIVE_FROM_INACTIVITY:
			return connectionState{"inactive", "inactivity"}, true
		case gmproto.AlertType_PUSH_THROTTLE_STARTED:
			return connectionState{"push_throttled", ""}, true
		case gmproto.AlertType_PUSH_THROTTLE_ENDED:
			return connectionState{"push_resumed", ""}, true
		}
	case *events.PingFailed:
		if errors.Is(e.Error, events.ErrRequestedEntityNotFound) {
			return connectionState{pairingLost, "not_found"}, true
		}
		// The first request of this session's that Google refuses as unauthenticated is the sign-in expiring, and said at
		// once: the app renews it within seconds, where the poll reopening could say it three to nineteen minutes later.
		if unauthenticated(e.Error) {
			return connectionState{signedOut, "ping_unauthenticated"}, true
		}
		if e.ErrorCount > 1 {
			return connectionState{"ping_failed", ""}, true
		}
	case *events.ListenFatalError:
		// Upstream reads only these two as the sign-in; a 403 or a "not found" here is neither, and it does not treat them
		// as a removed pairing either. The app reconnects on listen_failed.
		switch {
		// Since upstream cdbe4a4 and 5994925 the library says so when the phone keeps refusing this pairing: three relay
		// posts in a row refused with 403, or twenty pushes in a row from this pairing that fail their HMAC. The library
		// keeps listening, and every reconnect meets the same refusal, so a reconnect only loops. Upstream asks for a
		// re-link; here it is a lost pairing, which the person pairs again by hand (owner, 2026-11-26).
		case errors.Is(e.Error, libgm.ErrPairingPermissionDenied):
			return connectionState{pairingLost, "permission_denied"}, true
		case errors.Is(e.Error, libgm.ErrPairingKeysRejected):
			return connectionState{pairingLost, "keys_rejected"}, true
		case errors.Is(e.Error, events.ErrInvalidCredentials):
			return connectionState{signedOut, "invalid_credentials"}, true
		case unauthenticated(e.Error), e.Error != nil && e.Error.Error() == "http 401 while polling":
			return connectionState{signedOut, "unauthenticated"}, true
		}
		return connectionState{"listen_failed", ""}, true
	case *events.AccountChange:
		// Which kind of pairing the phone says it is using; never the account it names.
		switch {
		case e.IsFake:
			return connectionState{"account_mode", "fake"}, true
		case e.GetEnabled():
			return connectionState{"account_mode", "enabled"}, true
		}
		return connectionState{"account_mode", "disabled"}, true
	case *events.ListenTemporaryError:
		return connectionState{"listen_interrupted", ""}, true
	case *events.ListenRecovered:
		return connectionState{"listen_recovered", ""}, true
	case *events.PhoneNotResponding:
		return connectionState{"phone_not_responding", ""}, true
	case *events.PhoneRespondingAgain:
		return connectionState{"phone_responding", ""}, true
	}
	return connectionState{}, false
}

// Google refused a request as unauthenticated: its "invalid credentials", or a bare 401. A RequestError unwraps to the
// HTTPError it came with, so the status is found through either.
func unauthenticated(err error) bool {
	if errors.Is(err, events.ErrInvalidCredentials) {
		return true
	}
	var httpErr events.HTTPError
	return errors.As(err, &httpErr) && httpErr.Resp != nil && httpErr.Resp.StatusCode == http.StatusUnauthorized
}

// Reports a state, and on a lost pairing or a refused sign-in ends every request still waiting for an answer that will
// not come, so a read does not wait out its whole deadline for nothing. Each is said once: after a refused sign-in every
// request Google turns down says it again (acknowledgements every five seconds), and the app is already renewing it. A
// removed pairing is still said after a refused sign-in, and outranks it.
func (a *adapter) noteConnection(state connectionState) {
	switch state.State {
	case "active":
		a.setActive(true)
	case "inactive":
		a.setActive(false)
	case "set_active_may_fail":
		go a.retrySetActive()
	}
	if refusal := map[string]string{pairingLost: pairingLost, signedOut: signInRefused}[state.State]; refusal != "" {
		// Said before anything is cancelled, and under the lock every request reads the refusal through, so the app
		// hears the state before any answer the refusal decides: it then shows "renewing" rather than "disconnected".
		a.inflightMu.Lock()
		defer a.inflightMu.Unlock()
		said := a.refusal == refusal || a.refusal == pairingLost
		if a.refusal != pairingLost {
			a.refusal = refusal // A removed pairing outranks an expired sign-in: renewing cannot bring it back.
		}
		if !said {
			a.out.write(response{Event: "connection", Result: state})
		}
		for _, cancel := range a.inflight {
			cancel()
		}
		return
	}
	a.out.write(response{Event: "connection", Result: state})
}

// Google refused this session's sign-in; the step names the request it refused.
func (a *adapter) signOut(reason string) {
	a.noteConnection(connectionState{signedOut, reason})
}

func (a *adapter) setActive(active bool) {
	a.inflightMu.Lock()
	a.active = active
	a.inflightMu.Unlock()
}

// The library warns that becoming the active session may not have taken, when events were still queued at connect.
// Upstream (hackyResetActive) waits seven seconds and, if this computer still has not been made active, asks again;
// without it the helper can sit connected and hear nothing.
func (a *adapter) retrySetActive() {
	time.Sleep(7 * time.Second)
	a.inflightMu.Lock()
	client, active, refused := a.client, a.active, a.refusal != ""
	a.inflightMu.Unlock()
	if client == nil || active || refused {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	state := connectionState{"set_active_retried", "ok"}
	if client.SetActiveSession(ctx) != nil {
		state.Reason = "failed"
	}
	a.out.write(response{Event: "connection", Result: state})
}

// Registers a request's cancel so a lost pairing or refused sign-in can end it. The release removes it again.
func (a *adapter) track(cancel context.CancelFunc) (release func()) {
	a.inflightMu.Lock()
	defer a.inflightMu.Unlock()
	if a.inflight == nil {
		a.inflight = map[int]context.CancelFunc{}
	}
	a.nextCall++
	id := a.nextCall
	a.inflight[id] = cancel
	return func() {
		a.inflightMu.Lock()
		delete(a.inflight, id)
		a.inflightMu.Unlock()
	}
}

// Why every request is refused now, or "" while none is.
func (a *adapter) refused() string {
	a.inflightMu.Lock()
	defer a.inflightMu.Unlock()
	return a.refusal
}

// Every request comes through here, so a lost pairing or refused sign-in can end those still waiting and refuse the rest
// at once, with one code the app recognises instead of whatever each read happened to fail with.
func (a *adapter) serveCall(ctx context.Context, r request) (any, string) {
	if refusal := a.refused(); refusal != "" && !answersWithoutPairing(r.Method) {
		return nil, refusal
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	defer a.track(cancel)()
	call := a.callContext
	if a.answer != nil {
		call = a.answer
	}
	result, code := call(ctx, r)
	// Google answered 401 to one of the app's own requests: the sign-in has expired as surely as when a ping is refused.
	// Connect and renewal say their own refusals.
	if code == signInRefused && strings.HasPrefix(r.Method, "ui.") && !lifecycleMethod(r.Method) && a.refused() == "" {
		a.signOut("request_unauthenticated")
	}
	if refusal := a.refused(); code != "" && refusal != "" && !answersWithoutPairing(r.Method) {
		return nil, refusal
	}
	return result, code
}

// Methods that still mean something once the pairing is gone: the handshake, and handing back the session so the app
// can keep it.
func answersWithoutPairing(method string) bool {
	switch method {
	case "hello", "snapshot", "session", "disconnect":
		return true
	}
	return false
}
