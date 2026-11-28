package main

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/util/jsontime"
)

type fakeWriteClient struct {
	fakeHistoryClient
	sent      []*slidetypes.SendTextRequest
	read      []*slidetypes.MarkReadRequest
	validated []*slidetypes.MarkReadRequest
	reacted   []*slidetypes.CreateReactionRequest
	edited    []*slidetypes.EditMessageRequest
	unsent    []*slidetypes.UnsendMessageRequest
	sentMedia []*slidetypes.SendMediaRequest
	sendErr   error
	readErr   error
	validErr  error
	validates bool
	nilOnSend bool
}

func (c *fakeWriteClient) SendMessage(_ context.Context, r *slidetypes.SendTextRequest) (*slidetypes.SendTextResponse, error) {
	c.sent = append(c.sent, r)
	if c.sendErr != nil {
		return nil, c.sendErr
	}
	if c.nilOnSend {
		return nil, nil
	}
	return &slidetypes.SendTextResponse{Message: slidetypes.SentMessage{
		MessageID: "sent-1", ID: "sent-1", TimestampMS: at(4000),
	}}, nil
}

func (c *fakeWriteClient) MarkRead(_ context.Context, r *slidetypes.MarkReadRequest) (*slidetypes.MarkReadResponse, error) {
	c.read = append(c.read, r)
	return &slidetypes.MarkReadResponse{}, c.readErr
}

func (c *fakeWriteClient) MarkReadValidation(_ context.Context, r *slidetypes.MarkReadRequest) (*slidetypes.MarkReadValidationResponse, error) {
	c.validated = append(c.validated, r)
	return &slidetypes.MarkReadValidationResponse{Validated: c.validates}, c.validErr
}

func (c *fakeWriteClient) SendReaction(_ context.Context, r *slidetypes.CreateReactionRequest) (*slidetypes.SendReactionResponse, error) {
	c.reacted = append(c.reacted, r)
	return &slidetypes.SendReactionResponse{}, nil
}

func (c *fakeWriteClient) EditMessage(_ context.Context, r *slidetypes.EditMessageRequest) (*slidetypes.EditMessageResponse, error) {
	c.edited = append(c.edited, r)
	return &slidetypes.EditMessageResponse{}, nil
}

func (c *fakeWriteClient) UnsendMessage(_ context.Context, r *slidetypes.UnsendMessageRequest) (*slidetypes.UnsendMessageResponse, error) {
	c.unsent = append(c.unsent, r)
	return &slidetypes.UnsendMessageResponse{}, nil
}

func (c *fakeWriteClient) SendMedia(_ context.Context, r *slidetypes.SendMediaRequest) (*slidetypes.SendMediaResponse, error) {
	c.sentMedia = append(c.sentMedia, r)
	return &slidetypes.SendMediaResponse{Message: slidetypes.SentMessage{MessageID: "media-1", ID: "media-1", TimestampMS: at(5000)}}, nil
}

// The fixture's threads carry both ids, so the two mutations can be told apart by which one they used.
func writeFixture(t *testing.T) (*readOnlyAdapter, *fakeWriteClient) {
	t.Helper()
	a, read := chatFixture()
	client := &fakeWriteClient{fakeHistoryClient: *read}
	for _, thread := range client.details {
		thread.ThreadFBID = "short-" + thread.ID
		thread.ThreadID = "long-" + thread.ID
	}
	a.client = client
	a.canMutate = true
	chatsOf(t, a, `{}`)
	return a, client
}

