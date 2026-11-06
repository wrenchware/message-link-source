// Experimental local adapter; no Matrix server or Beeper process required.
package main

import (
	"context"
	"encoding/json"
	"io"
	"os"
	"sync"
	"time"

	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/events"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/util"
	"go.mau.fi/util/exhttp"
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

func (o *output) write(r response) {
	o.mu.Lock()
	defer o.mu.Unlock()
	if o.encoder.Encode(r) != nil {
		os.Exit(2)
	}
}

type typing struct {
	ChatID    string    `json:"chatId"`
	UserID    string    `json:"userId"`
	Active    bool      `json:"active"`
	ExpiresAt time.Time `json:"expiresAt"`
}

func mapTyping(t *gmproto.TypingData, now time.Time) *typing {
	if t == nil || t.GetConversationID() == "" || t.GetUser().GetNumber() == "" {
		return nil
	}
	if t.GetType() != gmproto.TypingTypes_STARTED_TYPING && t.GetType() != gmproto.TypingTypes_STOPPED_TYPING {
		return nil
	}
	active := t.GetType() == gmproto.TypingTypes_STARTED_TYPING
	expiry := now
	if active {
		expiry = now.Add(15 * time.Second)
	}
	return &typing{t.GetConversationID(), t.GetUser().GetNumber(), active, expiry}
}

type adapter struct {
	downloads         sync.Mutex
	lists             sync.Mutex
	mediaMu           sync.Mutex
	mediaRefs         map[string]*gmproto.MediaContent
	fullSizeTargets   map[string]fullSizeTarget
	fullSizeRequested map[fullSizeTarget]bool
	messageRefs       map[string]*gmproto.Message
	mediaJobs         map[string]*mediaDownload
	// Real sizes of downloads that came back larger than they had reserved and were let go to stay within the budget;
	// the next read reserves this much (media.go). Held with downloads.
	mediaSizes map[string]int
	// The timer that lets go of finished downloads nobody reads any more (media.go). Held with downloads.
	mediaSweep *time.Timer
	upload     []byte
	// Conversations the app has said take no more sends (send.go).
	heldSends  sync.Map
	simMu      sync.RWMutex
	sims       map[string]*gmproto.SIMCard
	// Attachments re-learned from a page of their conversation (media_relearn.go), and what tests stand in for its read
	// and its clock.
	relearn    relearnState
	fetchPage  pageFetcher
	relearnNow func() time.Time
	// What the app was last told about the phone's SIMs (sims.go), and whether it has been told at all.
	simsShown []simChoice
	simsTold  bool
	// The phone's latest description of each conversation, for the typing ping, sends and reactions (conversation_cache.go).
	conversations conversationCache
	client        *libgm.Client
	// Cards made for links the app has actually shown. Nothing else in the helper puts anything here.
	previewMu   sync.Mutex
	previews    map[string]*linkPreview
	previewJobs *previewJobs
	pairing     *libgm.PairingSession
	out         *output
	// Requests still waiting for an answer, and whether the library has said the pairing is gone (connection_state.go).
	inflightMu sync.Mutex
	inflight   map[int]context.CancelFunc
	nextCall   int
	refusal    string
	active     bool
	// Ends the background connection a Google pairing keeps while waiting for the phone.
	pairingStop context.CancelFunc
	// What the library merges into the session's cookies, watched and saved (cookie_watch.go).
	cookies cookieWatch
	// Stands in for asking Google which account new cookies belong to, in tests (cookie_replace.go).
	checkCookies func(ctx context.Context, cookies map[string]string) (email string, after map[string]string, err error)
	// Stands in for callContext in tests of what serveCall does with an answer.
	answer func(ctx context.Context, r request) (any, string)
	// A QR pairing under way (qr_pairing.go), and what tests stand in for its network and its timing.
	qr          *qrLogin
	qrSourceFor func(*libgm.Client) qrSource
	qrTune      func(*qrLogin)
}

func (a *adapter) init(auth *libgm.AuthData) {
	// An acknowledgement Google refuses as unauthenticated is, like a refused ping, the sign-in expiring (library_log.go).
	refused := func(string, int) { a.signOut("ack_unauthenticated") }
	a.client = libgm.NewClient(auth, nil, libraryLogger(a.out, refused), exhttp.SensibleClientSettings)
	// Upstream moved its default from one minute to twenty (v26.05): pinging the phone every minute drains its battery,
	// and a phone that saves battery by holding Google Messages back is the commonest cause of a dropped connection.
	a.client.SetPingInterval(20 * time.Minute)
	a.client.SetEventHandler(a.handleEvent)
}

// Everything libgm reports while connected. A method rather than a closure in init so a test can hand it an event.
func (a *adapter) handleEvent(evt any) {
	if state, ok := classifyConnectionEvent(evt); ok {
		a.noteConnection(state)
	}
	switch e := evt.(type) {
	case *events.AuthTokenRefreshed:
		a.publishSession()
	case *gmproto.Settings:
		if e.SIMCards != nil {
			// Which SIM each conversation sends from, shown by the app only when the phone has more than one (sims.go).
			if choices, tell := a.noteSIMs(e.SIMCards); tell {
				a.out.write(response{Event: "sims", Result: map[string]any{"sims": choices}})
			}
		}
		// Whether Google Messages is still the phone's default SMS app (settings.go). Only what arrives: nothing
		// asks the phone for its settings.
		if s := settingsEvent(e); s != nil {
			a.out.write(response{Event: "settings", Result: s})
		}
	case *gmproto.Conversation:
		a.conversations.note(e)
		a.out.write(response{Event: "chat", Result: mapChat(e)})
	case *libgm.WrappedMessage:
		a.publishMessage(e)
	}
	if t, ok := evt.(*gmproto.TypingData); ok {
		if mapped := mapTyping(t, time.Now().UTC()); mapped != nil {
			a.out.write(response{Event: "typing", Result: mapped})
		}
	}
}
func (a *adapter) publishMessage(e *libgm.WrappedMessage) {
	// Replayed messages can confirm a pending send. Reconcile without new alerts.
	kind := "message"
	if e.IsOld {
		kind = "messageUpdate"
	}
	a.out.write(response{Event: kind, Result: a.viewMessage(e.Message)})
}
func (a *adapter) call(r request) (any, string) {
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	return a.callContext(ctx, r)
}

func (a *adapter) callContext(ctx context.Context, r request) (any, string) {
	switch r.Method {
	case "ui.avatar":
		return a.avatarCall(ctx, r)
	case "reauthenticate":
		return a.reauthenticate(ctx, r)
	case "cookies.replace":
		return a.replaceCookies(ctx, r)
	case "ui.startGroup":
		return a.startGroupCall(ctx, r)
	case "ui.chatList", "ui.contacts", "ui.topContacts", "ui.startDirect":
		return a.directoryCall(ctx, r)
	case "ui.chat", "ui.markRead", "ui.actions", "ui.react":
		return a.actionCall(ctx, r)
	case "ui.updateChat":
		return a.updateChatCall(ctx, r)
	case "ui.mediaRead":
		return a.mediaRead(ctx, r)
	case "ui.mediaRelearn":
		return a.mediaRelearn(ctx, r)
	case "ui.linkPreviews":
		return a.loadLinkPreviews(r)
	case "ui.deleteMessage":
		return a.deleteMessage(ctx, r)
	case "ui.mediaStage":
		return a.mediaStage(r)
	case "ui.typing":
		typingContext, stopTyping := context.WithTimeout(ctx, 3*time.Second)
		defer stopTyping()
		return a.typingCall(typingContext, r)
	case "ui.holdSends":
		return a.holdSends(r)
	case "ui.sendPermission", "ui.sendText", "ui.sendMedia":
		return a.sendCall(ctx, r)
	case "hello":
		return map[string]any{"protocol": 7, "library": "mautrix-gmessages v0.2609.1-0.20261010212621-186284eea622", "paired": a.client != nil && a.client.AuthData.Browser != nil, "sendEnabled": true}, ""
	case "pair.start":
		if a.client != nil {
			return nil, "session_already_started"
		}
		var p struct {
			Cookies map[string]string `json:"cookies"`
		}
		if json.Unmarshal(r.Params, &p) != nil {
			return nil, "invalid_input"
		}
		for _, key := range []string{"SID", "HSID", "SSID", "OSID", "APISID", "SAPISID"} {
			if p.Cookies[key] == "" {
				return nil, "missing_cookies"
			}
		}
		auth := libgm.NewAuthData()
		auth.SetCookies(p.Cookies)
		a.init(auth)
		if a.client.FetchConfig(ctx) != nil {
			a.client.Disconnect()
			a.client = nil
			return nil, "config_failed"
		}
		// The pairing keeps a connection open in the background while it waits for the phone; it outlives this request.
		background, stop := context.WithCancel(context.Background())
		emoji, sess, err := a.client.StartGaiaPairing(ctx, background)
		if err != nil {
			stop()
			a.client.Disconnect()
			a.client = nil
			return nil, pairStartCode(err)
		}
		a.pairingStop = stop
		a.pairing = sess
		return map[string]string{"emoji": emoji}, ""
	case "pair.finish":
		if a.pairing == nil {
			return nil, "pairing_required"
		}
		_, err := a.client.FinishGaiaPairing(ctx, a.pairing)
		a.pairing = nil
		if a.pairingStop != nil {
			a.pairingStop()
			a.pairingStop = nil
		}
		if err != nil {
			return nil, pairFinishCode(err)
		}
		return sessionAnswer(a.client.AuthData)
	case "pair.qrStart":
		return a.qrStart(ctx)
	case "pair.qrWait":
		return a.qrWait(ctx)
	case "connect":
		if a.client != nil {
			return nil, "session_already_started"
		}
		var auth libgm.AuthData
		if json.Unmarshal(r.Params, &auth) != nil || auth.Browser == nil || auth.RequestCrypto == nil || auth.RefreshKey == nil || len(auth.TachyonAuthToken) == 0 {
			return nil, "invalid_session"
		}
		a.init(&auth)
		// Connection needs a lifetime beyond this individual request. Connect's only request is the auth-token refresh,
		// and only when the token is within an hour of expiry; the step says which of the two Google refused.
		step := "config"
		err := a.client.FetchConfig(ctx)
		if err == nil && a.configSignedOut() {
			// Refused before Connect, so no refresh is asked of Google for a sign-in it has already turned down.
			a.client.Disconnect()
			a.client = nil
			return nil, signInRefused
		}
		if err == nil {
			step = "refresh"
			// The connection lives as long as the helper, not this request.
			err = a.client.Connect(context.Background())
		}
		if err != nil {
			a.client.Disconnect()
			a.client = nil
			return nil, a.connectFailed(step, err)
		}
		a.publishSession()
		a.startCookieWatch()
		return map[string]bool{"connected": true}, ""
	case "chats", "ui.chats":
		if a.client == nil || a.client.AuthData.Browser == nil {
			return nil, "pairing_required"
		}
		result, err := a.listConversations(ctx, 25, gmproto.ListConversationsRequest_INBOX)
		if err != nil {
			return nil, "history_failed"
		}
		if r.Method == "ui.chats" {
			chats := make([]viewChat, 0, len(result.GetConversations()))
			for _, c := range result.GetConversations() {
				chats = append(chats, mapChat(c))
			}
			return chats, ""
		}
		return result, ""
	case "messages", "ui.messages", "ui.messagePage", "ui.archivePage":
		if a.client == nil || a.client.AuthData.Browser == nil {
			return nil, "pairing_required"
		}
		var p struct {
			ChatID string          `json:"chatId"`
			Cursor *gmproto.Cursor `json:"cursor"`
			Count  int             `json:"count"`
		}
		if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" {
			return nil, "invalid_input"
		}
		result, err := a.client.FetchMessages(ctx, p.ChatID, pageCount(p.Count), p.Cursor)
		if err != nil {
			return nil, "history_failed"
		}
		if r.Method == "ui.archivePage" {
			// Search indexing registers no media references, so background pages
			// cannot evict references needed by messages currently on screen.
			messages := make([]viewMessage, 0, len(result.GetMessages()))
			for _, m := range result.GetMessages() {
				messages = append(messages, mapMessage(m))
			}
			return map[string]any{"messages": messages, "cursor": pageCursor(result), "total": result.GetTotalMessages()}, ""
		}
		if r.Method == "ui.messages" || r.Method == "ui.messagePage" {
			messages := make([]viewMessage, 0, len(result.GetMessages()))
			for _, m := range result.GetMessages() {
				messages = append(messages, a.viewMessage(m))
			}
			if r.Method == "ui.messagePage" {
				return map[string]any{"messages": messages, "cursor": pageCursor(result)}, ""
			}
			return messages, ""
		}
		return result, ""
	case "snapshot":
		if a.client == nil {
			return nil, "pairing_required"
		}
		a.client.Disconnect()
		return sessionAnswer(a.client.AuthData)
	default:
		return nil, "unsupported_method"
	}
}
func serve(in io.Reader, out io.Writer) {
	// The name the phone lists this computer under in Device pairing, sent only when pairing. It stays a tablet, as
	// upstream is: a web client would take the single web slot from Messages for web in a real browser, while one web
	// client and two tablets can be connected at once. The library's own "libgm" said nothing to anyone.
	util.BrowserDetailsMessage.OS = "Messages for Windows"
	o := &output{encoder: json.NewEncoder(out)}
	a := &adapter{out: o}
	defer func() {
		if a.client != nil {
			a.client.Disconnect()
		}
	}()
	dispatch(in, o, a.serveCall)
}
func main() { serve(os.Stdin, os.Stdout) }
