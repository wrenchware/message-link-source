package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/instameow"
)

// A body of any length that is made as it is read, counting what was taken from it.
type countedBody struct {
	read *atomic.Int64
	left int64
}

func (b *countedBody) Read(p []byte) (int, error) {
	if b.left <= 0 {
		return 0, io.EOF
	}
	n := min(int64(len(p)), b.left)
	clear(p[:n])
	b.left -= n
	b.read.Add(n)
	return int(n), nil
}

func (b *countedBody) Close() error { return nil }

// What one request carried that would identify the session.
type seenRequest struct {
	host, agent string
	session     []string
}

// Session headers and cookie, by the names BuildHeaders gives them.
var sessionHeaders = []string{"Cookie", "X-Csrftoken", "X-Ig-Www-Claim", "X-Mid", "X-Fb-Lsd"}

// Stands in for the network: records every request and answers by path. Nothing leaves the process.
type mediaWire struct {
	mu   sync.Mutex
	seen []seenRequest
	read atomic.Int64
}

func (w *mediaWire) RoundTrip(r *http.Request) (*http.Response, error) {
	request := seenRequest{host: r.URL.Hostname(), agent: r.Header.Get("User-Agent")}
	for _, name := range sessionHeaders {
		if r.Header.Get(name) != "" {
			request.session = append(request.session, name)
		}
	}
	w.mu.Lock()
	w.seen = append(w.seen, request)
	w.mu.Unlock()
	answer := &http.Response{StatusCode: http.StatusOK, Header: http.Header{"Content-Type": {"image/jpeg"}}, ContentLength: -1, Request: r}
	redirect := func(to string) {
		answer.StatusCode, answer.Header, answer.Body = http.StatusFound, http.Header{"Location": {to}}, http.NoBody
	}
	switch r.URL.Path {
	case "/photo", "/collect":
		answer.Body = io.NopCloser(bytes.NewReader([]byte("synthetic-jpeg")))
	case "/endless":
		// No length declared, and more than the limit sent.
		answer.Body = &countedBody{read: &w.read, left: mediaLimit + 4<<20}
	case "/declared":
		answer.ContentLength = mediaLimit + 1
		answer.Body = &countedBody{read: &w.read, left: mediaLimit + 1}
	case "/away":
		redirect("https://example.invalid/collect")
	case "/inward":
		redirect("https://10.0.0.7/photo")
	case "/downgrade":
		redirect("http://example.invalid/photo")
	case "/loop":
		redirect("/loop")
	default:
		answer.StatusCode, answer.Body = http.StatusNotFound, http.NoBody
	}
	return answer, nil
}

func (w *mediaWire) requests() []seenRequest {
	w.mu.Lock()
	defer w.mu.Unlock()
	return append([]seenRequest(nil), w.seen...)
}

func (w *mediaWire) asked() []string {
	var hosts []string
	for _, request := range w.requests() {
		hosts = append(hosts, request.host)
	}
	return hosts
}

// The conversation fixture, with upstream's own client for the downloads so its redirect policy and headers are the
// ones under test, and a wire in place of the network for both clients.
func mediaWireFixture(t *testing.T) (*readOnlyAdapter, *mediaWire) {
	t.Helper()
	a, _ := chatFixture()
	// A list just read, so a download that made it stale would show.
	chatsOf(t, a, `{}`)
	values := fakeCookies()
	values["ds_user_id"] = "123"
	jar, code := readCookies(values)
	if code != "" {
		t.Fatal(code)
	}
	jar.SetWWWClaim("synthetic-claim")
	client := newAppClient(jar, nil, func() {}).(*instameow.Client)
	wire := &mediaWire{}
	client.GetHTTP().SetTransportOverride(wire)
	a.client, a.jar, a.media = client, jar, newMediaStore()
	a.anonymousMedia = newAnonymousMedia(wire)
	return a, wire
}

func readMedia(a *readOnlyAdapter, id string) (any, string) {
	params, _ := json.Marshal(map[string]string{"id": id})
	return a.mediaRead(context.Background(), params)
}

func TestMetaMediaHosts(t *testing.T) {
	for _, address := range []string{"https://scontent.cdninstagram.com/v/photo.jpg", "https://INSTAGRAM.fxyz1-1.FNA.FBCDN.NET/x",
		"https://lookaside.fbsbx.com/x", "https://www.instagram.com/x", "https://www.facebook.com/x", "https://www.messenger.com/x",
		"https://mmg.whatsapp.net/x", "https://cdninstagram.com/x", "https://scontent.cdninstagram.com./x"} {
		parsed, err := url.Parse(address)
		if err != nil || !metaMediaURL(parsed) {
			t.Fatalf("a Meta address was refused: %s", address)
		}
	}
	for _, address := range []string{"http://scontent.cdninstagram.com/x", "https://evilcdninstagram.com/x", "https://cdninstagram.com.example.invalid/x",
		"https://example.invalid/x", "https://scontent.cdninstagram.com@example.invalid/x", "https:scontent.cdninstagram.com",
		"//scontent.cdninstagram.com/x", "https://127.0.0.1/x", "file:///C:/x", ""} {
		parsed, _ := url.Parse(address)
		if metaMediaURL(parsed) {
			t.Fatalf("an address off Meta was allowed: %q", address)
		}
	}
}

