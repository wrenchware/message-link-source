package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"image/png"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// A made-up code that no real pairing key could be mistaken for, so a test can look for it anywhere it must not appear.
const syntheticCode = "https://invalid.example/qr-synthetic-pairing-key-0123456789"

type fakeQR struct {
	mu        sync.Mutex
	started   int
	refreshed int
	closed    int
	startErr  error
	refreshAt int // the refresh that fails (1 based), 0 for none
	paired    chan *gmproto.PairedData
}

func newFakeQR() *fakeQR { return &fakeQR{paired: make(chan *gmproto.PairedData, 1)} }

func (f *fakeQR) Start(context.Context) (string, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.started++
	return syntheticCode + "/0", f.startErr
}
func (f *fakeQR) Refresh() (string, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.refreshed++
	if f.refreshAt == f.refreshed {
		return "", errors.New("failed with " + syntheticCode)
	}
	return syntheticCode + "/" + string(rune('0'+f.refreshed)), nil
}
func (f *fakeQR) Paired() <-chan *gmproto.PairedData { return f.paired }
func (f *fakeQR) Close()                              { f.mu.Lock(); f.closed++; f.mu.Unlock() }

func fastLogin(source qrSource, emit func(int, []byte)) *qrLogin {
	login := newQRLogin(source, emit)
	login.expiry = 5 * time.Millisecond
	login.settle = 0
	login.render = func(code string) ([]byte, error) { return []byte("picture"), nil }
	return login
}

func TestQRPictureIsAPNGWithWholeModulePixels(t *testing.T) {
	data, err := renderQR(syntheticCode)
	if err != nil {
		t.Fatal(err)
	}
	img, err := png.Decode(bytes.NewReader(data))
	if err != nil {
		t.Fatal(err)
	}
	b := img.Bounds()
	if b.Dx() != b.Dy() || b.Dx()%qrModulePixels != 0 || b.Dx() < 20*qrModulePixels {
		t.Fatalf("size %dx%d", b.Dx(), b.Dy())
	}
	if bytes.Contains(data, []byte("synthetic")) {
		t.Fatal("the picture carries the code as text")
	}
}

func TestQRRefreshesFiveTimesThenTimesOut(t *testing.T) {
	source := newFakeQR()
	var numbers []int
	login := fastLogin(source, func(n int, _ []byte) { numbers = append(numbers, n) })
	if _, code := login.start(context.Background()); code != "" {
		t.Fatal(code)
	}
	if _, code := login.wait(context.Background()); code != "pair_qr_timeout" {
		t.Fatalf("ended with %q", code)
	}
	if len(numbers) != 5 || numbers[0] != 2 || numbers[4] != 6 || source.refreshed != 5 {
		t.Fatalf("numbers %v refreshed %d", numbers, source.refreshed)
	}
	if source.closed != 1 {
		t.Fatalf("closed %d", source.closed)
	}
}

func TestQRUpstreamNumbers(t *testing.T) {
	if qrExpiry != 30*time.Second || qrRefreshes != 5 {
		t.Fatal("upstream gives each code 30 s and refreshes five times")
	}
}

func TestQRPairingReturnsThePhoneAndStops(t *testing.T) {
	source := newFakeQR()
	login := fastLogin(source, func(int, []byte) {})
	login.expiry = time.Hour
	login.start(context.Background())
	source.paired <- &gmproto.PairedData{}
	data, code := login.wait(context.Background())
	if code != "" || data == nil || source.closed != 1 || source.refreshed != 0 {
		t.Fatalf("code %q closed %d refreshed %d", code, source.closed, source.refreshed)
	}
}

func TestQRCancelEndsTheWaitAndStopsListening(t *testing.T) {
	source := newFakeQR()
	login := fastLogin(source, nil)
	login.expiry = time.Hour
	login.start(context.Background())
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, code := login.wait(ctx); code != "request_cancelled" || source.closed != 1 {
		t.Fatalf("code %q closed %d", code, source.closed)
	}
}

func TestQRRefreshFailureIsAFixedCodeWithoutTheLibrarysWords(t *testing.T) {
	source := newFakeQR()
	source.refreshAt = 2
	login := fastLogin(source, func(int, []byte) {})
	login.start(context.Background())
	_, code := login.wait(context.Background())
	if code != "pair_qr_refresh_failed" || strings.Contains(code, "synthetic") {
		t.Fatalf("code %q", code)
	}
}

