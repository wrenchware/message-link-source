package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"sync"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/mautrix-meta/pkg/instameow"
	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
	"go.mau.fi/mautrix-meta/pkg/messagix/dgw"
	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
)

// Deliberately exposes no account mutation methods.
type readClient interface {
	LoadIndex(context.Context) (*types.PolarisViewer, *slidetypes.Mailbox, error)
	LoadState(json.RawMessage) error
	DumpState() (json.RawMessage, error)
	SetSeqID(int64, time.Time)
	Connect(context.Context)
	Disconnect()
}

// The last argument is called each time upstream reopens the typing stream by itself (flap.go).
type clientFactory func(*cookies.Cookies, instameow.EventHandler, func()) readClient

// The diagnostics' client. Typing stays disabled, so it opens no typing socket at all and its SetTyping returns before
// touching the network: the stage-2 and stage-3 probes stay socket-identical to what they always were.
func newReadClient(jar *cookies.Cookies, handler instameow.EventHandler, _ func()) readClient {
	return instameow.NewClient(instameow.ClientParams{
		Cookies: jar, Log: zerolog.Nop(), EventHandler: handler, DisableTyping: true,
	})
}

// The application's client, which is the same but for typing: it subscribes to typing reports on a second socket and
// sends this account's typing on a third, opened the first time it is needed. That is upstream's default, and a subset
// of the sockets Instagram's own web client keeps open. The logger stays silent: nothing the client says is kept, and
// all it is listened to for is the typing stream reopening.
func newAppClient(jar *cookies.Cookies, handler instameow.EventHandler, reopened func()) readClient {
	return instameow.NewClient(instameow.ClientParams{
		Cookies: jar, Log: appLogger(reopened), EventHandler: handler,
	})
}

// The envelope's format, written into every saved session and the only one login resumes. 2026-10-12: a server updated
// nightly can hand a PC back an envelope of a newer format than the PC's helper reads, so the app keeps the same number
// (InstagramEnvelope.Version) and refuses such a move back; a harness check fails when the two differ.
const savedSessionVersion = 2

// Private IPC only. The host protects this envelope for the Windows user; it is
// never an event, status, diagnostic or plaintext file. SeqID is NOT in DumpState.
type savedSession struct {
	Version    int               `json:"version"`
	AccountKey string            `json:"accountKey"`
	Cookies    map[string]string `json:"cookies"`
	Claim      string            `json:"claim"`
	State      json.RawMessage   `json:"connectionState"`
	SeqID      int64             `json:"seqID"`
	SeqTS      time.Time         `json:"seqTimestamp"`
}

type readSnapshot struct {
	State            string `json:"state"`
	IdentityVerified bool   `json:"identityVerified"`
	Resumed          bool   `json:"resumed"`
	Threads          int    `json:"threads"`
	Events           uint64 `json:"events"`
	// Who this session is signed in as, for the app to show in Settings. Taken from the viewer record the session
	// already carries, never from a thread's participants: Instagram's own account is not reliably among them, and
	// the viewer record is what the identity check at login already reads and compares.
	Account     string `json:"account,omitempty"`
	AccountName string `json:"accountName,omitempty"`
}

