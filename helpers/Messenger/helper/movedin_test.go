package main

import (
	"os"
	"path/filepath"
	"testing"

	wadevice "go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types/events"
)

// Facebook answering "not found" or "logged out" for the device of an account that arrived by a move must not make the
// helper register another one by itself (no device is, since 2026-11-18): the state is told as "device gone" and
// nothing is started. No client exists in this test, so nothing can reach Facebook.
func TestMovedDeviceRefusedByFacebookIsNotRegisteredAgain(t *testing.T) {
	root := moveRoot(t)
	a := moveAdapter(t)
	a.chats.setSelf(moveViewer)
	folder := moveFolder(root, moveViewer)
	if err := os.MkdirAll(folder, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(folder, movedInName), []byte(`{}`), 0o600); err != nil {
		t.Fatal(err)
	}
	a.crypto = &encrypted{device: &wadevice.Device{Deleted: true}}
	a.forgetDevice(a.crypto, events.ConnectFailureNotFound)
	if !a.encryptedGone.Load() {
		t.Fatal("a refused moved device was not reported as gone")
	}
	if a.crypto != nil {
		t.Fatal("the refused device is still in use")
	}
}

// A page must fit the 1 MiB line it goes back through, with room for its fields.
func TestAPageAndItsLargestUnitStayUnderTheLine(t *testing.T) {
	if backupPageBudget+backupUnitLimit+(64<<10) > 1<<20 {
		t.Fatalf("a page of %d plus a unit of %d does not fit one megabyte with room for its fields", backupPageBudget, backupUnitLimit)
	}
}
