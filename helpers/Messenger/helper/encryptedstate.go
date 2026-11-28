package main

import "time"

// How long the encrypted socket may take to come up, after it was asked to open or after it dropped, before it is
// called down rather than connecting. The readiness watch in startEncrypted gives it twenty seconds; whatsmeow's own
// reconnect after a drop starts within a few. Past this, nothing is arriving in encrypted conversations and Settings
// says so (2026-10-04).
const encryptedGrace = 30 * time.Second

// The encrypted connection's state for "counts", as one word:
//
//	off          not started in this run: never turned on, or it would not open (the app knows which it asked for)
//	connecting   started, not up yet, and still within encryptedGrace of being asked or of dropping
//	ready        up and signed in: encrypted conversations send and receive
//	down         started, but not up for longer than encryptedGrace
//	device_gone  Facebook no longer lists this computer as one of the account's devices
func encryptedStateWord(started, ready, gone bool, sinceChange time.Duration) string {
	switch {
	case gone:
		return "device_gone"
	case !started:
		return "off"
	case ready:
		return "ready"
	case sinceChange < encryptedGrace:
		return "connecting"
	default:
		return "down"
	}
}

// What the app is told when the encrypted connection's word changes. "counts" answers the same word on request, but
// nothing asks while a settings page is not being drawn, so a connection that came up (or dropped) after the last
// answer stayed unreported: the app showed Connecting for a connection that had been ready for seconds.
type encryptedStateEvent struct {
	Encrypted string `json:"encrypted"`
}

// Tells the app the encrypted connection's word when it differs from the one it was last told; the first call always
// tells. Cheap and idempotent, so every place the word can change calls it rather than working out whether it did.
func (a *adapter) announceEncrypted() {
	defer func() { _ = recover() }()
	word := a.encryptedState(time.Now())
	a.announcedMu.Lock()
	same := a.announced == word
	a.announced = word
	a.announcedMu.Unlock()
	if same || a.out == nil {
		return
	}
	a.out.write(response{Event: "encrypted", Result: encryptedStateEvent{word}})
}

// The encrypted socket was just asked to open, or was just seen to drop. The word changes now (to connecting), and
// again when the grace runs out with nothing up (to down), which no event announces, so that is a timer rather than a
// poll: one per change, a no-op if the connection came up meanwhile.
func (a *adapter) encryptedStateChanged() {
	a.encryptedChanged.Store(time.Now().UnixMilli())
	a.announceEncrypted()
	time.AfterFunc(encryptedGrace+time.Second, a.announceEncrypted)
}

func (a *adapter) encryptedState(now time.Time) string {
	a.mu.Lock()
	started := a.crypto != nil
	a.mu.Unlock()
	since := time.Duration(0)
	if changed := a.encryptedChanged.Load(); changed > 0 {
		since = now.Sub(time.UnixMilli(changed))
	}
	return encryptedStateWord(started, started && a.encryptedReady(), a.encryptedGone.Load(), since)
}
