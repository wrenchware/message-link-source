package main

import (
	"context"
	"os"
	"path/filepath"
	"strconv"
	"testing"
	"time"

	"go.mau.fi/whatsmeow/proto/waAdv"
	wadevice "go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/store/sqlstore"
	waTypes "go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	waLog "go.mau.fi/whatsmeow/util/log"
)

// The account's e2ee.db with one stored device of its own, opened as startEncrypted opens it. No client exists in
// these tests, so nothing can reach Facebook.
func logoutStore(t *testing.T) (path string, devices *sqlstore.Container, device *wadevice.Device) {
	t.Helper()
	root := moveRoot(t)
	folder := moveFolder(root, moveViewer)
	if err := os.MkdirAll(folder, 0o700); err != nil {
		t.Fatal(err)
	}
	path = filepath.Join(folder, "e2ee.db")
	devices = openDevices(t, path)
	device = devices.NewDevice()
	jid := waTypes.NewJID(strconv.FormatInt(moveViewer, 10), waTypes.MessengerServer)
	jid.Device = 7
	device.ID = &jid
	device.Account = &waAdv.ADVSignedDeviceIdentity{Details: []byte{1}, AccountSignature: make([]byte, 64), AccountSignatureKey: make([]byte, 32), DeviceSignature: make([]byte, 64)}
	if err := device.Save(context.Background()); err != nil {
		t.Fatal(err)
	}
	return path, devices, device
}

func openDevices(t *testing.T, path string) *sqlstore.Container {
	t.Helper()
	devices, err := sqlstore.New(context.Background(), "sqlite", sqliteFile(path, "_pragma=foreign_keys(1)&_pragma=busy_timeout(5000)"), waLog.Noop)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = devices.Close() })
	return devices
}

func storedDevices(t *testing.T, path string) int {
	t.Helper()
	devices := openDevices(t, path)
	stored, err := devices.GetAllDevices(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	_ = devices.Close()
	return len(stored)
}

// The release of a client given up on, waited for as the next startEncrypted waits for it.
func drained(t *testing.T, a *adapter) {
	t.Helper()
	a.mu.Lock()
	done := a.encryptedDrained
	a.mu.Unlock()
	if done == nil {
		t.Fatal("nothing was released")
	}
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("the release did not finish")
	}
}

func shortDeleteWait(t *testing.T, wait time.Duration) {
	previous := encryptedDeleteWait
	encryptedDeleteWait = wait
	t.Cleanup(func() { encryptedDeleteWait = previous })
}

// whatsmeow reports 401 and 406 (and a device_removed stream error) as LoggedOut, never as ConnectFailure. One of them
// lets go of the client and says device_gone, and that is all: no stored key is deleted by the helper and nothing is
// registered. It used to clear every stored device and register a new one at once.
func TestLoggedOutReleasesTheClientAndKeepsTheKeys(t *testing.T) {
	for _, reason := range []events.ConnectFailureReason{events.ConnectFailureLoggedOut, events.ConnectFailureUnknownLogout} {
		shortDeleteWait(t, 100*time.Millisecond)
		path, devices, device := logoutStore(t)
		a := moveAdapter(t)
		a.chats.setSelf(moveViewer)
		crypto := &encrypted{devices: devices, device: device, jid: *device.ID}
		a.crypto = crypto
		a.encryptedEvent(crypto, &events.LoggedOut{OnConnect: true, Reason: reason})
		if a.crypto != nil {
			t.Fatalf("%d: the signed-out device is still in use", int(reason))
		}
		if a.encryptedState(time.Now()) != "device_gone" {
			t.Fatalf("%d: a signed-out device read as %q", int(reason), a.encryptedState(time.Now()))
		}
		drained(t, a)
		if _, err := devices.GetAllDevices(context.Background()); err == nil {
			t.Fatalf("%d: the released device's e2ee.db pool is still open", int(reason))
		}
		if stored := storedDevices(t, path); stored != 1 {
			t.Fatalf("%d: the helper deleted stored keys itself (%d left)", int(reason), stored)
		}
	}
}

// whatsmeow deletes the signed-out device's keys on its own goroutine as it reports the sign-out. The release waits for
// that rather than closing the pool under it, which left the keys in place.
func TestReleaseLetsTheLibraryFinishDeletingTheSignedOutDevice(t *testing.T) {
	shortDeleteWait(t, 3*time.Second)
	path, devices, device := logoutStore(t)
	a := moveAdapter(t)
	a.chats.setSelf(moveViewer)
	crypto := &encrypted{devices: devices, device: device, jid: *device.ID}
	a.crypto = crypto
	deleted := make(chan error, 1)
	go func() {
		time.Sleep(300 * time.Millisecond)
		deleted <- device.Delete(context.Background())
	}()
	a.encryptedEvent(crypto, &events.LoggedOut{OnConnect: true, Reason: events.ConnectFailureLoggedOut})
	drained(t, a)
	if err := <-deleted; err != nil {
		t.Fatalf("the library's delete ran into a closed store: %v", err)
	}
	if stored := storedDevices(t, path); stored != 0 {
		t.Fatalf("the library's delete did not land (%d left)", stored)
	}
}