func TestQRStartFailureClosesAndSaysAFixedCode(t *testing.T) {
	source := newFakeQR()
	source.startErr = errors.New("registration refused for " + syntheticCode)
	login := fastLogin(source, nil)
	picture, code := login.start(context.Background())
	if picture != nil || code != "pair_qr_start_failed" || source.closed != 1 {
		t.Fatalf("code %q closed %d", code, source.closed)
	}
	source = newFakeQR()
	source.startErr = context.Canceled
	if _, code := fastLogin(source, nil).start(context.Background()); code != "request_cancelled" {
		t.Fatalf("cancelled start said %q", code)
	}
}

func TestQRMethodsInTheDispatcher(t *testing.T) {
	if !lifecycleMethod("pair.qrStart") || lifecycleMethod("pair.qrWait") {
		t.Fatal("the start runs alone, the wait must stay cancellable")
	}
	if requestBudget("pair.qrWait") < 3*time.Minute+30*time.Second {
		t.Fatal("the wait must outlast six codes")
	}
}

// The whole of it through the adapter: a pairing the phone accepts, with nothing of the code in anything the helper said.
func TestQRPairingThroughTheAdapterNeverSaysTheCodeAndSavesNoCookies(t *testing.T) {
	var buffer bytes.Buffer
	a := &adapter{out: &output{encoder: json.NewEncoder(&buffer)}}
	source := newFakeQR()
	a.qrSourceFor = func(client *libgm.Client) qrSource {
		client.AuthData.Mobile = &gmproto.Device{UserID: 7, SourceID: "synthetic-phone"}
		client.AuthData.Browser = &gmproto.Device{UserID: 8}
		client.AuthData.TachyonAuthToken = []byte("synthetic-token")
		return source
	}
	a.qrTune = func(login *qrLogin) {
		login.expiry = 5 * time.Millisecond
		login.settle = 0
	}
	started, code := a.serveCall(context.Background(), request{ID: 1, Method: "pair.qrStart"})
	if code != "" {
		t.Fatal(code)
	}
	first, ok := started.(qrPictureEvent)
	if !ok || first.N != 1 {
		t.Fatalf("start answered %v", started)
	}
	if _, err := png.Decode(base64.NewDecoder(base64.StdEncoding, strings.NewReader(first.PNG))); err != nil {
		t.Fatal("the first picture is not a PNG")
	}
	if again, code := a.serveCall(context.Background(), request{ID: 2, Method: "pair.qrStart"}); again != nil || code != "session_already_started" {
		t.Fatalf("a second start said %q", code)
	}
	// Two refreshes go by, then the phone accepts.
	done := make(chan struct{})
	var result any
	var waitCode string
	go func() {
		result, waitCode = a.serveCall(context.Background(), request{ID: 3, Method: "pair.qrWait"})
		close(done)
	}()
	for {
		source.mu.Lock()
		count := source.refreshed
		source.mu.Unlock()
		if count >= 2 {
			break
		}
		time.Sleep(time.Millisecond)
	}
	source.paired <- &gmproto.PairedData{}
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("the wait did not end")
	}
	if waitCode != "" {
		t.Fatal(waitCode)
	}
	session, ok := result.(json.RawMessage)
	if !ok {
		t.Fatalf("wait answered %T", result)
	}
	if bytes.Contains(session, []byte("cookies")) {
		t.Fatal("a QR session holds no cookies")
	}
	var auth libgm.AuthData
	if err := json.Unmarshal(session, &auth); err != nil || auth.Browser == nil || auth.Mobile == nil || len(auth.TachyonAuthToken) == 0 || auth.RequestCrypto == nil || auth.RefreshKey == nil {
		t.Fatalf("the session cannot be connected with: %v", err)
	}
	// libgm's own test of a plain device pairing: no Gaia pairing, so no cookie is asked for and none can expire.
	if auth.IsGoogleAccount() || !auth.HasCookies() {
		t.Fatal("a QR session must not look like a Google-account sign-in")
	}
	if a.client != nil || a.qr != nil {
		t.Fatal("the pairing client is kept after it answered")
	}
	// Everything the helper wrote to the host: pictures and numbers, never the code or the session's secrets.
	said := buffer.String()
	for _, secret := range []string{"invalid.example", "synthetic-pairing-key", syntheticCode} {
		if strings.Contains(said, secret) || bytes.Contains(session, []byte(secret)) {
			t.Fatalf("the code appears in %q", secret)
		}
	}
	if strings.Count(said, `"event":"qr"`) < 2 {
		t.Fatal("the refreshed pictures were not sent")
	}
}