// Sending addresses the short id and marking read the long one. Upstream calls both fields IGThreadIGID, so using
// one where the other belongs is a mistake nothing else would catch.
func TestSendAndMarkReadUseDifferentThreadIds(t *testing.T) {
	a, client := writeFixture(t)

	result, code := a.uiSendText(context.Background(), json.RawMessage(`{"chatId":"direct","text":"hello"}`))
	if code != "" {
		t.Fatalf("send: %s", code)
	}
	if len(client.sent) != 1 {
		t.Fatalf("expected one send, got %d", len(client.sent))
	}
	if client.sent[0].IGThreadIGID == nil || *client.sent[0].IGThreadIGID != "short-direct" {
		t.Fatalf("send used the wrong thread id: %v", client.sent[0].IGThreadIGID)
	}
	if client.sent[0].Text.Value != "hello" || client.sent[0].OfflineThreadingID == "" {
		t.Fatal("the message or its threading id was not carried")
	}
	if sent := result.(map[string]any); sent["id"] != "sent-1" {
		t.Fatalf("the sent message was not reported back: %v", sent["id"])
	}

	if _, code := a.uiMarkRead(context.Background(), json.RawMessage(`{"chatId":"direct","messageId":"d2","at":3000}`)); code != "" {
		t.Fatalf("mark read: %s", code)
	}
	if len(client.read) != 1 || client.read[0].Metadata.IGThreadIGID != "long-direct" {
		t.Fatalf("mark read used the wrong thread id: %v", client.read)
	}
	// Upstream sends the mutation and then a validation; one without the other leaves the account's own read state
	// disagreeing with itself.
	if len(client.validated) != 1 || client.validated[0].Metadata.IGThreadIGID != "long-direct" {
		t.Fatal("the validation call was not made against the same thread")
	}
	if client.validated[0].Data.MessageTimestampMS.UnixMilli() != 3000 {
		t.Fatal("the validation call did not carry the message's time")
	}
}

// The diagnostics must be incapable of changing the account, not merely careful never to ask.
func TestReadOnlyModeRefusesEveryMutation(t *testing.T) {
	a, client := writeFixture(t)
	a.canMutate = false
	for _, call := range []struct {
		name   string
		invoke func() (any, string)
	}{
		{"send", func() (any, string) {
			return a.uiSendText(context.Background(), json.RawMessage(`{"chatId":"direct","text":"hello"}`))
		}},
		{"markRead", func() (any, string) {
			return a.uiMarkRead(context.Background(), json.RawMessage(`{"chatId":"direct","messageId":"d2","at":3000}`))
		}},
	} {
		if _, code := call.invoke(); code != "read_only" {
			t.Fatalf("%s was not refused: %q", call.name, code)
		}
	}
	if len(client.sent) != 0 || len(client.read) != 0 {
		t.Fatal("a refused mutation still reached Instagram")
	}
}

func TestSendRefusesWhatItCannotAddress(t *testing.T) {
	a, client := writeFixture(t)
	cases := []struct{ params, code string }{
		// A conversation this helper has not read cannot be addressed: guessing an id is how a message reaches the
		// wrong person.
		{`{"chatId":"never-seen","text":"hello"}`, "chat_unknown"},
		// Replying to a message request would accept it, which is a separate decision nobody has made.
		{`{"chatId":"request","text":"hello"}`, "chat_unknown"},
		{`{"chatId":"spam","text":"hello"}`, "chat_not_writable"},
		{`{"chatId":"direct","text":""}`, "invalid_params"},
		{`{"chatId":"","text":"hello"}`, "invalid_params"},
		{`{"chatId":"direct"}`, "invalid_params"},
	}
	for _, test := range cases {
		if _, code := a.uiSendText(context.Background(), json.RawMessage(test.params)); code != test.code {
			t.Fatalf("%s gave %q, expected %q", test.params, code, test.code)
		}
	}
	// A message longer than anything a person types is refused rather than posted.
	long := make([]byte, maxSendLength+1)
	for i := range long {
		long[i] = 'a'
	}
	payload, _ := json.Marshal(map[string]string{"chatId": "direct", "text": string(long)})
	if _, code := a.uiSendText(context.Background(), payload); code != "invalid_params" {
		t.Fatalf("an oversized message was not refused: %q", code)
	}
	if len(client.sent) != 0 {
		t.Fatal("a refused send still reached Instagram")
	}
}

// An uncertain send is never reported as a success and never retried here: the message may have gone.
func TestSendReportsFailureWithoutRetrying(t *testing.T) {
	a, client := writeFixture(t)
	client.sendErr = errors.New("network")
	if _, code := a.uiSendText(context.Background(), json.RawMessage(`{"chatId":"direct","text":"hello"}`)); code == "" {
		t.Fatal("a failed send was reported as a success")
	}
	if len(client.sent) != 1 {
		t.Fatalf("a failed send was retried: %d attempts", len(client.sent))
	}
	client.sendErr, client.nilOnSend = nil, true
	if _, code := a.uiSendText(context.Background(), json.RawMessage(`{"chatId":"direct","text":"hello"}`)); code != "send_uncertain" {
		t.Fatalf("an unrecognizable answer was not reported as uncertain: %q", code)
	}
	if len(client.sent) != 2 {
		t.Fatal("an uncertain send was retried")
	}
}

