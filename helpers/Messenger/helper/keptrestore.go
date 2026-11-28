package main

import (
	"context"
	"time"
)

// The kept history, read back beside the login request rather than after it.
//
// On a cold start the two were run one after the other and were the largest things in it: the login page is one
// Facebook round trip (2394 ms measured) and the restore is mostly Windows data protection opening each kept cell
// (4136 messages in 2707 ms). The restore needs only the account id, which the session cookie already carries, and
// the store; the login needs neither. What the first apply needs is both, so it waits for the restore to finish, and
// the app receives exactly what it did before in the same order: nothing from the restore crosses the pipe, the
// restored rows are all in place before the first page is applied, and the reply to login is still the first thing
// the app hears.
//
// Before the login succeeds the account id is only what the cookie claims, so the early read stays inside what is
// already this account's on this computer:
//   - only a store that already exists is opened. No account folder is created for an id Facebook has not accepted,
//     and a first run, which has nothing to restore, keeps the old order;
//   - the legacy unpartitioned database is never adopted early. Adoption needs the store to be missing, and a missing
//     store is never read early, so adoption still happens only after the login has verified the session;
//   - the stale backup snapshot is removed only once the login has succeeded;
//   - if the login fails, the restore is cancelled and waited for, the store is closed and the conversations are put
//     back as they were, so a failed login leaves nothing restored in memory, as before.
//
// Opening the store does run its own housekeeping (the schema, the sealing of any legacy plaintext cell, and the
// receipts' own trimming of aged and over-limit rows) before the login is known to be good. That is the same
// account's own data on this computer, and the same writes the next successful login makes anyway.
type keptRestore struct {
	cancel context.CancelFunc
	done   chan struct{}
	// Written by the restoring goroutine, read only once done is closed.
	kept     *messageStore
	restored int
	took     time.Duration
}

// Starts reading the kept history into the conversations, or answers nil when there is nothing that may be read yet.
// The caller holds the lifecycle turn: nothing else reads or writes the conversations while it runs.
func (a *adapter) startKeptRestore(ctx context.Context, viewer int64) *keptRestore {
	path := existingAccountPath(viewer, "messages.db")
	if path == "" {
		return nil
	}
	restoreCtx, cancel := context.WithCancel(ctx)
	r := &keptRestore{cancel: cancel, done: make(chan struct{})}
	// A restored message needs to know whose it is before it goes in, and the id is the same one setSelf is given
	// after the login.
	a.chats.setSelf(viewer)
	into := a.chats
	go func() {
		defer close(r.done)
		// The same guard the helper's other goroutines have: a row of an unexpected shape must not take it down.
		// Logged as a step only (never the value, which could hold row data), so a partly restored store is visible.
		defer func() {
			if recover() != nil {
				logf("kept restore stopped by a panic")
			}
		}()
		started := time.Now()
		kept, err := openAccountMessageStore(restoreCtx, path, viewer)
		if err != nil {
			return
		}
		r.kept = kept
		r.restored = kept.restore(restoreCtx, into)
		kept.restoreReceipts(restoreCtx, into)
		r.took = time.Since(started)
	}()
	return r
}

// Waits for the restore and takes the store it opened, once the login has succeeded. loaded is how long the login
// page took, for the line that shows the two ran together.
func (r *keptRestore) finish(a *adapter, loaded time.Duration) {
	waiting := time.Now()
	<-r.done
	waited := time.Since(waiting)
	r.cancel()
	if r.kept == nil {
		logf("kept messages unavailable")
		return
	}
	a.kept = r.kept
	discardStaleBackup(r.kept)
	if r.restored > 0 {
		logf("restored %d kept messages in %dms", r.restored, r.took.Milliseconds())
	}
	// ms is the login page, restore_ms the store's open and restore beside it, waited_ms what was left of the restore
	// once the login was back. The saving is restore_ms minus waited_ms.
	logf("kept restore overlapped login ms=%d restore_ms=%d waited_ms=%d", loaded.Milliseconds(), r.took.Milliseconds(),
		waited.Milliseconds())
}

// Undoes an early restore after a failed login: nothing read for an id Facebook did not accept stays in memory.
func (r *keptRestore) abandon(a *adapter) {
	if r == nil {
		return
	}
	r.cancel()
	<-r.done
	if r.kept != nil {
		_ = r.kept.db.Close()
	}
	// The conversations held nothing before this login: the helper logs in once, and nothing fills them before it.
	a.chats = newStore()
	logf("kept restore abandoned after a failed login")
}