func TestQRCancelledWaitLeavesNothingAndAnswersACode(t *testing.T) {
	var buffer bytes.Buffer
	a := &adapter{out: &output{encoder: json.NewEncoder(&buffer)}}
	source := newFakeQR()
	a.qrSourceFor = func(*libgm.Client) qrSource { return source }
	a.qrTune = func(login *qrLogin) { login.expiry = time.Hour }
	if _, code := a.serveCall(context.Background(), request{ID: 1, Method: "pair.qrStart"}); code != "" {
		t.Fatal(code)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	result, code := a.serveCall(ctx, request{ID: 2, Method: "pair.qrWait"})
	if result != nil || code != "request_cancelled" || a.client != nil || source.closed < 1 {
		t.Fatalf("code %q", code)
	}
}

func TestQRWaitWithoutAStartIsRefused(t *testing.T) {
	a := &adapter{out: &output{encoder: json.NewEncoder(&bytes.Buffer{})}}
	if _, code := a.serveCall(context.Background(), request{ID: 1, Method: "pair.qrWait"}); code != "pairing_required" {
		t.Fatal(code)
	}
}

// What the host reads a session's kind from (GoogleSessionKind.IsQr in the app): a QR session names no Gaia pairing and holds no cookie, and a Gaia one names the phone it was
// made with. Pinned here because the host's reading depends on exactly how this library writes them.
func TestQRSessionIsToldFromAGoogleAccountSessionByWhatTheHostReads(t *testing.T) {
	qr := libgm.NewAuthData()
	qr.Mobile = &gmproto.Device{UserID: 7}
	qr.Browser = &gmproto.Device{UserID: 8}
	qr.TachyonAuthToken = []byte("synthetic-token")
	said, err := sessionJSON(qr)
	if err != nil {
		t.Fatal(err)
	}
	var fields map[string]json.RawMessage
	if json.Unmarshal(said, &fields) != nil {
		t.Fatal("not an object")
	}
	if string(fields["dest_reg_id"]) != `"00000000-0000-0000-0000-000000000000"` {
		t.Fatalf("dest_reg_id %s", fields["dest_reg_id"])
	}
	if _, has := fields["cookies"]; has {
		t.Fatal("a QR session carries a cookie map")
	}
	gaia := libgm.NewAuthData()
	gaia.Mobile, gaia.Browser = qr.Mobile, qr.Browser
	gaia.DestRegID = uuid.MustParse("11111111-2222-3333-4444-555555555555")
	gaia.SetCookies(map[string]string{"SID": "synthetic"})
	said, _ = sessionJSON(gaia)
	if !bytes.Contains(said, []byte(`"dest_reg_id":"11111111-2222-3333-4444-555555555555"`)) || !bytes.Contains(said, []byte(`"cookies"`)) {
		t.Fatal("a Google-account session lost what tells it apart")
	}
}

// Connecting with a QR session: the signed-out check that follows a configuration page with no account (configSignedOut) is for
// Google-account sessions only, so a QR session is never told its sign-in expired.
func TestConnectingWithAQRSessionNeverReadsItAsASignedOutAccount(t *testing.T) {
	var buffer bytes.Buffer
	a := &adapter{out: &output{encoder: json.NewEncoder(&buffer)}}
	a.init(libgm.NewAuthData())
	a.client.Config = &gmproto.Config{}
	if a.configSignedOut() || buffer.Len() != 0 {
		t.Fatalf("a QR session was read as a signed-out account: %q", buffer.String())
	}
	// And the same page for a Google-account session is the refusal it always was.
	gaia := libgm.NewAuthData()
	gaia.DestRegID = uuid.MustParse("11111111-2222-3333-4444-555555555555")
	a.init(gaia)
	a.client.Config = &gmproto.Config{}
	if !a.configSignedOut() || !strings.Contains(buffer.String(), "connect_config_no_email") {
		t.Fatal("a Google-account session with no account on its page stopped being refused")
	}
}
