// Experimental local adapter for Facebook Messenger; no Matrix server or bridge process required.
// The spike reports counts only: it never returns conversation names, participants or message text.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
	waTypes "go.mau.fi/whatsmeow/types"
)

const protocolVersion = 4
const libraryVersion = "mautrix-meta v0.2609.1-0.20261012222411-4b3889e607c0"

// How many conversations the list pages back to once connected.
const listDepth = 200

// How long a conversation Facebook had no messages for is left alone before asking again.
const emptyRetry = 90 * time.Second

// What Facebook's own thread fetch uses to mean "the most recent, from the end".
const endOfTime = 9999999999999

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

type adapter struct {
	unreadable unreadableMessages
	// False until the connection has finished its opening synchronisation, and again from a drop until the next one
	// finishes; only the arrival-age log reads it.
	socketLive     atomic.Bool
	mediaMu        sync.Mutex
	mediaRefreshes map[string]time.Time
	// What lookups of sent messages that have no row have spent, by Facebook's id for the message (sentrow.go).
	sentLooks      map[string]*sentLook
	downloadSlots  [3]chan struct{}
	historyLocks   sync.Map
	encryptedStart sync.Mutex
	mu             sync.Mutex
	deleteMu       sync.Mutex
	deleteWatches  map[int64]chan struct{}
	// Archive moves waiting for Messenger to state the conversation's new folder (updatechat.go).
	folders folderWatches
	// Serialize applying incoming state with persistence/deletion; never held during a network request.
	state            sync.Mutex
	identityRequests map[int64]bool
	// Offline tests replace the read-only encrypted group metadata request.
	groupInfo func(context.Context, waTypes.JID) (*waTypes.GroupInfo, error)
	// The pinned source patch synchronizes library cursor/thread-range state, including sync workers.
	client *messagix.Client
	// Stands in for the socket so a task round trip can be driven offline; nil means the client is used.
	executeTasks func(context.Context, ...socket.Task) (*table.LSTable, error)
	totals       summary
	events       map[string]int
	lifetime     context.Context
	stop         context.CancelFunc
	out          *output
	chats        *store
	media        []*mediaCache
	// Whether a sweep of idle downloads is pending, under mediaMu; and the clock they are aged by, nil but in tests.
	mediaSweep bool
	mediaNow   func() time.Time
	// The client for media off Meta's hosts, which carries no session (mediafetch.go); nil but in tests.
	anonymousMedia *http.Client
	// When each conversation's history was last asked for. The chat list asks for every conversation it has no
	// preview for, so a conversation Facebook answers nothing for must not cost a fetch every time it is asked about:
	// the helper answers one call at a time, and everything else — the pictures on screen — waits behind it.
	asked map[int64]time.Time
	// Tables waiting to have their cursors taken, see trackCursors.
	cursors  chan *table.LSTable
	tracking sync.Once
	// How paging the conversation list back is getting on, for the status in Settings.
	paging string
	// Whether a round of profile picture lookups is already running.
	facing atomic.Bool
	// The second connection, for conversations Facebook has moved to end-to-end encryption.
	crypto   *encrypted
	deviceID uint16
	// Receive-only diagnostic process: existing device only, no user actions or recovery registration.
	observeReceipts bool
	mediaCheck      bool // Ordinary download diagnostic; its request allowlist cannot mutate or start E2EE.
	// A process that only moves the kept history in or out of an account's messages.db (backup.open, then
	// backup.export or backup.import) with no Facebook contact at all: it never signs in, so no client is ever created.
	// Selected by the environment alone, so nothing a caller sends can leave it.
	offline        bool
	testReceipt    bool
	receiptProbe   *receiptProbe
	receiptOrigins sync.Map // Diagnostic only: message ID -> original device JID.
	// Set when Facebook has said this computer is no longer one of the account's devices, so Settings can say so
	// instead of reporting encrypted conversations as working.
	encryptedGone atomic.Bool
	// When the encrypted socket was last asked to open or was last seen to drop (Unix ms), so a connection that is
	// still coming up is told from one that is down (encryptedstate.go).
	encryptedChanged atomic.Int64
	// The encrypted connection's word the app was last told by an "encrypted" event (encryptedstate.go).
	announcedMu sync.Mutex
	announced   string
	// Whether this run has already registered the device again after Facebook said it was gone. Once is recovery;
	// twice would be the loop that filled the account with devices.
	reregistered atomic.Bool
	// Whether the app has asked the helper to connect yet. Until it has, waiting for the connection is waiting for
	// something nobody has started: see readyToSend.
	connectAsked atomic.Bool
	// Everything ever received, written down: Facebook stops serving a conversation's history the moment it encrypts
	// it, so what has already been fetched is the only copy that survives.
	kept *messageStore
	// True only for a reconnect using the host's protected pre-upgrade session. It authorizes the one-time move of
	// unpartitioned databases after the helper verifies which account those cookies actually open.
	adoptLegacy bool
	// Stands in for the anchored fetch in tests, so an attempted refresh can be told from a skipped one.
	fetch func(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error)
	// Pages whose refresh is already running, so the same page asked for twice does not start two.
	refreshing      map[int64]map[int64]bool
	recentCatchUp   *recentCatchUp
	historyRecovery historyRecovery
	// The file being sent, as its chunks arrive. One at a time: the app sends one and waits for the answer.
	upload       []byte
	batchUploads [][]byte
	previewJobs  *linkPreviewJobs
	// The history backup's export in progress, and the lock that keeps its steps one at a time; see backup.go.
	backup backupState
}

