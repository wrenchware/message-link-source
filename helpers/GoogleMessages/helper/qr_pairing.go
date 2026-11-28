package main

import (
	"context"
	"encoding/base64"
	"errors"
	"time"

	"github.com/skip2/go-qrcode"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// Pairing by QR code: the phone scans a code (Google Messages > Device pairing > QR code scanner), as the upstream
// bridge's QR login does (mautrix-gmessages pkg/connector/login.go, whose QRLoginProcess is switched off there but is the
// reference for the numbers below). The session that results holds no Google-account cookie and no Gaia pairing id, so it
// has no sign-in to expire: Google's two-hour refusal of copied account sign-ins (PROVIDERS.md) never applies to it, and
// connecting with it is the "connect" the helper already has (libgm treats a session without a Gaia pairing as a plain
// device pairing: IsGoogleAccount is false, so no cookie is asked for).
//
// The code is a credential: it carries the pairing key and this device's two request keys. It leaves this process only as
// a picture, in an event the host forwards to the one connection that asked, and it is never logged, never put in an
// error and never written to a file here. Every code is a fixed word or a number in anything that is said about it.

// Upstream gives each code thirty seconds and refreshes it five times before it gives up: six codes, three minutes.
const (
	qrExpiry    = 30 * time.Second
	qrRefreshes = 5
	// Dots a module is drawn with, so the picture is exact at one device pixel to a dot or any whole multiple of it, which a
	// window can scale without blurring the finder squares.
	qrModulePixels = 8
	// The tick the pairing's finish waits for the phone to save the pairing before anything connects with it: upstream
	// sleeps two seconds, "if we reconnect too quickly, the phone won't recognize the session and the bridge will get
	// unpaired".
	qrSettle = 2 * time.Second
)

// What a QR pairing needs from the library, so the logic around it can be tested without a network.
type qrSource interface {
	// Registers this device with the phone relay and starts listening for the phone; returns the first code's text.
	Start(ctx context.Context) (string, error)
	// A new code for the same registration.
	Refresh() (string, error)
	// The phone's answer, once it has scanned the code and accepted.
	Paired() <-chan *gmproto.PairedData
	// Stops listening. Safe to call more than once.
	Close()
}

// The pictures: one for the code text. A function so a test can stand in for the encoder.
type qrRenderer func(code string) ([]byte, error)

func renderQR(code string) ([]byte, error) {
	q, err := qrcode.New(code, qrcode.Medium)
	if err != nil {
		return nil, err
	}
	return q.PNG(len(q.Bitmap()) * qrModulePixels)
}

type qrLogin struct {
	source  qrSource
	render  qrRenderer
	expiry  time.Duration
	settle  time.Duration
	refresh int
	// Called with each new picture and its number (1 is the first). The first is also returned by start.
	emit func(n int, png []byte)
	n    int
}

func newQRLogin(source qrSource, emit func(n int, png []byte)) *qrLogin {
	return &qrLogin{source: source, render: renderQR, expiry: qrExpiry, settle: qrSettle, refresh: qrRefreshes, emit: emit}
}

// Registers and returns the first picture. A fixed code on failure.
func (q *qrLogin) start(ctx context.Context) ([]byte, string) {
	code, err := q.source.Start(ctx)
	if err != nil {
		q.source.Close()
		return nil, qrStartCode(err)
	}
	png, err := q.render(code)
	if err != nil {
		q.source.Close()
		return nil, "pair_qr_render_failed"
	}
	q.n = 1
	return png, ""
}

// Waits for the phone, refreshing the code as it expires. Returns the phone's pairing, or a fixed code: the wait ended
// because it was cancelled (request_cancelled), the codes ran out (pair_qr_timeout) or a refresh failed.
func (q *qrLogin) wait(ctx context.Context) (*gmproto.PairedData, string) {
	defer q.source.Close()
	timer := time.NewTimer(q.expiry)
	defer timer.Stop()
	for {
		select {
		case <-ctx.Done():
			return nil, "request_cancelled"
		case data := <-q.source.Paired():
			if data == nil {
				return nil, "pair_qr_failed"
			}
			// Let the phone save the pairing before anything connects with it.
			select {
			case <-time.After(q.settle):
			case <-ctx.Done():
				return nil, "request_cancelled"
			}
			return data, ""
		case <-timer.C:
			if q.n > q.refresh {
				return nil, "pair_qr_timeout"
			}
			code, err := q.source.Refresh()
			if err != nil {
				return nil, "pair_qr_refresh_failed"
			}
			png, err := q.render(code)
			if err != nil {
				return nil, "pair_qr_render_failed"
			}
			q.n++
			if q.emit != nil {
				q.emit(q.n, png)
			}
			timer.Reset(q.expiry)
		}
	}
}

func qrStartCode(err error) string {
	if errors.Is(err, context.Canceled) {
		return "request_cancelled"
	}
	return "pair_qr_start_failed"
}

// The library behind qrSource.
type libgmQR struct {
	client *libgm.Client
	paired chan *gmproto.PairedData
	stop   context.CancelFunc
}

func newLibgmQR(client *libgm.Client) *libgmQR {
	source := &libgmQR{client: client, paired: make(chan *gmproto.PairedData, 1)}
	callback := func(data *gmproto.PairedData) {
		select {
		case source.paired <- data:
		default:
		}
	}
	client.PairCallback.Store(&callback)
	return source
}

func (s *libgmQR) Start(ctx context.Context) (string, error) {
	// As upstream's QR login does before it registers: the configuration page, which names this device's session.
	if err := s.client.FetchConfig(ctx); err != nil {
		return "", err
	}
	// The listener outlives the request that started it, until Close.
	background, stop := context.WithCancel(context.Background())
	s.stop = stop
	code, err := s.client.StartLogin(background)
	if err != nil {
		stop()
		s.stop = nil
		return "", err
	}
	return code, nil
}

func (s *libgmQR) Refresh() (string, error)             { return s.client.RefreshPhoneRelay() }
func (s *libgmQR) Paired() <-chan *gmproto.PairedData { return s.paired }
func (s *libgmQR) Close() {
	if s.stop != nil {
		s.stop()
		s.stop = nil
	}
	s.client.Disconnect()
}

// qrPictureEvent is what the host is given for each code: the picture only.
type qrPictureEvent struct {
	PNG string `json:"png"`
	N   int    `json:"n"`
}

func (a *adapter) qrEmit(n int, png []byte) {
	a.out.write(response{Event: "qr", Result: qrPictureEvent{base64.StdEncoding.EncodeToString(png), n}})
}

// pair.qrStart: a device pairing of this computer's own, with no Google sign-in. Answers with the first picture; the
// refreshed ones arrive as qr events while pair.qrWait runs.
func (a *adapter) qrStart(ctx context.Context) (any, string) {
	if a.client != nil || a.qr != nil {
		return nil, "session_already_started"
	}
	auth := libgm.NewAuthData()
	a.init(auth)
	var source qrSource
	if a.qrSourceFor != nil {
		source = a.qrSourceFor(a.client)
	} else {
		source = newLibgmQR(a.client)
	}
	login := newQRLogin(source, a.qrEmit)
	if a.qrTune != nil {
		a.qrTune(login)
	}
	png, code := login.start(ctx)
	if code != "" {
		a.client.Disconnect()
		a.client = nil
		return nil, code
	}
	a.qr = login
	return qrPictureEvent{base64.StdEncoding.EncodeToString(png), 1}, ""
}

// pair.qrWait: until the phone has scanned the code and accepted, the codes run out, or the request is cancelled. The
// answer is the session as the app saves it, which has no cookies. Whatever the end, the pairing client is closed and
// nothing is kept: a failed or cancelled attempt leaves the app's saved pairing as it was, because the app writes nothing
// until this answers with a session.
func (a *adapter) qrWait(ctx context.Context) (any, string) {
	login := a.qr
	if login == nil || a.client == nil {
		return nil, "pairing_required"
	}
	data, code := login.wait(ctx)
	a.qr = nil
	client := a.client
	a.client = nil
	if code != "" {
		client.Disconnect()
		return nil, code
	}
	// The library has put the phone and this device into the session before it calls back; a pairing without them is no pairing.
	if data == nil || client.AuthData.Browser == nil || client.AuthData.Mobile == nil {
		client.Disconnect()
		return nil, "pair_qr_failed"
	}
	result, failure := sessionAnswer(client.AuthData)
	client.Disconnect()
	return result, failure
}