// The session — cookies, x-csrftoken, x-ig-www-claim — goes to Meta and nowhere else. A Meta address is fetched with
// it; any other https address, a GIF on Giphy say, and a redirect off Meta are fetched with nothing but a User-Agent;
// plain http is refused. Only what was asked of Meta counts as a request of Instagram's.
func TestMediaSessionStaysOnMeta(t *testing.T) {
	a, wire := mediaWireFixture(t)
	a.media.remember("meta", "https://scontent.cdninstagram.com/photo")
	a.media.remember("animated:m:0", "https://media2.example.invalid/photo")
	a.media.remember("avatar:redirected", "https://scontent.cdninstagram.com/away")
	for _, id := range []string{"meta", "animated:m:0", "avatar:redirected"} {
		result, code := readMedia(a, id)
		if code != "" || string(result.(map[string]any)["data"].([]byte)) != "synthetic-jpeg" {
			t.Fatalf("%s: did not download: %q", id, code)
		}
	}
	requests := wire.requests()
	if len(requests) != 4 {
		t.Fatalf("expected four requests, got %d", len(requests))
	}
	for index, request := range requests {
		meta := strings.HasSuffix(request.host, ".cdninstagram.com")
		if meta && !strings.HasPrefix(strings.Join(request.session, ","), "Cookie,X-Csrftoken,X-Ig-Www-Claim") {
			t.Fatalf("request %d to Meta lost its session: %v", index, request.session)
		}
		if !meta && (len(request.session) != 0 || request.agent == "") {
			t.Fatalf("request %d off Meta carried %v (agent %q)", index, request.session, request.agent)
		}
	}
	if counts := a.requests.counts(); counts.Media != 2 {
		t.Fatalf("expected the two Meta reads counted, got %d", counts.Media)
	}
	a.media.remember("plain", "http://scontent.cdninstagram.com/photo")
	a.media.remember("avatar:plain", "http://example.invalid/photo")
	for _, id := range []string{"plain", "avatar:plain"} {
		if _, code := readMedia(a, id); code != "media_unavailable" {
			t.Fatalf("%s: plain http answered %q", id, code)
		}
	}
	if len(wire.requests()) != 4 || a.requests.counts().Media != 2 {
		t.Fatal("a plain http address was asked for, or counted")
	}
	// Refused rather than expired: reading the list again would name the same address.
	if !a.chats.fresh() {
		t.Fatal("a refused avatar made the conversation list stale")
	}
}

// The anonymous fetch follows at most five redirects, each held to its own rules: never to plain http, never to an
// address that is not public.
func TestAnonymousMediaRedirectsAreHeldToTheSameRules(t *testing.T) {
	_, wire := mediaWireFixture(t)
	client := newAnonymousMedia(wire)
	for _, path := range []string{"/inward", "/downgrade", "/loop"} {
		if _, _, unsigned, err := fetchMedia(context.Background(), nil, client, "https://example.invalid"+path); err == nil || !unsigned {
			t.Fatalf("%s: a redirect was followed where it should not be", path)
		}
	}
	for _, host := range wire.asked() {
		if host != "example.invalid" {
			t.Fatalf("a redirect reached %s", host)
		}
	}
	if asked := len(wire.asked()); asked != 2+6 {
		t.Fatalf("expected one request each for the refused redirects and six for the loop, got %d", asked)
	}
}

// A host off Meta that is not a public address is never dialled: by name it fails the URL check, and a name that
// resolves to a private address fails the dial guard, which checks the address actually dialled.
func TestAnonymousMediaNeverDialsPrivateAddresses(t *testing.T) {
	var lookups, dials atomic.Int32
	lookup := func(context.Context, string, string) ([]netip.Addr, error) {
		lookups.Add(1)
		return []netip.Addr{netip.MustParseAddr("93.184.215.14"), netip.MustParseAddr("10.0.0.7")}, nil
	}
	dial := func(context.Context, string, string) (net.Conn, error) {
		dials.Add(1)
		return nil, errors.New("synthetic dial")
	}
	client := newAnonymousMedia(&http.Transport{DialContext: publicMediaDial(lookup, func() []netip.Prefix { return nil }, dial)})
	for _, address := range []string{"https://intranet.example.invalid/x", "https://10.0.0.7/x", "https://169.254.169.254/latest/meta-data",
		"https://100.64.0.1/x", "https://[::1]/x", "https://127.0.0.1/x", "https://0.0.0.0/x", "https://224.0.0.1/x", "https://[fe80::1]/x",
		"https://[fc00::1]/x", "https://localhost/x", "https://printer.local/x", "https://user@example.invalid/x"} {
		_, _, _, err := fetchMedia(context.Background(), nil, client, address)
		if !refusedMedia(err) {
			t.Fatalf("%s: not refused: %v", address, err)
		}
	}
	if dials.Load() != 0 || lookups.Load() != 1 {
		t.Fatalf("a private destination was dialled (%d dials, %d lookups)", dials.Load(), lookups.Load())
	}
	// The one the helper uses: no jar, no proxy, and its own dialler.
	if transport, ok := anonymousMedia.Transport.(*http.Transport); !ok || anonymousMedia.Jar != nil || transport.Proxy != nil ||
		transport.DialContext == nil || anonymousMedia.Timeout == 0 {
		t.Fatal("the anonymous client is not the guarded one")
	}
}

