package main

import (
	"context"
	"testing"
)

func TestUnpairWithoutPairing(t *testing.T) {
	a := &adapter{}
	if result, code := a.unpair(context.Background()); result != nil || code != "pairing_required" {
		t.Fatalf("unpair without a client = %v, %q", result, code)
	}
	if _, code := a.callContext(context.Background(), request{Method: "unpair"}); code != "pairing_required" {
		t.Fatalf("unpair is not dispatched: %q", code)
	}
}