type readOnlyAdapter struct {
	op         sync.Mutex // lifecycle calls; never taken by the client callback
	mu         sync.Mutex
	out        *output
	factory    clientFactory
	client     readClient
	jar        *cookies.Cookies
	key        string
	state      json.RawMessage
	seqID      int64
	seqTS      time.Time
	snapshot   readSnapshot
	generation uint64
	ready      chan string
	stop       context.CancelFunc
	done       chan struct{}
	probe      *historyProbe
	chats      *chatStore
	media      *mediaStore
	// The client for media off Meta's hosts, which carries no session (mediafetch.go); nil but in tests.
	anonymousMedia *http.Client
	// Whether this process may change the account at all. The diagnostics run with it false, so they cannot mutate
	// even by mistake: a mode that merely never asks is not the same as one that would refuse.
	canMutate bool
	// The file being sent, as its chunks arrive. One at a time: the app sends one and waits for the answer.
	upload []byte
	// Every request made of Instagram, for the life of the process. Never reset by login or close.
	requests requestLedger
	// Sending typing: consecutive failures, and until when it rests after too many. Under mu.
	typingFailures  int
	typingRestUntil time.Time
	// The realtime connection's reopening, in --app mode. Closed by close(), which ends any wait for the next attempt.
	// refusedRealtime is set when Instagram refused the session rather than dropping the connection: an auth error on
	// any request, the socket closed as unauthorized, or a list read refused (uiChats), which is what stops the app's
	// poll. Nothing reopens after that; only signing in again can help.
	realtimeQuit    chan struct{}
	refusedRealtime bool
	// How often upstream reopened the typing stream in the last hour, for the life of the process (flap.go).
	flaps streamFlaps
	// Stands in for time.After in tests, so the waits between attempts need not be waited out. Nil in the helper.
	after func(time.Duration) <-chan time.Time
}

func accountKey(id string) string {
	sum := sha256.Sum256([]byte("instagram:" + id))
	return hex.EncodeToString(sum[:])
}

func failureCode(err error) string {
	switch {
	case errors.Is(err, context.Canceled):
		return "request_cancelled"
	case errors.Is(err, context.DeadlineExceeded):
		return "request_timeout"
	case errors.Is(err, httpclient.ErrChallengeRequired):
		return "challenge_required"
	case errors.Is(err, httpclient.ErrCheckpointRequired):
		return "checkpoint_required"
	case errors.Is(err, httpclient.ErrConsentRequired):
		return "consent_required"
	case errors.Is(err, httpclient.ErrTokenInvalidated):
		return "session_expired"
	case errors.Is(err, httpclient.ErrAccountSuspended):
		return "account_suspended"
	default:
		return "connection_failed"
	}
}

// Instagram refusing the session, as against failing to answer. Only signing in again changes it, and the app's poll
// stops on exactly these codes (InstagramPollFailure.StopsPolling).
func refusal(code string) bool {
	switch code {
	case "session_expired", "checkpoint_required", "challenge_required", "consent_required", "account_suspended":
		return true
	}
	return false
}

// Validate pointers and identity before upstream LoadState (which dereferences
// Configs unconditionally). Old state is refreshed via LoadIndex with the cookies.
func validateState(raw json.RawMessage, id string) string {
	var state struct {
		Configs   *httpclient.Configs
		Timestamp time.Time
	}
	if json.Unmarshal(raw, &state) != nil || state.Configs == nil || state.Configs.BrowserConfigTable == nil ||
		state.Configs.Bitmap == nil || state.Configs.CSRBitmap == nil || state.Timestamp.IsZero() {
		return "invalid_session"
	}
	if state.Configs.BrowserConfigTable.PolarisViewer.ID != id {
		return "identity_mismatch"
	}
	if state.Timestamp.After(time.Now().Add(time.Minute)) {
		return "invalid_session"
	}
	if time.Since(state.Timestamp) > instameow.MaxCachedStateAge {
		return "state_expired"
	}
	return ""
}

// The account's own handle and name, from the same viewer record validateState compares the id against — so a session
// that resumes at all has this, and a session that does not never reaches here. Reading it costs no round trip.
// A thread's participants are deliberately not used: Instagram does not reliably list the viewer among them, which is
// why the app showed nothing for Instagram while the two providers that do list it were named.
func viewerFromState(raw json.RawMessage) (username, fullName string) {
	var state struct {
		Configs *httpclient.Configs
	}
	if json.Unmarshal(raw, &state) != nil || state.Configs == nil || state.Configs.BrowserConfigTable == nil {
		return "", ""
	}
	viewer := state.Configs.BrowserConfigTable.PolarisViewer
	return viewer.Data.Username, viewer.Data.FullName
}

