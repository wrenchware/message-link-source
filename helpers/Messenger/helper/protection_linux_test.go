package main

import (
	"bytes"
	"crypto/rand"
	"os"
	"path/filepath"
	"testing"
)

// The existing helper tests exercise retained history. On Linux they need a disposable key, never a real account's key.
func TestMain(m *testing.M) {
	root, err := os.MkdirTemp("", "messages-helper-tests-")
	if err != nil {
		os.Exit(2)
	}
	key := make([]byte, 32)
	if _, err = rand.Read(key); err != nil {
		os.Exit(2)
	}
	path := filepath.Join(root, "master.key")
	if os.WriteFile(path, key, 0600) != nil {
		os.Exit(2)
	}
	clear(key)
	if os.Setenv("MESSAGES_HELPER_KEY_FILE", path) != nil {
		os.Exit(2)
	}
	code := m.Run()
	os.RemoveAll(root)
	os.Exit(code)
}

func writeKey(t *testing.T, content []byte, mode os.FileMode) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "other.key")
	if err := os.WriteFile(path, content, mode); err != nil {
		t.Fatal("key write failed")
	}
	if err := os.Chmod(path, mode); err != nil {
		t.Fatal("key chmod failed")
	}
	return path
}

func TestServerProtectionRoundTripIntegrityAndNonce(t *testing.T) {
	for _, plain := range [][]byte{nil, []byte("offline synthetic text")} {
		first, err := protectLocal(plain)
		if err != nil {
			t.Fatal("protection failed")
		}
		second, err := protectLocal(plain)
		if err != nil || bytes.Equal(first, second) {
			t.Fatal("nonce was reused")
		}
		opened, err := unprotectLocal(first)
		if err != nil || !bytes.Equal(opened, plain) {
			t.Fatal("round trip failed")
		}
		first[len(first)-1] ^= 1
		if _, err := unprotectLocal(first); err == nil {
			t.Fatal("corruption was accepted")
		}
	}
}

func TestServerProtectionFailsClosed(t *testing.T) {
	sealed, err := protectLocal([]byte("synthetic"))
	if err != nil {
		t.Fatal("protection failed")
	}
	t.Setenv("MESSAGES_HELPER_KEY_FILE", "")
	if _, err := protectLocal([]byte("synthetic")); err == nil {
		t.Fatal("missing key allowed a write")
	}
	for _, windowsCell := range [][]byte{{'M', 'L', 'P', 1, 2}, {'N', 'B', 'P', 1, 2}} {
		if _, err := unprotectLocal(windowsCell); err == nil {
			t.Fatal("Windows protection was accepted")
		}
	}
	if _, err := unprotectLocal(sealed[:len(protectedPrefix)+5]); err == nil {
		t.Fatal("truncated value was accepted")
	}
}

func TestServerProtectionRejectsWrongKeyAndBadKeyFiles(t *testing.T) {
	sealed, err := protectLocal([]byte("synthetic"))
	if err != nil {
		t.Fatal("protection failed")
	}
	other := make([]byte, 32)
	if _, err = rand.Read(other); err != nil {
		t.Fatal("random failed")
	}
	t.Setenv("MESSAGES_HELPER_KEY_FILE", writeKey(t, other, 0600))
	if _, err := unprotectLocal(sealed); err == nil {
		t.Fatal("a different key opened the value")
	}
	t.Setenv("MESSAGES_HELPER_KEY_FILE", writeKey(t, other, 0644))
	if _, err := protectLocal([]byte("synthetic")); err == nil {
		t.Fatal("a group-readable key file was accepted")
	}
	t.Setenv("MESSAGES_HELPER_KEY_FILE", writeKey(t, other[:31], 0600))
	if _, err := protectLocal([]byte("synthetic")); err == nil {
		t.Fatal("a short key was accepted")
	}
	t.Setenv("MESSAGES_HELPER_KEY_FILE", writeKey(t, append(other, 0), 0600))
	if _, err := protectLocal([]byte("synthetic")); err == nil {
		t.Fatal("a long key was accepted")
	}
}
