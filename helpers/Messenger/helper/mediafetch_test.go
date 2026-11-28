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
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
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
	host, path, agent string
	session           []string
}

// Session headers and cookie, by the names BuildHeaders gives them.
var sessionHeaders = []string{"Cookie", "X-Fb-Lsd", "X-Csrftoken", "X-Ig-Www-Claim", "X-Mid"}

// Stands in for the network: records every request and answers by path. Nothing leaves the process.
type mediaWire struct {
	mu   sync.Mutex
	seen []seenRequest
	read atomic.Int64
}

func (w *mediaWire) RoundTrip(r *http.Request) (*http.Response, error) {
	request := seenRequest{host: r.URL.Hostname(), path: r.URL.Path, agent: r.Header.Get("User-Agent")}
	for _, name := range sessionHeaders {
		if r.Header.Get(name) != "" {
			request.session = append(request.session, name)
		}
	}
	w.mu.Lock()
	w.seen = append(w.seen, request)
	w.mu.Unlock()
	answer := &http.Response{StatusCode: http.StatusOK, Header: http.Header{"Content-Type": {"image/jpeg"}}, ContentLength: -1, Request: r}
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
		answer.StatusCode, answer.Header = http.StatusFound, http.Header{"Location": {"https://example.invalid/collect"}}
		answer.Body = http.NoBody
	case "/hop":
		answer.StatusCode, answer.Header = http.StatusFound, http.Header{"Location": {"https://lookaside.fbsbx.com/photo"}}
		answer.Body = http.NoBody
	case "/inward":
		answer.StatusCode, answer.Header = http.StatusFound, http.Header{"Location": {"https://10.0.0.7/photo"}}
		answer.Body = http.NoBody
	case "/downgrade":
		answer.StatusCode, answer.Header = http.StatusFound, http.Header{"Location": {"http://example.invalid/photo"}}
		answer.Body = http.NoBody
	case "/loop":
		answer.StatusCode, answer.Header = http.StatusFound, http.Header{"Location": {"/loop"}}
		answer.Body = http.NoBody
	default:
		answer.StatusCode, answer.Body = http.StatusNotFound, http.NoBody
	}
	return answer, nil
}

func (w *mediaWire) asked() []string {
	w.mu.Lock()
	defer w.mu.Unlock()
	hosts := make([]string, 0, len(w.seen))
	for _, request := range w.seen {
		hosts = append(hosts, request.host)
	}
	return hosts
}

func (w *mediaWire) requests() []seenRequest {
	w.mu.Lock()
	defer w.mu.Unlock()
	return append([]seenRequest(nil), w.seen...)
}

func photoRows(id, source string, size int64) *table.LSTable {
	return &table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "m-" + id, SenderId: 1, TimestampMs: 1700}},
		LSInsertBlobAttachment: []*table.LSInsertBlobAttachment{{ThreadKey: 7, MessageId: "m-" + id, AttachmentFbid: id,
			AttachmentType: table.AttachmentTypeImage, AttachmentMimeType: "image/jpeg", PlayableUrl: source, Filesize: size}},
	}
}

func mediaWireFixture(t *testing.T) (*adapter, *mediaWire) {
	t.Helper()
	a := newAdapter(&output{encoder: json.NewEncoder(io.Discard)})
	t.Cleanup(a.stop)
	jar := &cookies.Cookies{Platform: types.Facebook}
	jar.UpdateValues(map[cookies.MetaCookieName]string{cookies.FBCookieXS: "synthetic-secret"})
	a.client = messagix.NewClient(jar, zerolog.Nop(), &messagix.Config{})
	configs := httpclient.NewConfigs(nil)
	configs.LSDToken = "synthetic-lsd"
	a.client.GetHTTP().SetConfigs(configs)
	wire := &mediaWire{}
	a.client.GetHTTP().HTTP = &http.Client{Transport: wire}
	a.anonymousMedia = newAnonymousMedia(wire)
	return a, wire
}

// The helper's log for the length of a test, read back as text.
func captureHelperLog(t *testing.T) func() string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "helper.log")
	file, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	previous := diagnostics
	diagnostics = &logger{file: file}
	t.Cleanup(func() { closeLog(); diagnostics = previous })
	return func() string {
		data, _ := os.ReadFile(path)
		return string(data)
	}
}

func readMedia(a *adapter, id string) (any, string) {
	params, _ := json.Marshal(map[string]string{"id": id})
	return a.mediaRead(context.Background(), request{Params: params})
}