// Marking read succeeded even when only its confirmation failed, so reporting failure would invite a retry of
// something that already happened.
func TestMarkReadKeepsASucceededMutationWhenValidationFails(t *testing.T) {
	a, client := writeFixture(t)
	client.validErr = errors.New("network")
	result, code := a.uiMarkRead(context.Background(), json.RawMessage(`{"chatId":"direct","messageId":"d2","at":3000}`))
	if code != "" {
		t.Fatalf("a completed mark-read was reported as failed: %q", code)
	}
	if result.(map[string]any)["validated"] != false {
		t.Fatal("the unconfirmed validation was not reported")
	}
	if len(client.read) != 1 {
		t.Fatal("mark read was retried")
	}
}

func TestMarkReadRefusesWhatItCannotAddress(t *testing.T) {
	a, client := writeFixture(t)
	for _, params := range []string{
		`{"chatId":"never-seen","messageId":"m","at":1}`,
		`{"chatId":"direct","messageId":"","at":1}`,
		`{"chatId":"direct","messageId":"m","at":0}`,
		`{"chatId":"","messageId":"m","at":1}`,
	} {
		if _, code := a.uiMarkRead(context.Background(), json.RawMessage(params)); code == "" {
			t.Fatalf("mark read accepted %s", params)
		}
	}
	// Spam is Instagram's hidden message requests, whose senders are not told they were seen until one is accepted.
	// The thread is known and carries both ids, so only the folder can be what refuses it.
	if _, code := a.uiMarkRead(context.Background(), json.RawMessage(`{"chatId":"spam","messageId":"s1","at":100}`)); code != "chat_not_writable" {
		t.Fatalf("marking a spam thread read was not refused: %q", code)
	}
	if len(client.read) != 0 || len(client.validated) != 0 {
		t.Fatal("a refused mark-read still reached Instagram")
	}
	if a.requests.counts().Mutations != 0 {
		t.Fatal("a refused mark-read was counted as a request")
	}
}

// The validation call can succeed and still say no. Reporting the call's success as the answer made a wrong timestamp
// indistinguishable from a right one, which is the one thing the app's log line is there to tell apart.
func TestMarkReadReportsWhatValidationSaid(t *testing.T) {
	for _, test := range []struct {
		validates           bool
		err                 error
		validated, answered bool
	}{
		{true, nil, true, true},
		{false, nil, false, true},
		{true, errors.New("network"), false, false},
	} {
		a, client := writeFixture(t)
		client.validates, client.validErr = test.validates, test.err
		result, code := a.uiMarkRead(context.Background(), json.RawMessage(`{"chatId":"direct","messageId":"d2","at":3000}`))
		if code != "" {
			t.Fatalf("mark read: %s", code)
		}
		answer := result.(map[string]any)
		if answer["validated"] != test.validated || answer["answered"] != test.answered {
			t.Fatalf("validation reported as %v/%v, expected %v/%v", answer["validated"], answer["answered"], test.validated, test.answered)
		}
		// Two mutations reached Instagram whatever the answer, and a failed confirmation is one of them failing.
		counts := a.requests.counts()
		if counts.Mutations != 2 || (counts.Failures == 1) != (test.err != nil) {
			t.Fatalf("mark read was not counted as two requests: %+v", counts)
		}
	}
}

var _ = jsontime.UnixMilliString{}

