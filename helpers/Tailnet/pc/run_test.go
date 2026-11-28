package main

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"io"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

var errSealed = errors.New("sealed state unreadable")

// fakeNode stands in for the package's node.
type fakeNode struct {
	mu       sync.Mutex
	status   string
	peers    string
	peersErr error
	loginErr error
	startErr error
	doorErr  error
	calls    []string
	secret   []byte
	started  [2]string
}

func (f *fakeNode) record(call string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls = append(f.calls, call)
}

func (f *fakeNode) called(call string) bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, c := range f.calls {
		if c == call {
			return true
		}
	}
	return false
}

func (f *fakeNode) setStatus(s string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.status = s
}

func (f *fakeNode) OpenDoor(secret []byte) (int, error) {
	f.record("door")
	f.secret = secret
	return 49152, f.doorErr
}

func (f *fakeNode) Start(dir, hostname string) error {
	f.record("start")
	f.started = [2]string{dir, hostname}
	return f.startErr
}

func (f *fakeNode) Unreadable(err error) bool { return errors.Is(err, errSealed) }

func (f *fakeNode) Status() string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.status
}

func (f *fakeNode) Peers() (string, error) {
	f.record("peers")
	return f.peers, f.peersErr
}

func (f *fakeNode) Path(name, id string) string {
	f.record("path " + name + "/" + id)
	return "relayed"
}

func (f *fakeNode) Login() error {
	f.record("login")
	f.setStatus(`{"state":"NeedsLogin","login":"https://login.example/a","lock":"unknown"}`)
	return f.loginErr
}

func (f *fakeNode) NetworkChanged() { f.record("networkChanged") }

func (f *fakeNode) Logout() error {
	f.record("logout")
	f.setStatus(`{"state":"Stopped","lock":"unknown"}`)
	return nil
}

func (f *fakeNode) Stop() error {
	f.record("stop")
	return nil
}

const secretLine = "0707070707070707070707070707070707070707070707070707070707070707"

// session runs the program against f with its input and output in the test's hands.
type session struct {
	t     *testing.T
	in    *io.PipeWriter
	lines chan string
	code  chan int
}

func start(t *testing.T, f *fakeNode, args ...string) *session {
	t.Helper()
	if args == nil {
		args = []string{"--state", filepath.Join(t.TempDir(), "Tailnet"), "--hostname", "messagelink-desk"}
	}
	inR, inW := io.Pipe()
	outR, outW := io.Pipe()
	s := &session{t: t, in: inW, lines: make(chan string, 64), code: make(chan int, 1)}
	go func() {
		scanner := bufio.NewScanner(outR)
		for scanner.Scan() {
			s.lines <- scanner.Text()
		}
		close(s.lines)
	}()
	go func() {
		s.code <- run(args, inR, outW, f, 20*time.Millisecond)
		outW.Close()
	}()
	t.Cleanup(func() { inW.Close() })
	return s
}

func (s *session) send(line string) {
	s.t.Helper()
	if _, err := io.WriteString(s.in, line+"\n"); err != nil {
		s.t.Fatal(err)
	}
}

// next is the next line of output, which must be JSON.
func (s *session) next() map[string]any {
	s.t.Helper()
	select {
	case line, ok := <-s.lines:
		if !ok {
			s.t.Fatal("the output ended")
		}
		var v map[string]any
		if err := json.Unmarshal([]byte(line), &v); err != nil {
			s.t.Fatalf("a line that is not JSON: %q", line)
		}
		return v
	case <-time.After(5 * time.Second):
		s.t.Fatal("no output")
	}
	return nil
}

// reply waits for the reply to id, passing over status events (returned as well).
func (s *session) reply(id float64) (map[string]any, []map[string]any) {
	s.t.Helper()
	var statuses []map[string]any
	for {
		v := s.next()
		if v["event"] == "status" {
			statuses = append(statuses, v)
			continue
		}
		if v["id"] != id {
			s.t.Fatalf("expected the reply to %v, got %v", id, v)
		}
		return v, statuses
	}
}

func (s *session) exit() int {
	s.t.Helper()
	select {
	case code := <-s.code:
		return code
	case <-time.After(5 * time.Second):
		s.t.Fatal("the program did not end")
	}
	return -1
}