func (a *readOnlyAdapter) call(ctx context.Context, r request) (result any, code string) {
	a.op.Lock()
	defer a.op.Unlock()
	// Only fixed codes cross the pipe, including unexpected upstream state shapes.
	defer func() {
		if recover() != nil {
			a.close()
			result, code = nil, "internal_error"
		}
	}()
	if ctx.Err() != nil {
		return nil, failureCode(ctx.Err())
	}
	switch r.Method {
	case "hello":
		return map[string]any{"protocol": 2, "library": "mautrix-meta v0.2609.1-0.20261012222411-4b3889e607c0", "platform": "instagram", "readOnly": !a.canMutate}, ""
	case "login":
		return a.login(ctx, r.Params)
	case "connect":
		return a.connect(ctx)
	case "ui.chats":
		return a.uiChats(ctx, r.Params)
	case "ui.chat":
		return a.uiChat(ctx, r.Params)
	case "ui.messages":
		return a.uiMessages(ctx, r.Params)
	case "ui.mediaRead":
		return a.mediaRead(ctx, r.Params)
	case "ui.sendText":
		return a.uiSendText(ctx, r.Params)
	case "ui.markRead":
		return a.uiMarkRead(ctx, r.Params)
	case "ui.react":
		return a.uiReact(ctx, r.Params)
	case "ui.editMessage":
		return a.uiEditMessage(ctx, r.Params)
	case "ui.mediaStage":
		return a.uiMediaStage(r.Params)
	case "ui.sendMedia":
		return a.uiSendMedia(ctx, r.Params)
	case "ui.deleteMessage":
		return a.uiDeleteMessage(ctx, r.Params)
	case "ui.searchUsers":
		return a.uiSearchUsers(ctx, r.Params)
	case "ui.startDirect":
		return a.uiStartDirect(ctx, r.Params)
	case "ui.startDirectWithText":
		return a.uiStartDirectWithText(ctx, r.Params)
	case "ui.createGroup":
		return a.uiCreateGroup(ctx, r.Params)
	case "probe.scan", "probe.observe":
		// The diagnostics' alone. A scan pages through the mailbox, up to 120 conversations and their history: traffic
		// on the live account that nothing in the application asks for, so in --app mode the probes do not exist.
		if a.canMutate {
			return nil, "unknown_method"
		}
		if r.Method == "probe.scan" {
			return a.scan(ctx, r.Params)
		}
		return a.observe(ctx, r.Params)
	case "disconnect":
		a.close()
		return readSnapshot{State: "disconnected"}, ""
	case "snapshot":
		a.mu.Lock()
		defer a.mu.Unlock()
		return snapshotReply{a.snapshot, a.requests.counts(), a.requests.typing.counts(), a.requests.deltas.counts()}, ""
	case "session":
		a.mu.Lock()
		defer a.mu.Unlock()
		if a.client == nil || !a.snapshot.IdentityVerified || len(a.state) == 0 {
			return nil, "login_required"
		}
		values := map[string]string{}
		for k, v := range a.jar.GetAll() {
			values[string(k)] = v
		}
		return savedSession{savedSessionVersion, a.key, values, a.jar.GetWWWClaim(), append(json.RawMessage(nil), a.state...), a.seqID, a.seqTS}, ""
	default:
		return nil, "unknown_method"
	}
}

