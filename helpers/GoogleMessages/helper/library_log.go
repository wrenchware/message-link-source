package main

import (
	"encoding/json"
	"regexp"
	"strconv"
	"strings"

	"github.com/rs/zerolog"
)

// The library's own warnings and errors, reduced to their level and message. They were discarded, which is why a
// pairing the phone dropped on 2026-09-23 left nothing to say why. Only a message made of words passes: the library's
// fields and any message carrying ids, numbers, addresses or text from the network are dropped, and what passes goes
// to the app as an event rather than to a file.
var libraryMessage = regexp.MustCompile(`^[A-Za-z][A-Za-z ,'()-]{0,95}$`)

// The HTTP status Google answered with; only the number passes. The listen request's line carries it as status_code;
// otherwise it is read from the error, in any of the library's shapes: "HTTP 401: 16: …" (a RequestError), "unexpected
// http 401" and "http 401 while polling" (an HTTPError). On 2026-09-23 acknowledgements failed every five seconds for
// eight minutes before a restart met "unauthenticated", and the line could not say whether Google had answered 401 all
// along.
var libraryStatus = regexp.MustCompile(`(?i)\bhttp ([1-5][0-9]{2})\b`)

type libraryLine struct {
	Level   string `json:"level"`
	Message string `json:"message"`
	Status  int    `json:"status,omitempty"`
}

type libraryWriter struct {
	out *output
	// Told when Google refuses an acknowledgement as unauthenticated: the library only logs it and retries every five
	// seconds, and the sign-in has expired. Optional.
	refused func(message string, status int)
}

func (w libraryWriter) Write(p []byte) (int, error) {
	var line struct {
		libraryLine
		Error      string          `json:"error"`
		StatusCode json.RawMessage `json:"status_code"`
	}
	if json.Unmarshal(p, &line) == nil && line.Level != "" {
		message := line.Message
		if !libraryMessage.MatchString(message) {
			message = "(other)"
		}
		status := lineStatus(line.StatusCode, line.Error)
		if w.out != nil {
			w.out.write(response{Event: "library", Result: libraryLine{line.Level, message, status}})
		}
		if w.refused != nil && line.Level == "error" && status == 401 && strings.HasPrefix(line.Message, "Failed to send acks") {
			w.refused(message, status)
		}
	}
	return len(p), nil
}

func lineStatus(field json.RawMessage, text string) int {
	if status, err := strconv.Atoi(string(field)); err == nil && status >= 100 && status <= 599 {
		return status
	}
	if found := libraryStatus.FindStringSubmatch(text); found != nil {
		status, _ := strconv.Atoi(found[1])
		return status
	}
	return 0
}

func libraryLogger(out *output, refused func(message string, status int)) zerolog.Logger {
	return zerolog.New(libraryWriter{out, refused}).Level(zerolog.WarnLevel)
}
