package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"slices"
	"strings"
	"sync"
	"testing"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

func syntheticThread() *gvproto.Thread {
	return &gvproto.Thread{
		ID:   "t.+15550001",
		Read: false,
		Contacts: []*gvproto.Contact{{Name: "Synthetic Person", PhoneNumber: "+15550001"}},
		Folders:  []gvproto.ThreadFolder{gvproto.ThreadFolder_ALL_THREADS, gvproto.ThreadFolder_ALL_ARCHIVED_THREADS},
		Messages: []*gvproto.Message{
			{ID: "m3", Timestamp: 3000, Type: gvproto.Message_SMS_IN, Contact: &gvproto.Contact{PhoneNumber: "+15550001"}, Text: "newest"},
			{ID: "m2", Timestamp: 2000, Type: gvproto.Message_SMS_OUT, Text: "reply"},
			{ID: "m1", Timestamp: 1000, Type: gvproto.Message_MISSED_CALL, CoarseType: gvproto.Message_CALL_TYPE_MISSED},
		},
	}
}

func TestThreadSummaryUsesNewestMessageAndFolders(t *testing.T) {
	chat := convertThread(syntheticThread())
	if chat.Title != "Synthetic Person" || chat.Group || !chat.Unread || !chat.Archived || chat.Spam {
		t.Fatalf("unexpected summary: %+v", chat)
	}
	if chat.LastActivity != 3000 || chat.Preview == nil || chat.Preview.Text != "newest" || chat.Preview.Outgoing {
		t.Fatalf("preview should be the newest incoming message: %+v", chat.Preview)
	}
}

func TestGroupTitleFallsBackToNumbers(t *testing.T) {
	chat := convertThread(&gvproto.Thread{ID: "g", PhoneNumbers: []string{"+15550002", "+15550003"},
		Contacts: []*gvproto.Contact{{Name: "Named", PhoneNumber: "+15550002"}}})
	if !chat.Group || chat.Title != "Named, +15550003" || len(chat.Participants) != 2 {
		t.Fatalf("unexpected group: %+v", chat)
	}
}

func TestMessagesAreOldestFirstWithCallsInWords(t *testing.T) {
	messages := convertMessages("t", syntheticThread().GetMessages())
	if len(messages) != 3 || messages[0].ID != "m1" || messages[2].ID != "m3" {
		t.Fatalf("messages should be oldest first: %+v", messages)
	}
	if messages[0].Kind != "call" || messages[0].CallKind != "missed" || messages[0].Text != "Missed voice call" {
		t.Fatalf("missed call should be described: %+v", messages[0])
	}
	if !messages[1].Outgoing || messages[1].Sender != "" || messages[2].Sender != "+15550001" {
		t.Fatalf("direction and sender are wrong: %+v", messages)
	}
}

func TestCallsVoicemailAndMms(t *testing.T) {
	cases := []struct {
		message *gvproto.Message
		kind    string
		callKind string
		text    string
	}{
		{&gvproto.Message{Type: gvproto.Message_INCOMING_CALL, DurationSeconds: 75}, "call", "incoming", "Voice call · 1m 15s"},
		{&gvproto.Message{Type: gvproto.Message_OUTGOING_CALL, DurationSeconds: 7}, "call", "outgoing", "Voice call · 7s"},
		{&gvproto.Message{Type: gvproto.Message_OUTGOING_CALL_CANCELLED}, "call", "unanswered", "Unanswered voice call"},
		{&gvproto.Message{Type: gvproto.Message_VOICEMAIL, Transcript: &gvproto.Message_Transcript{
			Tokens: []*gvproto.Message_TranscriptToken{{Text: []byte("call ")}, {Text: []byte(" me")}, {Text: []byte{0xff}}}}}, "voicemail", "voicemail", "Voicemail: call me"},
		{&gvproto.Message{Type: gvproto.Message_SMS_IN, MMS: &gvproto.MMSMessage{Subject: "Subject", Text: "body",
			SenderPhoneNumber: "+15550009", Attachments: []*gvproto.Attachment{{ID: "a1", MimeType: "image/jpeg",
				Metadata: []*gvproto.Attachment_Metadata{{Size: gvproto.Attachment_Metadata_S128, Width: 128, Height: 96},
					{Size: gvproto.Attachment_Metadata_ORIGINAL, Width: 1600, Height: 1200}}}}}}, "text", "", "Subject\nbody"},
		{&gvproto.Message{Type: gvproto.Message_SMS_IN}, "unsupported", "", ""},
	}
	for _, c := range cases {
		got := convertMessage("t", c.message)
		if got.Kind != c.kind || got.CallKind != c.callKind || got.Text != c.text {
			t.Fatalf("got %q/%q/%q, want %q/%q/%q", got.Kind, got.CallKind, got.Text, c.kind, c.callKind, c.text)
		}
	}
	mms := convertMessage("t", cases[4].message)
	if mms.Sender != "+15550009" || len(mms.Attachments) != 1 || mms.Attachments[0].Width != 1600 {
		t.Fatalf("MMS sender or original size missing: %+v", mms)
	}
}

