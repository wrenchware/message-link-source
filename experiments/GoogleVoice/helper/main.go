// Local adapter for one Google Voice account; no Matrix server or bridge process required. One helper process serves
// one account, so several Voice accounts run side by side without sharing a session. It reads, marks read and sends texts.
package main

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"runtime/debug"
	"slices"
	"strconv"
	"strings"
	"sync"
	"time"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

const protocolVersion = 1
const libraryVersion = "mautrix-gvoice 8775ef52af27"

// Upstream's bridge refreshes on realtime hints no more than this often, and in the background at the longer interval.
const minRefreshInterval = 10 * time.Second
const backgroundRefresh = 15 * time.Minute
const maxTransientRetries = 5

// A realtime channel that ran this long before failing was working; its failure starts a new count. libgv's channel
// never returns without an error, and until 0.1.309 the nine-minute cookie rotation restarted it (and the count) anyway.
const realtimeStable = 5 * time.Minute

// How many failures in a row the realtime channel has had, counting one that ended after running for `ran`.
func realtimeFailures(previous int, ran time.Duration) int {
	if ran >= realtimeStable {
		return 1
	}
	return previous + 1
}

const mediaChunk = 512 * 1024
const mediaLimit = 50 * 1024 * 1024
const mediaKeep = 16

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
	if o == nil || o.encoder == nil {
		return
	}
	o.mu.Lock()
	defer o.mu.Unlock()
	if o.encoder.Encode(r) != nil {
		os.Exit(2)
	}
}

type heldMedia struct {
	id   string
	mime string
	data []byte
	// When it was downloaded or last read (photo.go). Held with mediaMu.
	lastUse time.Time
}

type adapter struct {
	out      *output
	lifetime context.Context
	stop     context.CancelFunc

	mu     sync.Mutex
	client *libgv.Client
	// Every client receives a generation when it becomes current. Realtime clients can emit a final
	// event after a cookie rotation replaced them, so those old events must not sign the account out.
	clientGeneration uint64
	// Cookie events have their own monotonically increasing generation. One current client can emit
	// several successive cookie snapshots, and the app must never persist an older one after a newer one.
	cookieGeneration uint64
	realtime         context.CancelFunc
	lastSeen         map[string]int64
	// Whether each conversation was read when last listed, so reading it elsewhere (the phone, the Voice website)
	// updates it here too.
	lastRead map[string]bool
	// Conversations started from New chat that Google answered it has no thread for: their first message goes to the
	// number (send.go). Held with mu.
	unstarted map[string]bool
	// The newest message time any listing has shown. A conversation change detection has not watched yet is announced
	// only for messages newer than this, so old history is never announced as new. The first listing sets it; after
	// that only change detection raises it, because a list the app reads between an arrival and the next detection
	// must not absorb that arrival.
	seenThrough       int64
	watermarked       bool
	refresh           chan struct{}
	mediaMu           sync.Mutex
	media             []*heldMedia
	mediaSweep        *time.Timer // lets idle media go (photo.go); held with mediaMu
	listThreads       func(ctx context.Context) (*gvproto.RespListThreads, error)
	listFolders       func(ctx context.Context, folder gvproto.ThreadFolder) (*gvproto.RespListThreads, error)
	cookieSums        map[string][32]byte
	getAccount        func(ctx context.Context, client *libgv.Client) (*gvproto.RespGetAccount, error)
	updateAttributes  func(ctx context.Context, client *libgv.Client, req *gvproto.ReqUpdateAttributes) error
	removeThread      func(ctx context.Context, client *libgv.Client, id string) error // libgv's DeleteThread, in tests (delete.go)
	voicemailReported bool
	// Voicemails by message id, from the history the app has been shown: true with a recording, false without one.
	voicemailsWithAudio map[string]bool
	// Whether a recording was asked for a voicemail this helper had not seen yet (reported once).
	unseenVoicemailAsked bool
	downloadVoicemail   func(ctx context.Context, client *libgv.Client, address, attempt string, from int) (*http.Response, error)
	downloadAttachment  func(ctx context.Context, client *libgv.Client, id string) ([]byte, string, error) // downloadPhoto, in tests
	downloadAvatar      func(ctx context.Context, client *libgv.Client, address string) ([]byte, string, error)
	sendSMS             func(ctx context.Context, client *libgv.Client, req *gvproto.ReqSendSMS) (*gvproto.RespSendSMS, error)
	contacts            contactBook
	listPageAfter       func(ctx context.Context, token string) (*gvproto.RespListThreads, error)
	// The account's Voice number, as login read it. A renewed sign-in the app hands over must name the same one.
	number string
	// One replacement of the current client at a time: a read or realtime recovery, or a renewed sign-in handed over.
	recoveryMu sync.Mutex
	// A photo being staged for ui.sendMedia.
	upload    []byte
	getThread func(ctx context.Context, client *libgv.Client, id string, count int, before string) (*gvproto.RespGetThread, error)
	idsNoted  bool
}

