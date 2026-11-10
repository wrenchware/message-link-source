package main

import (
	"context"
	"fmt"
	"time"

	"go.mau.fi/libsignal/keys/prekey"
	"go.mau.fi/whatsmeow"
	wadevice "go.mau.fi/whatsmeow/store"
	waTypes "go.mau.fi/whatsmeow/types"
)

// A sender who needs a new session with this device asks Facebook for its keys and builds on what comes back. When
// that copy and the device file disagree, the session it builds fails the MAC check here on every message and every
// resend, and the Signal library reports "no valid sessions" rather than a key it could not find: whatsmeow numbers
// its keys from 1, so another lineage of the same device holds the same numbers with different keys. One sender's
// device failed that way from 2026-11-06, including on a resend built 300 ms after a retry receipt that carried no
// keys, which can only have come from Facebook's copy.
//
// So once a start this device asks for its own keys, as any sender would, and says whether each one is the key it
// holds. The answer costs one one-time key of the hundreds published. Only words reach the log.
func (a *adapter) checkOwnKeys(client *whatsmeow.Client, device *wadevice.Device) {
	if device == nil || device.ID == nil {
		return
	}
	ctx, cancel := context.WithTimeout(a.lifetime, 30*time.Second)
	defer cancel()
	internals := client.DangerousInternals()
	published := "unknown"
	if count, err := internals.GetServerPreKeyCount(ctx); err == nil {
		published = fmt.Sprint(count)
	}
	bundle := internals.FetchPreKeysNoError(ctx, []waTypes.JID{*device.ID})[*device.ID]
	if bundle == nil {
		logf("e2ee own keys outcome=no_answer published=%s", published)
		return
	}
	logf("e2ee own keys outcome=answered %s published=%s", compareOwnKeys(bundle, device, func(id uint32) (*[32]byte, error) {
		held, err := device.PreKeys.GetPreKey(ctx, id)
		if err != nil || held == nil {
			return nil, err
		}
		return held.Pub, nil
	}), published)
}

// Facebook's copy of this device's keys against the device file, one word a key: match, differs, missing (a one-time
// key number this file does not hold), none (no one-time key came back) or unknown (the file could not be read).
func compareOwnKeys(bundle *prekey.Bundle, device *wadevice.Device, held func(id uint32) (*[32]byte, error)) string {
	word := func(same bool) string {
		if same {
			return "match"
		}
		return "differs"
	}
	identity := "unknown"
	if device.IdentityKey != nil && device.IdentityKey.Pub != nil && bundle.IdentityKey() != nil {
		identity = word(bundle.IdentityKey().PublicKey().PublicKey() == *device.IdentityKey.Pub)
	}
	signed := "unknown"
	if device.SignedPreKey != nil && device.SignedPreKey.Pub != nil && bundle.SignedPreKey() != nil {
		signed = word(bundle.SignedPreKeyID() == device.SignedPreKey.KeyID && bundle.SignedPreKey().PublicKey() == *device.SignedPreKey.Pub)
	}
	oneTime := "none"
	if id := bundle.PreKeyID(); id != nil && !id.IsEmpty && bundle.PreKey() != nil {
		switch pub, err := held(id.Value); {
		case err != nil:
			oneTime = "unknown"
		case pub == nil:
			oneTime = "missing"
		default:
			oneTime = word(bundle.PreKey().PublicKey() == *pub)
		}
	}
	return fmt.Sprintf("identity=%s registration=%s signed=%s onetime=%s",
		identity, word(bundle.RegistrationID() == device.RegistrationID), signed, oneTime)
}