func (a *adapter) setPaging(state string) {
	a.mu.Lock()
	a.paging = state
	a.mu.Unlock()
}

func newAdapter(out *output) *adapter {
	ctx, cancel := context.WithCancel(context.Background())
	return &adapter{out: out, events: map[string]int{}, lifetime: ctx, stop: cancel, chats: newStore(),
		asked: map[int64]time.Time{}, cursors: make(chan *table.LSTable, 64),
		deleteWatches:   map[int64]chan struct{}{},
		observeReceipts: os.Getenv("NATIVEBEEPER_MESSENGER_OBSERVE_RECEIPTS") == "1",
		mediaCheck:      os.Getenv("NATIVEBEEPER_MESSENGER_MEDIA_CHECK") == "1",
		offline:         os.Getenv("NATIVEBEEPER_MESSENGER_OFFLINE") == "1",
		testReceipt:     os.Getenv("NATIVEBEEPER_MESSENGER_TEST_RECEIPT") == "1"}
}

// Events carry counts only, never the rows themselves.
func (a *adapter) record(kind string, counts summary) {
	a.mu.Lock()
	a.events[kind]++
	a.totals = a.totals.add(counts)
	a.mu.Unlock()
	a.out.write(response{Event: kind, Result: counts})
}

func (a *adapter) handle(ctx context.Context, evt any) {
	// This runs on the library's own goroutine: an unexpected row shape must not take the whole helper down with it.
	defer func() { _ = recover() }()
	if tbl, ok := evt.(*table.LSTable); ok {
		a.observeHistoryTable(tbl)
		// Which messages are new is a question about the store before this table is applied.
		arrivals := a.chats.arrivalRows(tbl)
		touched := a.applyTable(ctx, tbl)
		a.ordinaryTyping(tbl)
		a.record("table", summarize(tbl))
		logRows("socket", tbl, touched)
		a.logArrivalAge(tbl, arrivals)
		// Handed on rather than taken here: this is the socket's own goroutine, and it also delivers the reply to a
		// request that may be holding the request lock. A table that cannot be handed on straight away is worth a
		// short wait: dropping it loses the cursor it carried, and paging the list back then stops early for no
		// reason anyone can see. Waiting forever is not an option either — this goroutine delivers the replies the
		// queue's own consumer is waiting for, so blocking here would deadlock the two of them together.
		select {
		case a.cursors <- tbl:
		default:
			timeout := time.NewTimer(2 * time.Second)
			select {
			case a.cursors <- tbl:
			case <-timeout.C:
				logf("a table was dropped before its cursor could be taken")
			}
			timeout.Stop()
		}
		// Ids only, so the app can refresh the conversations that moved without asking for everything.
		if !touched.empty() {
			a.out.write(response{Event: "changed", Result: touched})
		}
		return
	}
	switch evt.(type) {
	case *messagix.ConnectedEvent:
		a.socketLive.Store(true)
		a.record("connected", summary{})
		if !a.mediaCheck {
			a.startRecentCatchUp()
		}
	case *messagix.ReconnectedEvent:
		a.socketLive.Store(true)
		a.historyConnectionResumed()
		a.record("reconnected", summary{})
	case *messagix.TransientDisconnectEvent:
		a.socketLive.Store(false)
		a.historyConnectionInterrupted()
		a.record("disconnected", summary{})
	case *messagix.PermanentErrorEvent:
		// messagix has left its connection loop for good (Facebook closed the socket as unauthorised) and will not
		// reconnect. The app restarts the helper on this event, so the line marks why the next one began.
		logf("socket failed permanently")
		a.record("failed", summary{})
	}
}

func platformFrom(name string) types.Platform {
	if name == "messenger" {
		return types.Messenger
	}
	return types.Facebook
}

func readCookies(raw map[string]string, platform types.Platform) (*cookies.Cookies, string) {
	if len(raw) == 0 {
		return nil, "missing_cookies"
	}
	jar := &cookies.Cookies{Platform: platform}
	values := make(map[cookies.MetaCookieName]string, len(raw))
	for key, value := range raw {
		values[cookies.MetaCookieName(key)] = value
	}
	jar.UpdateValues(values)
	if len(jar.GetMissingCookieNames()) > 0 {
		return nil, "missing_cookies"
	}
	return jar, ""
}

// Only fixed codes cross the pipe; upstream error text may describe the account.
func loginError(err error) string {
	switch {
	case errors.Is(err, httpclient.ErrChallengeRequired):
		return "challenge_required"
	case errors.Is(err, httpclient.ErrCheckpointRequired):
		return "checkpoint_required"
	case errors.Is(err, httpclient.ErrConsentRequired):
		return "consent_required"
	case errors.Is(err, httpclient.ErrTokenInvalidated):
		return "session_expired"
	default:
		return "login_failed"
	}
}

