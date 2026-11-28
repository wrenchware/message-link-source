package main

import (
	"context"
	"encoding/json"
	"maps"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/util/exhttp"
)

// How long the check of new cookies may take. The app waits longer than this, so it always hears whether they were taken.
const replaceCheckBudget = 40 * time.Second

// Fresh Google-account cookies for the running connection, from the app's kept browser profile, taken before Google
// refuses the ones in use. Google ends a sign-in two hours after it was made; the profile, loaded ten minutes before
// that, is handed new ones (2026-09-25 13:28), while the running connection went on working until the two hours were up.
// Until now the only way to use new cookies was the renewal after a refusal, which ends this helper and starts another
// (reauthenticate): about ten seconds with nothing arriving, every two hours. Here nothing stops: the library reads its
// cookies under a lock for each request it makes, so the next request simply carries the new ones.
//
// The cookies are checked with Google first, on a client of their own, as the renewal checks them: the configuration page
// must name the paired account. What that answer set on them (a new OSID, say) is kept, as the renewal keeps it. Nothing
// is swapped otherwise, nor once this helper has been refused meanwhile, and the cookies in use, still good for minutes,
// stay.
func (a *adapter) replaceCookies(ctx context.Context, r request) (any, string) {
	if a.client == nil || a.client.AuthData == nil || a.client.AuthData.Mobile == nil {
		return nil, "pairing_required"
	}
	var p struct {
		Cookies map[string]string `json:"cookies"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	for _, name := range []string{"SID", "HSID", "SSID", "OSID", "APISID", "SAPISID"} {
		if p.Cookies[name] == "" {
			return nil, "missing_cookies"
		}
	}
	check := a.checkCookies
	if check == nil {
		check = configAccount
	}
	checkCtx, cancel := context.WithTimeout(ctx, replaceCheckBudget)
	email, checked, err := check(checkCtx, maps.Clone(p.Cookies))
	cancel()
	if err != nil {
		return nil, readErrorCode(err)
	}
	if code := renewedAccountCode(a.client.AuthData.Mobile.GetSourceID(), email); code != "" {
		return nil, code
	}
	// Refused while the check ran (the look that fetched these may itself have cost the cookies in use), or given up on:
	// the renewal that follows a refusal is what takes over, so nothing here is changed. Decided under the lock a refusal
	// is latched under, so none can land between this and the swap; and swapped under the watch's lock, so the watch
	// never compares the new set with the old and calls the app's replacement a merge by the library.
	if code := a.swapCookies(ctx, checked); code != "" {
		return nil, code
	}
	a.publishSession()
	return map[string]bool{"replaced": true}, ""
}

// Lock order: inflightMu, then the watch's, then the library's CookiesLock; nothing takes them the other way round.
func (a *adapter) swapCookies(ctx context.Context, cookies map[string]string) string {
	a.inflightMu.Lock()
	defer a.inflightMu.Unlock()
	if a.refusal != "" {
		return a.refusal
	}
	if ctx.Err() != nil {
		return "request_cancelled"
	}
	a.cookies.mu.Lock()
	defer a.cookies.mu.Unlock()
	a.client.AuthData.SetCookies(cookies)
	// A watch not started yet stays unstarted; one running compares from the new set.
	if a.cookies.seen != nil {
		a.cookies.seen = snapshotCookies(a.client.AuthData)
	}
	return ""
}

// The account Google's configuration page names for these cookies, asked by a client that is never connected, and the
// cookies as that answer left them.
func configAccount(ctx context.Context, cookies map[string]string) (string, map[string]string, error) {
	auth := libgm.NewAuthData()
	auth.SetCookies(cookies)
	probe := libgm.NewClient(auth, nil, zerolog.Nop(), exhttp.SensibleClientSettings)
	defer probe.Disconnect()
	if err := probe.FetchConfig(ctx); err != nil {
		return "", nil, err
	}
	auth.CookiesLock.RLock()
	after := maps.Clone(auth.Cookies)
	auth.CookiesLock.RUnlock()
	return probe.Config.GetDeviceInfo().GetEmail(), after, nil
}
