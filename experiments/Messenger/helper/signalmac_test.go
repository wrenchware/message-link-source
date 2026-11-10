package main

import (
	"crypto/hmac"
	"crypto/sha256"
	"testing"

	"go.mau.fi/libsignal/protocol"
	"go.mau.fi/libsignal/serialize"
	"go.mau.fi/libsignal/util/keyhelper"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
)

// A message's MAC covers the bytes that arrived. libsignal through v0.2.2 checked it against its own re-encoding of the
// fields it knew, so a message carrying one more field failed as "no valid sessions" on every resend: one sender's
// messages from 2026-11-06, and the owner's own from another device on 11-09. This holds the pin at a version that
// does not.
func TestSignalMessageMacCoversFieldsThisBuildDoesNotKnow(t *testing.T) {
	sender, err := keyhelper.GenerateIdentityKeyPair()
	if err != nil {
		t.Fatal(err)
	}
	receiver, err := keyhelper.GenerateIdentityKeyPair()
	if err != nil {
		t.Fatal(err)
	}
	counter, previous := uint32(0), uint32(0)
	body, err := proto.Marshal(&serialize.SignalMessage{
		RatchetKey:      sender.PublicKey().PublicKey().Serialize(),
		Counter:         &counter,
		PreviousCounter: &previous,
		Ciphertext:      make([]byte, 32),
	})
	if err != nil {
		t.Fatal(err)
	}
	body = protowire.AppendVarint(protowire.AppendTag(body, 7, protowire.VarintType), 1)
	signed := append([]byte{0x33}, body...)
	macKey := make([]byte, 32)
	mac := hmac.New(sha256.New, macKey)
	mac.Write(sender.PublicKey().PublicKey().Serialize())
	mac.Write(receiver.PublicKey().PublicKey().Serialize())
	mac.Write(signed)
	message, err := protocol.NewSignalMessageFromBytes(append(signed, mac.Sum(nil)[:protocol.MacLength]...), &serialize.ProtoBufSignalMessageSerializer{})
	if err != nil {
		t.Fatal(err)
	}
	if err := message.VerifyMac(3, sender.PublicKey(), receiver.PublicKey(), macKey); err != nil {
		t.Fatalf("a message with a field this build does not know failed its MAC: %v", err)
	}
}