func newAdapter(out *output) *adapter {
	ctx, cancel := context.WithCancel(context.Background())
	return &adapter{out: out, lifetime: ctx, stop: cancel, lastSeen: map[string]int64{}, lastRead: map[string]bool{}, unstarted: map[string]bool{}, refresh: make(chan struct{}, 1)}
}

func (a *adapter) current() *libgv.Client {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.client
}

func (a *adapter) currentWithGeneration() (*libgv.Client, uint64) {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.client, a.clientGeneration
}

// installClient makes a client current and gives its events an identity. libgv does not start any work until asked,
// so installing its handler before returning is enough to keep all emitted events generation-safe.
func (a *adapter) installClient(client *libgv.Client) uint64 {
	a.mu.Lock()
	a.clientGeneration++
	generation := a.clientGeneration
	client.EventHandler = func(ctx context.Context, raw any) { a.handleEvent(generation, ctx, raw) }
	a.client = client
	a.mu.Unlock()
	return generation
}

func authCode(err error) string {
	if libgv.IsAuthError(err) {
		return "auth_required"
	}
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return "request_cancelled"
	}
	return "voice_unavailable"
}

func (a *adapter) callContext(ctx context.Context, r request) (any, string) {
	switch r.Method {
	case "hello":
		return map[string]any{"protocol": protocolVersion, "library": libraryVersion}, ""
	case "login":
		var p struct {
			Cookies map[string]string `json:"cookies"`
		}
		if json.Unmarshal(r.Params, &p) != nil || len(p.Cookies) == 0 {
			return nil, "invalid_input"
		}
		client, account, attempt, err := a.login(ctx, p.Cookies)
		if err != nil {
			return nil, authCode(err)
		}
		number := account.GetAccount().GetPrimaryDestinationID()
		a.installClient(client)
		a.mu.Lock()
		a.number = number
		a.mu.Unlock()
		// The account's own Voice number identifies it; the app keys its session folder on a hash of it.
		return map[string]any{"number": number, "attempt": attempt}, ""
	case "session":
		client := a.current()
		if client == nil {
			return nil, "login_required"
		}
		return map[string]any{"cookies": client.GetCookies()}, ""
	case "connect":
		if a.current() == nil {
			return nil, "login_required"
		}
		a.startRealtime()
		return map[string]any{"connected": true}, ""
	case "cookies.replace":
		return a.replaceCookies(ctx, r.Params)
	case "disconnect":
		a.stopRealtime()
		return map[string]any{"connected": false}, ""
	case "ui.chats":
		client := a.current()
		if client == nil {
			return nil, "login_required"
		}
		page, err := runtimeRead(a, ctx, client, func(ctx context.Context, client *libgv.Client) (*chatPage, error) {
			return a.chatPage(ctx, client, "")
		})
		if err != nil {
			return nil, authCode(err)
		}
		return page.Chats, ""
	case "ui.chatsPage":
		client := a.current()
		if client == nil {
			return nil, "login_required"
		}
		var p struct {
			Cursor string `json:"cursor"`
		}
		if json.Unmarshal(r.Params, &p) != nil || len(p.Cursor) > 2000 {
			return nil, "invalid_input"
		}
		page, err := runtimeRead(a, ctx, client, func(ctx context.Context, client *libgv.Client) (*chatPage, error) {
			return a.chatPage(ctx, client, p.Cursor)
		})
		if err != nil {
			return nil, authCode(err)
		}
		return page, ""
	case "ui.markRead":
		client := a.current()
		if client == nil {
			return nil, "login_required"
		}
		var p struct {
			ChatID string `json:"chatId"`
		}
		if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" {
			return nil, "invalid_input"
		}
		// The request Google Voice's website and upstream's bridge send when a conversation is read. It marks the whole
		// conversation in this account; Voice keeps no per-message read state and tells the other people nothing.
		update := a.updateAttributes
		if update == nil {
			update = func(ctx context.Context, client *libgv.Client, req *gvproto.ReqUpdateAttributes) error {
				_, err := client.UpdateThreadAttributes(ctx, req)
				return err
			}
		}
		err := update(ctx, client, &gvproto.ReqUpdateAttributes{
			Attributes:      &gvproto.ThreadAttributes{ThreadID: p.ChatID, Read: true},
			OtherAttributes: &gvproto.ThreadAttributes{Read: true},
			UnknownInt:      1,
		})
		if err != nil {
			return nil, authCode(err)
		}
		a.mu.Lock()
		a.lastRead[p.ChatID] = true
		a.mu.Unlock()
		return map[string]any{"outcome": "accepted"}, ""
	case "ui.updateChat":
		client := a.current()
		if client == nil {
			return nil, "login_required"
		}
		var p struct {
			ChatID   string `json:"chatId"`
			Archived *bool  `json:"archived"`
			Spam     *bool  `json:"spam"`
		}
		if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" || (p.Archived == nil) == (p.Spam == nil) {
			return nil, "invalid_input"
		}
		if p.Archived != nil {
			return a.setArchived(ctx, client, p.ChatID, *p.Archived)
		}
		if *p.Spam {
			return nil, "invalid_input"
		}
		return a.removeSpam(ctx, client, p.ChatID)
	case "ui.deleteChat":
		client := a.current()
		if client == nil {
			return nil, "login_required"
		}
		var p struct {
			ChatID string `json:"chatId"`
		}
		if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" || len(p.ChatID) > 200 {
			return nil, "invalid_input"
		}
		return a.deleteThread(ctx, client, p.ChatID)
	case "ui.contacts":
		client := a.current()
		if client == nil {
			return nil, "login_required"
		}
		return a.contacts.all(ctx, client), ""
	case "ui.mediaStage":
		return a.mediaStage(r)
	case "ui.sendMedia":
		client := a.current()
		if client == nil {
			return nil, "login_required"
		}
		return a.sendMedia(ctx, client, r)
	case "ui.sendText":
		client := a.current()
		if client == nil {
			return nil, "login_required"
		}
		var p struct {
			ChatID string `json:"chatId"`
			Text   string `json:"text"`
		}
		if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" || strings.TrimSpace(p.Text) == "" || len(p.Text) > maxTextBytes {
			return nil, "invalid_input"
		}
		return a.sendText(ctx, client, p.ChatID, p.Text)
	case "ui.messages":
		client := a.current()
		if client == nil {
			return nil, "login_required"
		}
		var p struct {
			ChatID string `json:"chatId"`
			Count  int    `json:"count"`
			Before string `json:"before"`
		}
		if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" {
			return nil, "invalid_input"
		}
		if p.Count <= 0 || p.Count > 100 {
			p.Count = 50
		}
		get := a.getThread
		if get == nil {
			get = func(ctx context.Context, client *libgv.Client, id string, count int, before string) (*gvproto.RespGetThread, error) {
				return client.GetThread(ctx, id, count, before)
			}
		}
		resp, err := runtimeRead(a, ctx, client, func(ctx context.Context, client *libgv.Client) (*gvproto.RespGetThread, error) {
			return get(ctx, client, p.ChatID, p.Count, p.Before)
		})
		if err != nil {
			// A conversation started from New chat has no thread at Google until its first message is sent.
			if status := statusOf(err); p.Before == "" && strings.HasPrefix(p.ChatID, "t.") && (status == http.StatusNotFound || status == http.StatusBadRequest) {
				a.diagnose(map[string]any{"thread": "not_started", "status": status})
				a.setUnstarted(p.ChatID, true)
				return map[string]any{"messages": []voiceMessage{}, "next": ""}, ""
			}
			return nil, authCode(err)
		}
		thread := resp.GetThread()
		a.noteVoicemailMedia(thread.GetMessages())
		return map[string]any{"messages": convertMessages(p.ChatID, thread.GetMessages()), "next": thread.GetPaginationToken()}, ""
	case "ui.chat":
		// One conversation's summary, read from its own thread: one older than the listings read since launch (a missed
		// call from weeks back) is otherwise unknown here, and could be neither marked read nor shown as read after.
		client := a.current()
		if client == nil {
			return nil, "login_required"
		}
		var p struct {
			ChatID string `json:"chatId"`
		}
		if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" {
			return nil, "invalid_input"
		}
		get := a.getThread
		if get == nil {
			get = func(ctx context.Context, client *libgv.Client, id string, count int, before string) (*gvproto.RespGetThread, error) {
				return client.GetThread(ctx, id, count, before)
			}
		}
		resp, err := runtimeRead(a, ctx, client, func(ctx context.Context, client *libgv.Client) (*gvproto.RespGetThread, error) {
			return get(ctx, client, p.ChatID, 1, "")
		})
		if err != nil {
			return nil, authCode(err)
		}
		thread := resp.GetThread()
		if thread.GetID() == "" {
			return nil, "not_found"
		}
		named, stats := a.contacts.infoFor(ctx, client, participantNumbers([]*gvproto.Thread{thread}))
		if stats != nil {
			a.diagnose(stats)
		}
		chat := convertThreadNamed(thread, named)
		a.mu.Lock()
		a.lastRead[chat.ID] = !chat.Unread
		a.mu.Unlock()
		return chat, ""
	case "ui.mediaRead":
		client := a.current()
		if client == nil {
			return nil, "login_required"
		}
		var p struct {
			ID     string `json:"id"`
			Offset int    `json:"offset"`
		}
		if json.Unmarshal(r.Params, &p) != nil || p.ID == "" || p.Offset < 0 {
			return nil, "invalid_input"
		}
		held := a.cached(p.ID)
		if held == nil {
			var data []byte
			var mime string
			if strings.HasPrefix(p.ID, voicemailPrefix) {
				var code string
				data, mime, code = a.voicemailAudio(ctx, client, strings.TrimPrefix(p.ID, voicemailPrefix))
				if code != "" {
					return nil, code
				}
			} else if strings.HasPrefix(p.ID, "avatar:") {
				address := a.contacts.avatarURL(p.ID)
				if address == "" {
					return nil, "media_unavailable"
				}
				download := a.downloadAvatar
				if download == nil {
					download = downloadContactAvatar
				}
				var err error
				data, mime, err = download(ctx, client, address)
				// A contact photo is optional. A failed or expired photo URL must never make the Voice account look signed
				// out, trigger cookie recovery or turn an initials avatar into a provider error.
				if err != nil {
					return nil, "media_unavailable"
				}
			} else {
				var code string
				data, mime, code = a.photo(ctx, client, p.ID)
				if code != "" {
					return nil, code
				}
			}
			if len(data) == 0 || len(data) > mediaLimit {
				return nil, "media_unavailable"
			}
			if mime == "" {
				mime = http.DetectContentType(data)
			}
			held = &heldMedia{id: p.ID, mime: mime, data: data}
			a.keep(held)
		}
		return mediaChunkOf(held, p.Offset)
	}
	return nil, "unknown_method"
}

