//go:build windows

package tailnet

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
	store, err := stateStore(dir)
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
	if _, err := os.Stat(filepath.Join(dir, sealedStateFile+".tmp")); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("the temporary file was left beside the state")
	}
	again, err := stateStore(dir)
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
	third, err := stateStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := third.ReadState("_machinekey"); !errors.Is(err, ipn.ErrStateNotExist) {
		t.Fatal("a deleted key came back")
	}
}

// A file that cannot be unsealed is an error, never an empty node that would sign in beside the old one, and Start says
// which error it is so the helper does not start again and again.
func TestSealedStoreRefusesDamage(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, sealedStateFile), []byte("not sealed at all"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := stateStore(dir); !errors.Is(err, ErrStateUnreadable) {
		t.Fatalf("a damaged state answered %v", err)
	}
	// Start points these at the node's folder; the test puts them back.
	for _, name := range []string{"TS_LOGS_DIR", "TMPDIR", "TMP", "TEMP"} {
		t.Setenv(name, os.Getenv(name))
	}
	if err := Start(dir, "messagelink-test"); !errors.Is(err, ErrStateUnreadable) {
		t.Fatalf("Start with a damaged state answered %v", err)
	}
	if node() != nil {
		t.Fatal("a node started from a damaged state")
	}
}