func TestMediaIsServedInOrderedChunks(t *testing.T) {
	data := bytes.Repeat([]byte{7}, mediaChunk+10)
	held := &heldMedia{id: "a", mime: "image/jpeg", data: data}
	var joined []byte
	for offset := 0; offset < len(data); {
		result, code := mediaChunkOf(held, offset)
		if code != "" {
			t.Fatal(code)
		}
		chunk := result.(map[string]any)
		part, _ := base64.StdEncoding.DecodeString(chunk["data"].(string))
		joined = append(joined, part...)
		if chunk["total"].(int) != len(data) || chunk["next"].(int) != offset+len(part) {
			t.Fatalf("bad chunk bookkeeping: %+v", chunk)
		}
		offset = chunk["next"].(int)
	}
	if !bytes.Equal(joined, data) {
		t.Fatal("chunks did not reassemble the attachment")
	}
	if _, code := mediaChunkOf(held, len(data)+1); code == "" {
		t.Fatal("an offset past the end must be refused")
	}
}

type capture struct {
	mu    sync.Mutex
	lines []string
}

func (c *capture) Write(p []byte) (int, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.lines = append(c.lines, strings.TrimSpace(string(p)))
	return len(p), nil
}

func TestChangeDetectionReportsOnlyNewerThreads(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	threads := []*gvproto.Thread{syntheticThread(), {ID: "quiet", Messages: []*gvproto.Message{{ID: "q", Timestamp: 500}}}}
	a.listThreads = func(context.Context) (*gvproto.RespListThreads, error) {
		return &gvproto.RespListThreads{Threads: threads}, nil
	}
	a.mu.Lock()
	a.lastSeen["t.+15550001"] = 3000
	a.lastSeen["quiet"] = 500
	a.mu.Unlock()
	a.detectChanges(context.Background())
	if len(sink.lines) != 0 {
		t.Fatalf("nothing changed, but events were sent: %v", sink.lines)
	}
	threads[0].Messages = append([]*gvproto.Message{{ID: "m4", Timestamp: 4000, Type: gvproto.Message_SMS_IN, Text: "later"}}, threads[0].Messages...)
	a.detectChanges(context.Background())
	got := changes(t, sink)
	if len(got) != 1 || fmt.Sprint(got[0]["chats"]) != "[t.+15550001]" {
		t.Fatalf("expected one change for the newer thread only: %v", sink.lines)
	}
	if fmt.Sprint(got[0]["messages"]) != "map[t.+15550001:[m4]]" {
		t.Fatalf("only the newly arrived message should be named: %v", sink.lines)
	}
	if strings.Contains(strings.Join(sink.lines, "\n"), "later") {
		t.Fatalf("message text never leaves in an event: %v", sink.lines)
	}
}

func TestHelloAndUnknownMethod(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	result, code := a.callContext(context.Background(), request{Method: "hello"})
	if code != "" || result.(map[string]any)["protocol"] != protocolVersion {
		t.Fatalf("hello failed: %v %s", result, code)
	}
	if _, code := a.callContext(context.Background(), request{Method: "ui.chats"}); code != "login_required" {
		t.Fatalf("reading before login must be refused, got %q", code)
	}
	// ui.deleteChat was the example until it was built (delete.go); Voice has no reactions, so this one stays unbuilt.
	if _, code := a.callContext(context.Background(), request{Method: "ui.react"}); code != "unknown_method" {
		t.Fatalf("an unbuilt method must be refused by name, got %q", code)
	}
}

