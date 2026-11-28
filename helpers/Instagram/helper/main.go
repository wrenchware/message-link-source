// Offline by default. The explicit --read-only flag enables stage-2 authentication.
package main

import (
	"context"
	"encoding/json"
	"io"
	"os"
	"runtime/debug"
	"sync"

	"go.mau.fi/mautrix-meta/pkg/instameow"
	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
)

type request struct {
	ID     int             `json:"id"`
	Method string          `json:"method"`
	Params json.RawMessage `json:"params"`
}

type response struct {
	ID     int    `json:"id,omitempty"`
	Event  string `json:"event,omitempty"`
	Result any    `json:"result,omitempty"`
	Error  string `json:"error,omitempty"`
}

type output struct {
	mu      sync.Mutex
	encoder *json.Encoder
}

func (o *output) write(r response) error {
	o.mu.Lock()
	defer o.mu.Unlock()
	return o.encoder.Encode(r)
}

type summary struct {
	Events uint64 `json:"events"`
}

type sessionStatus struct {
	State         string `json:"state"`
	Authenticated bool   `json:"authenticated"`
	Connected     bool   `json:"connected"`
	OfflineOnly   bool   `json:"offlineOnly"`
}

type adapter struct {
	mu        sync.Mutex
	validated bool
	totals    summary
	out       *output
}

func readCookies(raw map[string]string) (*cookies.Cookies, string) {
	jar := &cookies.Cookies{Platform: types.Instagram}
	values := make(map[cookies.MetaCookieName]string, len(raw))
	for key, value := range raw {
		values[cookies.MetaCookieName(key)] = value
	}
	jar.UpdateValues(values)
	if len(jar.GetMissingCookieNames()) != 0 {
		return nil, "missing_cookies"
	}
	return jar, ""
}

func (a *adapter) status() sessionStatus {
	state := "disconnected"
	if a.validated {
		state = "cookies_validated"
	}
	return sessionStatus{State: state, OfflineOnly: true}
}

func (a *adapter) call(ctx context.Context, r request) (any, string) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if ctx.Err() != nil {
		return nil, "request_cancelled"
	}
	switch r.Method {
	case "hello":
		return struct {
			Protocol    int    `json:"protocol"`
			Library     string `json:"library"`
			Platform    string `json:"platform"`
			OfflineOnly bool   `json:"offlineOnly"`
		}{1, "mautrix-meta v0.2611.1-0.20261126163501-16724d697cdc", "instagram", true}, ""
	case "login":
		var params struct {
			Cookies map[string]string `json:"cookies"`
		}
		if json.Unmarshal(r.Params, &params) != nil {
			return nil, "invalid_params"
		}
		if _, code := readCookies(params.Cookies); code != "" {
			return nil, code
		}
		// Presence validation is not authentication. Do not retain credentials.
		a.validated = true
		return a.status(), ""
	case "connect":
		return nil, "offline_only"
	case "disconnect":
		a.validated = false
		a.totals = summary{}
		return a.status(), ""
	case "session":
		// No cookie export, DumpState, LoadState or persistence in stage 1.
		return a.status(), ""
	case "snapshot":
		return a.totals, ""
	default:
		return nil, "unknown_method"
	}
}

// Match instameow's callback now without constructing a transport. Never inspect,
// format or serialize the event: it may contain credentials, identities or text.
var _ instameow.EventHandler = (*adapter)(nil).handle

func (a *adapter) handle(ctx context.Context, event slidetypes.ClientEvent) error {
	if ctx.Err() != nil {
		return ctx.Err()
	}
	if event == nil {
		return nil
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	a.totals.Events++
	return a.out.write(response{Event: "counts", Result: summary{Events: 1}})
}

func run(in io.Reader, out io.Writer) error {
	o := &output{encoder: json.NewEncoder(out)}
	a := &adapter{out: o}
	return dispatch(in, o, a.call, nil, nil)
}

func main() {
	// Runtime dumps can contain private protocol buffers. Never print them.
	debug.SetTraceback("none")
	var err error
	// --read-only keeps its meaning and cannot mutate, so any diagnostic run on it stays provably incapable
	// of changing the account. --app is the mode the application uses, and it can send.
	if len(os.Args) == 2 && os.Args[1] == "--read-only" {
		err = runReadOnly(os.Stdin, os.Stdout, false)
	} else if len(os.Args) == 2 && os.Args[1] == "--app" {
		err = runReadOnly(os.Stdin, os.Stdout, true)
	} else if len(os.Args) == 1 {
		err = run(os.Stdin, os.Stdout)
	} else {
		os.Exit(2)
	}
	if err != nil {
		os.Exit(2)
	}
}