func (a *adapter) login(ctx context.Context, r request) (any, string) {
	if a.client != nil {
		return nil, "session_already_started"
	}
	var p struct {
		Cookies     map[string]string `json:"cookies"`
		Platform    string            `json:"platform"`
		AdoptLegacy bool              `json:"adoptLegacy"`
	}
	if json.Unmarshal(r.Params, &p) != nil {
		return nil, "invalid_input"
	}
	jar, code := readCookies(p.Cookies, platformFrom(p.Platform))
	if code != "" {
		return nil, code
	}
	viewer := jar.GetUserID()
	// An account whose device now lives on another machine is not signed in here: no request, nothing written.
	if movedAway(viewer) {
		logf("login refused reason=moved_away")
		return nil, "moved_away"
	}
	client := messagix.NewClient(jar, libraryLogger(), &messagix.Config{})
	client.SetEventHandler(a.handle)
	// The kept history is read while Facebook answers, not after: the two were a cold start's largest costs, about 2.4 s
	// of round trip and 2.7 s of unsealing, and neither needs the other. See startKeptRestore for what it may and may
	// not do before the login is known to be good.
	early := a.startKeptRestore(ctx, viewer)
	// Signing in has four distinct costs and they are minutes apart on a cold start, so each one says how long it
	// took. Without these the whole of login and connect is one unexplained gap in the log, and the app is showing a
	// conversation as still updating for the length of it.
	started := time.Now()
	_, tbl, err := client.LoadMessagesPage(ctx)
	if err != nil {
		client.Disconnect()
		early.abandon(a)
		return nil, loginError(err)
	}
	loaded := time.Since(started)
	logf("login page loaded in %dms", loaded.Milliseconds())
	a.chats.setSelf(viewer)
	a.adoptLegacy = p.AdoptLegacy
	// Everything kept from earlier runs, before anything new arrives. Facebook will not serve the history of a
	// conversation it has since encrypted, so this is the only copy of it there is.
	if early != nil {
		early.finish(a, loaded)
	} else {
		restoring := time.Now()
		if kept, err := openAccountMessageStore(ctx, adoptLegacyDatabase(viewer, "messages.db", p.AdoptLegacy), viewer); err == nil {
			a.kept = kept
			discardStaleBackup(kept)
			if restored := kept.restore(ctx, a.chats); restored > 0 {
				logf("restored %d kept messages in %dms", restored, time.Since(restoring).Milliseconds())
			}
			kept.restoreReceipts(ctx, a.chats)
		} else {
			logf("kept messages unavailable")
		}
	}
	applying := time.Now()
	a.prepareRecentCatchUp()
	a.applyTable(ctx, tbl)
	counts := summarize(tbl)
	logf("login applied first page in %dms", time.Since(applying).Milliseconds())
	a.mu.Lock()
	a.client = client
	a.totals = a.totals.add(counts)
	a.mu.Unlock()
	return map[string]any{"loggedIn": true, "counts": counts}, ""
}

// How many times a conversation is asked of the ordinary connection for more of its history.
//
// None at all when it is encrypted. That history travels on the encrypted connection and lives in the local archive;
// the ordinary connection has never answered one of these with a single row, which 0.1.200 established for the
// refresh behind the page and is just as true of the rounds in front of it. The asking was not free: the first open
// of each encrypted conversation in a session spent about 1.6 seconds on it and returned exactly the messages it
// already had — returning=27 known=27, and returning=5 known=5, measured. Every open after that was instant, because
// the one that paid had marked the conversation asked.
//
// A conversation with nothing known needs two rounds: one to be given its most recent messages, one to page back
// from them. The rounds stop as soon as one brings nothing, and the helper answers one call at a time, so this is
// deliberately short — everything else on screen is waiting behind it.
func historyRounds(known int, secure bool) int {
	switch {
	case secure:
		return 0
	case known == 0:
		return 2
	default:
		return 4
	}
}

