package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/instameow"
	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
)

func syntheticState(id string, timestamp time.Time) json.RawMessage {
	return namedState(id, "", "", timestamp)
}

// The same state with the viewer's own handle and name filled in, which is where a resumed session reads the
// account it is signed in as.
func namedState(id, username, fullName string, timestamp time.Time) json.RawMessage {
	configs := httpclient.NewConfigs(nil)
	configs.BrowserConfigTable.PolarisViewer.ID = id
	configs.BrowserConfigTable.PolarisViewer.Data.Username = username
	configs.BrowserConfigTable.PolarisViewer.Data.FullName = fullName
	raw, _ := json.Marshal(struct {
		Configs   *httpclient.Configs
		Timestamp time.Time
	}{configs, timestamp})
	return raw
}

type fakeReadClient struct {
	handler                instameow.EventHandler
	indexCalls, stateCalls int
	seq                    int64
	ts                     time.Time
	viewer                 string
	username, fullName     string
	err                    error
}

func (c *fakeReadClient) LoadIndex(ctx context.Context) (*types.PolarisViewer, *slidetypes.Mailbox, error) {
	c.indexCalls++
	if c.err != nil {
		return nil, nil, c.err
	}
	_ = c.handler(ctx, &slidetypes.ReconnectionStateUpdate{State: namedState(c.viewer, c.username, c.fullName, time.Now())})
	return &types.PolarisViewer{ID: c.viewer, Data: types.ViewerData{Username: c.username, FullName: c.fullName}},
		&slidetypes.Mailbox{UQSeqID: 42}, nil
}
func (c *fakeReadClient) LoadState(json.RawMessage) error { c.stateCalls++; return nil }
func (c *fakeReadClient) DumpState() (json.RawMessage, error) {
	return namedState(c.viewer, c.username, c.fullName, time.Now()), nil
}
func (c *fakeReadClient) SetSeqID(seq int64, ts time.Time) { c.seq, c.ts = seq, ts }
func (c *fakeReadClient) Connect(ctx context.Context) {
	_ = c.handler(ctx, &slidetypes.SeqIDUpdate{SeqID: 73, Timestamp: time.Now()})
	_ = c.handler(ctx, &slidetypes.Connected{})
	<-ctx.Done()
}
func (c *fakeReadClient) Disconnect() {}

func testReadAdapter() (*readOnlyAdapter, *fakeReadClient, *bytes.Buffer) {
	var buffer bytes.Buffer
	fake := &fakeReadClient{viewer: "123", username: "sam.okafor", fullName: "Sam Okafor"}
	a := &readOnlyAdapter{out: &output{encoder: json.NewEncoder(&buffer)}}
	a.factory = func(_ *cookies.Cookies, handler instameow.EventHandler, _ func()) readClient {
		fake.handler = handler
		return fake
	}
	return a, fake, &buffer
}

func loginParams() json.RawMessage {
	values := fakeCookies()
	values["ds_user_id"] = "123"
	raw, _ := json.Marshal(map[string]any{"cookies": values})
	return raw
}

func callRead(t *testing.T, a *readOnlyAdapter, method string, params json.RawMessage) any {
	t.Helper()
	result, code := a.call(context.Background(), request{Method: method, Params: params})
	if code != "" {
		t.Fatalf("%s: %s", method, code)
	}
	return result
}

func TestReadOnlyRestartRestoresSeparateSequence(t *testing.T) {
	a, first, diagnostics := testReadAdapter()
	defer a.close()
	if _, code := a.call(context.Background(), request{Method: "session"}); code != "login_required" {
		t.Fatal("session exported before login")
	}
	login := callRead(t, a, "login", loginParams()).(readSnapshot)
	if !login.IdentityVerified || login.Resumed || first.indexCalls != 1 {
		t.Fatal("fresh login did not verify index")
	}
	connected := callRead(t, a, "connect", nil).(readSnapshot)
	if connected.State != "connected" {
		t.Fatal("connect returned before subscription")
	}
	session := callRead(t, a, "session", nil).(savedSession)
	if session.SeqID != 73 || session.SeqTS.IsZero() || session.AccountKey != accountKey("123") {
		t.Fatal("session lost sequence or account partition")
	}
	callRead(t, a, "disconnect", nil)
	if strings.Contains(diagnostics.String(), "synthetic-secret") || strings.Contains(diagnostics.String(), "connectionState") {
		t.Fatal("private data in diagnostics")
	}
	b, second, _ := testReadAdapter()
	defer b.close()
	params, _ := json.Marshal(map[string]any{"session": session})
	restored := callRead(t, b, "login", params).(readSnapshot)
	if !restored.Resumed || second.indexCalls != 0 || second.stateCalls != 1 || second.seq != 73 || !second.ts.Equal(session.SeqTS) {
		t.Fatal("restart did not restore cached state and cursor")
	}
	callRead(t, b, "connect", nil)
	callRead(t, b, "disconnect", nil)
	// A delayed callback from the old client cannot populate a disconnected session.
	_ = second.handler(context.Background(), &slidetypes.Connected{})
	if got := callRead(t, b, "snapshot", nil).(snapshotReply); got.State != "disconnected" || got.Events != 0 {
		t.Fatal("late callback escaped generation guard")
	}
}