func mediaChunkOf(held *heldMedia, offset int) (any, string) {
	if offset > len(held.data) {
		return nil, "invalid_input"
	}
	end := min(offset+mediaChunk, len(held.data))
	return map[string]any{"total": len(held.data), "next": end, "mime": held.mime,
		"data": base64.StdEncoding.EncodeToString(held.data[offset:end])}, ""
}

func (a *adapter) list(ctx context.Context) (*gvproto.RespListThreads, error) {
	return a.listFor(ctx, a.current())
}

func (a *adapter) listFor(ctx context.Context, client *libgv.Client) (*gvproto.RespListThreads, error) {
	if a.listThreads != nil {
		return a.listThreads(ctx)
	}
	if client == nil {
		return nil, errLoggedOut
	}
	return client.ListThreads(ctx, gvproto.ThreadFolder_ALL_THREADS, "")
}

var errLoggedOut = errors.New("not logged in")

func (a *adapter) handleEvent(generation uint64, ctx context.Context, raw any) {
	_, currentGeneration := a.currentWithGeneration()
	if generation != currentGeneration {
		// A replaced realtime client may finish after its replacement connected. Its cookie/auth events no
		// longer describe the saved session, and its auth event must not prompt the user to sign in again.
		return
	}
	switch evt := raw.(type) {
	case *libgv.CookieChanged:
		a.mu.Lock()
		if generation != a.clientGeneration {
			a.mu.Unlock()
			return
		}
		a.cookieGeneration++
		cookieGeneration := a.cookieGeneration
		a.mu.Unlock()
		a.noteCookies(evt.Cookies)
		// The app saves rotated cookies so the next launch signs in with what Google last issued.
		a.out.write(response{Event: "cookies", Result: map[string]any{"cookies": evt.Cookies, "generation": cookieGeneration}})
	case *libgv.RealtimeConnected:
		a.mu.Lock()
		if generation == a.clientGeneration {
			a.out.write(response{Event: "connected", Result: map[string]any{}})
			a.wake()
		}
		a.mu.Unlock()
	case *libgv.RealtimeEvent:
		a.mu.Lock()
		if generation == a.clientGeneration {
			a.wake()
		}
		a.mu.Unlock()
	}
}