// Messenger delivers a fetched page as table rows over the socket rather than in the reply, so this asks, then waits
// for the rows to land. Facebook answers with a page at a time, so it asks again until the conversation has enough or
// stops growing. before is a cursor in milliseconds: 0 for the newest messages, otherwise page back from there.
func (a *adapter) history(ctx context.Context, key int64, count int, before int64) (any, string) {
	value, _ := a.historyLocks.LoadOrStore(key, make(chan struct{}, 1))
	turn := value.(chan struct{})
	select {
	case turn <- struct{}{}:
	case <-ctx.Done():
		return nil, "request_cancelled"
	}
	defer func() { <-turn }()
	if ctx.Err() != nil {
		return nil, "request_cancelled"
	}
	a.mu.Lock()
	asked := a.asked[key]
	a.mu.Unlock()
	known := a.chats.messageCount(key)
	// The kind decides which connection a conversation's messages and pictures travel on, so a conversation whose
	// pictures never arrive is a different question depending on it.
	kind, secure := a.chats.kindOf(key)
	logf("history chat=%s before=%d asked=%v known=%d kind=%d encrypted=%v", tag(key), before, !asked.IsZero(), known, kind, secure)
	// Whether this particular restored page still needs reconciliation. The archive keeps attachments too, but its
	// URLs can expire and bootstrap can omit a whole recent message. Judged per page: an older-page request marks the chat asked,
	// and the archiver asks for older pages in the background, so a conversation-wide mark would let background work
	// answer the newest page from the stored copy without ever refreshing it.
	stale := a.pageNeedsRefresh(key, before)
	// Answer from what is known when there is nothing to be gained by asking again: a conversation already fetched, or
	// one Facebook had nothing for a moment ago. The chat list asks about every conversation it has no preview for, so
	// without this each of those costs a fetch, and everything else waits behind them.
	if !stale && before == 0 && !asked.IsZero() && (known > 0 || time.Since(asked) < emptyRetry) {
		page := a.chats.historyBefore(key, 0, count)
		logf("history chat=%s answered from memory=%d", tag(key), len(page))
		return page, ""
	}
	a.mu.Lock()
	a.asked[key] = time.Now()
	a.mu.Unlock()
	// A conversation with nothing known needs two rounds: one to be given its most recent messages, one to page back
	// from them. The rounds stop as soon as one brings nothing, and the helper answers one call at a time, so this is
	// deliberately short — everything else on screen is waiting behind it.
	rounds := historyRounds(known, secure)
	if rounds == 0 && secure {
		logf("history chat=%s encrypted, not asked of the ordinary connection", tag(key))
	}
	// Restored messages do not prove a page is complete. A photo omitted at login can be missing entirely, and stored
	// attachment URLs expire. Only a refresh that actually covered the page counts: marking it beforehand meant a
	// cancelled or failed attempt was never tried again.
	// The refresh happens behind the answer, not in front of it. It costs several requests on a pipe that carries one
	// call at a time, and holding the conversation back for them stalled everything else waiting — the pictures on
	// screen and the faces in the list included. The page is served from what is known, and the conversation is
	// announced again when the pictures arrive, which is the same path anything else arriving takes.
	if stale {
		a.refreshBehind(key, before, count)
	}
	{
		for round := 0; round < rounds; round++ {
			if a.chats.countBefore(key, before) >= count {
				break
			}
			reference, referenceID := before, ""
			group := int64(1)
			if timestamp, id := a.chats.oldestBefore(key, before); timestamp > 0 {
				reference, referenceID = timestamp, id
			} else if before != 0 {
				// Nothing older than the cursor is held, which is the normal case when paging back: the cursor is the
				// oldest message there is. Facebook needs a real message to page from and ignores a bare timestamp, so
				// the message at the cursor itself is the one to ask from.
				if at, id := a.chats.oldestBefore(key, before+1); id != "" {
					reference, referenceID = at, id
				}
			} else if reference == 0 {
				// Nothing of this conversation has arrived, so there is nothing to page back from. Facebook answers
				// such a request with an empty table on every sync group it has, and answers the contact group with
				// rows that carry no text; there is no request left that brings this conversation's messages. The one
				// message Facebook has named for it, if it named one, is the only way in.
				at, id := a.chats.anchor(key)
				if id == "" {
					logf("history chat=%s has no message to ask from", tag(key))
					break
				}
				reference, referenceID = at, id
			}
			logf("history chat=%s round=%d group=%d reference=%d anchored=%v known=%d", tag(key), round, group, reference, referenceID != "", a.chats.countBefore(key, before))
			tbl, err := a.request(ctx, func() []socket.Task {
				return []socket.Task{&socket.FetchMessagesTask{
					ThreadKey:            key,
					Direction:            0,
					ReferenceTimestampMs: reference,
					ReferenceMessageId:   referenceID,
					SyncGroup:            group,
					Cursor:               a.client.GetCursor(group),
				}}
			})
			if err != nil {
				logf("history chat=%s round=%d failed", tag(key), round)
				if round == 0 {
					return nil, "history_failed"
				}
				break
			}
			applied := a.applyTable(ctx, tbl)
			logRows("history reply", tbl, applied)
			if !a.waitForMessages(ctx, key, before, count) {
				// Nothing more arrived, so this is the whole conversation.
				break
			}
		}
	}
	page := a.chats.historyBefore(key, before, count)
	logf("history chat=%s returning=%d withMedia=%d known=%d stale=%v", tag(key), len(page), withMedia(page), a.chats.messageCount(key), stale)
	return page, ""
}

// messagix learns from a table that there are older conversations, but only when the caller tells it to; the bridge
// does this for every table its handler receives. Without it the thread key store keeps its starting "nothing older"
// state, and paging the list back asks for nothing, forever.
func (a *adapter) trackCursors() {
	for tbl := range a.cursors {
		a.client.PostHandlePublishResponse(tbl)
	}
}

// Cursor getters return owned snapshots; network requests can run independently.
// One fetch anchored at a known message. Behind a field so a test can stand in for it and count what was asked for:
// without that, the only way to tell an attempted refresh from a skipped one is whether it happened to fail.
func (a *adapter) fetchFrom(ctx context.Context, key int64, anchor pageAnchor) (*table.LSTable, error) {
	if a.fetch != nil {
		return a.fetch(ctx, key, anchor)
	}
	return a.request(ctx, func() []socket.Task {
		return []socket.Task{&socket.FetchMessagesTask{
			ThreadKey:            key,
			Direction:            0,
			ReferenceTimestampMs: anchor.at,
			ReferenceMessageId:   anchor.id,
			SyncGroup:            1,
			Cursor:               a.client.GetCursor(1),
		}}
	})
}

func (a *adapter) request(ctx context.Context, build func() []socket.Task) (*table.LSTable, error) {
	if a.executeTasks != nil {
		return a.executeTasks(ctx, build()...)
	}
	return a.client.ExecuteTasks(ctx, build()...)
}

func (a *adapter) fetchThreads(ctx context.Context, syncGroup int64) (*table.LSTable, error) {
	_, tbl, err := a.client.FetchMoreThreads(ctx, syncGroup)
	return tbl, err
}

// How far back the conversation list has been asked for, and whether Facebook says there is more before that. The
// store behind these is one of the plain maps the request lock protects, and the socket updates it as pages arrive.
func (a *adapter) threadCursor(syncGroup int64) (int64, bool) {
	keys := a.client.GetSyncGroupKeyStore(syncGroup)
	if keys == nil {
		return 0, false
	}
	return keys.MinLastActivityTimestampMs, keys.HasMoreBefore
}

