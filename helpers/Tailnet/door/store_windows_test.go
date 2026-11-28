//go:build windows

package main

import (
	"bytes"
	"errors"
	"os"
	"path/filepath"
	"testing"

	"tailscale.com/ipn"
)

// The node's state survives a restart sealed, and the file on disk never holds it in the clear.
func TestSealedStoreRoundTrip(t *testing.T) {
	dir := t.TempDir()
	store, err := newStateStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.ReadState("_machinekey"); !errors.Is(err, ipn.ErrStateNotExist) {
		t.Fatalf("an empty store answered %v", err)
	}
	secret := []byte(`privkey:0123456789abcdef-a-secret-the-file-must-not-show`)
	if err := store.WriteState("_machinekey", secret); err != nil {
		t.Fatal(err)
	}
	onDisk, err := os.ReadFile(filepath.Join(dir, sealedStateFile))
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(onDisk, []byte("a-secret-the-file-must-not-show")) || bytes.Contains(onDisk, []byte("_machinekey")) {
		t.Fatal("the state is on disk in the clear")
	}
	again, err := newStateStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	if got, err := again.ReadState("_machinekey"); err != nil || !bytes.Equal(got, secret) {
		t.Fatalf("the state did not come back: %v", err)
	}
	// Deleting a key is kept too.
	if err := again.WriteState("_machinekey", nil); err != nil {
		t.Fatal(err)
	}
	third, err := newStateStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := third.ReadState("_machinekey"); !errors.Is(err, ipn.ErrStateNotExist) {
		t.Fatal("a deleted key came back")
	}
}

// A file that cannot be unsealed is an error, never an empty node that would sign in beside the old one.
func TestSealedStoreRefusesDamage(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, sealedStateFile), []byte("not sealed at all"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := newStateStore(dir); err == nil {
		t.Fatal("a damaged state opened")
	}
}