func (a *readOnlyAdapter) login(ctx context.Context, raw json.RawMessage) (any, string) {
	if a.client != nil {
		return nil, "already_logged_in"
	}
	var params struct {
		Cookies map[string]string `json:"cookies"`
		Session *savedSession     `json:"session"`
	}
	if json.Unmarshal(raw, &params) != nil {
		return nil, "invalid_params"
	}
	if params.Session != nil && params.Cookies != nil {
		return nil, "invalid_params"
	}
	if params.Session != nil {
		params.Cookies = params.Session.Cookies
	}
	jar, code := readCookies(params.Cookies)
	if code != "" {
		return nil, code
	}
	id := jar.Get(cookies.IGCookieDSUserID)
	if jar.GetUserID() <= 0 {
		return nil, "invalid_cookies"
	}
	key := accountKey(id)
	resume := false
	if s := params.Session; s != nil {
		if s.Version != savedSessionVersion || s.AccountKey != key {
			return nil, "identity_mismatch"
		}
		code = validateState(s.State, id)
		if code != "" && code != "state_expired" {
			return nil, code
		}
		if s.SeqID < 0 || s.SeqTS.After(time.Now().Add(time.Minute)) {
			return nil, "invalid_session"
		}
		resume = code == "" && s.SeqID > 0 && !s.SeqTS.IsZero()
		jar.SetWWWClaim(s.Claim)
	}
	a.mu.Lock()
	a.generation++
	generation := a.generation
	a.ready = make(chan string, 1)
	a.snapshot = readSnapshot{State: "authenticating"}
	a.refusedRealtime = false
	a.mu.Unlock()
	client := a.factory(jar, func(ctx context.Context, event slidetypes.ClientEvent) error { return a.handle(ctx, generation, event) },
		func() { a.streamReopened(generation) })
	a.client, a.jar, a.key = client, jar, key
	ok := false
	defer func() {
		if !ok {
			a.close()
		}
	}()
	if resume {
		s := params.Session
		if err := client.LoadState(s.State); err != nil {
			return nil, "invalid_session"
		}
		client.SetSeqID(s.SeqID, s.SeqTS)
		a.mu.Lock()
		a.state, a.seqID, a.seqTS = append(json.RawMessage(nil), s.State...), s.SeqID, s.SeqTS
		username, name := viewerFromState(s.State)
		a.snapshot = readSnapshot{State: "session_loaded", IdentityVerified: true, Resumed: true,
			Account: username, AccountName: name}
		a.mu.Unlock()
	} else {
		viewer, mailbox, err := client.LoadIndex(ctx)
		a.requests.note(ctx, &a.requests.index, err != nil)
		if err != nil {
			return nil, failureCode(err)
		}
		if viewer == nil || viewer.ID != id {
			return nil, "identity_mismatch"
		}
		if mailbox == nil {
			return nil, "invalid_response"
		}
		state, err := client.DumpState()
		if err != nil || validateState(state, id) != "" {
			return nil, "session_failed"
		}
		a.mu.Lock()
		a.state = append(json.RawMessage(nil), state...)
		a.seqID, a.seqTS = mailbox.UQSeqID, time.Now()
		// LoadIndex just answered with the viewer itself, so a fresh login takes it from there rather than reading
		// back the state it has only this moment written.
		a.snapshot = readSnapshot{State: "authenticated", IdentityVerified: true, Threads: len(mailbox.ThreadsByFolder.Edges),
			Account: viewer.Data.Username, AccountName: viewer.Data.FullName}
		a.mu.Unlock()
	}
	ok = true
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.snapshot, ""
}

