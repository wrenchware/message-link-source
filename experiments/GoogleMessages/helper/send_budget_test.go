package main

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"slices"
	"testing"
	"time"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/util"
)

func incoming(chat, id string) *gmproto.Message {
	return &gmproto.Message{ConversationID: chat, MessageID: id,
		MessageStatus: &gmproto.MessageStatus{Status: gmproto.MessageStatusType_INCOMING_COMPLETE}}
}

func replyText(t *testing.T, a *adapter, chat, target string) (any, string) {
	params, _ := json.Marshal(map[string]string{"chatId": chat, "text": "hello", "replyTo": target})
	return a.sendCall(shortCall(t), request{Method: "ui.sendText", Params: params})
}

// messageRefs is cleared whole at 5000 entries, and a reply looked its target up there alone: a reply to a message
// still on screen was refused with no reason. The conversation's newest page is asked for, as a reaction does.
func TestReplyFindsATargetNoLongerHeld(t *testing.T) {
	a, relay, phone, _ := sendingAdapter(t, true)
	phone.describes(rcsChat("chat", "sim-a"))
	phone.messages = map[string][]*gmproto.Message{"chat": {incoming("chat", "older"), incoming("chat", "target")}}
	result, code := replyText(t, a, "chat", "target")
	posts := relay.takePosts()
	if got := actionsOf(posts); !slices.Equal(got, []gmproto.ActionType{gmproto.ActionType_GET_CONVERSATION, gmproto.ActionType_LIST_MESSAGES, gmproto.ActionType_SEND_MESSAGE}) {
		t.Fatalf("a reply to a message no longer held asked the phone for %v", got)
	}
	expectAnswer(t, "a reply to a message no longer held", result, code, "pending", "fetched")
	var sent gmproto.SendMessageRequest
	posts[len(posts)-1].open(t, a.client, &sent)
	if sent.GetReply().GetMessageID() != "target" {
		t.Fatalf("the reply went out pointing at %q", sent.GetReply().GetMessageID())
	}
	// Found once, it is held again: the next reply asks nothing more than a send does.
	result, code = replyText(t, a, "chat", "target")
	if got := relay.take(); !slices.Equal(got, []gmproto.ActionType{gmproto.ActionType_SEND_MESSAGE}) {
		t.Fatalf("a reply to a held message asked the phone for %v", got)
	}
	expectAnswer(t, "a reply to a held message", result, code, "pending", "cached")
}

// A target the newest page does not hold either, or a page the phone does not give, is refused before anything is sent,
// with a reason the app shows.
func TestReplyToAMessageNotFoundIsRefusedWithAReason(t *testing.T) {
	a, relay, phone, _ := sendingAdapter(t, true)
	phone.describes(rcsChat("chat", "sim-a"))
	phone.messages = map[string][]*gmproto.Message{"chat": {incoming("chat", "older")}}
	result, code := replyText(t, a, "chat", "gone")
	if answer, _ := result.(map[string]any); code != "" || answer["outcome"] != "rejected" || answer["reason"] != replyMissingReason {
		t.Fatalf("a reply to a message not found answered %v %q", result, code)
	}
	if slices.Contains(relay.take(), gmproto.ActionType_SEND_MESSAGE) {
		t.Fatal("a reply to a message not found was sent")
	}

	quiet, relay, _, _ := sendingAdapter(t, false)
	quiet.handleEvent(rcsChat("chat", "sim-a"))
	result, code = replyText(t, quiet, "chat", "gone")
	if answer, _ := result.(map[string]any); code != "" || answer["outcome"] != "rejected" || answer["reason"] != replyLookupReason {
		t.Fatalf("a reply whose target could not be looked up answered %v %q", result, code)
	}
	if slices.Contains(relay.take(), gmproto.ActionType_SEND_MESSAGE) {
		t.Fatal("a reply whose target could not be looked up was sent")
	}
}

// A send whose budget is already spent fails before it is posted. That error read as pending, so the app counted a
// message nobody sent as handed to the phone.
func TestSubmitWithItsBudgetSpentIsRefusedNotPending(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), time.Millisecond)
	defer cancel()
	<-ctx.Done()
	posted := false
	result, code := submitText(ctx, &gmproto.SendMessageRequest{TmpID: "txn"}, func(ctx context.Context, _ *gmproto.SendMessageRequest) (*gmproto.SendMessageResponse, error) {
		posted = true
		return nil, ctx.Err()
	})
	if code != "" || result["outcome"] != "rejected" || result["reason"] != sendLateReason || result["pendingId"] != nil {
		t.Fatalf("a send with its budget spent answered %v %q", result, code)
	}
	if posted {
		t.Fatal("a send with its budget spent was posted")
	}
}