func TestLoginRetriesOnceWithoutShortLivedCookiesOnlyWhenRefused(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	var seen []map[string]string
	refuse := func(status int) func(context.Context, *libgv.Client) (*gvproto.RespGetAccount, error) {
		return func(_ context.Context, client *libgv.Client) (*gvproto.RespGetAccount, error) {
			cookies := client.GetCookies()
			seen = append(seen, cookies)
			if _, stale := cookies["__Secure-1PSIDTS"]; stale || status != http.StatusUnauthorized {
				return nil, &libgv.ResponseError{Resp: &http.Response{StatusCode: status}}
			}
			return &gvproto.RespGetAccount{Account: &gvproto.Account{PrimaryDestinationID: "+15550009"}}, nil
		}
	}
	saved := map[string]string{"SID": "synthetic", "SAPISID": "synthetic", "__Secure-1PSIDTS": "synthetic", "SIDCC": "synthetic"}

	a.getAccount = refuse(http.StatusUnauthorized)
	_, account, attempt, err := a.login(context.Background(), saved)
	if err != nil || attempt != "without_short_lived" || account.GetAccount().GetPrimaryDestinationID() != "+15550009" {
		t.Fatalf("expected the second attempt to sign in: %v %q", err, attempt)
	}
	if len(seen) != 2 || len(seen[1]) != 2 || seen[1]["SID"] == "" || len(saved) != 4 {
		t.Fatalf("the retry must drop only short-lived cookies and leave the saved map alone: %v", seen)
	}
	refused := []string{}
	for _, line := range sink.lines {
		if strings.Contains(line, "synthetic") {
			t.Fatalf("diagnostics must never carry cookie values: %v", line)
		}
		if strings.Contains(line, `"login"`) {
			refused = append(refused, line)
		}
	}
	if len(refused) != 1 || !strings.Contains(refused[0], `"login":"saved"`) || !strings.Contains(refused[0], `"status":401`) {
		t.Fatalf("a refused attempt is reported by name and status only: %v", sink.lines)
	}
	if !strings.Contains(strings.Join(sink.lines, "\n"), `"removed":["SIDCC","__Secure-1PSIDTS"]`) {
		t.Fatalf("the retry should be visible as the short-lived cookies leaving: %v", sink.lines)
	}

	seen, sink.lines = nil, nil
	a.getAccount = refuse(http.StatusInternalServerError)
	if _, _, _, err := a.login(context.Background(), saved); err == nil || len(seen) != 1 || authCode(err) != "voice_unavailable" {
		t.Fatalf("an outage is not a refusal and must not retry: %v %d", err, len(seen))
	}

	seen = nil
	a.getAccount = refuse(http.StatusForbidden)
	if _, _, _, err := a.login(context.Background(), map[string]string{"SID": "synthetic"}); authCode(err) != "auth_required" || len(seen) != 1 {
		t.Fatalf("without short-lived cookies there is nothing to retry: %v %d", err, len(seen))
	}
}

func TestReadingElsewhereChangesTheConversationWithoutNewMessages(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	thread := syntheticThread()
	a.listThreads = func(context.Context) (*gvproto.RespListThreads, error) {
		return &gvproto.RespListThreads{Threads: []*gvproto.Thread{thread}}, nil
	}
	if _, code := a.callContext(context.Background(), request{Method: "ui.chats"}); code != "login_required" {
		t.Fatalf("listing needs a signed-in client, got %q", code)
	}
	a.mu.Lock()
	a.lastSeen[thread.ID] = 3000
	a.mu.Unlock()
	a.detectChanges(context.Background())
	if len(sink.lines) != 0 {
		t.Fatalf("the first look only records read state: %v", sink.lines)
	}
	thread.Read = true
	a.detectChanges(context.Background())
	if got := changes(t, sink); len(got) != 1 || fmt.Sprint(got[0]["chats"]) != "[t.+15550001]" || fmt.Sprint(got[0]["messages"]) != "map[]" {
		t.Fatalf("reading elsewhere should change the conversation and name no messages: %v", sink.lines)
	}
	a.detectChanges(context.Background())
	if len(changes(t, sink)) != 1 {
		t.Fatalf("an unchanged read state must not be reported again: %v", sink.lines)
	}
}