func TestMetaMediaHosts(t *testing.T) {
	for _, address := range []string{"https://scontent.xx.fbcdn.net/v/photo.jpg", "https://SCONTENT-iad3-1.XX.FBCDN.NET/x",
		"https://lookaside.fbsbx.com/x", "https://scontent.cdninstagram.com/x", "https://www.facebook.com/x", "https://instagram.com/x",
		"https://www.messenger.com/x", "https://mmg.whatsapp.net/x", "https://fbcdn.net/x", "https://scontent.xx.fbcdn.net./x",
		"https://scontent.xx.fbcdn.net:443/x"} {
		parsed, err := url.Parse(address)
		if err != nil || !metaMediaURL(parsed) {
			t.Fatalf("a Meta address was refused: %s", address)
		}
	}
	for _, address := range []string{"http://scontent.xx.fbcdn.net/x", "https://evilfbcdn.net/x", "https://fbcdn.net.example.invalid/x",
		"https://example.invalid/x", "https://scontent.xx.fbcdn.net@example.invalid/x", "https:scontent.xx.fbcdn.net", "//scontent.xx.fbcdn.net/x",
		"ftp://scontent.xx.fbcdn.net/x", "https://127.0.0.1/x", "https://localhost/x", "file:///C:/x", ""} {
		parsed, _ := url.Parse(address)
		if metaMediaURL(parsed) {
			t.Fatalf("an address off Meta was allowed: %q", address)
		}
	}
}

