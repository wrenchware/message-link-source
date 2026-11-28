package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"io"
	"path/filepath"
	"regexp"
	"sync"
	"sync/atomic"
	"time"
)

const (
	exitStopped    = 0
	exitFailed     = 1
	exitUsage      = 2
	exitSignedOut  = 3
	exitUnreadable = 4
)

// The words a reply's error is one of.
const (
	errBadRequest    = "bad_request"
	errUnknownMethod = "unknown_method"
	errNotRunning    = "not_running"
	errTimeout       = "timeout"
	errFailed        = "failed"
)

// maxLine bounds one line of input. A request is a few dozen bytes; a longer line is answered bad_request and skipped.
const maxLine = 64 << 10

// node is what run drives: the package's node (tsNode), or a stand-in in the tests.
type node interface {
	OpenDoor(secret []byte) (int, error)
	Start(dir, hostname string) error
	// Unreadable says whether a Start error is the sealed state that cannot be opened.
	Unreadable(err error) bool
	Status() string
	Peers() (string, error)
	Path(name, id string) string
	Login() error
	NetworkChanged()
	Logout() error
	Stop() error
}

var hostnamePattern = regexp.MustCompile(`^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$`)

// parseArgs takes exactly --state <absolute folder> and --hostname <one DNS label>.
func parseArgs(args []string) (state, hostname string, ok bool) {
	flags := flag.NewFlagSet("tailnet-helper", flag.ContinueOnError)
	flags.SetOutput(io.Discard)
	flags.StringVar(&state, "state", "", "")
	flags.StringVar(&hostname, "hostname", "", "")
	if err := flags.Parse(args); err != nil || flags.NArg() != 0 {
		return "", "", false
	}
	if state == "" || !filepath.IsAbs(state) || !hostnamePattern.MatchString(hostname) {
		return "", "", false
	}
	return filepath.Clean(state), hostname, true
}

// readLine reads one line without its ending. tooLong is a line past maxLine, whose rest is skipped; ended is the input
// ending (a last line without an ending is still read).
func readLine(input *bufio.Reader) (line []byte, tooLong, ended bool) {
	data, err := input.ReadSlice('\n')
	if err == bufio.ErrBufferFull {
		for err == bufio.ErrBufferFull {
			_, err = input.ReadSlice('\n')
		}
		return nil, true, err != nil
	}
	if err != nil && len(data) == 0 {
		return nil, false, true
	}
	return bytes.TrimRight(data, "\r\n"), false, false
}

// output writes the JSON lines, one whole line at a time.
type output struct {
	mu  sync.Mutex
	out io.Writer
}

func (o *output) send(v any) {
	b, err := json.Marshal(v)
	if err != nil {
		return
	}
	o.mu.Lock()
	defer o.mu.Unlock()
	o.out.Write(append(b, '\n'))
}

type doorEvent struct {
	Event string `json:"event"`
	Port  int    `json:"port"`
}

type statusEvent struct {
	Event  string          `json:"event"`
	Status json.RawMessage `json:"status"`
}

type result struct {
	ID int64 `json:"id"`
	// Result is null when nil.
	Result json.RawMessage `json:"result"`
}

type failure struct {
	ID    int64  `json:"id"`
	Error string `json:"error"`
}

type request struct {
	ID     *int64          `json:"id"`
	Method string          `json:"method"`
	Params json.RawMessage `json:"params"`
}

type helper struct {
	node    node
	out     *output
	closing atomic.Bool
	// statusMu keeps one status check at a time, so the same change is not said twice.
	statusMu   sync.Mutex
	lastStatus string
	poke       chan struct{}
	running    sync.WaitGroup
}