func (a *readOnlyAdapter) connect(ctx context.Context) (any, string) {
	if a.client == nil {
		return nil, "login_required"
	}
	if a.stop != nil {
		return nil, "already_connecting"
	}
	ctx, cancel := context.WithTimeout(ctx, 45*time.Second)
	defer cancel()
	lifetime, stop := context.WithCancel(context.Background())
	a.mu.Lock()
	a.stop, a.done = stop, make(chan struct{})
	client, done := a.client, a.done
	a.mu.Unlock()
	go func() {
		defer close(done)
		defer func() {
			if recover() != nil {
				select {
				case a.ready <- "internal_error":
				default:
				}
			}
		}()
		client.Connect(lifetime)
	}()
	var code string
	select {
	case code = <-a.ready:
	case <-ctx.Done():
		code = failureCode(ctx.Err())
	case <-done:
		select {
		case code = <-a.ready:
		default:
			code = "connection_failed"
		}
	}
	if code != "" {
		a.close()
		return nil, code
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	// The application keeps its realtime connection, which typing arrives on, through a dropped network; the
	// diagnostics observe one connection and nothing more.
	if a.canMutate {
		a.realtimeQuit = make(chan struct{})
		go a.watchRealtime(a.generation, done, a.realtimeQuit)
	}
	return a.snapshot, ""
}

func (a *readOnlyAdapter) handle(ctx context.Context, generation uint64, event slidetypes.ClientEvent) error {
	a.mu.Lock()
	defer a.mu.Unlock()
	if generation != a.generation || event == nil {
		return nil
	}
	// A report per keystroke burst is not a connection event: it gets its own frame, or none, and never a counts frame.
	// Returning nil matters as well, since the typing stream treats a handler error as a broken frame.
	if e, ok := event.(*slidetypes.TypingNotification); ok {
		a.publishTyping(e)
		return nil
	}
	a.snapshot.Events++
	code := ""
	switch e := event.(type) {
	case *slidetypes.Delta:
		// Counted, not applied: see deltaLedger.
		self := int64(0)
		if owner, ok := a.client.(interface{ GetOwnFBID() int64 }); ok {
			self = owner.GetOwnFBID()
		}
		a.requests.deltas.note(e, self)
		if a.probe != nil {
			a.probe.apply(e)
		}
	case *slidetypes.Connected:
		a.snapshot.State = "connected"
		select {
		case a.ready <- "":
		default:
		}
	case *slidetypes.SeqIDUpdate:
		a.seqID, a.seqTS = e.SeqID, e.Timestamp
	case *slidetypes.ReconnectionStateUpdate:
		a.state = append(json.RawMessage(nil), e.State...)
	case *slidetypes.Disconnected:
		code = failureCode(e.Error)
		// Reported as connection_failed like any other close, but it is Instagram refusing the session.
		if dgw.IsUnauthorized(e.Error) {
			a.refusedRealtime = true
		}
	case *slidetypes.AuthError:
		code = failureCode(e.Error)
		a.refusedRealtime = true
	case *slidetypes.ResnapshotRequired:
		if a.probe != nil {
			a.probe.realtime.ResnapshotEvents++
		}
		a.seqID, a.seqTS = 0, time.Time{}
		code = "resnapshot_required"
	}
	if code != "" {
		a.snapshot.State = code
		select {
		case a.ready <- code:
		default:
		}
		if a.stop != nil {
			a.stop()
		}
	}
	// The account belongs to the answer the app asked for, not to a frame pushed on every event. Repeating an
	// identity on every callback would put it in the pipe hundreds of times for nothing, and event traffic is the
	// half of this that gets logged.
	counts := a.snapshot
	counts.Account, counts.AccountName = "", ""
	if err := a.out.write(response{Event: "counts", Result: counts}); err != nil {
		return errors.New("output_failed")
	}
	if code != "" {
		return errors.New(code)
	} // Stop upstream automatic retries.
	return nil
}

func (a *readOnlyAdapter) close() {
	a.mu.Lock()
	a.generation++ // callbacks from this session may no longer change local state
	if a.realtimeQuit != nil {
		close(a.realtimeQuit)
		a.realtimeQuit = nil
	}
	stop, client, done := a.stop, a.client, a.done
	a.mu.Unlock()
	if stop != nil {
		stop()
	}
	if client != nil {
		client.Disconnect()
	}
	if done != nil {
		<-done
	}
	a.mu.Lock()
	a.client, a.jar, a.stop, a.done = nil, nil, nil, nil
	a.state, a.key, a.seqID, a.seqTS = nil, "", 0, time.Time{}
	a.probe = nil
	a.snapshot = readSnapshot{State: "disconnected"}
	a.mu.Unlock()
}

func runReadOnly(in io.Reader, out io.Writer, canMutate bool) error {
	factory := clientFactory(newReadClient)
	if canMutate {
		factory = newAppClient
	}
	a := &readOnlyAdapter{out: &output{encoder: json.NewEncoder(out)}, factory: factory, snapshot: readSnapshot{State: "disconnected"}, chats: newChatStore(), media: newMediaStore(), canMutate: canMutate}
	defer a.close()
	// Typing is registered in both modes: the diagnostics' answer is read_only, from the same refusal every mutation has.
	return dispatch(in, a.out, a.call, map[string]sideCall{"ui.typing": a.uiTyping}, func() { a.requests.cancelled.Add(1) })
}