func running() *fakeNode {
	return &fakeNode{status: `{"state":"Running","user":"someone","lock":"off"}`,
		peers: `[{"name":"messagelink-server","dns":"messagelink-server.t.ts.net","address":"100.64.0.9","id":"n1","online":true,"mine":true,"tagged":false,"os":"linux"}]`}
}

func TestParseArgs(t *testing.T) {
	state := filepath.Join(t.TempDir(), "Tailnet")
	good := [][]string{
		{"--state", state, "--hostname", "messagelink-desk"},
		{"--hostname", "messagelink-desk", "--state", state},
		{"--state=" + state, "--hostname=a"},
	}
	for _, args := range good {
		if got, host, ok := parseArgs(args); !ok || got != state || host == "" {
			t.Errorf("%q refused", args)
		}
	}
	bad := [][]string{
		nil,
		{"--state", state},
		{"--hostname", "messagelink-desk"},
		{"--state", "Tailnet", "--hostname", "messagelink-desk"},
		{"--state", state, "--hostname", "two.labels"},
		{"--state", state, "--hostname", "-dash"},
		{"--state", state, "--hostname", strings.Repeat("a", 64)},
		{"--state", state, "--hostname", "messagelink-desk", "extra"},
		{"--state", state, "--hostname", "messagelink-desk", "--other", "x"},
	}
	for _, args := range bad {
		if _, _, ok := parseArgs(args); ok {
			t.Errorf("%q taken", args)
		}
	}
}

func TestBadArgumentsAndSecret(t *testing.T) {
	f := running()
	if code := run([]string{"--state", "relative"}, strings.NewReader(secretLine+"\n"), io.Discard, f, time.Second); code != exitUsage {
		t.Errorf("bad arguments: exit %d", code)
	}
	args := []string{"--state", filepath.Join(t.TempDir(), "Tailnet"), "--hostname", "messagelink-desk"}
	for _, secret := range []string{"not hex at all", secretLine[:62], secretLine + "07", strings.Repeat("0", maxLine+10)} {
		if code := run(args, strings.NewReader(secret+"\n"), io.Discard, f, time.Second); code != exitUsage {
			t.Errorf("secret of %d characters: exit %d", len(secret), code)
		}
	}
	if code := run(args, strings.NewReader(""), io.Discard, f, time.Second); code != exitStopped {
		t.Errorf("no input: exit %d", code)
	}
	if f.called("door") || f.called("start") {
		t.Error("the door or the node opened without a good secret")
	}
}

func TestStartFailures(t *testing.T) {
	for _, c := range []struct {
		err  error
		want int
	}{{errSealed, exitUnreadable}, {errors.New("other"), exitFailed}} {
		f := running()
		f.startErr = c.err
		s := start(t, f)
		s.send(secretLine)
		if door := s.next(); door["event"] != "door" || door["port"] != float64(49152) {
			t.Fatalf("first line %v", door)
		}
		if code := s.exit(); code != c.want {
			t.Errorf("%v: exit %d, want %d", c.err, code, c.want)
		}
	}
	f := running()
	f.doorErr = errors.New("no port")
	s := start(t, f)
	s.send(secretLine)
	if code := s.exit(); code != exitFailed || f.called("start") {
		t.Errorf("door failure: exit %d", code)
	}
}

