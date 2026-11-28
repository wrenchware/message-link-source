package main

import (
	"context"
	"time"
)

// Removing Google Messages from the server asks the phone to drop this computer from Device pairing first, as upstream's
// logout does (libgm Client.Unpair: a Google-account pairing is revoked through the phone, a QR one through Google's relay).
// Only a connected client can ask; the app forgets the pairing whatever the answer, so a phone that is off or has already
// removed this computer costs at most the timeout.
const unpairTimeout = 15 * time.Second

func (a *adapter) unpair(ctx context.Context) (any, string) {
	if a.client == nil || a.client.AuthData == nil || a.client.AuthData.Browser == nil {
		return nil, "pairing_required"
	}
	ctx, cancel := context.WithTimeout(ctx, unpairTimeout)
	defer cancel()
	// A QR pairing's revoke (libgm UnpairBugle) is an HTTP request that takes no context, so the deadline is kept here: the
	// app disposes this helper right after, which ends a request still waiting.
	done := make(chan error, 1)
	client := a.client
	go func() { done <- client.Unpair(ctx) }()
	select {
	case err := <-done:
		if err == nil {
			return map[string]bool{"unpaired": true}, ""
		}
		if ctx.Err() != nil {
			return nil, "unpair_timeout"
		}
		return nil, "unpair_failed"
	case <-ctx.Done():
		return nil, "unpair_timeout"
	}
}