func TestMarkReadSendsOneThreadReadUpdate(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	if _, code := a.callContext(context.Background(), request{Method: "ui.markRead", Params: json.RawMessage(`{"chatId":"t1"}`)}); code != "login_required" {
		t.Fatalf("marking read needs a signed-in client, got %q", code)
	}
	a.client = libgv.NewClient(map[string]string{"SID": "synthetic"})
	var sent []*gvproto.ReqUpdateAttributes
	a.updateAttributes = func(_ context.Context, _ *libgv.Client, req *gvproto.ReqUpdateAttributes) error {
		sent = append(sent, req)
		return nil
	}
	if _, code := a.callContext(context.Background(), request{Method: "ui.markRead", Params: json.RawMessage(`{"chatId":""}`)}); code != "invalid_input" || len(sent) != 0 {
		t.Fatalf("a missing conversation must be refused before anything is sent: %q %d", code, len(sent))
	}
	result, code := a.callContext(context.Background(), request{Method: "ui.markRead", Params: json.RawMessage(`{"chatId":"t1"}`)})
	if code != "" || result.(map[string]any)["outcome"] != "accepted" || len(sent) != 1 {
		t.Fatalf("expected one accepted update: %v %q %d", result, code, len(sent))
	}
	if sent[0].GetAttributes().GetThreadID() != "t1" || !sent[0].GetAttributes().GetRead() || !sent[0].GetOtherAttributes().GetRead() ||
		sent[0].GetAttributes().GetArchived() || sent[0].GetAttributes().GetIsBlocked() || sent[0].GetAttributes().GetIsSpammer() {
		t.Fatalf("only the read attribute of that one conversation may change: %+v", sent[0])
	}
	if !a.lastRead["t1"] {
		t.Fatal("a conversation marked read here must not come back as changed elsewhere")
	}
	a.updateAttributes = func(context.Context, *libgv.Client, *gvproto.ReqUpdateAttributes) error {
		return &libgv.ResponseError{Resp: &http.Response{StatusCode: http.StatusUnauthorized}}
	}
	if _, code := a.callContext(context.Background(), request{Method: "ui.markRead", Params: json.RawMessage(`{"chatId":"t2"}`)}); code != "auth_required" || a.lastRead["t2"] {
		t.Fatalf("a refused update reports sign-in and records nothing: %q", code)
	}
	if !mutationMethod("ui.markRead") {
		t.Fatal("read updates run in order with other account changes")
	}
}

func TestVoicemailIdShapeNamesOnlyKindsAndLength(t *testing.T) {
	if got := idShape("PCIFFSUKSKNDVUVSCKBBRUBQIAYIAKIAC"); got != "upper_33" {
		t.Fatalf("unexpected shape %q", got)
	}
	if got := idShape("c.Ab1"); got != "upper_lower_digits_other_5" {
		t.Fatalf("unexpected shape %q", got)
	}
}

