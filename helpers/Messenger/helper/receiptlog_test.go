package main

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"go.mau.fi/whatsmeow"
	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/proto/waWa6"
	wadevice "go.mau.fi/whatsmeow/store"
	waTypes "go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	waLog "go.mau.fi/whatsmeow/util/log"
)

type receiptLogSentinel struct{}

func (receiptLogSentinel) String() string { panic("protocol content was formatted") }

func TestReceiptDiagnosticsOnlyRecordAllowedFields(t *testing.T) {
	path := filepath.Join(t.TempDir(), "receipts.log")
	file, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	previous := diagnostics
	diagnostics = &logger{file: file}
	t.Cleanup(func() { closeLog(); diagnostics = previous })
	const secret = "PRIVATE_RECEIPT_SENTINEL"
	root := &receiptLog{Logger: waLog.Noop}
	send, recv := root.Sub("Send"), root.Sub("Recv")
	send.Debugf("%s", &waBinary.Node{Tag: "receipt", Attrs: waBinary.Attrs{
		"id": secret, "type": "read", "to": waTypes.NewJID(secret, waTypes.MessengerServer), "t": time.Now().Unix(),
	}, Content: []waBinary.Node{{Tag: "list", Content: []waBinary.Node{{Tag: "item", Attrs: waBinary.Attrs{"id": secret}}}}}})
	for _, code := range []any{"403", secret, 1234567890, receiptLogSentinel{}} {
		recv.Debugf("%s", &waBinary.Node{Tag: "ack", Attrs: waBinary.Attrs{
			"id": secret, "class": "receipt", "type": "read", "error": code, "from": secret,
		}, Content: []waBinary.Node{{Tag: "error", Content: receiptLogSentinel{}}}})
	}
	recv.Debugf("%s", &waBinary.Node{Tag: "ack", Attrs: waBinary.Attrs{"id": secret, "class": "receipt", "type": secret}})
	recv.Debugf("%s", &waBinary.Node{Tag: "receipt", Attrs: waBinary.Attrs{
		"id": secret, "type": "read-self", "from": waTypes.NewJID(secret, waTypes.MessengerServer),
		"recipient": waTypes.NewJID(secret, waTypes.MessengerServer), "t": time.Now().Unix(),
	}, Content: receiptLogSentinel{}})
	// Unrelated payloads must never be inspected or formatted, including malformed logger calls.
	for _, logger := range []waLog.Logger{root, send, recv, root.Sub("Other")} {
		logger.Debugf("%s", &waBinary.Node{Tag: "message", Attrs: waBinary.Attrs{"id": secret}, Content: receiptLogSentinel{}})
		logger.Debugf("%s", receiptLogSentinel{})
		logger.Debugf("Errored frame hex: %s", receiptLogSentinel{})
		logger.Debugf("%s", (*waBinary.Node)(nil))
	}
	closeLog()
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	text := string(raw)
	for _, required := range []string{"receipt frame id=" + tagOf(secret), "address=direct", "listed=1", "timestamp=true", "receipt ack", "code=403", "errorChild=true", "mode=\"other\""} {
		if !strings.Contains(text, required) {
			t.Errorf("missing diagnostic field %s", required)
		}
	}
	for _, forbidden := range []string{secret, "1234567890", "<receipt", "<ack", "<message"} {
		if strings.Contains(text, forbidden) {
			t.Fatal("protocol content or identifier leaked")
		}
	}
}

func TestReceiptObserverRefusesUserActionsAndDeviceRecovery(t *testing.T) {
	t.Setenv("MESSAGELINK_MESSENGER_OBSERVE_RECEIPTS", "1")
	a := newAdapter(nil)
	defer a.stop()
	for _, method := range []string{"ui.markRead", "ui.sendText", "ui.typing", "ui.react", "ui.deleteChat", "session", "unknown"} {
		if _, code := a.call(request{Method: method}); code != "observation_only" {
			t.Fatalf("observer allowed %s: %s", method, code)
		}
	}
	// A normal initialization/read-only call reaches its existing login guard.
	if _, code := a.call(request{Method: "ui.encrypted"}); code != "login_required" {
		t.Fatal("observer cannot initialize existing device connection")
	}
	crypto := &encrypted{device: &wadevice.Device{}}
	a.crypto = crypto
	a.forgetDevice(crypto, events.ConnectFailureNotFound)
	if a.crypto != crypto || a.encryptedGone.Load() {
		t.Fatal("observer attempted device removal/re-registration")
	}
}

func TestReceiptObserverRefusesMissingDeviceBeforeRegistration(t *testing.T) {
	t.Setenv("MESSAGELINK_MESSENGER_OBSERVE_RECEIPTS", "1")
	cache := t.TempDir()
	t.Setenv("LOCALAPPDATA", cache)
	t.Setenv("XDG_CACHE_HOME", cache)
	a := newAdapter(nil)
	defer a.stop()
	a.chats.setSelf(1)
	// No messaging client exists: touching a registration API would panic here.
	if code := a.startEncrypted(context.Background(), false); code != "existing_device_required" {
		t.Fatalf("observer did not refuse a missing device: %s", code)
	}
	if a.crypto != nil {
		t.Fatal("observer created a replacement device")
	}
}

func TestReceiptObserverRetainsPreparedMessengerConfiguration(t *testing.T) {
	device := &wadevice.Device{}
	prepared := whatsmeow.NewClient(device, waLog.Noop)
	payload := &waWa6.ClientPayload{}
	prepared.GetClientPayload = func() *waWa6.ClientPayload { return payload }
	refreshed := false
	prepared.RefreshCAT = func(context.Context) error { refreshed = true; return nil }
	prepared.MessengerConfig = &whatsmeow.MessengerConfig{BaseURL: "https://example.invalid"}
	prepared.EnableAutoReconnect = false
	prepared.InitialAutoReconnect = true
	observed := observeReceiptAcks(prepared)
	if observed == prepared || observed.Store != device || observed.MessengerConfig != prepared.MessengerConfig ||
		observed.EnableAutoReconnect || !observed.InitialAutoReconnect || observed.GetClientPayload() != payload {
		t.Fatal("prepared client settings changed")
	}
	if err := observed.RefreshCAT(context.Background()); err != nil || !refreshed {
		t.Fatal("authentication renewal callback lost")
	}
	if _, ok := observed.Log.(*receiptLog); !ok {
		t.Fatal("receipt observer not installed")
	}
	if observed.IsConnected() {
		t.Fatal("observing receipts must not connect or register a device")
	}
}