func (a *adapter) wake() {
	select {
	case a.refresh <- struct{}{}:
	default:
	}
}

func (a *adapter) startRealtime() {
	a.mu.Lock()
	if a.realtime != nil {
		a.mu.Unlock()
		return
	}
	ctx, cancel := context.WithCancel(a.lifetime)
	a.realtime = cancel
	client := a.client
	generation := a.clientGeneration
	a.mu.Unlock()
	go a.refreshLoop(ctx)
	go func() {
		retries := 0
		for ctx.Err() == nil {
			started := time.Now()
			err := client.RunRealtimeChannel(ctx)
			if ctx.Err() != nil {
				return
			}
			if err != nil {
				a.diagnose(map[string]any{"realtime": statusOf(err)})
			}
			if libgv.IsAuthError(err) {
				if a.recoverRuntime(ctx, client) {
					// replaceCurrent cancels this realtime loop and starts one for its validated replacement.
					// Returning avoids two realtime channels using that replacement at the same time.
					return
				}
				if a.isCurrentGeneration(generation) {
					a.out.write(response{Event: "auth", Result: map[string]any{}})
				}
				return
			}
			if err == nil {
				retries = 0
				continue
			}
			retries = realtimeFailures(retries, time.Since(started))
			if retries > maxTransientRetries || errors.Is(err, libgv.ErrTooManyUnknownSID) {
				a.out.write(response{Event: "disconnected", Result: map[string]any{"final": true}})
				return
			}
			a.out.write(response{Event: "disconnected", Result: map[string]any{"final": false}})
			select {
			case <-ctx.Done():
				return
			case <-time.After(time.Minute):
			}
		}
	}()
}

