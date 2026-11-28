package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/coder/websocket"
	"go.mau.fi/mautrix-meta/pkg/instameow"
	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
	"go.mau.fi/mautrix-meta/pkg/messagix/dgw"
)

type typed struct {
	thread string
	typing bool
}

type fakeTypingClient struct {
	fakeWriteClient
	mu    sync.Mutex
	typed []typed
	err   error
}

func (c *fakeTypingClient) SetTyping(_ context.Context, thread string, typing bool) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.typed = append(c.typed, typed{thread, typing})
	return c.err
}

func (c *fakeTypingClient) calls() []typed {
	c.mu.Lock()
	defer c.mu.Unlock()
	return append([]typed(nil), c.typed...)
}

// The write fixture, with everybody but the account given the same Instagram id — distinct from every messaging id, so
// only the thread can say who it is — the account's own cookie naming it, and a connection that is up.
func typingFixture(t *testing.T) (*readOnlyAdapter, *fakeTypingClient, *bytes.Buffer) {
	t.Helper()
	a, read := chatFixture()
	var buffer bytes.Buffer
	a.out = &output{encoder: json.NewEncoder(&buffer)}
	client := &fakeTypingClient{fakeWriteClient: fakeWriteClient{fakeHistoryClient: *read}}
	for _, thread := range client.details {
		thread.ThreadFBID = "short-" + thread.ID
		thread.ThreadID = "long-" + thread.ID
		for _, user := range thread.Users {
			if user.InteropMessagingUserFBID != 1234 {
				user.ID = "2002"
			}
		}
	}
	a.client = client
	a.canMutate = true
	values := fakeCookies()
	values["ds_user_id"] = "123"
	jar, code := readCookies(values)
	if code != "" {
		t.Fatal(code)
	}
	a.jar = jar
	chatsOf(t, a, `{}`)
	a.snapshot.State = "connected"
	buffer.Reset()
	return a, client, &buffer
}

// The diagnostics' client cannot type at all — its SetTyping returns before the network, because typing is disabled —
// while the application's reaches for a connection. Proved offline, on the real upstream client, in both modes.
func TestReadClientCannotType(t *testing.T) {
	values := fakeCookies()
	values["ds_user_id"] = "123"
	jar, _ := readCookies(values)
	read, ok := newReadClient(jar, nil, nil).(*instameow.Client)
	if !ok {
		t.Fatal("the diagnostics' client is not upstream's")
	}
	if err := read.SetTyping(context.Background(), "1", true); err != nil {
		t.Fatalf("the diagnostics' client has typing enabled: %v", err)
	}
	app := newAppClient(jar, nil, func() {}).(*instameow.Client)
	if err := app.SetTyping(context.Background(), "1", true); err == nil || err.Error() != "connection context is not set" {
		t.Fatalf("the application's client does not have typing enabled: %v", err)
	}
	// And the adapter refuses in the diagnostics' mode before any client is asked.
	a, client, _ := typingFixture(t)
	a.canMutate = false
	if _, code := a.uiTyping(context.Background(), request{Params: json.RawMessage(`{"chatId":"direct","typing":true}`)}); code != "read_only" {
		t.Fatalf("typing was not refused in the diagnostics' mode: %q", code)
	}
	if len(client.calls()) != 0 {
		t.Fatal("a refused typing call reached the client")
	}
}

// A report names the thread by its long id and the sender by Instagram id; the app gets the conversation and the
// messaging id, and never the Instagram one.
func TestTypingMapsLongThreadAndInstagramSender(t *testing.T) {
	a, _, buffer := typingFixture(t)
	report := func(thread string, sender int64, status int) string {
		t.Helper()
		buffer.Reset()
		if err := a.handle(context.Background(), a.generation, &slidetypes.TypingNotification{
			ThreadID: thread, SenderID: sender, ActivityStatus: status, TTL: 25000,
		}); err != nil {
			t.Fatalf("a typing report failed the stream: %v", err)
		}
		return buffer.String()
	}
	events := a.snapshot.Events
	if got := report("long-direct", 2002, 1); got != `{"event":"typing","result":{"chatId":"direct","sender":"456","typing":true}}`+"\n" {
		t.Fatalf("typing was not mapped: %s", got)
	}
	if got := report("long-direct", 2002, 0); got != `{"event":"typing","result":{"chatId":"direct","sender":"456","typing":false}}`+"\n" {
		t.Fatalf("a stop was not mapped: %s", got)
	}
	if a.snapshot.Events != events {
		t.Fatal("a typing report was counted as a connection event")
	}
	for _, unplaced := range []struct {
		name   string
		thread string
		sender int64
	}{
		{"an unknown thread", "long-elsewhere", 2002},
		{"an unknown sender", "long-direct", 3003},
		{"a spam thread", "long-spam", 2002},
		{"a message request", "long-request", 2002},
		{"the short id", "short-direct", 2002},
	} {
		if got := report(unplaced.thread, unplaced.sender, 1); got != "" {
			t.Fatalf("%s was shown: %s", unplaced.name, got)
		}
	}
	if counts := a.requests.typing.counts(); counts.In != 2 || counts.Unplaced != 5 || counts.TTL != 25000 {
		t.Fatalf("typing was not counted: %+v", counts)
	}
	// This account typing on another device is neither shown nor counted as unplaced.
	if got := report("long-direct", 123, 1); got != "" || a.requests.typing.counts().Unplaced != 5 {
		t.Fatalf("this account's own typing was shown: %s", got)
	}
	// Nothing in the diagnostics' mode, even were a report to arrive.
	a.canMutate = false
	if got := report("long-direct", 2002, 1); got != "" {
		t.Fatalf("typing was shown in the diagnostics' mode: %s", got)
	}
	if strings.Contains(buffer.String(), "2002") {
		t.Fatal("the Instagram id reached the pipe")
	}
}

