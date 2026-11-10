package main

import (
	"errors"
	"testing"

	"go.mau.fi/libsignal/ecc"
	"go.mau.fi/libsignal/keys/identity"
	"go.mau.fi/libsignal/keys/prekey"
	"go.mau.fi/libsignal/util/optional"
	wadevice "go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/util/keys"
)

// Each key Facebook hands a sender is told apart on its own: a one-time key with this file's number and another key's
// bytes is the case that fails as "no valid sessions", and must not read as a key that is simply absent.
func TestCompareOwnKeysNamesTheKeyThatDiffers(t *testing.T) {
	identityKey := keys.NewKeyPair()
	device := &wadevice.Device{RegistrationID: 7, IdentityKey: identityKey, SignedPreKey: identityKey.CreateSignedPreKey(1)}
	oneTime := keys.NewPreKey(40)
	other := keys.NewKeyPair()
	bundle := func(registration uint32, identityPub, signedPub, oneTimePub *[32]byte) *prekey.Bundle {
		if oneTimePub == nil {
			return prekey.NewBundle(registration, 3, optional.NewEmptyUint32(), 1, nil, ecc.NewDjbECPublicKey(*signedPub),
				*device.SignedPreKey.Signature, identity.NewKey(ecc.NewDjbECPublicKey(*identityPub)))
		}
		return prekey.NewBundle(registration, 3, optional.NewOptionalUint32(40), 1, ecc.NewDjbECPublicKey(*oneTimePub),
			ecc.NewDjbECPublicKey(*signedPub), *device.SignedPreKey.Signature, identity.NewKey(ecc.NewDjbECPublicKey(*identityPub)))
	}
	holds := func(pub *[32]byte, err error) func(uint32) (*[32]byte, error) {
		return func(id uint32) (*[32]byte, error) {
			if id != 40 {
				t.Fatalf("asked for one-time key %d", id)
			}
			return pub, err
		}
	}
	cases := []struct {
		name   string
		bundle *prekey.Bundle
		held   func(uint32) (*[32]byte, error)
		want   string
	}{
		{"same", bundle(7, identityKey.Pub, device.SignedPreKey.Pub, oneTime.Pub), holds(oneTime.Pub, nil), "identity=match registration=match signed=match onetime=match"},
		{"another key under the same number", bundle(7, identityKey.Pub, device.SignedPreKey.Pub, other.Pub), holds(oneTime.Pub, nil), "identity=match registration=match signed=match onetime=differs"},
		{"a number not held", bundle(7, identityKey.Pub, device.SignedPreKey.Pub, oneTime.Pub), holds(nil, nil), "identity=match registration=match signed=match onetime=missing"},
		{"unreadable file", bundle(7, identityKey.Pub, device.SignedPreKey.Pub, oneTime.Pub), holds(nil, errors.New("locked")), "identity=match registration=match signed=match onetime=unknown"},
		{"no one-time key", bundle(7, identityKey.Pub, device.SignedPreKey.Pub, nil), holds(nil, nil), "identity=match registration=match signed=match onetime=none"},
		{"another device's keys", bundle(8, other.Pub, other.Pub, oneTime.Pub), holds(oneTime.Pub, nil), "identity=differs registration=differs signed=differs onetime=match"},
	}
	for _, c := range cases {
		if got := compareOwnKeys(c.bundle, device, c.held); got != c.want {
			t.Fatalf("%s: %q, want %q", c.name, got, c.want)
		}
	}
}