// A refusal that arrives late from a client already given up on cannot touch the one started since: it used to clear
// a.crypto whatever client it came from.
func TestLateRefusalFromAReleasedClientLeavesTheReplacement(t *testing.T) {
	shortDeleteWait(t, 100*time.Millisecond)
	_, devices, device := logoutStore(t)
	a := moveAdapter(t)
	a.chats.setSelf(moveViewer)
	first := &encrypted{devices: devices, device: device, jid: *device.ID}
	a.crypto = first
	a.encryptedEvent(first, &events.ConnectFailure{Reason: events.ConnectFailureNotFound})
	drained(t, a)
	second := &encrypted{}
	a.mu.Lock()
	a.crypto = second
	a.mu.Unlock()
	a.encryptedGone.Store(false)
	a.chats.setSecure(true)
	a.encryptedEvent(first, &events.LoggedOut{OnConnect: true, Reason: events.ConnectFailureLoggedOut})
	a.encryptedEvent(first, &events.ConnectFailure{Reason: events.ConnectFailureNotFound})
	a.encryptedEvent(first, &events.TemporaryBan{})
	if a.crypto != second {
		t.Fatal("a late refusal from the released client cleared its replacement")
	}
	if a.encryptedGone.Load() {
		t.Fatal("a late refusal from the released client called the replacement gone")
	}
	a.chats.mu.Lock()
	secure := a.chats.secure
	a.chats.mu.Unlock()
	if !secure {
		t.Fatal("a late event from the released client turned the encrypted conversations off")
	}
	// The replacement's own refusal still counts.
	a.encryptedEvent(second, &events.ConnectFailure{Reason: events.ConnectFailureNotFound})
	if a.crypto != nil || !a.encryptedGone.Load() {
		t.Fatal("the current client's refusal was ignored")
	}
}

// No replacement starts while the client given up on is still being released.
func TestStartWaitsForTheReleasedClient(t *testing.T) {
	moveRoot(t)
	a := moveAdapter(t)
	a.chats.setSelf(moveViewer)
	pending := make(chan struct{})
	a.mu.Lock()
	a.encryptedDrained = pending
	a.mu.Unlock()
	ctx, cancel := context.WithTimeout(context.Background(), 200*time.Millisecond)
	defer cancel()
	if code := a.startEncrypted(ctx, true); code != "e2ee_failed" {
		t.Fatalf("a start during a release went ahead: %q", code)
	}
	close(pending)
	if code := a.startEncrypted(context.Background(), true); code != "device_missing" {
		t.Fatalf("a start after the release answered %q", code)
	}
}

// The device numbers stored for the test account, in no particular order.
func storedDeviceNumbers(t *testing.T, path string) map[uint16]bool {
	t.Helper()
	devices := openDevices(t, path)
	stored, err := devices.GetAllDevices(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	_ = devices.Close()
	numbers := map[uint16]bool{}
	for _, device := range stored {
		if device.ID != nil {
			numbers[device.ID.Device] = true
		}
	}
	return numbers
}

// Turning encrypted conversations on after Facebook refused the device is what clears the refused keys, and only
// those: another device the account holds here is kept, and is the one started. It used to clear every stored device
// of the account. Without a refusal nothing is cleared.
func TestExplicitEnableAfterRefusalClearsOnlyTheRefusedDevice(t *testing.T) {
	for _, refused := range []bool{false, true} {
		shortDeleteWait(t, 100*time.Millisecond)
		path, devices, device := logoutStore(t)
		other := devices.NewDevice()
		otherJID := waTypes.NewJID(strconv.FormatInt(moveViewer, 10), waTypes.MessengerServer)
		otherJID.Device = 9
		other.ID = &otherJID
		other.Account = device.Account
		if err := other.Save(context.Background()); err != nil {
			t.Fatal(err)
		}
		a := moveAdapter(t)
		a.chats.setSelf(moveViewer)
		if refused {
			crypto := &encrypted{devices: devices, device: device, jid: *device.ID}
			a.crypto = crypto
			// 415 leaves the keys in place, so only the helper could clear them.
			a.encryptedEvent(crypto, &events.ConnectFailure{Reason: events.ConnectFailureNotFound})
			drained(t, a)
		} else {
			_ = devices.Close()
		}
		// No Messenger client exists here, so the start stops (with a panic on the missing client) right after the
		// stored device is chosen and before anything could be registered.
		func() {
			defer func() { _ = recover() }()
			a.startEncrypted(context.Background(), false)
		}()
		left := storedDeviceNumbers(t, path)
		if !left[9] {
			t.Fatalf("refused=%v: the account's other device was cleared", refused)
		}
		if left[7] == refused {
			t.Fatalf("refused=%v: the refused device's keys left=%v", refused, left[7])
		}
	}
}

// 403 is the account's main device, not this one: the client is not given up on for it.
func TestMainDeviceGoneKeepsTheClient(t *testing.T) {
	_, devices, device := logoutStore(t)
	a := moveAdapter(t)
	a.chats.setSelf(moveViewer)
	crypto := &encrypted{devices: devices, device: device, jid: *device.ID}
	a.crypto = crypto
	a.encryptedEvent(crypto, &events.LoggedOut{OnConnect: true, Reason: events.ConnectFailureMainDeviceGone})
	if a.crypto != crypto || a.encryptedGone.Load() {
		t.Fatal("a 403 gave up the client")
	}
	if _, err := devices.GetAllDevices(context.Background()); err != nil {
		t.Fatal("a 403 closed the device store")
	}
}
