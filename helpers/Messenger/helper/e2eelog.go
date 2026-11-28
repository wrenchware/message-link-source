package main

import (
	"fmt"
	"regexp"
	"encoding/json"
	"strings"
	"unicode/utf8"

	"github.com/rs/zerolog"

	waLog "go.mau.fi/whatsmeow/util/log"
)

// The encrypted connection's own account of itself. It was thrown away — the library was given a logger that does
// nothing — so a socket that would not come up, and a send Facebook refused, both arrived here as a single line
// saying only that something had failed. Warnings and errors are kept now, which is where the reason lives.
//
// What it says is library text: socket states, protocol errors, stanza names. It can also carry account numbers and
// message ids, which do not belong in a log that is safe to read, so every run of digits is replaced by its length
// before anything is written.
type secureLog struct{ module string }

// Five or more, not three: a connection failure's reason is a three-digit code, and masking those threw away the one
// thing worth having. Account numbers, message ids and phone numbers are all far longer than this.
var digits = regexp.MustCompile(`[0-9]{5,}`)

// An address. A failed upload's error carries the address it went to, and that address carries the upload's own
// authorisation in its query string (whatsmeow's upload URL has an `auth` parameter), so a URL is reduced to its scheme
// and host: nothing after the host is kept, because a path can hold a token as well.
var addresses = regexp.MustCompile(`(?i)([a-z][a-z0-9+.-]*)://([^/?#\s"'<>)\]]*)[^\s"'<>)\]]*`)

// A parameter named for a secret, left over where no scheme made it an address (a bare "/upload?auth=..." path, or a
// library quoting just the query).
var secretParameters = regexp.MustCompile(`(?i)\b(auth|token|access_token|signature|sig|key|secret|password|cookie|authorization)=[^\s&"'<>)]*`)

// A long unbroken run of the characters tokens, keys, hashes and base64 are made of (standard and URL-safe). Words and
// error sentences are broken by spaces and punctuation well before this length.
var tokens = regexp.MustCompile(`[A-Za-z0-9_+/=-]{20,}`)

// Long runs of digits are account numbers, message ids and phone numbers. Their length is all that is useful.
// Addresses, secret parameters and long tokens go first, then the digits, then the line is shortened, so a token cut
// in half by the length limit cannot survive it.
func scrub(text string) string {
	scrubbed := addresses.ReplaceAllString(text, "$1://$2/<address>")
	scrubbed = secretParameters.ReplaceAllString(scrubbed, "$1=<removed>")
	scrubbed = tokens.ReplaceAllStringFunc(scrubbed, func(run string) string {
		return fmt.Sprintf("<%d characters>", len(run))
	})
	scrubbed = digits.ReplaceAllStringFunc(scrubbed, func(run string) string {
		return fmt.Sprintf("<%d digits>", len(run))
	})
	if len(scrubbed) > 300 {
		cut := 300
		for cut > 0 && !utf8.RuneStart(scrubbed[cut]) {
			cut--
		}
		return scrubbed[:cut]
	}
	return scrubbed
}

func (s *secureLog) write(level, msg string, args ...any) {
	text := msg
	if len(args) > 0 {
		text = fmt.Sprintf(msg, args...)
	}
	where := s.module
	if where == "" {
		where = "e2ee"
	}
	logf("%s %s: %s", where, level, scrub(strings.TrimSpace(text)))
}

func (s *secureLog) Errorf(msg string, args ...any) { s.write("error", msg, args...) }
func (s *secureLog) Warnf(msg string, args ...any)  { s.write("warning", msg, args...) }

// Not kept: these are a running commentary on every stanza, and the log is meant to stay readable.
func (s *secureLog) Infof(msg string, args ...any)  {}
func (s *secureLog) Debugf(msg string, args ...any) {}

func (s *secureLog) Sub(module string) waLog.Logger {
	if s.module == "" {
		return &secureLog{module: "e2ee/" + module}
	}
	return &secureLog{module: s.module + "/" + module}
}

// The library's own logger. messagix is handed one at login and passes it on to the encrypted client, so a logger
// that does nothing silences both — which is why a socket that would not come up had nothing to say for itself.
//
// zerolog writes a JSON object per event. Only the few fields worth having are taken from it: a whole line could
// carry anything the library chose to attach, and this log has to stay safe to read.
type libraryWriter struct{}

func (libraryWriter) Write(line []byte) (int, error) {
	var event map[string]any
	if json.Unmarshal(line, &event) != nil {
		return len(line), nil
	}
	text, _ := event["message"].(string)
	component, _ := event["component"].(string)
	level, _ := event["level"].(string)
	if component == "" {
		component = "messenger"
	}
	if level == "" {
		level = "error"
	}
	if failure, ok := event["error"].(string); ok && failure != "" {
		text = text + ": " + failure
	}
	if text == "" {
		return len(line), nil
	}
	logf("%s %s: %s", component, level, scrub(strings.TrimSpace(text)))
	return len(line), nil
}

// Warnings and errors only. Anything below is a running commentary on every request and stanza.
func libraryLogger() zerolog.Logger {
	return zerolog.New(libraryWriter{}).Level(zerolog.WarnLevel)
}