// Waits for a requested page to arrive over the socket. False when nothing new came.
//
// The app speaks to the helper one call at a time, so whatever this waits for, everything else waits for too — the
// pictures in the conversation being opened included. It is deliberately brief: rows that arrive after it gives up
// are announced as a change, which brings the conversation up to date anyway.
// About how many messages Facebook answers a single fetch with, which decides how far apart the anchors are when a
// page has to be walked.
const refreshStep = 20

// The most asks one page refresh will make. A page that shows fewer messages than were asked for has no far end to
// stop at, so without this a conversation restored whole would be walked from end to end on its first open.
const refreshAsks = 6

// Whether a page's refresh is still running.
func (a *adapter) refreshRunning(key int64, before int64) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.refreshing[key][before]
}

// Starts a refresh for a page and returns at once, so the conversation is not held up by it. One at a time per page:
// the app asks for the same page more than once while a refresh is still running, and each ask must not start another.
func (a *adapter) refreshBehind(key int64, before int64, count int) bool {
	a.mu.Lock()
	if a.refreshing == nil {
		a.refreshing = map[int64]map[int64]bool{}
	}
	pages, ok := a.refreshing[key]
	if !ok {
		pages = map[int64]bool{}
		a.refreshing[key] = pages
	}
	if pages[before] || a.chats.pageRefreshed(key, before) {
		a.mu.Unlock()
		return false
	}
	pages[before] = true
	a.mu.Unlock()
	go func() {
		defer func() { _ = recover() }()
		defer func() {
			a.mu.Lock()
			delete(a.refreshing[key], before)
			a.mu.Unlock()
		}()
		covered := a.refreshPage(a.lifetime, key, before, count)
		if a.lifetime.Err() != nil {
			return
		}
		first := a.chats.pageRefreshTries(key, before) == 0
		a.chats.notePageRefreshTry(key, before)
		if covered {
			a.chats.markPageRefreshed(key, before)
		}
		// Covered, the pictures are here and the conversation is worth drawing again. The end of the first try is
		// announced even when Facebook answered nothing: the app is waiting on it to stop showing the page as updating.
		if covered || first {
			chat := strconv.FormatInt(key, 10)
			a.out.write(response{Event: "changed", Result: &changes{Chats: []string{chat}}})
		}
	}()
	return true
}

// An uncovered page is tried again behind later reads, but only a few times in one helper session: a page Facebook
// never answers for would otherwise send it a round of fetches on every visit.
const maxPageRefreshTries = 3

func (a *adapter) pageNeedsRefresh(key int64, before int64) bool {
	return a.pageRefreshable(key) && a.chats.restoredFrom(key) && !a.chats.pageRefreshed(key, before) &&
		a.chats.pageRefreshTries(key, before) < maxPageRefreshTries
}

// Whether it is worth waiting for the connection before acting on the account. Every mutation waits up to sendWait for
// the socket to become usable, which is right once connecting has been asked for and a trap before it: connect is a
// lifecycle method, so the dispatcher holds it until every request already in flight has finished, and a request
// spending twenty seconds waiting for the connection is therefore waiting for something queued behind itself. A cold
// start spent exactly that — the app marks the conversation it reopens as read, that arrives while login is still
// running, and connect could not begin until the wait gave up. Refusing at once is also the truthful answer: nothing
// reached the account either way, so the refusal stays definite rather than becoming an uncertain mutation.
func (a *adapter) readyToSend(ctx context.Context) bool {
	return a.connectAsked.Load() && a.client.WaitUntilCanSendMessages(ctx, sendWait) == nil
}

// Whether asking Facebook for a page of this conversation can bring anything back at all. An encrypted conversation's
// messages and pictures travel on the encrypted connection, and the Lightspeed fetch a refresh makes is answered for
// one with a table holding no rows whatsoever — every attempt, on every encrypted conversation, across a whole
// session. So its page is never covered, which leaves it stale, which costs the conversation a foreground fetch and a
// background one on each of its next few opens and shows the app a refresh that was never going to finish. Its
// history comes from the local archive, and its pictures from the encrypted connection, instead.
func (a *adapter) pageRefreshable(key int64) bool {
	_, secure := a.chats.kindOf(key)
	return !secure
}

// Whether the page the app is drawing is still waiting on its first refresh. Later tries run quietly: judged by what
// the helper has done rather than by who has read the page, so a chat-list preview read cannot use up the signal.
func (a *adapter) pageFirstRefreshPending(key int64, before int64) bool {
	return a.pageRefreshable(key) && a.chats.restoredFrom(key) && !a.chats.pageRefreshed(key, before) &&
		a.chats.pageRefreshTries(key, before) == 0
}

