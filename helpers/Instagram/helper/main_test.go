package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"strings"
	"sync"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
)

func fakeCookies() map[string]string {
	values := map[string]string{}
	for _, name := range cookies.IGRequiredCookies {
		values[string(name)] = "synthetic-secret"
	}
	return values
}

func TestRequiredCookies(t *testing.T) {
	for _, name := range cookies.IGRequiredCookies {
		for _, empty := range []bool{false, true} {
			values := fakeCookies()
			if empty {
				values[string(name)] = ""
			} else {
				delete(values, string(name))
			}
			if jar, code := readCookies(values); jar != nil || code != "missing_cookies" {
				t.Fatal("incomplete cookies accepted")
			}
		}
	}
	if _, code := readCookies(map[string]string{"xs": "fake", "c_user": "fake", "datr": "fake"}); code != "missing_cookies" {
		t.Fatal("Messenger cookies accepted")
	}
	jar, code := readCookies(fakeCookies())
	if code != "" || jar.Platform != types.Instagram {
		t.Fatal("complete Instagram cookies rejected")
	}
}

// Drives a dispatcher the way the app does: requests go in, and input stays open until every answer is out. The end of
// input is teardown, so anything still queued when it arrives is answered without being run.
func exchange(t *testing.T, serve func(io.Reader, io.Writer) error, input []byte, replies int) *bytes.Buffer {
	t.Helper()
	inRead, inWrite := io.Pipe()
	outRead, outWrite := io.Pipe()
	served := make(chan error, 1)
	go func() {
		err := serve(inRead, outWrite)
		outWrite.Close()
		served <- err
	}()
	go func() { _, _ = inWrite.Write(input) }()
	var output bytes.Buffer
	decoder := json.NewDecoder(io.TeeReader(outRead, &output))
	for i := 0; i < replies; i++ {
		var reply json.RawMessage
		if err := decoder.Decode(&reply); err != nil {
			t.Fatalf("reply %d: %v", i, err)
		}
	}
	inWrite.Close()
	_, _ = io.Copy(&output, outRead)
	if err := <-served; err != nil {
		t.Fatal(err)
	}
	return &output
}

func TestLifecycleOverStdio(t *testing.T) {
	var input bytes.Buffer
	enc := json.NewEncoder(&input)
	login, _ := json.Marshal(map[string]any{"cookies": fakeCookies()})
	methods := []string{"hello", "connect", "session", "login", "session", "connect", "snapshot", "disconnect", "session", "disconnect"}
	for i, method := range methods {
		if err := enc.Encode(request{ID: i + 1, Method: method, Params: login}); err != nil {
			t.Fatal(err)
		}
	}
	output := exchange(t, run, input.Bytes(), len(methods))
	if strings.Contains(output.String(), "synthetic-secret") {
		t.Fatal("credentials escaped")
	}
	dec := json.NewDecoder(output)
	for i, method := range methods {
		var reply struct {
			ID     int
			Error  string
			Result json.RawMessage
		}
		if err := dec.Decode(&reply); err != nil {
			t.Fatal(err)
		}
		if reply.ID != i+1 {
			t.Fatal("lifecycle replies reordered")
		}
		if method == "connect" {
			if reply.Error != "offline_only" {
				t.Fatal("connect did not fail closed")
			}
		} else if reply.Error != "" {
			t.Fatal(reply.Error)
		}
		if method == "session" || method == "login" || method == "disconnect" {
			var status sessionStatus
			if err := json.Unmarshal(reply.Result, &status); err != nil {
				t.Fatal(err)
			}
			want := "disconnected"
			if i == 3 || i == 4 {
				want = "cookies_validated"
			}
			if status.State != want || status.Authenticated || status.Connected || !status.OfflineOnly {
				t.Fatal("offline validation misrepresented as authentication")
			}
		}
	}
	if _, err := dec.Token(); err != io.EOF {
		t.Fatal("unexpected output")
	}
}

