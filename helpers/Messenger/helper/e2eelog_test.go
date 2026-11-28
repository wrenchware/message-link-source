package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// A failed upload's error names the address it went to, and whatsmeow's upload address carries the upload's own
// authorisation in its query string. None of that may reach the log (security review, 2026-10-11).
func TestScrubRemovesUploadAddressSecrets(t *testing.T) {
	secret := "AbCdEfGhIjKlMnOpQrStUvWx-_0123"
	cases := []string{
		"failed to upload: Post \"https://mmg.facebook.com/v/t62.7117-24/12345678_n.enc?auth=" + secret + "&token=" + secret + "\": EOF",
		"HTTP 503 from https://mmg.facebook.com/path/" + secret + "#frag" + secret,
		"bare query /upload?auth=" + secret + "&x=1",
		"unexpected response " + secret,
		"auth=" + secret,
		"base64 " + strings.Repeat("QUJD", 12) + "==",
		"hex " + strings.Repeat("deadbeef", 8),
	}
	for _, input := range cases {
		got := scrub(input)
		for _, leak := range []string{secret, "QUJDQUJD", "deadbeefdeadbeef", "t62.7117", "frag"} {
			if strings.Contains(got, leak) {
				t.Fatalf("scrub(%q) = %q still holds %q", input, got, leak)
			}
		}
	}
	if got := scrub("Post \"https://mmg.facebook.com/x?auth=" + secret + "\": EOF"); !strings.Contains(got, "https://mmg.facebook.com") || !strings.Contains(got, "EOF") {
		t.Fatalf("scrub dropped the host or the reason: %q", got)
	}
}

func TestScrubKeepsWhatIsUseful(t *testing.T) {
	for _, input := range []string{"websocket: close 1006 (abnormal closure)", "reason code 401", "socket is not connected", "get media connection: HTTP 429"} {
		if got := scrub(input); got != input {
			t.Fatalf("scrub(%q) = %q, want it unchanged", input, got)
		}
	}
	if got := scrub("account 1234567890123"); got != "account <13 digits>" {
		t.Fatalf("digit runs: %q", got)
	}
}

func TestScrubTruncatesAfterMasking(t *testing.T) {
	// A token that straddles the 300-byte limit must be masked whole, not cut in half and kept.
	input := strings.Repeat("x ", 140) + strings.Repeat("Zq9", 30)
	got := scrub(input)
	if len(got) > 300 {
		t.Fatalf("length %d", len(got))
	}
	if strings.Contains(got, "Zq9Zq9") {
		t.Fatalf("part of the token survived: %q", got)
	}
}

// The library logger's events and the encrypted client's own logger go through the same scrub on their way to the file.
func TestLibraryLogsAreScrubbedOnTheirWayToTheFile(t *testing.T) {
	secret := "AbCdEfGhIjKlMnOpQrStUvWx-_0123"
	path := filepath.Join(t.TempDir(), "helper.log")
	file, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	diagnostics.mu.Lock()
	previous, previousWritten := diagnostics.file, diagnostics.written
	diagnostics.file, diagnostics.written = file, 0
	diagnostics.mu.Unlock()
	defer func() {
		diagnostics.mu.Lock()
		diagnostics.file, diagnostics.written = previous, previousWritten
		diagnostics.mu.Unlock()
		file.Close()
	}()
	line := `{"level":"error","message":"upload","error":"Post \"https://h.example/p?auth=` + secret + `\": EOF"}`
	if _, err := (libraryWriter{}).Write([]byte(line)); err != nil {
		t.Fatal(err)
	}
	(&secureLog{}).Errorf("send failed: %s", "https://h.example/p?auth="+secret)
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(data), secret) || strings.Contains(string(data), "auth=") {
		t.Fatalf("the log kept upload authorisation: %s", data)
	}
	if !strings.Contains(string(data), "h.example") {
		t.Fatalf("the log lost the host: %s", data)
	}
}