// Reacting and editing address the short id like sending; unsending addresses the long one like marking read. Four
// mutations across two ids is exactly where a wrong one would go unnoticed.
func TestReactEditAndUnsendUseTheRightThreadId(t *testing.T) {
	a, client := writeFixture(t)

	if _, code := a.uiReact(context.Background(), json.RawMessage(`{"chatId":"direct","messageId":"d2","emoji":"❤"}`)); code != "" {
		t.Fatalf("react: %s", code)
	}
	if len(client.reacted) != 1 || client.reacted[0].Input.ThreadID != "short-direct" {
		t.Fatalf("react used the wrong thread id: %v", client.reacted)
	}
	if client.reacted[0].Input.ReactionStatus != slidetypes.ReactionStatusCreated || client.reacted[0].Input.Emoji != "❤" {
		t.Fatal("the reaction was not created with its emoji")
	}
	// Removing is the same call with a different status, so it cannot disagree with adding.
	if _, code := a.uiReact(context.Background(), json.RawMessage(`{"chatId":"direct","messageId":"d2","remove":true}`)); code != "" {
		t.Fatalf("remove reaction: %s", code)
	}
	if client.reacted[1].Input.ReactionStatus != slidetypes.ReactionStatusDeleted {
		t.Fatal("removing a reaction did not send the deleted status")
	}

	if _, code := a.uiEditMessage(context.Background(), json.RawMessage(`{"chatId":"direct","messageId":"d1","text":"changed"}`)); code != "" {
		t.Fatalf("edit: %s", code)
	}
	if len(client.edited) != 1 || client.edited[0].ThreadID != "short-direct" || client.edited[0].Body.Value != "changed" {
		t.Fatalf("edit used the wrong thread id or lost the words: %v", client.edited)
	}

	if _, code := a.uiDeleteMessage(context.Background(), json.RawMessage(`{"chatId":"direct","messageId":"d1"}`)); code != "" {
		t.Fatalf("unsend: %s", code)
	}
	// Unsending is the one that takes the long id, like marking read.
	if len(client.unsent) != 1 || client.unsent[0].SendData.ThreadID != "long-direct" {
		t.Fatalf("unsend used the wrong thread id: %v", client.unsent)
	}
}

func TestEveryMutationIsRefusedWhereItCannotBeMade(t *testing.T) {
	a, client := writeFixture(t)
	calls := map[string]func(string) (any, string){
		"react":  func(p string) (any, string) { return a.uiReact(context.Background(), json.RawMessage(p)) },
		"edit":   func(p string) (any, string) { return a.uiEditMessage(context.Background(), json.RawMessage(p)) },
		"unsend": func(p string) (any, string) { return a.uiDeleteMessage(context.Background(), json.RawMessage(p)) },
	}
	cases := []struct{ call, params, code string }{
		{"react", `{"chatId":"never-seen","messageId":"m","emoji":"x"}`, "chat_unknown"},
		{"react", `{"chatId":"spam","messageId":"m","emoji":"x"}`, "chat_not_writable"},
		// Adding a reaction with no emoji would be a reaction to nothing.
		{"react", `{"chatId":"direct","messageId":"m"}`, "invalid_params"},
		{"react", `{"chatId":"direct","messageId":""}`, "invalid_params"},
		{"edit", `{"chatId":"never-seen","messageId":"m","text":"x"}`, "chat_unknown"},
		{"edit", `{"chatId":"spam","messageId":"m","text":"x"}`, "chat_not_writable"},
		{"edit", `{"chatId":"direct","messageId":"m","text":""}`, "invalid_params"},
		{"unsend", `{"chatId":"never-seen","messageId":"m"}`, "chat_unknown"},
		{"unsend", `{"chatId":"direct","messageId":""}`, "invalid_params"},
	}
	for _, test := range cases {
		if _, code := calls[test.call](test.params); code != test.code {
			t.Fatalf("%s %s gave %q, expected %q", test.call, test.params, code, test.code)
		}
	}
	if len(client.reacted)+len(client.edited)+len(client.unsent) != 0 {
		t.Fatal("a refused mutation still reached Instagram")
	}

	// And none of them exist at all in the mode the diagnostics use.
	a.canMutate = false
	for name, call := range calls {
		if _, code := call(`{"chatId":"direct","messageId":"d1","emoji":"x","text":"x"}`); code != "read_only" {
			t.Fatalf("%s was not refused in read-only mode: %q", name, code)
		}
	}
}

// A reply carries the message it answers; an ordinary message carries nothing rather than an empty string, which
// Instagram would read as a reply to a message with no id.
func TestReplyCarriesItsTargetAndAPlainMessageDoesNot(t *testing.T) {
	a, client := writeFixture(t)
	if _, code := a.uiSendText(context.Background(), json.RawMessage(`{"chatId":"direct","text":"hi","replyTo":"d2"}`)); code != "" {
		t.Fatal(code)
	}
	if client.sent[0].ReplyToMessageID == nil || *client.sent[0].ReplyToMessageID != "d2" {
		t.Fatal("a reply lost the message it answers")
	}
	if _, code := a.uiSendText(context.Background(), json.RawMessage(`{"chatId":"direct","text":"hi"}`)); code != "" {
		t.Fatal(code)
	}
	if client.sent[1].ReplyToMessageID != nil {
		t.Fatal("an ordinary message was sent as a reply to nothing")
	}
}