func (a *adapter) stopRealtime() {
	a.mu.Lock()
	cancel := a.realtime
	a.realtime = nil
	a.mu.Unlock()
	if cancel != nil {
		cancel()
	}
}

// Google's realtime channel says that something changed, not what. Re-list threads, rate limited, and tell the app
// which conversations have newer messages than it was last shown.
func (a *adapter) refreshLoop(ctx context.Context) {
	ticker := time.NewTicker(backgroundRefresh)
	defer ticker.Stop()
	var last time.Time
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		case <-a.refresh:
		}
		if wait := minRefreshInterval - time.Since(last); wait > 0 {
			select {
			case <-ctx.Done():
				return
			case <-time.After(wait):
			}
		}
		last = time.Now()
		a.detectChanges(ctx)
	}
}

func (a *adapter) detectChanges(ctx context.Context) {
	client := a.current()
	resp, err := runtimeRead(a, ctx, client, func(ctx context.Context, client *libgv.Client) (*gvproto.RespListThreads, error) {
		return a.listFor(ctx, client)
	})
	if err != nil {
		a.diagnose(map[string]any{"refresh": statusOf(err)})
		if libgv.IsAuthError(err) {
			a.out.write(response{Event: "auth", Result: map[string]any{}})
		}
		return
	}
	changed := []string{}
	// The messages newer than the last detection, per thread, so an open conversation adds them in place and a new one
	// can notify. A thread not watched yet is compared with the newest message any listing has shown.
	arrived := map[string][]string{}
	announced, newThreads := 0, 0
	a.mu.Lock()
	highWater, watermarked := a.seenThrough, a.watermarked
	for _, thread := range resp.GetThreads() {
		messages := thread.GetMessages()
		if len(messages) == 0 {
			continue
		}
		newest := messages[0].GetTimestamp()
		a.seenThrough = max(a.seenThrough, newest)
		previous, known := a.lastSeen[thread.GetID()]
		wasRead, readKnown := a.lastRead[thread.GetID()]
		a.lastRead[thread.GetID()] = thread.GetRead()
		if !known && (!watermarked || newest <= highWater) {
			// Nothing in it is newer than what was already listed: start watching it quietly, as at startup.
			a.lastSeen[thread.GetID()] = newest
			previous, known = newest, true
		}
		if known && newest <= previous {
			// Nothing new arrived, but it may have been read (or marked unread) somewhere else.
			if readKnown && wasRead != thread.GetRead() {
				changed = append(changed, thread.GetID())
			}
			continue
		}
		if !known {
			// A new number, or a conversation beyond the list watched so far, with a message newer than every listing.
			previous = highWater
			newThreads++
		}
		a.lastSeen[thread.GetID()] = newest
		changed = append(changed, thread.GetID())
		for _, message := range messages {
			if message.GetTimestamp() > previous && message.GetID() != "" {
				arrived[thread.GetID()] = append(arrived[thread.GetID()], message.GetID())
				announced++
			}
		}
	}
	a.watermarked = true
	a.mu.Unlock()
	if len(changed) > 0 {
		a.diagnose(map[string]any{"refresh": "changed", "threads": len(changed), "arrived": announced, "newThreads": newThreads})
		a.out.write(response{Event: "changed", Result: map[string]any{"chats": changed, "messages": arrived}})
	}
}

