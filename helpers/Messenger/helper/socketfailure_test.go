package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix"
)

// messagix leaves its connection loop for good when Facebook closes the socket as unauthorised. The app can only
// recover if it hears about it, and the log has to say why the helper that follows was started.
func TestPermanentSocketFailureIsReportedAndLogged(t *testing.T) {
	logFile := filepath.Join(t.TempDir(), "helper.log")
	t.Setenv("MESSAGELINK_MESSENGER_LOG", logFile)
	openLog()
	defer closeLog()
	var wire bytes.Buffer
	a := newAdapter(&output{encoder: json.NewEncoder(&wire)})
	defer a.stop()
	a.handle(context.Background(), &messagix.PermanentErrorEvent{Err: errors.New("synthetic")})
	var event response
	if err := json.Unmarshal(wire.Bytes(), &event); err != nil || event.Event != "failed" {
		t.Fatalf("the app was not told the socket failed: %q", wire.String())
	}
	closeLog()
	log, err := os.ReadFile(logFile)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(log), "socket failed permanently") || strings.Contains(string(log), "synthetic") {
		t.Fatal("the log does not carry the fixed failure line, or it carried the upstream error text")
	}
}
