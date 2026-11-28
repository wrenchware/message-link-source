package main

import (
	"context"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
)

// Deletes one conversation from this Voice account (2026-10-04), with the request Google Voice's website and upstream's
// bridge send (libgv DeleteThread; pkg/connector/handlematrix.go HandleMatrixDeleteChat). It names the thread by the same
// id the read and archive requests use. Only this account loses the conversation; the other people keep theirs.
//
// A delete is asked exactly once. libgv repeats a request after a network or server error, so the send guard is put on
// this one too (send.go): after the first answer the repeat is stopped, and an answer that leaves the outcome open is
// reported as unknown, never asked again. Only a request that never left, or one Google refused outright, is certain.
func (a *adapter) deleteThread(ctx context.Context, client *libgv.Client, chatID string) (any, string) {
	ctx, stop := context.WithCancel(ctx)
	defer stop()
	attempt := &sendAttempt{stop: stop}
	ctx = context.WithValue(ctx, sendAttemptKey{}, attempt)
	remove := a.removeThread
	if remove == nil {
		remove = func(ctx context.Context, client *libgv.Client, id string) error {
			_, err := client.DeleteThread(ctx, id)
			return err
		}
	}
	err := remove(ctx, client, chatID)
	attempt.mu.Lock()
	attempts, status, failed := attempt.attempts, attempt.status, attempt.failed
	attempt.mu.Unlock()
	if err == nil {
		a.diagnose(map[string]any{"delete": "accepted", "status": status})
		// Change detection forgets it, so a new message from the same number later is announced as a new conversation.
		a.mu.Lock()
		delete(a.lastSeen, chatID)
		delete(a.lastRead, chatID)
		a.mu.Unlock()
		a.wake()
		return map[string]any{"outcome": "accepted"}, ""
	}
	outcome := deleteOutcome(sendOutcome(err, attempts, status, failed))
	a.diagnose(map[string]any{"delete": outcome, "status": status})
	return nil, outcome
}

// The send outcomes, named for a delete: certain refusals stay certain, and everything else is unknown.
func deleteOutcome(send string) string {
	switch send {
	case "send_rejected":
		return "delete_rejected"
	case "send_failed":
		return "delete_failed"
	}
	return send
}