func serve(in io.Reader, out io.Writer) {
	o := &output{encoder: json.NewEncoder(out)}
	a := newAdapter(o)
	defer a.stop()
	dispatch(in, o, a.callContext)
}

func main() {
	// Runtime dumps can contain private protocol buffers. Never print them.
	debug.SetTraceback("none")
	serve(os.Stdin, os.Stdout)
}

// Cookies that Google issues for a short time or derives from the others. A saved session can outlive them, and sending
// a stale one may be refused even though the long-lived session cookies are still good.
var shortLivedCookies = []string{"__Secure-1PSIDTS", "__Secure-3PSIDTS", "SIDCC", "__Secure-1PSIDCC", "__Secure-3PSIDCC"}

// Signs in with the cookies as given and, only when Google refuses them, once more without the short-lived ones. Each
// refused attempt is reported by name and HTTP status alone, never with the cookies or Google's response text.
func (a *adapter) login(ctx context.Context, cookies map[string]string) (*libgv.Client, *gvproto.RespGetAccount, string, error) {
	try := func(name string, cookies map[string]string) (*libgv.Client, *gvproto.RespGetAccount, error) {
		a.noteCookies(cookies)
		client := newSessionClient(cookies, "")
		account, err := a.account(ctx, client)
		if err != nil {
			a.out.write(response{Event: "diagnostic", Result: map[string]any{"login": name, "status": statusOf(err)}})
			return nil, nil, err
		}
		return client, account, nil
	}
	client, account, err := try("saved", cookies)
	if err == nil {
		return client, account, "saved", nil
	}
	if !libgv.IsAuthError(err) {
		return nil, nil, "", err
	}
	if trimmed, changed := removeShortLived(cookies); changed {
		if client, account, err = try("without_short_lived", trimmed); err == nil {
			return client, account, "without_short_lived", nil
		}
	}
	return nil, nil, "", err
}

func statusOf(err error) int {
	var failure *libgv.ResponseError
	if errors.As(err, &failure) && failure.Resp != nil {
		return failure.Resp.StatusCode
	}
	return 0
}

// Diagnostics for the app's log: fixed field names, HTTP statuses and well-known cookie names only.
func (a *adapter) diagnose(fields map[string]any) {
	a.out.write(response{Event: "diagnostic", Result: fields})
}

var knownCookies = []string{"SID", "HSID", "SSID", "APISID", "SAPISID", "OSID", "COMPASS", "NID", "AEC", "__Secure-1PSID", "__Secure-3PSID",
	"__Secure-1PAPISID", "__Secure-3PAPISID", "__Secure-OSID", "__Secure-1PSIDTS", "__Secure-3PSIDTS", "SIDCC", "__Secure-1PSIDCC", "__Secure-3PSIDCC"}

func cookieLabel(name string) string {
	if slices.Contains(knownCookies, name) {
		return name
	}
	return "other"
}

