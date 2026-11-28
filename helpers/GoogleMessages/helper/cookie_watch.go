package main

import (
	"crypto/sha256"
	"regexp"
	"slices"
	"sync"
	"time"

	"go.mau.fi/mautrix-gmessages/pkg/libgm"
)

// The library merges every Set-Cookie of every answer it reads into the session (AuthData.UpdateCookiesFromResponse).
// Every nine minutes the helper says which cookies that added, changed or removed since it last looked, by name, and
// hands the session to the app to save when anything did.
//
// Until 0.1.307 the helper also called accounts.google.com/RotateCookies on this schedule, as Chrome does to renew its
// short-lived __Secure-1PSIDTS. For this client Google answered 200 and only ever changed the SIDCC cookies, which its
// ordinary answers change anyway (`cookies merged` and `rotate=sidcc_only` side by side, every nine minutes, on
// 2026-09-25), and a sign-in was refused two hours after it was made regardless. Upstream (mautrix-gmessages, whose
// bridge Beeper runs) has never made the call. What renews the sign-in is the app's refresh from its kept browser
// profile (MainWindow.GoogleRecovery).
const cookieWatchFirst = 2 * time.Minute
const cookieWatchEvery = 9 * time.Minute

// A cookie name that may be reported. Anything else is left out of the name lists.
var reportableCookie = regexp.MustCompile(`^[A-Za-z0-9_.-]{1,64}$`)

type cookieWatch struct {
	once sync.Once
	// A digest of each cookie's value by name, as last looked at; nil until the watch starts. The watch reads and writes
	// it, and so does a replacement of the cookies (cookie_replace.go), which starts it afresh.
	mu   sync.Mutex
	seen map[string][sha256.Size]byte
}

// Which cookies the library merged into the session between two looks, by name.
type cookieNameChanges struct {
	Added   []string `json:"added,omitempty"`
	Changed []string `json:"changed,omitempty"`
	Removed []string `json:"removed,omitempty"`
}

// Starts the watch for this helper's session, once, from a first look at its cookies.
func (a *adapter) startCookieWatch() {
	a.cookies.once.Do(func() {
		if a.client != nil && a.client.AuthData != nil {
			a.lookAtCookies(a.client.AuthData)
		}
		go func() {
			wait := cookieWatchFirst
			for {
				time.Sleep(wait)
				wait = cookieWatchEvery
				a.watchCookies()
			}
		}()
	})
}

// One look: the names the library merged since the last one go to the app, and so, when anything changed, does the
// session, so what Google set is what a restart signs in with. Nothing is sent to Google.
func (a *adapter) watchCookies() {
	client := a.client
	if client == nil || client.AuthData == nil {
		return
	}
	if a.noteMergedCookies(client.AuthData) {
		a.publishSession()
	}
}

// Says which names the library added, changed or removed since the last look, from a digest of each value kept only to
// compare. True when anything did.
func (a *adapter) noteMergedCookies(auth *libgm.AuthData) bool {
	a.cookies.mu.Lock()
	defer a.cookies.mu.Unlock()
	if a.cookies.seen == nil {
		return false
	}
	was := a.cookies.seen
	a.cookies.seen = snapshotCookies(auth)
	var merged cookieNameChanges
	for name, digest := range a.cookies.seen {
		if before, known := was[name]; !known {
			merged.Added = append(merged.Added, name)
		} else if before != digest {
			merged.Changed = append(merged.Changed, name)
		}
	}
	for name := range was {
		if _, kept := a.cookies.seen[name]; !kept {
			merged.Removed = append(merged.Removed, name)
		}
	}
	if len(merged.Added)+len(merged.Changed)+len(merged.Removed) == 0 {
		return false
	}
	slices.Sort(merged.Added)
	slices.Sort(merged.Changed)
	slices.Sort(merged.Removed)
	a.out.write(response{Event: "cookies", Result: map[string]cookieNameChanges{"merged": merged}})
	return true
}

// Takes a look at the session's cookies, which the next look is compared with.
func (a *adapter) lookAtCookies(auth *libgm.AuthData) {
	a.cookies.mu.Lock()
	a.cookies.seen = snapshotCookies(auth)
	a.cookies.mu.Unlock()
}

func snapshotCookies(auth *libgm.AuthData) map[string][sha256.Size]byte {
	auth.CookiesLock.RLock()
	defer auth.CookiesLock.RUnlock()
	return cookieDigests(auth.Cookies)
}

// A digest of each cookie's value by name. A name that may not be reported is left out, and an emptied cookie, which is
// what the library keeps for one Google deleted, counts as absent.
func cookieDigests(cookies map[string]string) map[string][sha256.Size]byte {
	digests := make(map[string][sha256.Size]byte, len(cookies))
	for name, value := range cookies {
		if value != "" && reportableCookie.MatchString(name) {
			digests[name] = sha256.Sum256([]byte(value))
		}
	}
	return digests
}