// run is the whole program over its arguments, input and output; every is how often the status is checked.
func run(args []string, in io.Reader, out io.Writer, n node, every time.Duration) int {
	state, hostname, ok := parseArgs(args)
	if !ok {
		return exitUsage
	}
	input := bufio.NewReaderSize(in, maxLine)
	line, tooLong, ended := readLine(input)
	if tooLong {
		return exitUsage
	}
	if ended {
		return exitStopped
	}
	secret, err := hex.DecodeString(string(line))
	if err != nil || len(secret) != 32 {
		return exitUsage
	}
	h := &helper{node: n, out: &output{out: out}, poke: make(chan struct{}, 1)}
	port, err := n.OpenDoor(secret)
	if err != nil {
		return exitFailed
	}
	// The door answers "off" until the node has started, so the app can use the port at once.
	h.out.send(doorEvent{Event: "door", Port: port})
	if err := n.Start(state, hostname); err != nil {
		if n.Unreadable(err) {
			return exitUnreadable
		}
		return exitFailed
	}
	h.checkStatus()
	ctx, cancel := context.WithCancel(context.Background())
	var watching sync.WaitGroup
	watching.Add(1)
	go func() {
		defer watching.Done()
		h.watch(ctx, every)
	}()
	stop := func(code int) int {
		cancel()
		watching.Wait()
		h.running.Wait()
		return code
	}

	for {
		line, tooLong, ended := readLine(input)
		switch {
		case ended:
			h.closing.Store(true)
			n.Stop()
			return stop(exitStopped)
		case tooLong:
			h.out.send(failure{ID: 0, Error: errBadRequest})
			continue
		case len(bytes.TrimSpace(line)) == 0:
			continue
		}
		var req request
		if err := json.Unmarshal(line, &req); err != nil || req.ID == nil {
			h.out.send(failure{ID: 0, Error: errBadRequest})
			continue
		}
		if req.Method == "logout" {
			if h.closing.Swap(true) {
				h.out.send(failure{ID: *req.ID, Error: errNotRunning})
				continue
			}
			// The node signs out of Tailscale and closes (Logout closes it either way). The app then removes the state
			// folder, so even a sign-out Tailscale did not hear leaves nothing on this PC that is the node.
			if err := n.Logout(); err != nil {
				h.out.send(failure{ID: *req.ID, Error: h.word(err)})
			} else {
				h.out.send(result{ID: *req.ID})
			}
			h.checkStatus()
			n.Stop()
			return stop(exitSignedOut)
		}
		h.running.Add(1)
		go func(req request) {
			defer h.running.Done()
			h.answer(req)
		}(req)
	}
}

// answer runs one request other than logout and sends its reply.
func (h *helper) answer(req request) {
	id := *req.ID
	switch req.Method {
	case "peers", "path", "login", "networkChanged":
	default:
		h.out.send(failure{ID: id, Error: errUnknownMethod})
		return
	}
	if h.closing.Load() {
		h.out.send(failure{ID: id, Error: errNotRunning})
		return
	}
	switch req.Method {
	case "peers":
		list, err := h.node.Peers()
		if err != nil {
			h.out.send(failure{ID: id, Error: h.word(err)})
			return
		}
		if !json.Valid([]byte(list)) {
			h.out.send(failure{ID: id, Error: errFailed})
			return
		}
		h.out.send(result{ID: id, Result: json.RawMessage(list)})
	case "path":
		var params struct {
			Name string `json:"name"`
			ID   string `json:"id"`
		}
		if len(req.Params) == 0 || json.Unmarshal(req.Params, &params) != nil || params.Name == "" && params.ID == "" {
			h.out.send(failure{ID: id, Error: errBadRequest})
			return
		}
		path, _ := json.Marshal(h.node.Path(params.Name, params.ID))
		h.out.send(result{ID: id, Result: path})
	case "login":
		err := h.node.Login()
		if err != nil {
			h.out.send(failure{ID: id, Error: h.word(err)})
		} else {
			h.out.send(result{ID: id})
		}
		// The new sign-in link is in the status; the app should not wait a second for it.
		select {
		case h.poke <- struct{}{}:
		default:
		}
	case "networkChanged":
		h.node.NetworkChanged()
		h.out.send(result{ID: id})
	}
}

// word is a failed call's error word.
func (h *helper) word(err error) string {
	switch {
	case h.closing.Load():
		return errNotRunning
	case errors.Is(err, context.DeadlineExceeded):
		return errTimeout
	}
	return errFailed
}

// watch checks the status every interval, and at once when poked, until ctx ends.
func (h *helper) watch(ctx context.Context, every time.Duration) {
	ticker := time.NewTicker(every)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		case <-h.poke:
		}
		h.checkStatus()
	}
}

// checkStatus sends the node's status when it differs from the last one sent.
func (h *helper) checkStatus() {
	h.statusMu.Lock()
	defer h.statusMu.Unlock()
	status := h.node.Status()
	if status == h.lastStatus || !json.Valid([]byte(status)) {
		return
	}
	h.lastStatus = status
	h.out.send(statusEvent{Event: "status", Status: json.RawMessage(status)})
}