// Which cookies Google added, replaced or removed since the last look, by name. Only digests of the values are kept.
func (a *adapter) noteCookies(cookies map[string]string) {
	sums := make(map[string][32]byte, len(cookies))
	for name, value := range cookies {
		sums[name] = sha256.Sum256([]byte(value))
	}
	a.mu.Lock()
	previous := a.cookieSums
	a.cookieSums = sums
	a.mu.Unlock()
	if previous == nil {
		names := []string{}
		for name := range sums {
			names = append(names, cookieLabel(name))
		}
		slices.Sort(names)
		a.diagnose(map[string]any{"cookies": names})
		return
	}
	added, changed, removed := []string{}, []string{}, []string{}
	for name, sum := range sums {
		if old, ok := previous[name]; !ok {
			added = append(added, cookieLabel(name))
		} else if old != sum {
			changed = append(changed, cookieLabel(name))
		}
	}
	for name := range previous {
		if _, ok := sums[name]; !ok {
			removed = append(removed, cookieLabel(name))
		}
	}
	slices.Sort(added)
	slices.Sort(changed)
	slices.Sort(removed)
	if len(added)+len(changed)+len(removed) == 0 {
		return
	}
	a.diagnose(map[string]any{"added": added, "changed": changed, "removed": removed})
}

// Which voicemails in the history the app was shown have a recording. Reported once per helper by count and by the
// shape of the ids (letter case, length), never the ids themselves.
func (a *adapter) noteVoicemailMedia(messages []*gvproto.Message) {
	voicemails, withAudio, shape := 0, 0, ""
	for _, message := range messages {
		if message.GetType() != gvproto.Message_VOICEMAIL {
			continue
		}
		voicemails++
		if message.GetID() == "" {
			continue
		}
		audio := message.GetMediaURL() != ""
		a.mu.Lock()
		if a.voicemailsWithAudio == nil || len(a.voicemailsWithAudio) >= 2000 {
			a.voicemailsWithAudio = map[string]bool{}
		}
		a.voicemailsWithAudio[message.GetID()] = audio
		a.mu.Unlock()
		if !audio {
			continue
		}
		withAudio++
		if shape == "" {
			shape = idShape(message.GetID())
		}
	}
	if voicemails == 0 {
		return
	}
	a.mu.Lock()
	reported := a.voicemailReported
	a.voicemailReported = true
	a.mu.Unlock()
	if !reported {
		a.diagnose(map[string]any{"voicemails": voicemails, "withMedia": withAudio, "shape": shape})
	}
}

func idShape(id string) string {
	kinds := ""
	if strings.ContainsAny(id, "ABCDEFGHIJKLMNOPQRSTUVWXYZ") {
		kinds += "upper_"
	}
	if strings.ContainsAny(id, "abcdefghijklmnopqrstuvwxyz") {
		kinds += "lower_"
	}
	if strings.ContainsAny(id, "0123456789") {
		kinds += "digits_"
	}
	if strings.Trim(id, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789") != "" {
		kinds += "other_"
	}
	return fmt.Sprintf("%s%d", kinds, len(id))
}