// Reconciles a page answered from the local store, fetching missing messages and refreshing attachment locations.
// A response can repeat already known messages with their attachment rows, so completion does not require growth.
// Reports whether the page was actually covered. Anything less — an error, a cancellation, an ask that went
// unanswered — leaves the page unmarked so the next request tries again.
func (a *adapter) refreshPage(ctx context.Context, key int64, before int64, count int) bool {
	// Bounded by how far back the page actually reaches rather than by a count of messages: the rows that are missing
	// are the ones not in the page yet, so counting what is in it would stop short of them. A page showing fewer
	// messages than were asked for has no far end to stop at — everything older is still missing — so it is walked to
	// the end of what is stored, within a limit.
	page := a.chats.historyBefore(key, before, count)
	until := int64(0)
	if len(page) >= count && len(page) > 0 {
		until = page[0].At.UnixMilli()
	}
	anchors := a.chats.pageAnchors(key, before, until, refreshStep)
	if len(anchors) > refreshAsks {
		anchors = anchors[:refreshAsks]
	}
	landed := 0
	for _, anchor := range anchors {
		if ctx.Err() != nil {
			logf("history chat=%s refresh gave up after %d of %d asks", tag(key), landed, len(anchors))
			return false
		}
		revision := a.chats.revision(key)
		recoveryTicket := a.historyRecoveryAttempt()
		tbl, err := a.fetchFrom(ctx, key, anchor)
		if err != nil {
			logf("history chat=%s refresh failed", tag(key))
			return false
		}
		a.observeHistoryTable(tbl)
		a.applyTable(ctx, tbl)
		// Facebook answers over the socket a moment after the reply, and the answer changes no count — these messages
		// are already known — so waiting for the conversation to grow would not wait at all.
		if !a.waitForAnswer(ctx, key, revision) {
			logf("history chat=%s refresh went unanswered after %d of %d asks", tag(key), landed, len(anchors))
			a.historyUnanswered(ctx, key, anchor, recoveryTicket, tbl)
			return false
		}
		a.historyRefreshAnswered(recoveryTicket)
		landed++
	}
	logf("history chat=%s refreshed asks=%d landed=%d", tag(key), len(anchors), landed)
	return true
}

// Waits for Facebook's answer to land rather than for the conversation to grow. A refresh asks for messages that are
// already known and wants the attachment rows that come with them, and those change no count at all — so the only sign
// that the answer has arrived is the conversation being touched again.
func (a *adapter) waitForAnswer(ctx context.Context, key int64, revision int) bool {
	deadline := time.After(2 * time.Second)
	answered, quiet := false, 0
	for {
		select {
		case <-ctx.Done():
			return false
		case <-deadline:
			// Rows that were still arriving when the time ran out are an answer all the same.
			return answered
		case <-time.After(50 * time.Millisecond):
			if current := a.chats.revision(key); current != revision {
				revision, answered, quiet = current, true, 0
				continue
			}
			// The rows are applied in batches, so the answer is complete only once the conversation stops being
			// touched. Waiting for two quiet polls rather than a fixed pause settles a single-batch answer — which is
			// what an anchored refresh usually gets -- in about 100 ms instead of 250, and still gives one that is
			// genuinely still arriving all the time it needs. A page walks up to six anchors, so the pause was worth
			// up to 1.5 seconds of the conversation showing as updating.
			if answered {
				if quiet++; quiet >= 2 {
					return true
				}
			}
		}
	}
}

func (a *adapter) waitForMessages(ctx context.Context, key, before int64, count int) bool {
	deadline := time.After(1200 * time.Millisecond)
	start := a.chats.countBefore(key, before)
	known, settled := start, 0
	for known < count && settled < 3 {
		select {
		case <-ctx.Done():
			return false
		case <-deadline:
			settled = 3
		case <-time.After(100 * time.Millisecond):
			updated := a.chats.countBefore(key, before)
			if updated != known {
				known, settled = updated, 0
				continue
			}
			// Settle only once the page has started arriving; before that, wait for it.
			if updated > start {
				settled++
			}
		}
	}
	return known > start
}

// The conversation a request names, as Facebook's own thread key.
func chatKey(params json.RawMessage) (int64, string) {
	var p struct {
		ChatID string `json:"chatId"`
	}
	if json.Unmarshal(params, &p) != nil || p.ChatID == "" {
		return 0, "invalid_input"
	}
	key, err := strconv.ParseInt(p.ChatID, 10, 64)
	if err != nil {
		return 0, "invalid_input"
	}
	return key, ""
}

func (a *adapter) call(r request) (any, string) {
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	return a.callContext(ctx, r)
}