// Google's upload endpoint as a slow network: it answers only when the request's own context ends, or after two seconds.
type stallingUpload struct{ relay *recordingRelay }

func (s stallingUpload) RoundTrip(req *http.Request) (*http.Response, error) {
	if req.URL.String() != util.UploadMediaURL {
		return s.relay.RoundTrip(req)
	}
	select {
	case <-req.Context().Done():
		return nil, req.Context().Err()
	case <-time.After(2 * time.Second):
		return nil, errors.New("upload stalled")
	}
}

// The upload ignored the request's budget, so one that ran past it went on to a send that could only fail, reported as
// pending. It now stops in time to leave the send its reserve, and is refused, with a reason, with nothing sent.
func TestSlowUploadIsRefusedNotPending(t *testing.T) {
	reserve := sendReserve
	sendReserve = 400 * time.Millisecond
	t.Cleanup(func() { sendReserve = reserve })
	relay := &recordingRelay{}
	a := relayAdapter(stallingUpload{relay})
	a.sims = map[string]*gmproto.SIMCard{"sim-a": testSIM("sim-a", 1)}
	a.handleEvent(rcsChat("chat", "sim-a"))
	a.upload = []byte("staged photo")
	params, _ := json.Marshal(map[string]string{"chatId": "chat", "name": "photo.jpg", "mime": "image/jpeg"})
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	began := time.Now()
	result, code := a.sendCall(ctx, request{Method: "ui.sendMedia", Params: params})
	took := time.Since(began)
	if answer, _ := result.(map[string]any); code != "" || answer["outcome"] != "rejected" || answer["reason"] != uploadLateReason {
		t.Fatalf("an upload past its budget answered %v %q", result, code)
	}
	// The stalled upload is cut off at the budget less the reserve (600 ms), not at the budget itself.
	if took < 500*time.Millisecond || took > 850*time.Millisecond {
		t.Fatalf("the upload was cut off after %v, not at the budget less the reserve", took)
	}
	if slices.Contains(relay.take(), gmproto.ActionType_SEND_MESSAGE) {
		t.Fatal("an upload past its budget went on to a send")
	}
}

// Google's relay as a network that stalls on the send itself: the post is cut off by the request's own deadline, with
// no answer from the relay.
type stallingSend struct{ relay *recordingRelay }

func (s stallingSend) RoundTrip(req *http.Request) (*http.Response, error) {
	res, err := s.relay.RoundTrip(req)
	if actions := s.relay.peek(); len(actions) == 0 || actions[len(actions)-1] != gmproto.ActionType_SEND_MESSAGE {
		return res, err
	}
	<-req.Context().Done()
	return nil, req.Context().Err()
}

// A deadline during the post to the relay was read as the phone not answering, so a send the relay may never have
// taken was reported as handed on. Only a deadline while waiting for the phone, after the relay took the post, is.
func TestSendCutOffDuringThePostIsUnknownNotPending(t *testing.T) {
	relay := &recordingRelay{}
	a := relayAdapter(stallingSend{relay})
	a.sims = map[string]*gmproto.SIMCard{"sim-a": testSIM("sim-a", 1)}
	a.handleEvent(rcsChat("chat", "sim-a"))
	result, code := sendText(t, a, "ui.sendText", "chat")
	if answer, _ := result.(map[string]any); code != "" || answer["outcome"] != "unknown" || answer["pendingId"] == nil {
		t.Fatalf("a send cut off during its post answered %v %q", result, code)
	}
	if got := relay.take(); !slices.Equal(got, []gmproto.ActionType{gmproto.ActionType_SEND_MESSAGE}) {
		t.Fatalf("a send cut off during its post asked the phone for %v", got)
	}

	// The relay takes the post and the phone does not answer within the budget: pending, its echo still to come.
	quiet, relay, _, _ := sendingAdapter(t, false)
	quiet.handleEvent(rcsChat("chat", "sim-a"))
	result, code = sendText(t, quiet, "ui.sendText", "chat")
	if answer, _ := result.(map[string]any); code != "" || answer["outcome"] != "pending" || answer["pendingId"] == nil {
		t.Fatalf("a send the phone has not answered answered %v %q", result, code)
	}
	if got := relay.take(); !slices.Equal(got, []gmproto.ActionType{gmproto.ActionType_SEND_MESSAGE}) {
		t.Fatalf("a send the phone has not answered asked the phone for %v", got)
	}
}

// The actions posted so far, left in place.
func (r *recordingRelay) peek() []gmproto.ActionType {
	r.mu.Lock()
	defer r.mu.Unlock()
	return slices.Clone(r.actions)
}
