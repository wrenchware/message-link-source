package main

import (
	"bytes"
	"context"
	"errors"
	"net/http"
	"strings"
	"sync"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

// Voice splits long texts itself; this only keeps an unreasonable request from leaving the app.
const maxTextBytes = 10000

type sendAttemptKey struct{}

// What happened to one send's request. libgv retries a request after a network error or a server error, which for a
// send could deliver the text twice. A send is asked exactly once: after that answer the retry is stopped, and the
// outcome is decided from what the one request returned.
type sendAttempt struct {
	mu       sync.Mutex
	attempts int
	status   int
	failed   bool
	stop     context.CancelFunc
}

var errSendNotRepeated = errors.New("a send is not repeated")

type sendGuard struct{ base http.RoundTripper }

func (g *sendGuard) RoundTrip(req *http.Request) (*http.Response, error) {
	base := g.base
	if base == nil {
		base = http.DefaultTransport
	}
	attempt, guarded := req.Context().Value(sendAttemptKey{}).(*sendAttempt)
	// Other requests, and redirects within the one send, pass as they are.
	if !guarded || req.Response != nil {
		return base.RoundTrip(req)
	}
	attempt.mu.Lock()
	attempt.attempts++
	first := attempt.attempts == 1
	attempt.mu.Unlock()
	if !first {
		attempt.stop()
		return nil, errSendNotRepeated
	}
	resp, err := base.RoundTrip(req)
	attempt.mu.Lock()
	if err != nil {
		attempt.failed = true
	} else {
		attempt.status = resp.StatusCode
	}
	attempt.mu.Unlock()
	// The answers libgv would retry: end the send here instead.
	if err != nil || resp.StatusCode >= 500 {
		attempt.stop()
	}
	return resp, err
}

// Sends a text, without Google's optional anti-abuse signature (the user's choice, as a hosted upstream bridge sends).
// The text is never logged.
func (a *adapter) sendText(ctx context.Context, client *libgv.Client, chatID, text string) (any, string) {
	return a.sendRequest(ctx, client, &gvproto.ReqSendSMS{ThreadID: chatID, Text: text})
}

// One message, text or photo, sent exactly once with a new transaction id. A conversation Google has no thread for yet
// (one started from New chat) is addressed by its number in the request's recipients, with no thread id: Google refused
// a first message sent to the "t." id it would have (400, 2026-10-29). A conversation not yet known to be unstarted that
// Google refuses that way is tried once more by its number; the refusal means the first was certainly not sent.
func (a *adapter) sendRequest(ctx context.Context, client *libgv.Client, req *gvproto.ReqSendSMS) (any, string) {
	chatID := req.GetThreadID()
	number, direct := directNumber(chatID)
	if direct && a.isUnstarted(chatID) {
		return a.sendToNumber(ctx, client, req, number)
	}
	result, outcome, status := a.sendShaped(ctx, client, req, "thread")
	if direct && outcome == "send_rejected" && status == http.StatusBadRequest {
		a.setUnstarted(chatID, true)
		return a.sendToNumber(ctx, client, req, number)
	}
	return result, outcome
}

func (a *adapter) sendToNumber(ctx context.Context, client *libgv.Client, req *gvproto.ReqSendSMS, number string) (any, string) {
	result, outcome, _ := a.sendShaped(ctx, client, &gvproto.ReqSendSMS{Text: req.GetText(), Recipients: []string{number}, Media: req.GetMedia()}, "recipients")
	if outcome == "" {
		a.setUnstarted("t."+number, false)
	}
	return result, outcome
}

// The number of a one-to-one text conversation, which Voice names "t." and the number.
func directNumber(chatID string) (string, bool) {
	number, found := strings.CutPrefix(chatID, "t.")
	return number, found && strings.HasPrefix(number, "+") && len(number) > 1
}

func (a *adapter) isUnstarted(chatID string) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.unstarted[chatID]
}

func (a *adapter) setUnstarted(chatID string, on bool) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.unstarted == nil {
		a.unstarted = map[string]bool{}
	}
	if on {
		a.unstarted[chatID] = true
	} else {
		delete(a.unstarted, chatID)
	}
}

func (a *adapter) sendShaped(ctx context.Context, client *libgv.Client, req *gvproto.ReqSendSMS, shape string) (any, string, int) {
	ctx, stop := context.WithCancel(ctx)
	defer stop()
	attempt := &sendAttempt{stop: stop}
	ctx = context.WithValue(ctx, sendAttemptKey{}, attempt)
	send := a.sendSMS
	if send == nil {
		send = func(ctx context.Context, client *libgv.Client, req *gvproto.ReqSendSMS) (*gvproto.RespSendSMS, error) {
			return client.SendMessage(ctx, req)
		}
	}
	req.TransactionID = &gvproto.ReqSendSMS_WrappedTxnID{ID: libgv.GenerateTransactionID()}
	resp, err := send(ctx, client, req)
	attempt.mu.Lock()
	attempts, status, failed := attempt.attempts, attempt.status, attempt.failed
	attempt.mu.Unlock()
	if err == nil && resp.GetThreadItemID() != "" {
		a.diagnose(map[string]any{"send": "accepted", "status": status, "shape": shape})
		// The sent message arrives through the ordinary refresh, like any other.
		a.wake()
		return map[string]any{"id": resp.GetThreadItemID(), "timestamp": resp.GetTimestampMS()}, "", status
	}
	outcome := sendOutcome(err, attempts, status, failed)
	a.diagnose(map[string]any{"send": outcome, "status": status, "shape": shape, "reason": refusalReason(err)})
	return nil, outcome, status
}

// Google's own word for a refusal (its error body names a canonical status), and nothing else from that body. A
// Voice refusal that carries a voice_error code (too many messages, file too large) is named by that code instead.
var refusalReasons = []string{"INVALID_ARGUMENT", "FAILED_PRECONDITION", "NOT_FOUND", "PERMISSION_DENIED", "RESOURCE_EXHAUSTED",
	"UNAUTHENTICATED", "ABORTED", "OUT_OF_RANGE", "UNIMPLEMENTED", "ALREADY_EXISTS", "UNAVAILABLE", "INTERNAL"}

func refusalReason(err error) string {
	var refused *libgv.ResponseError
	if !errors.As(err, &refused) {
		return "none"
	}
	if refused.VoiceError != nil {
		return "voice_" + strings.ToLower(refused.VoiceError.GetCode().String())
	}
	for _, reason := range refusalReasons {
		if bytes.Contains(refused.Body, []byte(reason)) {
			return reason
		}
	}
	return "unnamed"
}

// Only a request that never left, or one Google answered with a client error, is certainly not sent. A network error,
// a server error, or an accepted answer without a message id may have sent it.
func sendOutcome(err error, attempts, status int, failed bool) string {
	switch {
	case err != nil && attempts == 0 && !failed && errors.Is(err, context.Canceled):
		return "request_cancelled"
	case err != nil && attempts == 0 && !failed:
		return "send_rejected"
	case !failed && (status == http.StatusUnauthorized || status == http.StatusForbidden):
		return "auth_required"
	case !failed && status >= 400 && status < 500:
		return "send_rejected"
	default:
		return "send_failed"
	}
}