// A voicemail's recording, from where Voice's own website plays it: voice.google.com/u/<account>/a/v/<voicemail id>,
// beside the address libgv downloads photos from. It is asked with the account's session the way photos are, and once
// more with cookies only if Google refuses that. A page instead of audio (a sign-in page, an error) is refused rather
// than played. Each download is reported by HTTP status, media type and size only.
func (a *adapter) voicemailAudio(ctx context.Context, client *libgv.Client, messageID string) ([]byte, string, string) {
	// Refused only for a voicemail Voice said has no recording. One not seen since this helper started is asked for: the app keeps
	// voicemails across the helper's restarts (every server update and the nightly one), and the list shows each conversation's newest
	// without its thread being read, so knowing only what was read since starting left older voicemails unplayable (2026-10-17). The id's
	// shape and the audio-only answer below still hold.
	a.mu.Lock()
	audio, seen := a.voicemailsWithAudio[messageID]
	unseenFirst := !seen && !a.unseenVoicemailAsked
	if !seen {
		a.unseenVoicemailAsked = true
	}
	a.mu.Unlock()
	id := strings.TrimPrefix(messageID, "c.")
	if seen && !audio || id == "" || len(id) > 200 || strings.Trim(id, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") != "" {
		return nil, "", "media_unavailable"
	}
	if unseenFirst {
		a.diagnose(map[string]any{"voicemail": "unseen_asked"})
	}
	address := fmt.Sprintf("https://voice.google.com/u/%s/a/v/%s", url.PathEscape(client.AuthUser), id)
	download := a.downloadVoicemail
	if download == nil {
		download = func(ctx context.Context, client *libgv.Client, address, attempt string, from int) (*http.Response, error) {
			headers := http.Header{}
			if from > 0 {
				headers.Set("Range", fmt.Sprintf("bytes=%d-", from))
			}
			if attempt == "cookies_only" {
				return cookieOnlyGet(ctx, client, address, headers)
			}
			return client.MakeRequest(ctx, http.MethodGet, address, nil, headers, nil)
		}
	}
	var resp *http.Response
	var err error
	var used string
	for _, attempt := range []string{"direct", "cookies_only"} {
		used = attempt
		resp, err = download(ctx, client, address, attempt, 0)
		if err == nil {
			break
		}
		a.diagnose(map[string]any{"voicemail": attempt, "status": statusOf(err)})
		if !libgv.IsAuthError(err) {
			break
		}
	}
	if err != nil {
		// A refused recording is not a signed-out account: the conversation and its history keep working.
		if libgv.IsAuthError(err) {
			return nil, "", "media_unavailable"
		}
		return nil, "", authCode(err)
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(io.LimitReader(resp.Body, mediaLimit+1))
	if err != nil {
		return nil, "", authCode(err)
	}
	// Google answers with part of the recording (HTTP 206). When it says more remains, ask for the rest, the way the
	// browser's player does, until the whole recording is here.
	if resp.StatusCode == http.StatusPartialContent {
		total, ok := rangeTotal(resp.Header.Get("Content-Range"))
		for parts := 1; ok && len(data) < total && total <= mediaLimit; parts++ {
			if parts >= 64 {
				return nil, "", "media_unavailable"
			}
			next, err := download(ctx, client, address, used, len(data))
			if err != nil {
				return nil, "", "media_unavailable"
			}
			more, err := io.ReadAll(io.LimitReader(next.Body, int64(total-len(data))))
			next.Body.Close()
			if err != nil || len(more) == 0 {
				return nil, "", "media_unavailable"
			}
			data = append(data, more...)
		}
		if ok && (len(data) != total || total > mediaLimit) {
			return nil, "", "media_unavailable"
		}
	}
	mime := strings.ToLower(strings.TrimSpace(strings.Split(resp.Header.Get("Content-Type"), ";")[0]))
	if mime == "" || mime == "application/octet-stream" {
		mime = http.DetectContentType(data)
	}
	a.diagnose(map[string]any{"voicemail": "downloaded", "status": resp.StatusCode, "type": mediaType(mime), "kb": len(data) / 1024})
	if !strings.HasPrefix(mime, "audio/") && mime != "video/mp4" && mime != "application/ogg" {
		return nil, "", "media_unavailable"
	}
	if len(data) == 0 || len(data) > mediaLimit {
		return nil, "", "media_unavailable"
	}
	return data, mime, ""
}

func mediaType(mime string) string {
	if mime == "" || len(mime) > 40 || strings.Trim(mime, "abcdefghijklmnopqrstuvwxyz0123456789/.+-") != "" {
		return "unknown"
	}
	return strings.NewReplacer("/", "_", ".", "_", "+", "_").Replace(mime)
}

// A plain GET with the account's cookies, as a browser's audio element sends it from voice.google.com. Go's client drops
// the cookies if Google redirects to another host.
func cookieOnlyGet(ctx context.Context, client *libgv.Client, address string, headers http.Header) (*http.Response, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, address, nil)
	if err != nil {
		return nil, err
	}
	for name, values := range headers {
		req.Header[name] = values
	}
	req.Header.Set("User-Agent", libgv.UserAgent)
	req.Header.Set("Referer", "https://voice.google.com/")
	req.Header.Set("Accept", "audio/*,*/*;q=0.8")
	for name, value := range client.GetCookies() {
		req.AddCookie(&http.Cookie{Name: name, Value: value})
	}
	resp, err := client.HTTP.Do(req)
	if err != nil {
		return nil, err
	}
	if resp.StatusCode >= 400 {
		resp.Body.Close()
		return nil, &libgv.ResponseError{Req: req, Resp: resp}
	}
	return resp, nil
}

// The full size from a Content-Range header such as "bytes 0-16383/44210".
func rangeTotal(header string) (int, bool) {
	slash := strings.LastIndex(header, "/")
	if !strings.HasPrefix(header, "bytes ") || slash < 0 {
		return 0, false
	}
	total, err := strconv.Atoi(header[slash+1:])
	return total, err == nil && total > 0
}
