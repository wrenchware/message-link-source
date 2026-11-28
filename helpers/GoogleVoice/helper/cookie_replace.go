package main

import (
	"context"
	"encoding/json"
	"net/http"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
)

// What a renewed sign-in must carry to be taken: the Google-account session libgv signs its requests with, and the
// timestamp cookie whose renewal is the point of handing it over.
var replaceRequired = []string{"SID", "HSID", "SSID", "APISID", "SAPISID", "__Secure-1PSID", "__Secure-1PSIDTS"}

// cookies.replace takes a sign-in the app renewed from its kept browser profile, before Google's two hours ran out, in
// place of the current one: no new helper and no new login, and realtime reconnects once. The cookies are checked first
// with one account read on a client of their own, which Google must accept for the same Voice number; only then do
// they become current, through replaceCurrent, which hands them to the app to save. A call the app stopped waiting for
// before the check finished changes nothing. Only the outcome is reported, never anything of the cookies.
func (a *adapter) replaceCookies(ctx context.Context, params json.RawMessage) (any, string) {
	var p struct {
		Cookies map[string]string `json:"cookies"`
	}
	if json.Unmarshal(params, &p) != nil {
		return nil, "invalid_input"
	}
	for _, name := range replaceRequired {
		if p.Cookies[name] == "" {
			a.diagnose(map[string]any{"replace": "missing_cookies"})
			return nil, "invalid_input"
		}
	}
	a.recoveryMu.Lock()
	defer a.recoveryMu.Unlock()
	current := a.current()
	if current == nil {
		return nil, "login_required"
	}
	a.mu.Lock()
	number := a.number
	a.mu.Unlock()
	candidate := newSessionClient(p.Cookies, current.AuthUser)
	account, err := a.account(ctx, candidate)
	if err != nil {
		outcome := "failed"
		if libgv.IsAuthError(err) {
			outcome = "refused"
		}
		a.diagnose(map[string]any{"replace": outcome, "status": statusOf(err)})
		return nil, authCode(err)
	}
	if number == "" || account.GetAccount().GetPrimaryDestinationID() != number {
		a.diagnose(map[string]any{"replace": "wrong_account"})
		return nil, "wrong_account"
	}
	if ctx.Err() != nil {
		return nil, "request_cancelled"
	}
	if !a.replaceCurrent(current, candidate) {
		// Only a new login changes the client outside recoveryMu; what was checked no longer applies.
		return nil, "state_expired"
	}
	a.diagnose(map[string]any{"replace": "adopted", "status": http.StatusOK})
	return map[string]any{"replaced": true}, ""
}