func (a *adapter) callContext(ctx context.Context, r request) (any, string) {
	if a.offline {
		// Checked before anything else: nothing in this process may reach Facebook, so a method that could is refused by
		// name rather than by whatever login state it happens to find.
		switch r.Method {
		case "hello", "backup.open", "backup.export", "backup.exportEnd", "backup.import", "backup.count", "disconnect":
		default:
			return nil, "offline_only"
		}
	}
	if r.Method == "backup.open" {
		return a.backupOpen(ctx, r)
	}
	if a.mediaCheck {
		switch r.Method {
		case "hello", "login", "connect", "counts", "disconnect", "probe.media":
		default:
			return nil, "observation_only"
		}
	}
	if r.Method == "probe.media" {
		if !a.mediaCheck {
			return nil, "observation_only"
		}
		return a.checkMedia(ctx, r)
	}
	if r.Method == "probe.receipt" {
		if !a.observeReceipts || !a.testReceipt {
			return nil, "observation_only"
		}
		return a.probeReadReceipt(a.lifetime, r)
	}
	if a.observeReceipts {
		switch r.Method {
		case "hello", "login", "connect", "ui.encrypted", "counts", "disconnect":
		default:
			return nil, "observation_only"
		}
	}
	switch r.Method {
	case "hello":
		hello := map[string]any{"protocol": protocolVersion, "library": libraryVersion, "connected": a.client != nil && a.client.IsConnected(), "deviceSchema": deviceSchema()}
		if a.offline {
			hello["offline"] = true
		}
		return hello, ""
	case "login":
		return a.login(ctx, r)
	case "connect":
		if a.client == nil {
			return nil, "login_required"
		}
		// The connection outlives this request.
		a.connectAsked.Store(true)
		connecting := time.Now()
		if a.client.Connect(a.lifetime) != nil {
			logf("connect failed after %dms", time.Since(connecting).Milliseconds())
			return nil, "connect_failed"
		}
		logf("connected in %dms", time.Since(connecting).Milliseconds())
		a.tracking.Do(func() { go a.trackCursors() })
		// The rest of the conversation list arrives page by page, so it is fetched behind the connect rather than
		// keeping the app waiting for it.
		if !a.mediaCheck {
			go a.deepen(a.lifetime, listDepth)
			// And the history of everything not yet encrypted, while Facebook will still serve it.
			go a.archive(a.lifetime)
		}
		return map[string]bool{"connected": true}, ""
	case "ui.encrypted":
		if a.client == nil {
			return nil, "login_required"
		}
		// Registers this computer as one of the account's Messenger devices the first time, then connects. The app
		// asks for this only when the user has turned encrypted conversations on.
		//
		// {"restore": true} is the app reconnecting a device it already has (every start after the first): it never
		// registers one, and answers device_missing when this computer has none. Without it, this is the explicit turn-on
		// (or a first sign-in), which registers if there is no device.
		var p struct {
			Restore bool `json:"restore"`
		}
		if len(r.Params) > 0 && json.Unmarshal(r.Params, &p) != nil {
			return nil, "invalid_input"
		}
		if code := a.startEncrypted(ctx, p.Restore); code != "" {
			return nil, code
		}
		return map[string]bool{"connected": true}, ""
	case "ui.chats":
		if a.client == nil {
			return nil, "login_required"
		}
		var p struct {
			Count int `json:"count"`
		}
		if len(r.Params) > 0 && json.Unmarshal(r.Params, &p) != nil {
			return nil, "invalid_input"
		}
		// The list the app is about to show is the one worth having pictures for. One round at a time, and each round
		// announces what it found, which brings the app back here for the next.
		if a.facing.CompareAndSwap(false, true) {
			go a.faces(a.lifetime, 5)
		}
		list := a.chats.chats(p.Count)
		logf("ui.chats returning=%d known=%d", len(list), a.chats.threadCount())
		return list, ""
	case "ui.chat":
		if a.client == nil {
			return nil, "login_required"
		}
		key, code := chatKey(r.Params)
		if code != "" {
			return nil, code
		}
		found, ok := a.chats.chatByKey(key)
		if !ok {
			return nil, "unknown_chat"
		}
		return found, ""
	case "ui.message":
		if a.client == nil {
			return nil, "login_required"
		}
		var m struct {
			ChatID string `json:"chatId"`
			ID     string `json:"id"`
		}
		if json.Unmarshal(r.Params, &m) != nil || m.ID == "" {
			return nil, "invalid_input"
		}
		key, err := strconv.ParseInt(m.ChatID, 10, 64)
		if err != nil {
			return nil, "invalid_input"
		}
		found, ok := a.chats.messageByID(key, m.ID)
		if !ok {
			// Sent and paired, but the row never arrived: read the newest page once instead of leaving the send
			// unconfirmed (sentrow.go).
			found, ok = a.findSentRow(ctx, key, m.ID)
		}
		if !ok {
			return nil, "unknown_message"
		}
		return found, ""
	case "ui.sendText":
		if a.client == nil {
			return nil, "login_required"
		}
		var s struct {
			ChatID  string `json:"chatId"`
			Text    string `json:"text"`
			ReplyTo string `json:"replyTo"`
		}
		if json.Unmarshal(r.Params, &s) != nil || s.Text == "" {
			return nil, "invalid_input"
		}
		key, err := strconv.ParseInt(s.ChatID, 10, 64)
		if err != nil {
			return nil, "invalid_input"
		}
		return a.sendText(ctx, key, s.Text, s.ReplyTo)
	case "ui.deleteChat":
		return a.deleteChat(ctx, r)
	case "ui.updateChat":
		return a.updateChat(ctx, r)
	case "ui.searchContacts":
		return a.searchContacts(ctx, r)
	case "ui.startDirect":
		return a.startDirect(ctx, r)
	case "ui.startGroup":
		return a.startGroup(ctx, r)
	case "ui.renameGroup":
		return a.renameGroup(ctx, r)
	case "ui.addGroupMembers":
		return a.addGroupMembers(ctx, r)
	case "ui.removeGroupMember":
		return a.removeGroupMember(ctx, r)
	case "ui.typing":
		return a.sendTyping(ctx, r)
	case "ui.markRead":
		return a.markRead(ctx, r)
	case "ui.editMessage":
		return a.editMessage(ctx, r)
	case "ui.deleteMessage":
		return a.deleteMessage(ctx, r)
	case "ui.mediaStage":
		return a.mediaStage(r)
	case "ui.mediaBatchStage":
		return a.mediaBatchStage(r)
	case "ui.mediaBatchDiscard":
		return a.mediaBatchDiscard()
	case "ui.sendMedia":
		return a.sendMedia(ctx, r)
	case "ui.sendMediaBatch":
		return a.sendMediaBatch(ctx, r)
	case "ui.react":
		if a.client == nil {
			return nil, "login_required"
		}
		var p struct {
			ChatID    string `json:"chatId"`
			MessageID string `json:"messageId"`
			Key       string `json:"key"`
			Remove    bool   `json:"remove"`
		}
		if json.Unmarshal(r.Params, &p) != nil || p.MessageID == "" {
			return nil, "invalid_input"
		}
		key, err := strconv.ParseInt(p.ChatID, 10, 64)
		if err != nil {
			return nil, "invalid_input"
		}
		return a.reactTo(ctx, key, p.MessageID, p.Key, p.Remove)
	case "ui.messages", "ui.messagePage":
		if a.client == nil {
			return nil, "login_required"
		}
		var m struct {
			ChatID string `json:"chatId"`
			Count  int    `json:"count"`
			Before string `json:"before"`
		}
		if json.Unmarshal(r.Params, &m) != nil || m.ChatID == "" {
			return nil, "invalid_input"
		}
		key, err := strconv.ParseInt(m.ChatID, 10, 64)
		if err != nil {
			return nil, "invalid_input"
		}
		count := m.Count
		if count <= 0 || count > 200 {
			count = 50
		}
		var before int64
		if m.Before != "" {
			if before, err = strconv.ParseInt(m.Before, 10, 64); err != nil || before < 0 {
				return nil, "invalid_input"
			}
		}
		page, code := a.history(ctx, key, count, before)
		if code != "" || r.Method == "ui.messages" {
			return page, code
		}
		// The app can draw a restored page immediately and keep its recovery affordance until the first background
		// refresh of that page has finished. ui.messages stays as the array-only compatibility API.
		return map[string]any{"messages": page, "refreshing": a.pageFirstRefreshPending(key, before)}, ""
	case "ui.messageWindow":
		// A window of a conversation's history around a moment, for a search result further back than the app holds:
		// answered from what this helper already knows (the kept store, restored at login), never by asking Facebook for
		// older messages. A window whose rows came back from the store without their pictures is refreshed behind the
		// answer as any page is, which is what scrolling back to it would have done.
		if a.client == nil {
			return nil, "login_required"
		}
		var m struct {
			ChatID string `json:"chatId"`
			At     string `json:"at"`
			Older  int    `json:"older"`
			Newer  int    `json:"newer"`
		}
		if json.Unmarshal(r.Params, &m) != nil || m.ChatID == "" || m.Older < 0 || m.Older > 200 || m.Newer < 0 || m.Newer > 200 {
			return nil, "invalid_input"
		}
		key, err := strconv.ParseInt(m.ChatID, 10, 64)
		if err != nil {
			return nil, "invalid_input"
		}
		at, err := strconv.ParseInt(m.At, 10, 64)
		if err != nil || at < 0 {
			return nil, "invalid_input"
		}
		page, moreOlder, moreNewer := a.chats.historyAround(key, at, m.Older, m.Newer)
		logf("history window chat=%s older=%d newer=%d returned=%d more_older=%v more_newer=%v", tag(key), m.Older, m.Newer, len(page), moreOlder, moreNewer)
		if len(page) > 0 {
			// Keyed as historyBefore's page ending at the window's newest message would be.
			before := page[len(page)-1].At.UnixMilli() + 1
			if a.pageNeedsRefresh(key, before) {
				a.refreshBehind(key, before, len(page))
			}
		}
		return map[string]any{"messages": page, "moreOlder": moreOlder, "moreNewer": moreNewer}, ""
	case "ui.linkPreviews":
		if a.client == nil {
			return nil, "login_required"
		}
		var p struct {
			ChatID     string   `json:"chatId"`
			MessageIDs []string `json:"messageIds"`
		}
		if json.Unmarshal(r.Params, &p) != nil || len(p.MessageIDs) > 200 {
			return nil, "invalid_input"
		}
		key, err := strconv.ParseInt(p.ChatID, 10, 64)
		if err != nil {
			return nil, "invalid_input"
		}
		a.loadLinkPreviews(key, p.MessageIDs)
		return map[string]bool{"queued": true}, ""
	case "ui.mediaRead":
		return a.mediaRead(ctx, r)
	case "backup.export", "backup.exportEnd", "backup.import", "backup.count":
		return a.backupCall(ctx, r)
	case "counts":
		// Before the lock: reading the encrypted connection takes the same lock.
		encryptedNow := a.encryptedState(time.Now())
		a.mu.Lock()
		defer a.mu.Unlock()
		events := make(map[string]int, len(a.events))
		for kind, count := range a.events {
			events[kind] = count
		}
		state := a.paging
		if state == "" {
			state = "idle"
		}
		return map[string]any{"totals": a.totals, "events": events, "threads": a.chats.threadCount(), "paging": state,
			// Whether this computer is still one of the account's Messenger devices. Encrypted conversations carry
			// nothing without one, and that is worth saying in Settings rather than only in a log.
			"deviceGone": a.encryptedGone.Load(),
			// How the encrypted connection is doing, so Settings does not call Messenger connected while the
			// conversations most of the account lives in can carry nothing (encryptedstate.go).
			"encrypted": encryptedNow}, ""
	case "session":
		if a.client == nil {
			return nil, "login_required"
		}
		// Cookies only; they may have been refreshed while connected.
		return map[string]any{"cookies": a.client.GetCookies()}, ""
	case "disconnect":
		if a.client == nil {
			if a.offline {
				// Nothing was ever connected; there is nothing to close.
				return map[string]bool{"connected": false}, ""
			}
			return nil, "login_required"
		}
		a.client.Disconnect()
		return map[string]bool{"connected": false}, ""
	default:
		return nil, "unsupported_method"
	}
}

func serve(in io.Reader, out io.Writer) {
	o := &output{encoder: json.NewEncoder(out)}
	a := newAdapter(o)
	openLog()
	defer func() {
		// Before the log closes, so the export's end is recorded.
		a.endBackup("shutdown")
		closeLog()
		a.stop()
		if a.client != nil {
			a.client.Disconnect()
		}
	}()
	dispatch(in, o, a.callContext)
}

func main() { serve(os.Stdin, os.Stdout) }