// Typing addresses the short id, as upstream does and as sending does; a request or spam thread is never told this
// account is typing; nothing is sent on a connection that is down, and repeated failures make it rest.
func TestTypingAddressesTheShortIdAndRefusesRequests(t *testing.T) {
	a, client, _ := typingFixture(t)
	typing := func(params string) (map[string]any, string) {
		t.Helper()
		result, code := a.uiTyping(context.Background(), request{Params: json.RawMessage(params)})
		answer, _ := result.(map[string]any)
		return answer, code
	}
	if answer, code := typing(`{"chatId":"direct","typing":true}`); code != "" || answer["sent"] != true {
		t.Fatalf("typing was not sent: %v %q", answer, code)
	}
	if answer, code := typing(`{"chatId":"direct","typing":false}`); code != "" || answer["sent"] != true {
		t.Fatalf("a stop was not sent: %v %q", answer, code)
	}
	if calls := client.calls(); len(calls) != 2 || calls[0] != (typed{"short-direct", true}) || calls[1] != (typed{"short-direct", false}) {
		t.Fatalf("typing addressed the wrong thread: %v", calls)
	}
	for _, refused := range []struct{ params, code string }{
		{`{"chatId":"spam","typing":true}`, "chat_not_writable"},
		{`{"chatId":"request","typing":true}`, "chat_unknown"}, // requests are not even loaded
		{`{"chatId":"nowhere","typing":true}`, "chat_unknown"},
		{`{"chatId":"direct"}`, "invalid_params"},
		{`{"typing":true}`, "invalid_params"},
	} {
		if _, code := typing(refused.params); code != refused.code {
			t.Fatalf("%s: %q, expected %q", refused.params, code, refused.code)
		}
	}
	if len(client.calls()) != 2 {
		t.Fatal("a refused typing call reached Instagram")
	}
	a.snapshot.State = "connection_failed"
	if answer, code := typing(`{"chatId":"direct","typing":true}`); code != "" || answer["sent"] != false || len(client.calls()) != 2 {
		t.Fatalf("typing was sent on a connection that is down: %v %q", answer, code)
	}
	a.snapshot.State = "connected"
	client.err = errors.New("synthetic-private-error")
	for i := 0; i < typingFailuresBeforeRest; i++ {
		if answer, code := typing(`{"chatId":"direct","typing":true}`); code != "" || answer["sent"] != false {
			t.Fatalf("a failed send was not reported as not sent: %v %q", answer, code)
		}
	}
	client.err = nil
	before := len(client.calls())
	if answer, _ := typing(`{"chatId":"direct","typing":true}`); answer["sent"] != false || len(client.calls()) != before {
		t.Fatal("typing did not rest after repeated failures")
	}
	counts := a.requests.typing.counts()
	if counts.Out != 2 || counts.Failed != typingFailuresBeforeRest || counts.Skipped != 2 {
		t.Fatalf("typing was not counted: %+v", counts)
	}
}

// A client whose connections are scripted: each Connect announces itself and then does what the test says.
type realtimeFake struct {
	fakeReadClient
	mu       sync.Mutex
	connects int
	outcome  func(n int, ctx context.Context, handler instameow.EventHandler)
	opened   chan int
}

func (c *realtimeFake) Connect(ctx context.Context) {
	c.mu.Lock()
	c.connects++
	n := c.connects
	c.mu.Unlock()
	c.outcome(n, ctx, c.handler)
	c.opened <- n
	<-ctx.Done()
}

func (c *realtimeFake) connected(n int, ctx context.Context, handler instameow.EventHandler) {
	_ = handler(ctx, &slidetypes.Connected{})
}