func TestDispatchRejectsAndRecovers(t *testing.T) {
	input := "not json\n{\"id\":0,\"method\":\"hello\"}\n{\"cancel\":7}\n"
	// Everything the app might one day ask for and this helper still does not implement. ui.sendText, ui.markRead
	// and ui.startDirect have left this list because they exist now; the rest must still never reach the adapter, so
	// that adding one is a deliberate act rather than something that happens by a method name being recognized.
	// ui.typing stays: it is answered only where the adapter registers it beside the queue, never by being named.
	methods := []string{"ui.typing", "ui.updateChat", "ui.deleteChat", "ui.startGroup", "ui.acceptMessageRequest", "unknown"}
	for _, method := range methods {
		line, _ := json.Marshal(request{ID: 1, Method: method})
		input += string(line) + "\n"
	}
	input += "{\"id\":2,\"method\":\"hello\"}\n"
	calls := 0
	buffer := exchange(t, func(in io.Reader, out io.Writer) error {
		return dispatch(in, &output{encoder: json.NewEncoder(out)}, func(_ context.Context, r request) (any, string) {
			calls++
			return true, ""
		}, nil, nil)
	}, []byte(input), 2+len(methods)+1)
	if calls != 1 {
		t.Fatal("unsupported method reached adapter")
	}
	dec := json.NewDecoder(buffer)
	for i := 0; i < 2+len(methods)+1; i++ {
		var reply response
		if err := dec.Decode(&reply); err != nil {
			t.Fatal(err)
		}
		want := "unknown_method"
		if i < 2 {
			want = "invalid_request"
		}
		if i == 2+len(methods) {
			want = ""
		}
		if reply.Error != want {
			t.Fatal("incorrect fixed error")
		}
	}
}

func TestMalformedLoginAndCancelledCall(t *testing.T) {
	a := &adapter{}
	for _, raw := range []string{`{"cookies":1}`, `{"cookies":{"sessionid":false}}`, `{`} {
		if _, code := a.call(context.Background(), request{Method: "login", Params: json.RawMessage(raw)}); code != "invalid_params" {
			t.Fatal("malformed cookies accepted")
		}
	}
	for _, raw := range []string{`null`, `{}`, `{"cookies":{}}`} {
		if _, code := a.call(context.Background(), request{Method: "login", Params: json.RawMessage(raw)}); code != "missing_cookies" {
			t.Fatal("missing cookies accepted")
		}
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, code := a.call(ctx, request{Method: "login"}); code != "request_cancelled" {
		t.Fatal("cancellation ignored")
	}
}

type failingWriter struct{}

func (failingWriter) Write([]byte) (int, error) { return 0, errors.New("synthetic-private-error") }

func TestPipeFailures(t *testing.T) {
	if err := run(strings.NewReader("{\"id\":1,\"method\":\"hello\"}\n"), failingWriter{}); err == nil {
		t.Fatal("write failure ignored")
	}
	var buffer bytes.Buffer
	if err := run(strings.NewReader(strings.Repeat("x", 1024*1024+1)), &buffer); err == nil {
		t.Fatal("oversized frame accepted")
	}
	if buffer.String() != "{\"error\":\"input_failed\"}\n" {
		t.Fatal("input error was not sanitized")
	}
}

func TestCountsNeverSerializeEvents(t *testing.T) {
	var buffer bytes.Buffer
	a := &adapter{out: &output{encoder: json.NewEncoder(&buffer)}}
	events := []slidetypes.ClientEvent{
		&slidetypes.AuthError{Error: errors.New("synthetic-private-error")},
		&slidetypes.Disconnected{Error: errors.New("synthetic-private-error")},
		&slidetypes.ReconnectionStateUpdate{State: json.RawMessage(`{"cookie":"synthetic-secret"}`)},
		&slidetypes.Connected{SubscribedSeqID: 123456789},
		&slidetypes.Delta{}, &slidetypes.TypingNotification{},
		&slidetypes.SeqIDUpdate{}, &slidetypes.ResnapshotRequired{},
	}
	var workers sync.WaitGroup
	for _, event := range events {
		workers.Add(1)
		go func() {
			defer workers.Done()
			if err := a.handle(context.Background(), event); err != nil {
				t.Error(err)
			}
		}()
	}
	workers.Wait()
	want := strings.Repeat("{\"event\":\"counts\",\"result\":{\"events\":1}}\n", len(events))
	if buffer.String() != want {
		t.Fatal("event output was not counts only")
	}
	result, code := a.call(context.Background(), request{Method: "snapshot"})
	if code != "" || result.(summary).Events != uint64(len(events)) {
		t.Fatal("counts lost")
	}
	a.call(context.Background(), request{Method: "disconnect"})
	result, _ = a.call(context.Background(), request{Method: "snapshot"})
	if result.(summary).Events != 0 {
		t.Fatal("disconnect did not reset counts")
	}
}