func TestVoicemailAudioComesFromTheVoicePlayerAddressOnlyAsAudio(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	a.client = libgv.NewClient(map[string]string{"SID": "synthetic"})
	voicemail := &gvproto.Message{ID: "VMONE", Type: gvproto.Message_VOICEMAIL, DurationSeconds: 23.4, MediaURL: "https://www.google.com/voice/media/svm/VMONE/x"}
	converted := convertMessage("t", voicemail)
	if converted.Kind != "voicemail" || len(converted.Attachments) != 1 || converted.Attachments[0].ID != "voicemail:VMONE" || !converted.Attachments[0].Voicemail || converted.Attachments[0].Duration != 23 {
		t.Fatalf("a voicemail with audio should carry one playable attachment: %+v", converted)
	}
	if len(convertMessage("t", &gvproto.Message{ID: "VMTWO", Type: gvproto.Message_VOICEMAIL}).Attachments) != 0 {
		t.Fatal("a voicemail without audio has nothing to play")
	}
	read := func(id string) (map[string]any, string) {
		result, code := a.callContext(context.Background(), request{Method: "ui.mediaRead", Params: json.RawMessage(`{"id":"` + id + `","offset":0}`)})
		if code != "" {
			return nil, code
		}
		return result.(map[string]any), ""
	}
	var asked, addresses []string
	body, contentType := "ID3synthetic-audio", "audio/mpeg"
	directRefused := false
	a.downloadVoicemail = func(_ context.Context, _ *libgv.Client, address, attempt string, _ int) (*http.Response, error) {
		asked, addresses = append(asked, attempt), append(addresses, address)
		if directRefused && attempt == "direct" {
			return nil, &libgv.ResponseError{Resp: &http.Response{StatusCode: http.StatusUnauthorized}}
		}
		return &http.Response{StatusCode: 200, Header: http.Header{"Content-Type": {contentType}}, Body: io.NopCloser(strings.NewReader(body))}, nil
	}
	// One not seen since the helper started (kept by the app across a restart) is asked for, and said once by kind only.
	if chunk, code := read("voicemail:VMOLD"); code != "" || chunk["total"] != len(body) || len(asked) != 1 || addresses[0] != "https://voice.google.com/u/0/a/v/VMOLD" {
		t.Fatalf("an older voicemail must be fetched: %v %q %v", chunk, code, asked)
	}
	if !slices.ContainsFunc(sink.lines, func(line string) bool { return strings.Contains(line, "unseen_asked") }) {
		t.Fatalf("asking for an unseen voicemail must be said: %v", sink.lines)
	}
	asked, addresses = nil, nil
	a.media = nil
	a.noteVoicemailMedia([]*gvproto.Message{voicemail, {ID: "../../x?y", Type: gvproto.Message_VOICEMAIL, MediaURL: "https://www.google.com/x"},
		{ID: "NOAUDIO", Type: gvproto.Message_VOICEMAIL}})
	if _, code := read("voicemail:../../x?y"); code != "media_unavailable" || len(asked) != 0 {
		t.Fatalf("an id that could change the address must never be asked: %q %v", code, asked)
	}
	if _, code := read("voicemail:NOAUDIO"); code != "media_unavailable" || len(asked) != 0 {
		t.Fatalf("a voicemail Voice said has no recording must never be asked: %q %v", code, asked)
	}
	chunk, code := read("voicemail:VMONE")
	if code != "" || chunk["mime"] != "audio/mpeg" || chunk["total"] != len(body) || len(asked) != 1 {
		t.Fatalf("expected the audio in one chunk: %v %q %v", chunk, code, asked)
	}
	if addresses[0] != "https://voice.google.com/u/0/a/v/VMONE" {
		t.Fatalf("the recording must come from Voice's own player address: %q", addresses[0])
	}
	contentType = "text/html"
	a.media = nil
	if _, code := read("voicemail:VMONE"); code != "media_unavailable" {
		t.Fatalf("a page instead of audio must be refused: %q", code)
	}
	contentType, directRefused, asked = "audio/mpeg", true, nil
	a.media = nil
	if chunk, code := read("voicemail:VMONE"); code != "" || chunk["total"] != len(body) || len(asked) != 2 || asked[1] != "cookies_only" {
		t.Fatalf("a refused request should be retried once with cookies only: %v %q %v", chunk, code, asked)
	}
	a.downloadVoicemail = func(context.Context, *libgv.Client, string, string, int) (*http.Response, error) {
		return nil, &libgv.ResponseError{Resp: &http.Response{StatusCode: http.StatusUnauthorized}}
	}
	a.media = nil
	if _, code := read("voicemail:VMONE"); code != "media_unavailable" {
		t.Fatalf("a refused recording is unavailable, not a signed-out account: %q", code)
	}
	for _, line := range sink.lines {
		if strings.Contains(line, "synthetic") || strings.Contains(line, "VMONE") || strings.Contains(line, "VMOLD") || strings.Contains(line, "google.com") {
			t.Fatalf("diagnostics must not carry audio, ids or addresses: %v", line)
		}
	}
}

func TestVoicemailAudioInPartsIsJoinedWhole(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	a.client = libgv.NewClient(map[string]string{"SID": "synthetic"})
	a.noteVoicemailMedia([]*gvproto.Message{{ID: "VMLONG", Type: gvproto.Message_VOICEMAIL, MediaURL: "https://www.google.com/x"}})
	whole := "ID3" + strings.Repeat("a", 97)
	var froms []int
	a.downloadVoicemail = func(_ context.Context, _ *libgv.Client, _, _ string, from int) (*http.Response, error) {
		froms = append(froms, from)
		end := min(from+40, len(whole))
		return &http.Response{StatusCode: http.StatusPartialContent, Header: http.Header{"Content-Type": {"audio/mpeg"},
			"Content-Range": {fmt.Sprintf("bytes %d-%d/%d", from, end-1, len(whole))}}, Body: io.NopCloser(strings.NewReader(whole[from:end]))}, nil
	}
	data, mime, code := a.voicemailAudio(context.Background(), a.client, "VMLONG")
	if code != "" || mime != "audio/mpeg" || string(data) != whole || len(froms) != 3 || froms[1] != 40 || froms[2] != 80 {
		t.Fatalf("expected the three parts joined in order: %q %q %d bytes %v", code, mime, len(data), froms)
	}
	if total, ok := rangeTotal("bytes 0-16383/44210"); !ok || total != 44210 {
		t.Fatal("Content-Range total not read")
	}
	if _, ok := rangeTotal("bytes 0-10/*"); ok {
		t.Fatal("an unknown total is not a total")
	}
}