func TestRequests(t *testing.T) {
	f := running()
	state := filepath.Join(t.TempDir(), "Tailnet")
	s := start(t, f, "--state", state, "--hostname", "messagelink-desk")
	s.send(secretLine)
	if door := s.next(); door["event"] != "door" || door["port"] != float64(49152) {
		t.Fatalf("first line %v", door)
	}
	if len(f.secret) != 32 || f.secret[0] != 7 {
		t.Errorf("the door got secret %x", f.secret)
	}
	if status := s.next(); status["event"] != "status" || status["status"].(map[string]any)["state"] != "Running" {
		t.Fatalf("second line %v", status)
	}
	if f.started != [2]string{state, "messagelink-desk"} {
		t.Errorf("started with %q", f.started)
	}

	s.send(`{"id":1,"method":"peers","params":{}}`)
	if r, _ := s.reply(1); r["result"].([]any)[0].(map[string]any)["name"] != "messagelink-server" {
		t.Errorf("peers: %v", r)
	}
	s.send(`{"id":2,"method":"path","params":{"name":"messagelink-server","id":"n1"}}`)
	if r, _ := s.reply(2); r["result"] != "relayed" || !f.called("path messagelink-server/n1") {
		t.Errorf("path: %v", r)
	}
	s.send(`{"id":3,"method":"path","params":{}}`)
	if r, _ := s.reply(3); r["error"] != errBadRequest {
		t.Errorf("path without a name: %v", r)
	}
	s.send(`{"id":4,"method":"sendEverything"}`)
	if r, _ := s.reply(4); r["error"] != errUnknownMethod {
		t.Errorf("unknown method: %v", r)
	}
	s.send(`not json`)
	if r, _ := s.reply(0); r["error"] != errBadRequest {
		t.Errorf("bad line: %v", r)
	}
	s.send(``)
	s.send(`{"method":"peers"}`)
	if r, _ := s.reply(0); r["error"] != errBadRequest {
		t.Errorf("no id: %v", r)
	}
	s.send(`{"id":5,"method":"networkChanged"}`)
	if r, _ := s.reply(5); r["error"] != nil || !f.called("networkChanged") {
		t.Errorf("networkChanged: %v", r)
	}

	f.peersErr = context.DeadlineExceeded
	s.send(`{"id":6,"method":"peers"}`)
	if r, _ := s.reply(6); r["error"] != errTimeout {
		t.Errorf("peers timing out: %v", r)
	}
	f.peersErr = errors.New("broken")
	s.send(`{"id":7,"method":"peers"}`)
	if r, _ := s.reply(7); r["error"] != errFailed {
		t.Errorf("peers failing: %v", r)
	}

	// Login: the reply, then the new status with its link at once.
	s.send(`{"id":8,"method":"login"}`)
	r, statuses := s.reply(8)
	if v, has := r["result"]; !has || v != nil {
		t.Errorf("login: %v", r)
	}
	for len(statuses) == 0 {
		if v := s.next(); v["event"] == "status" {
			statuses = append(statuses, v)
		}
	}
	if statuses[0]["status"].(map[string]any)["state"] != "NeedsLogin" {
		t.Errorf("after login: %v", statuses)
	}

	// A change of the node's own is said within a check.
	f.setStatus(`{"state":"Running","user":"someone","lock":"signed"}`)
	if v := s.next(); v["event"] != "status" || v["status"].(map[string]any)["lock"] != "signed" {
		t.Errorf("status change: %v", v)
	}

	// The input ending stops the node.
	s.in.Close()
	if code := s.exit(); code != exitStopped || !f.called("stop") {
		t.Errorf("end of input: exit %d", code)
	}
}

func TestLogout(t *testing.T) {
	f := running()
	s := start(t, f)
	s.send(secretLine)
	s.next()
	s.next()
	s.send(`{"id":9,"method":"logout"}`)
	r, _ := s.reply(9)
	if v, has := r["result"]; !has || v != nil {
		t.Errorf("logout: %v", r)
	}
	if v := s.next(); v["event"] != "status" || v["status"].(map[string]any)["state"] != "Stopped" {
		t.Errorf("after logout: %v", v)
	}
	if code := s.exit(); code != exitSignedOut || !f.called("logout") || !f.called("stop") {
		t.Errorf("logout: exit %d", code)
	}
}

// Nothing but whole JSON lines comes out, even when replies run at once.
func TestOutputIsJSONLines(t *testing.T) {
	f := running()
	s := start(t, f)
	s.send(secretLine)
	s.next()
	s.next()
	for i := 1; i <= 50; i++ {
		s.send(`{"id":` + strconv.Itoa(100+i) + `,"method":"peers"}`)
	}
	for i := 0; i < 50; i++ {
		if v := s.next(); v["result"] == nil && v["event"] == nil {
			t.Fatalf("line %v", v)
		}
	}
	s.in.Close()
	s.exit()
}