// A file arrives a chunk at a time because the pipe carries one line of JSON at a time, then one call uploads and
// sends it. Only one file is ever in hand.
func TestMediaStagingTakesChunksInOrderAndOnlyOneFile(t *testing.T) {
	a, _ := writeFixture(t)
	stage := func(offset int, data string) (any, string) {
		payload, _ := json.Marshal(map[string]any{"offset": offset, "data": []byte(data)})
		return a.uiMediaStage(payload)
	}
	if result, code := stage(0, "abc"); code != "" || result.(map[string]int)["next"] != 3 {
		t.Fatalf("first chunk: %q %v", code, result)
	}
	if result, code := stage(3, "de"); code != "" || result.(map[string]int)["next"] != 5 {
		t.Fatalf("second chunk: %q %v", code, result)
	}
	// Out of order is a muddle of two files, not a file.
	if _, code := stage(99, "x"); code != "invalid_params" {
		t.Fatalf("an out-of-order chunk was accepted: %q", code)
	}
	// Starting again replaces what was there rather than appending to it.
	if result, code := stage(0, "z"); code != "" || result.(map[string]int)["next"] != 1 {
		t.Fatalf("restaging did not replace the file: %q %v", code, result)
	}
	a.canMutate = false
	if _, code := stage(0, "a"); code != "read_only" {
		t.Fatalf("staging was allowed in read-only mode: %q", code)
	}
}

func TestSendMediaRefusesWithNothingStaged(t *testing.T) {
	a, client := writeFixture(t)
	if _, code := a.uiSendMedia(context.Background(), json.RawMessage(`{"chatId":"direct","name":"a.jpg","mime":"image/jpeg"}`)); code != "no_media_staged" {
		t.Fatalf("expected no_media_staged, got %q", code)
	}
	if len(client.sentMedia) != 0 {
		t.Fatal("a message was sent with no file")
	}
	// A conversation that cannot be written to consumes the staged file rather than leaving it to be sent by accident.
	payload, _ := json.Marshal(map[string]any{"offset": 0, "data": []byte("bytes")})
	if _, code := a.uiMediaStage(payload); code != "" {
		t.Fatal(code)
	}
	if _, code := a.uiSendMedia(context.Background(), json.RawMessage(`{"chatId":"spam","name":"a.jpg"}`)); code != "chat_not_writable" {
		t.Fatalf("spam accepted a file: %q", code)
	}
	if _, code := a.uiSendMedia(context.Background(), json.RawMessage(`{"chatId":"direct","name":"a.jpg"}`)); code != "no_media_staged" {
		t.Fatalf("a refused send left its file staged: %q", code)
	}
}

// Only audio the app recorded as a voice message is uploaded as a voice clip; a picture, or audio sent as a file, is not.
func TestOnlyRecordedAudioIsAVoiceClip(t *testing.T) {
	if !voiceClip(true, "audio/mp4") || !voiceClip(true, "Audio/MP4") {
		t.Error("a recorded voice message was not a voice clip")
	}
	for _, mime := range []string{"image/jpeg", "video/mp4", "application/pdf", ""} {
		if voiceClip(true, mime) {
			t.Errorf("%q was marked a voice clip", mime)
		}
	}
	if voiceClip(false, "audio/mp4") {
		t.Error("an audio file the person chose was marked a voice clip")
	}
}

// The gap between the thread's activity time and the watermark Instagram says it recorded, read from the answer to the
// read itself; nothing when Instagram's answer carried no watermark.
func TestMarkReadWatermarkShortOfActivity(t *testing.T) {
	answer := &slidetypes.MarkReadResponse{ReadReceipt: slidetypes.ReadReceipt{WatermarkTimestampMS: unixMilli(1000)}}
	if short, ok := watermarkShort(answer, 1600); !ok || short != 600 {
		t.Fatalf("short=%d ok=%v", short, ok)
	}
	if _, ok := watermarkShort(&slidetypes.MarkReadResponse{}, 1600); ok {
		t.Fatal("an answer without a watermark was measured")
	}
	if _, ok := watermarkShort(nil, 1600); ok {
		t.Fatal("no answer was measured")
	}
}