// The session goes to Meta and nowhere else. A Meta address is fetched with it, redirects within Meta included; any
// other https address — a GIF on Giphy, an attachment a backup restored — and a redirect off Meta are fetched with
// nothing but a User-Agent; plain http is refused. Each anonymous fetch is logged as such, with no host.
func TestMediaSessionStaysOnMeta(t *testing.T) {
	a, wire := mediaWireFixture(t)
	logged := captureHelperLog(t)
	a.chats.apply(photoRows("meta", "https://scontent.xx.fbcdn.net/photo", 0))
	a.chats.apply(photoRows("hop", "https://scontent.xx.fbcdn.net/hop", 0))
	a.chats.apply(photoRows("elsewhere", "https://media2.example.invalid/photo", 0))
	a.chats.apply(photoRows("redirected", "https://scontent.xx.fbcdn.net/away", 0))
	a.chats.restoreAttachment("m-restored", &attachment{id: "restored", mime: "image/jpeg", source: "https://example.invalid/photo",
		preview: "restored:preview"}, "https://example.invalid/collect")
	for _, id := range []string{"meta", "hop", "elsewhere", "redirected", "restored", "restored:preview"} {
		result, code := readMedia(a, id)
		if code != "" || string(result.(map[string]any)["data"].([]byte)) != "synthetic-jpeg" {
			t.Fatalf("%s: did not download: %q", id, code)
		}
	}
	for index, request := range wire.requests() {
		meta := strings.HasSuffix(request.host, ".fbcdn.net") || strings.HasSuffix(request.host, ".fbsbx.com")
		// Go itself drops the cookie, and only the cookie, on a redirect from fbcdn.net to fbsbx.com, as it always has.
		if meta && (len(request.session) == 0 || index == 0 && strings.Join(request.session, ",") != "Cookie,X-Fb-Lsd") {
			t.Fatalf("a Meta request lost its session: %v", request.session)
		}
		if !meta && (len(request.session) != 0 || request.agent == "") {
			t.Fatalf("a request off Meta carried %v (agent %q)", request.session, request.agent)
		}
	}
	if asked := wire.asked(); len(asked) != 8 || asked[5] != "example.invalid" {
		t.Fatalf("unexpected requests: %v", asked)
	}
	before := len(wire.asked())
	a.chats.apply(photoRows("plain", "http://scontent.xx.fbcdn.net/photo", 0))
	a.chats.apply(photoRows("plain-elsewhere", "http://example.invalid/photo", 0))
	for _, id := range []string{"plain", "plain-elsewhere"} {
		if _, code := readMedia(a, id); code != "media_unavailable" {
			t.Fatalf("%s: plain http answered %q", id, code)
		}
	}
	if len(wire.asked()) != before {
		t.Fatal("a plain http address was asked for")
	}
	text := logged()
	if strings.Count(text, "anonymous=yes") != 4 || strings.Count(text, "refused reason=address") != 2 ||
		strings.Contains(text, "example.invalid") || strings.Contains(text, "fbcdn") {
		t.Fatalf("anonymous fetches and refusals were not logged as fixed words alone: %q", text)
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
		return []netip.Addr{netip.MustParseAddr("10.0.0.7")}, nil
	}
	dial := func(context.Context, string, string) (net.Conn, error) {
		dials.Add(1)
		return nil, errors.New("synthetic dial")
	}
	client := newAnonymousMedia(&http.Transport{DialContext: previewPublicDial(lookup, noHostNetworks, dial)})
	for _, address := range []string{"https://intranet.example.invalid/x", "https://10.0.0.7/x", "https://169.254.169.254/latest/meta-data",
		"https://100.64.0.1/x", "https://[::1]/x", "https://127.0.0.1/x", "https://0.0.0.0/x", "https://224.0.0.1/x", "https://localhost/x",
		"https://printer.local/x", "https://user@example.invalid/x"} {
		_, _, _, err := fetchMedia(context.Background(), nil, client, address)
		if mediaRefusal(err) != "address" {
			t.Fatalf("%s: not refused as an address: %v", address, err)
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

// Nothing is buffered past the limit: a declared length above it is refused before the body is read, a body with no
// length is read one byte past it and no further, and an attachment that says it is too large is never asked for.
func TestMediaDownloadsStopAtTheLimit(t *testing.T) {
	a, wire := mediaWireFixture(t)
	logged := captureHelperLog(t)
	a.chats.apply(photoRows("endless", "https://scontent.xx.fbcdn.net/endless", 0))
	if _, code := readMedia(a, "endless"); code != "media_unavailable" {
		t.Fatalf("an oversized body answered %q", code)
	}
	if read := wire.read.Load(); read > mediaLimit+1 {
		t.Fatalf("read %d bytes of an oversized body, more than the limit and one", read)
	}
	wire.read.Store(0)
	a.chats.apply(photoRows("declared", "https://scontent.xx.fbcdn.net/declared", 0))
	if _, code := readMedia(a, "declared"); code != "media_unavailable" || wire.read.Load() != 0 {
		t.Fatalf("a declared length above the limit was read: %q, %d bytes", code, wire.read.Load())
	}
	// The anonymous fetch is held to the same limit.
	a.chats.apply(photoRows("endless-elsewhere", "https://example.invalid/endless", 0))
	if _, code := readMedia(a, "endless-elsewhere"); code != "media_unavailable" || wire.read.Load() > mediaLimit+1 {
		t.Fatalf("an oversized anonymous body was read past the limit: %q, %d bytes", code, wire.read.Load())
	}
	// An encrypted file's declared length is its own, so one above the limit is not fetched. Lightspeed's Filesize may
	// be the original upload's where a smaller playable copy is fetched, so that one is fetched and held to the body's
	// bound: a "large" original whose copy is small still arrives.
	before := len(wire.asked())
	a.chats.attachSecure(7, "m-secure", 1700, &attachment{id: "e2ee:m-secure", mime: "video/mp4", size: mediaLimit + 1},
		&secureMedia{transport: []byte{}, kind: "video"})
	if _, code := readMedia(a, "e2ee:m-secure"); code != "media_unavailable" {
		t.Fatalf("an encrypted attachment declared too large answered %q", code)
	}
	if len(wire.asked()) != before {
		t.Fatal("an encrypted attachment declared too large was asked for")
	}
	a.chats.apply(photoRows("large", "https://scontent.xx.fbcdn.net/photo", mediaLimit+1))
	if _, code := readMedia(a, "large"); code != "" {
		t.Fatalf("an attachment whose original is declared large, served small, answered %q", code)
	}
	text := logged()
	if strings.Count(text, "refused reason=size") != 3 || strings.Count(text, "refused reason=declared_size") != 1 {
		t.Fatalf("size refusals were not logged: %q", text)
	}
	if held := a.cached("endless"); held != nil {
		t.Fatal("an oversized download was kept")
	}
}

// The encrypted connection's downloads, bounded on the wire beneath whatsmeow, which reads a file whole before it
// checks anything. Uploads share the client and pass untouched.
func TestEncryptedDownloadsAreBoundedOnTheWire(t *testing.T) {
	var read atomic.Int64
	serve := func(length, declared int64) http.RoundTripper {
		return mediaTestTransport(func(r *http.Request) (*http.Response, error) {
			return &http.Response{StatusCode: http.StatusOK, ContentLength: declared, Header: http.Header{},
				Body: &countedBody{read: &read, left: length}, Request: r}, nil
		})
	}
	get := func(transport http.RoundTripper, method string) ([]byte, error) {
		request, _ := http.NewRequest(method, "https://mmg.whatsapp.net/v/t62/synthetic", nil)
		resp, err := (&http.Client{Transport: boundedDownloads{next: transport}}).Do(request)
		if err != nil {
			return nil, err
		}
		defer resp.Body.Close()
		return io.ReadAll(resp.Body)
	}
	if _, err := get(serve(secureDownloadLimit+4<<20, -1), http.MethodGet); !errors.Is(err, errMediaTooLarge) || read.Load() > secureDownloadLimit+1 {
		t.Fatalf("an oversized body was read past the limit: %v, %d bytes", err, read.Load())
	}
	read.Store(0)
	if _, err := get(serve(secureDownloadLimit+1, secureDownloadLimit+1), http.MethodGet); !errors.Is(err, errMediaTooLarge) || read.Load() != 0 {
		t.Fatalf("a declared length above the limit was read: %v, %d bytes", err, read.Load())
	}
	if data, err := get(serve(secureDownloadLimit, secureDownloadLimit), http.MethodGet); err != nil || len(data) != secureDownloadLimit {
		t.Fatalf("a file at the limit did not come through whole: %v, %d bytes", err, len(data))
	}
	if data, err := get(serve(secureDownloadLimit+5, -1), http.MethodPost); err != nil || len(data) != secureDownloadLimit+5 {
		t.Fatalf("an upload's answer was bounded: %v", err)
	}
	if _, ok := secureMediaHTTP().Transport.(boundedDownloads); !ok {
		t.Fatal("the encrypted client's downloads are not bounded")
	}
}
