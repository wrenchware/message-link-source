package main

import (
	"context"
	"maps"
	"net/http"
	"slices"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

// Google refuses a sign-in this app copied out of a browser two hours after Google made it, whatever the helper does
// meanwhile. Until 0.1.309 the helper called accounts.google.com/RotateCookies every nine minutes on the belief that it
// renewed __Secure-1PSIDTS as Chrome's does; every answer it logged changed only SIDCC and its two siblings, and each one
// restarted realtime for nothing. Only the app's kept browser profile renews the sign-in: before the two hours through
// cookies.replace (cookie_replace.go), after them by starting a new helper with what the profile hands back.

func (a *adapter) isCurrentGeneration(generation uint64) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.client != nil && a.clientGeneration == generation
}

func newSessionClient(cookies map[string]string, authUser string) *libgv.Client {
	client := libgv.NewClient(cookies)
	// A send goes out once; recovery itself never sends, but a recovered client must retain that guarantee.
	client.HTTP.Transport = &sendGuard{}
	// libgv's initial account is "0". An empty selector means "keep the library default", not an empty
	// X-Goog-AuthUser header; clearing it makes Google reject otherwise valid fresh and restored sessions.
	if authUser != "" {
		client.AuthUser = authUser
	}
	return client
}

func (a *adapter) account(ctx context.Context, client *libgv.Client) (*gvproto.RespGetAccount, error) {
	get := a.getAccount
	if get == nil {
		get = func(ctx context.Context, client *libgv.Client) (*gvproto.RespGetAccount, error) {
			return client.GetAccount(ctx)
		}
	}
	return get(ctx, client)
}

func removeShortLived(cookies map[string]string) (map[string]string, bool) {
	trimmed := make(map[string]string, len(cookies))
	for name, value := range cookies {
		if !slices.Contains(shortLivedCookies, name) {
			trimmed[name] = value
		}
	}
	return trimmed, len(trimmed) != len(cookies)
}

// recoverRuntime verifies one replacement client after an auth refusal from a read or realtime request: the long-lived
// session without its short-lived cookies. The failed read is repeated once by runtimeRead; no mutation calls this
// function. At Google's two hours this is refused as well, and the app's browser profile takes over.
func (a *adapter) recoverRuntime(ctx context.Context, stale *libgv.Client) bool {
	a.recoveryMu.Lock()
	defer a.recoveryMu.Unlock()
	current := a.current()
	if current == nil {
		return false
	}
	if current != stale {
		// Another reader already completed the bounded recovery, or the app handed over a renewed sign-in. Either
		// replacement is fully validated.
		return true
	}
	trimmed, changed := removeShortLived(stale.GetCookies())
	if !changed {
		return false
	}
	candidate := newSessionClient(trimmed, stale.AuthUser)
	if _, err := a.account(ctx, candidate); err != nil {
		a.diagnose(map[string]any{"recovery": "without_short_lived", "status": statusOf(err)})
		return false
	}
	if !a.replaceCurrent(stale, candidate) {
		return a.current() != nil
	}
	a.diagnose(map[string]any{"recovery": "without_short_lived", "status": http.StatusOK})
	return true
}

// runtimeRead retries a non-mutating request once, after one serialized recovery when and only when Google refused it.
// It intentionally is not used by sends, mark-read, archive, or any other account-changing operation.
func runtimeRead[T any](a *adapter, ctx context.Context, client *libgv.Client, read func(context.Context, *libgv.Client) (T, error)) (T, error) {
	result, err := read(ctx, client)
	if err == nil || !libgv.IsAuthError(err) || !a.recoverRuntime(ctx, client) {
		return result, err
	}
	current := a.current()
	if current == nil {
		return result, errLoggedOut
	}
	return read(ctx, current)
}

// libgv keeps its cookies private, so renewed cookies mean a new client. Live updates reconnect with it, so the old
// timestamp cookie is not used again after Google has replaced it. The app saves the renewed session.
func (a *adapter) replaceClient(cookies map[string]string) {
	a.mu.Lock()
	old := a.client
	a.mu.Unlock()
	if old == nil {
		return
	}
	a.replaceCurrent(old, newSessionClient(cookies, old.AuthUser))
}

// replaceCurrent swaps a validated client only if the client that was validated is still the current one. The cookie
// event carries its monotonic generation, allowing the app to ignore anything emitted by a superseded client.
func (a *adapter) replaceCurrent(old, next *libgv.Client) bool {
	a.mu.Lock()
	if a.client != old {
		a.mu.Unlock()
		return false
	}
	a.clientGeneration++
	clientGeneration := a.clientGeneration
	a.cookieGeneration++
	cookieGeneration := a.cookieGeneration
	next.EventHandler = func(ctx context.Context, raw any) { a.handleEvent(clientGeneration, ctx, raw) }
	a.client = next
	running := a.realtime != nil
	a.mu.Unlock()
	cookies := next.GetCookies()
	a.noteCookies(cookies)
	a.out.write(response{Event: "cookies", Result: map[string]any{"cookies": maps.Clone(cookies), "generation": cookieGeneration}})
	if running {
		a.stopRealtime()
		a.startRealtime()
	}
	return true
}
