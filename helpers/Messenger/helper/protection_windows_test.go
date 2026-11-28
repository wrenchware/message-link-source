package main

import (
	"bytes"
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"testing"
	"unsafe"

	"golang.org/x/sys/windows"
)

// sealWith is protectLocal with a chosen prefix and entropy, so a test can write a cell in a form the helper no longer
// writes. Synthetic text only.
func sealWith(t *testing.T, plain []byte, prefix, entropy []byte) []byte {
	t.Helper()
	payload := append([]byte{1}, plain...)
	in, salt := dataBlob(payload), dataBlob(entropy)
	var out windows.DataBlob
	if err := windows.CryptProtectData(&in, nil, &salt, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out); err != nil {
		t.Fatal("protection failed")
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	return append(append([]byte{}, prefix...), unsafe.Slice(out.Data, int(out.Size))...)
}

// The C# reader's active declarations, comments stripped and spacing free: a stale value left in a comment cannot
// pass, and reformatting cannot fail it.
var (
	csharpLineComment   = regexp.MustCompile(`//[^\n]*`)
	csharpBlockComment  = regexp.MustCompile(`(?s)/\*.*?\*/`)
	csharpPrefixDecl    = regexp.MustCompile(`\bbyte\s*\[\s*\]\s+Prefix\s*=\s*\[([^\]]*)\]\s*;`)
	csharpEntropyDecl   = regexp.MustCompile(`\bbyte\s*\[\s*\]\s+Entropy\s*=\s*Encoding\s*\.\s*ASCII\s*\.\s*GetBytes\s*\(\s*"([^"\\]*)"\s*\)\s*;`)
	csharpByteCharValue = regexp.MustCompile(`^\(\s*byte\s*\)\s*'(.)'$`)
)

func readerDeclarations(t *testing.T) ([]byte, []byte) {
	t.Helper()
	source, err := os.ReadFile(filepath.Join("..", "..", "..", "src", "MessageLink.Windows", "MessengerSealedCells.cs"))
	if err != nil {
		t.Fatal("reader source missing")
	}
	code := csharpLineComment.ReplaceAllString(csharpBlockComment.ReplaceAllString(string(source), ""), "")
	prefixes, entropies := csharpPrefixDecl.FindAllStringSubmatch(code, -1), csharpEntropyDecl.FindAllStringSubmatch(code, -1)
	if len(prefixes) != 1 || len(entropies) != 1 {
		t.Fatalf("the reader declares %d prefixes and %d entropies, not one each", len(prefixes), len(entropies))
	}
	var prefix []byte
	for _, item := range strings.Split(prefixes[0][1], ",") {
		item = strings.TrimSpace(item)
		if quoted := csharpByteCharValue.FindStringSubmatch(item); quoted != nil {
			prefix = append(prefix, quoted[1][0])
		} else if number, err := strconv.ParseUint(item, 10, 8); err == nil {
			prefix = append(prefix, byte(number))
		} else {
			t.Fatal("the reader's prefix has an item this check does not read")
		}
	}
	return prefix, []byte(entropies[0][1])
}

func TestWindowsProtectionConstants(t *testing.T) {
	if !bytes.Equal(protectedPrefix, []byte{'M', 'L', 'P', 1}) {
		t.Fatal("prefix changed")
	}
	if string(protectedEntropy) != "Message Link Messenger retained history v1" {
		t.Fatal("entropy changed")
	}
	// The app's reader keeps its own copy; the contract harness compares them too, this catches a drift here first.
	prefix, entropy := readerDeclarations(t)
	if !bytes.Equal(prefix, protectedPrefix) || !bytes.Equal(entropy, protectedEntropy) {
		t.Fatal("the app's reader does not use the helper's prefix and entropy")
	}
}

func TestSealFormsAreToldApart(t *testing.T) {
	own, err := protectLocal([]byte("synthetic"))
	if err != nil {
		t.Fatal("protection failed")
	}
	dpapi := own[len(protectedPrefix):]
	for _, c := range []struct {
		label string
		cell  []byte
		form  string
	}{
		{"own", own, sealOwn},
		{"retired", append([]byte{'N', 'B', 'P', 1}, dpapi...), sealRetired},
		{"server", []byte{'M', 'S', 'P', 2, 9, 9, 9}, sealUnknown},
		{"bare dpapi", dpapi, sealUnknown},
		{"dpapi under another prefix", append([]byte{'X', 'Y', 'Z', 1}, dpapi...), sealUnknown},
		{"plaintext", []byte("plain synthetic text"), sealPlain},
		{"near prefix", []byte{'M', 'L', 'P', 2, 9, 9, 9}, sealPlain},
		{"empty", nil, sealPlain},
	} {
		if got := sealForm(c.cell); got != c.form {
			t.Fatalf("%s read as %q", c.label, got)
		}
	}
}

func TestWindowsProtectionRoundTrip(t *testing.T) {
	for _, plain := range [][]byte{nil, []byte("offline synthetic text")} {
		sealed, err := protectLocal(plain)
		if err != nil || !bytes.HasPrefix(sealed, []byte{'M', 'L', 'P', 1}) {
			t.Fatal("protection failed")
		}
		opened, err := unprotectLocal(sealed)
		if err != nil || !bytes.Equal(opened, plain) {
			t.Fatal("round trip failed")
		}
		// The same form written by hand opens too: what the harness seals for the app's reader is this.
		opened, err = unprotectLocal(sealWith(t, plain, []byte{'M', 'L', 'P', 1}, []byte("Message Link Messenger retained history v1")))
		if err != nil || !bytes.Equal(opened, plain) {
			t.Fatal("hand-sealed cell did not open")
		}
	}
	if text, err := unprotectString(nil); err != nil || text != "" {
		t.Fatal("empty cell was not empty")
	}
}

func TestWindowsProtectionRefusesRetiredSeal(t *testing.T) {
	before := refusedRetired.Load()
	retired := sealWith(t, []byte("synthetic"), []byte{'N', 'B', 'P', 1}, []byte("retired Messenger kept-history entropy v1"))
	if plain, err := unprotectLocal(retired); err == nil || plain != nil || !errors.Is(err, errRetiredSeal) {
		t.Fatal("a retired cell was opened")
	}
	if text, err := unprotectString(retired); err == nil || text != "" {
		t.Fatal("a retired cell was read as text")
	}
	if refusedRetired.Load() != before+2 {
		t.Fatal("retired refusals were not counted")
	}
	// The new prefix over the old entropy is not a way in either: DPAPI refuses it.
	if _, err := unprotectLocal(sealWith(t, []byte("synthetic"), protectedPrefix, []byte("retired Messenger kept-history entropy v1"))); err == nil {
		t.Fatal("the old entropy opened")
	}
}

func TestWindowsProtectionRefusesUnknownSealsAndPlaintext(t *testing.T) {
	before := refusalsNow()
	for _, cell := range [][]byte{
		{'M', 'S', 'P', 2, 9, 9, 9},
		sealWith(t, []byte("synthetic"), []byte{'X', 'Y', 'Z', 1}, protectedEntropy),
	} {
		if plain, err := unprotectLocal(cell); err == nil || plain != nil || !errors.Is(err, errUnknownSeal) {
			t.Fatal("an unknown cell was opened")
		}
		if text, err := unprotectString(cell); err == nil || text != "" {
			t.Fatal("an unknown cell was read as text")
		}
	}
	for _, cell := range [][]byte{[]byte("plain synthetic text"), {'M', 'L', 'P', 2, 9, 9, 9}} {
		if plain, err := unprotectLocal(cell); err == nil || plain != nil || !errors.Is(err, errNotProtected) {
			t.Fatal("a plaintext cell was opened")
		}
		if text, err := unprotectString(cell); err == nil || text != "" {
			t.Fatal("a plaintext cell was read as text")
		}
	}
	if got := refusalsNow().since(before); got != (refusalCounts{unknown: 4, plain: 4}) {
		t.Fatalf("refusals counted %+v", got)
	}
	// The right prefix over bytes DPAPI did not write is refused by DPAPI, not passed through.
	if _, err := unprotectLocal(append(append([]byte{}, protectedPrefix...), []byte("not a dpapi blob")...)); err == nil {
		t.Fatal("a forged cell was opened")
	}
}

func TestWindowsProtectionRefusalLogsOneFixedLinePerReason(t *testing.T) {
	path := filepath.Join(t.TempDir(), "helper.log")
	file, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	diagnostics.mu.Lock()
	previous, previousWritten := diagnostics.file, diagnostics.written
	diagnostics.file, diagnostics.written = file, 0
	diagnostics.mu.Unlock()
	refusalLogged.Clear()
	defer func() {
		diagnostics.mu.Lock()
		diagnostics.file, diagnostics.written = previous, previousWritten
		diagnostics.mu.Unlock()
		file.Close()
	}()
	retired := sealWith(t, []byte("synthetic secret words"), []byte{'N', 'B', 'P', 1}, []byte("retired Messenger kept-history entropy v1"))
	server := append([]byte{'M', 'S', 'P', 2}, []byte("synthetic secret words")...)
	for range 3 {
		unprotectString(retired)
		unprotectString(server)
		unprotectString([]byte("plain synthetic secret words"))
	}
	file.Sync()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var lines []string
	for _, line := range strings.Split(strings.TrimSpace(string(data)), "\n") {
		// Each line starts with its time; the rest is the fixed text.
		if _, rest, ok := strings.Cut(line, " "); ok {
			lines = append(lines, rest)
		}
	}
	if len(lines) != 3 || !strings.HasPrefix(lines[0], "kept cell refused reason=retired-seal count=") ||
		!strings.HasPrefix(lines[1], "kept cell refused reason=unknown-seal count=") ||
		!strings.HasPrefix(lines[2], "kept cell refused reason=plain count=") {
		t.Fatalf("refusals logged %d lines in an unexpected form", len(lines))
	}
	if strings.Contains(string(data), "secret") || strings.Contains(string(data), "synthetic") {
		t.Fatal("a refused cell's content reached the log")
	}
}