func TestReadOnlyIdentityAndFixedFailures(t *testing.T) {
	for _, test := range []struct {
		viewer string
		err    error
		code   string
	}{
		{"456", nil, "identity_mismatch"},
		{"123", fmt.Errorf("synthetic-private-data: %w", httpclient.ErrChallengeRequired), "challenge_required"},
		{"123", errors.New("synthetic-private-data"), "connection_failed"},
	} {
		a, fake, buffer := testReadAdapter()
		fake.viewer, fake.err = test.viewer, test.err
		_, code := a.call(context.Background(), request{Method: "login", Params: loginParams()})
		if code != test.code || a.client != nil {
			t.Fatal("failed login retained session or leaked error")
		}
		if strings.Contains(buffer.String(), "synthetic-private-data") {
			t.Fatal("upstream error leaked")
		}
	}
}

func TestReadOnlyRejectsMalformedAndRefreshesExpiredState(t *testing.T) {
	values := fakeCookies()
	values["ds_user_id"] = "123"
	for _, raw := range []json.RawMessage{nil, []byte(`null`), []byte(`{}`), []byte(`{"Configs":null}`)} {
		if code := validateState(raw, "123"); code != "invalid_session" {
			t.Fatal("unsafe LoadState input accepted")
		}
	}
	for _, test := range []struct {
		key, id string
		age     time.Duration
		want    string
		refresh bool
	}{
		{accountKey("456"), "123", 0, "identity_mismatch", false},
		{accountKey("123"), "456", 0, "identity_mismatch", false},
		{accountKey("123"), "123", 25 * time.Hour, "", true},
	} {
		a, fake, _ := testReadAdapter()
		saved := savedSession{Version: 2, AccountKey: test.key, Cookies: values, State: syntheticState(test.id, time.Now().Add(-test.age)), SeqID: 42, SeqTS: time.Now()}
		params, _ := json.Marshal(map[string]any{"session": saved})
		_, code := a.call(context.Background(), request{Method: "login", Params: params})
		if code != test.want {
			t.Fatalf("got %s want %s", code, test.want)
		}
		if test.refresh && (fake.indexCalls != 1 || fake.stateCalls != 0) {
			t.Fatal("expired state used without refresh")
		}
		if !test.refresh && (fake.indexCalls != 0 || fake.stateCalls != 0) {
			t.Fatal("wrong account reached transport")
		}
		a.close()
	}
}

func TestReadOnlyConnectionFailureStopsAndNeverRetries(t *testing.T) {
	a, fake, buffer := testReadAdapter()
	defer a.close()
	callRead(t, a, "login", loginParams())
	err := fake.handler(context.Background(), &slidetypes.AuthError{Error: errors.New("synthetic-private-data")})
	if err == nil || err.Error() != "connection_failed" {
		t.Fatal("upstream retry not stopped")
	}
	if strings.Contains(buffer.String(), "synthetic-private-data") {
		t.Fatal("error leaked")
	}
	if _, code := a.call(context.Background(), request{Method: "connect"}); code != "connection_failed" {
		t.Fatal("failure not returned")
	}
	if a.client != nil {
		t.Fatal("failed connection retained")
	}
}

// Settings names the account Instagram is signed in as. The app cannot work it out for itself: unlike Google Messages
// and Messenger, Instagram does not reliably list the viewer among a thread's participants, which is why that card
// stayed blank while the other two filled in. The viewer record does carry it, on both paths into a session, and a
// resume must not spend a round trip to find out — the state it already validates has the answer in it.
func TestReadOnlyNamesTheSignedInAccount(t *testing.T) {
	a, first, diagnostics := testReadAdapter()
	defer a.close()
	login := callRead(t, a, "login", loginParams()).(readSnapshot)
	if login.Account != "sam.okafor" || login.AccountName != "Sam Okafor" || first.indexCalls != 1 {
		t.Fatal("fresh login did not name the account from the viewer it just read")
	}
	callRead(t, a, "connect", nil)
	if got := callRead(t, a, "snapshot", nil).(snapshotReply); got.Account != "sam.okafor" {
		t.Fatal("connecting lost the account")
	}
	session := callRead(t, a, "session", nil).(savedSession)
	callRead(t, a, "disconnect", nil)

	b, second, _ := testReadAdapter()
	defer b.close()
	params, _ := json.Marshal(map[string]any{"session": session})
	restored := callRead(t, b, "login", params).(readSnapshot)
	if restored.Account != "sam.okafor" || restored.AccountName != "Sam Okafor" {
		t.Fatal("a resumed session did not name the account")
	}
	if second.indexCalls != 0 {
		t.Fatal("naming the account cost a round trip on resume")
	}
	// It is an identity, so it travels the private pipe and nothing else.
	if strings.Contains(diagnostics.String(), "sam.okafor") || strings.Contains(diagnostics.String(), "Sam Okafor") {
		t.Fatal("the account's name reached diagnostics")
	}

	// A session whose viewer record names nobody says nothing, rather than inventing a name from somewhere else.
	c, third, _ := testReadAdapter()
	defer c.close()
	third.username, third.fullName = "", ""
	anonymous := callRead(t, c, "login", loginParams()).(readSnapshot)
	if anonymous.Account != "" || anonymous.AccountName != "" {
		t.Fatal("an unnamed viewer produced a name")
	}
}