func realtimeFixture(t *testing.T) (*readOnlyAdapter, *realtimeFake, *[]time.Duration) {
	t.Helper()
	a, _, _ := testReadAdapter()
	fake := &realtimeFake{fakeReadClient: fakeReadClient{viewer: "123"}, opened: make(chan int, 16)}
	fake.outcome = fake.connected
	a.factory = func(_ *cookies.Cookies, handler instameow.EventHandler, _ func()) readClient {
		fake.handler = handler
		return fake
	}
	a.canMutate = true
	var waits []time.Duration
	var waitsMu sync.Mutex
	a.after = func(d time.Duration) <-chan time.Time {
		if d == realtimeConnectWait {
			return time.After(5 * time.Second)
		}
		waitsMu.Lock()
		waits = append(waits, d)
		waitsMu.Unlock()
		fired := make(chan time.Time, 1)
		fired <- time.Now()
		return fired
	}
	callRead(t, a, "login", loginParams())
	callRead(t, a, "connect", nil)
	<-fake.opened
	return a, fake, &waits
}

func (a *readOnlyAdapter) stateNow() string {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.snapshot.State
}

func waitFor(t *testing.T, what string, ok func() bool) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for !ok() {
		if time.Now().After(deadline) {
			t.Fatalf("never: %s", what)
		}
		time.Sleep(10 * time.Millisecond)
	}
}

// A dropped connection comes back after a wait, and keeps being tried further apart while it fails — without ever
// closing the session, which is what a failed first connect does. Nothing reopens after Instagram refused the session.
func TestRealtimeReopensOnlyAfterTransientDrops(t *testing.T) {
	a, fake, waits := realtimeFixture(t)
	defer a.close()
	// Two failed attempts, then a connection.
	fake.mu.Lock()
	fake.outcome = func(n int, ctx context.Context, handler instameow.EventHandler) {
		if n <= 3 {
			_ = handler(ctx, &slidetypes.Disconnected{Error: errors.New("synthetic-private-error")})
			return
		}
		_ = handler(ctx, &slidetypes.Connected{})
	}
	fake.mu.Unlock()
	a.mu.Lock()
	gen := a.generation
	a.mu.Unlock()
	if err := fake.handler(context.Background(), &slidetypes.Disconnected{Error: errors.New("synthetic-private-error")}); err == nil {
		t.Fatal("upstream's own retry was not stopped")
	}
	for want := 2; want <= 4; want++ {
		select {
		case n := <-fake.opened:
			if n != want {
				t.Fatalf("connection %d opened, expected %d", n, want)
			}
		case <-time.After(5 * time.Second):
			t.Fatalf("connection %d was never opened", want)
		}
	}
	waitFor(t, "connected again", func() bool { return a.stateNow() == "connected" })
	if a.client == nil || a.generation != gen {
		t.Fatal("a failed reopen closed the session")
	}
	if got := *waits; len(got) != 3 || got[0] != time.Minute || got[1] != 2*time.Minute || got[2] != 4*time.Minute {
		t.Fatalf("attempts were not spaced further apart: %v", got)
	}
	if a.requests.typing.counts().Reopened != 3 {
		t.Fatalf("reopens were not counted: %+v", a.requests.typing.counts())
	}
	// Instagram refusing the session is not a drop: nothing reopens after it, however it arrives.
	for _, refusal := range []slidetypes.ClientEvent{
		&slidetypes.AuthError{Error: errors.New("synthetic-private-error")},
		&slidetypes.Disconnected{Error: websocket.CloseError{Code: dgw.CloseStatusUnauthorized}},
	} {
		b, other, _ := realtimeFixture(t)
		_ = other.handler(context.Background(), refusal)
		time.Sleep(200 * time.Millisecond)
		other.mu.Lock()
		connects := other.connects
		other.mu.Unlock()
		if connects != 1 {
			t.Fatalf("%T reopened a refused session", refusal)
		}
		b.close()
	}
	// Nor does a resnapshot, whose cursor Instagram will not continue from.
	c, other, _ := realtimeFixture(t)
	_ = other.handler(context.Background(), &slidetypes.ResnapshotRequired{})
	time.Sleep(200 * time.Millisecond)
	if other.connects != 1 {
		t.Fatal("a resnapshot reopened the connection")
	}
	c.close()
}

// Closing ends the watch, so a reopen never outlives the session it belonged to.
func TestClosingStopsTheRealtimeWatch(t *testing.T) {
	a, fake, _ := realtimeFixture(t)
	a.after = func(time.Duration) <-chan time.Time { return make(chan time.Time) } // never fires by itself
	_ = fake.handler(context.Background(), &slidetypes.Disconnected{Error: errors.New("synthetic-private-error")})
	a.close()
	time.Sleep(100 * time.Millisecond)
	fake.mu.Lock()
	defer fake.mu.Unlock()
	if fake.connects != 1 {
		t.Fatal("the connection reopened after the session closed")
	}
}