// Nothing is buffered past the limit, through the session or not: a declared length above it is refused before the
// body is read, and a body with no length is read one byte past it and no further.
func TestMediaDownloadsStopAtTheLimit(t *testing.T) {
	a, wire := mediaWireFixture(t)
	a.media.remember("endless", "https://scontent.cdninstagram.com/endless")
	if _, code := readMedia(a, "endless"); code != "media_unavailable" {
		t.Fatalf("an oversized body answered %q", code)
	}
	if read := wire.read.Load(); read > mediaLimit+1 {
		t.Fatalf("read %d bytes of an oversized body, more than the limit and one", read)
	}
	wire.read.Store(0)
	a.media.remember("declared", "https://scontent.cdninstagram.com/declared")
	if _, code := readMedia(a, "declared"); code != "media_unavailable" || wire.read.Load() != 0 {
		t.Fatalf("a declared length above the limit was read: %q, %d bytes", code, wire.read.Load())
	}
	a.media.remember("endless-elsewhere", "https://example.invalid/endless")
	if _, code := readMedia(a, "endless-elsewhere"); code != "media_unavailable" || wire.read.Load() > mediaLimit+1 {
		t.Fatalf("an oversized anonymous body was read past the limit: %q, %d bytes", code, wire.read.Load())
	}
	if a.media.cached("endless") != nil || a.media.cached("declared") != nil || a.media.cached("endless-elsewhere") != nil {
		t.Fatal("an oversized download was kept")
	}
}

// A finished download is let go once nothing has asked for it for a minute, whether or not later downloads come along
// to push it out: on the always-on server six of them could otherwise hold 150 MiB for good.
func TestIdleDownloadsAreLetGo(t *testing.T) {
	store := newMediaStore()
	now := time.Unix(1_700_000_000, 0)
	store.now = func() time.Time { return now }
	store.keep(&mediaItem{id: "old", data: []byte("old")})
	now = now.Add(40 * time.Second)
	store.keep(&mediaItem{id: "new", data: []byte("new")})
	now = now.Add(30 * time.Second)
	if store.cached("old") != nil {
		t.Fatal("a download idle for over a minute was still held")
	}
	if store.cached("new") == nil {
		t.Fatal("a download kept 30 seconds ago was dropped")
	}
	// Being read is being used.
	now = now.Add(50 * time.Second)
	if store.cached("new") == nil {
		t.Fatal("a download still being read was dropped")
	}
	// And with nothing touching the store at all, the pending sweep lets go of what is left.
	if !store.sweeping {
		t.Fatal("no sweep was pending while downloads were held")
	}
	now = now.Add(61 * time.Second)
	store.sweep()
	if len(store.items) != 0 || store.sweeping {
		t.Fatalf("the sweep left %d idle downloads held", len(store.items))
	}
}

// An address on one of this computer's own networks is refused like a private one: on a global IPv6 network the
// router, a NAS or a printer has a public-looking address in the same /64.
func TestAnonymousMediaNeverDialsTheComputersOwnNetwork(t *testing.T) {
	dials := 0
	lookup := func(context.Context, string, string) ([]netip.Addr, error) {
		return []netip.Addr{netip.MustParseAddr("2600:1f18:1:2::1")}, nil
	}
	dial := func(context.Context, string, string) (net.Conn, error) {
		dials++
		return nil, errors.New("synthetic dial")
	}
	local := func() []netip.Prefix { return []netip.Prefix{netip.MustParsePrefix("2600:1f18:1:2::/64")} }
	guarded := publicMediaDial(lookup, local, dial)
	for _, address := range []string{"lan.example.invalid:443", "[2600:1f18:1:2::7]:443"} {
		if _, err := guarded(context.Background(), "tcp", address); !errors.Is(err, errMediaDestination) {
			t.Fatalf("%s: an address on this computer's network was not refused: %v", address, err)
		}
	}
	if dials != 0 {
		t.Fatalf("an address on this computer's network was dialled %d times", dials)
	}
	// A /128 interface address still refuses its /64, as the Messenger helper's check does.
	_, wide, _ := net.ParseCIDR("2600:1f18:1:2::5/128")
	wide.IP = net.ParseIP("2600:1f18:1:2::5")
	if prefixes := hostNetworkPrefixes([]net.Addr{wide}); !onHostNetwork(netip.MustParseAddr("2600:1f18:1:2::1"), prefixes) {
		t.Fatalf("a /128 interface address did not cover its /64: %v", prefixes)
	}
}
